#!/usr/bin/env python3
"""Prepare relocatable arm64 native dependencies; audit/sign an assembled macOS app.

Build-machine inputs only: Homebrew's five media tools and a VLC 3 application.
No package manager, downloads, or mutations of installed dependencies are performed.
"""

import argparse
import hashlib
import json
import os
from pathlib import Path
import plistlib
import re
import shutil
import subprocess
import sys
import tempfile


TOOLS = ("ffmpeg", "ffprobe", "mkvmerge", "mkvextract", "mkvpropedit")
SYSTEM_PREFIXES = ("/usr/lib/", "/System/Library/")
MACH_MAGIC = {b"\xcf\xfa\xed\xfe", b"\xce\xfa\xed\xfe", b"\xfe\xed\xfa\xcf", b"\xfe\xed\xfa\xce", b"\xca\xfe\xba\xbe", b"\xbe\xba\xfe\xca", b"\xca\xfe\xba\xbf"}
MINIMUM = (15, 0, 0)
REPO = Path(__file__).resolve().parent.parent


def run(*args):
    result = subprocess.run([str(arg) for arg in args], capture_output=True, text=True)
    if result.returncode:
        raise RuntimeError(f"Command failed: {args}\n{result.stdout}{result.stderr}")
    return result.stdout.strip()


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def is_macho(path):
    if path.is_symlink() or not path.is_file():
        return False
    with path.open("rb") as stream:
        return stream.read(4) in MACH_MAGIC


def native_files(root):
    return sorted(path for path in root.rglob("*") if is_macho(path))


def system_reference(reference):
    return reference.startswith(SYSTEM_PREFIXES)


def inspect(path):
    architectures = run("/usr/bin/lipo", "-archs", path).split()
    if "arm64" not in architectures:
        raise RuntimeError(f"Missing arm64 slice: {path}: {architectures}")
    commands = run("/usr/bin/otool", "-arch", "arm64", "-l", path)
    dependencies, rpaths, minimums = [], [], []
    identity = None
    for block in re.split(r"Load command \d+\n", commands)[1:]:
        match = re.search(r"\bcmd (LC_\S+)", block)
        if not match:
            continue
        command = match.group(1)
        if command in {"LC_LOAD_DYLIB", "LC_LOAD_WEAK_DYLIB", "LC_REEXPORT_DYLIB", "LC_LOAD_UPWARD_DYLIB", "LC_LAZY_LOAD_DYLIB", "LC_ID_DYLIB"}:
            name = re.search(r"\bname (.+) \(offset \d+\)", block).group(1)
            if command == "LC_ID_DYLIB":
                identity = name
            else:
                dependencies.append(name)
        elif command == "LC_RPATH":
            rpaths.append(re.search(r"\bpath (.+) \(offset \d+\)", block).group(1))
        elif command == "LC_BUILD_VERSION":
            platform = re.search(r"\bplatform (\S+)", block).group(1)
            if platform not in {"1", "macos"}:
                raise RuntimeError(f"Not a macOS binary: {path}: platform {platform}")
            minimums.append(re.search(r"\bminos (\S+)", block).group(1))
        elif command == "LC_VERSION_MIN_MACOSX":
            minimums.append(re.search(r"\bversion (\S+)", block).group(1))
    if not minimums:
        raise RuntimeError(f"No macOS deployment target in {path}")
    for minimum in minimums:
        version = tuple(int(part) for part in minimum.split("."))
        if (version + (0, 0))[:3] > MINIMUM:
            raise RuntimeError(f"{path} needs macOS {minimum}; this package supports macOS 15.0")
    return {"architectures": architectures, "minimum_macos": max(minimums), "id": identity, "dependencies": dependencies, "rpaths": rpaths}


def expand(reference, loader, executable):
    return reference.replace("@loader_path", str(loader.parent)).replace("@executable_path", str(executable.parent))


def inventory(root):
    """Content hashes AND symlink targets/modes; timestamps never establish cache validity."""
    result = {}
    for path in sorted(root.rglob("*")):
        relative = str(path.relative_to(root))
        if path.is_symlink():
            result[relative] = {"link": os.readlink(path)}
        elif path.is_file():
            result[relative] = {"sha256": sha256(path), "mode": path.stat().st_mode & 0o777}
    return result


def copy(source, destination):
    destination.parent.mkdir(parents=True, exist_ok=True)
    if source.is_dir():
        shutil.copytree(source, destination, symlinks=True, dirs_exist_ok=True)
    else:
        shutil.copy2(source, destination)


class Bundle:
    def __init__(self, brew_bin, vlc_app):
        self.brew_bin = brew_bin
        self.vlc_app = vlc_app
        self.vlc = vlc_app / "Contents/MacOS"
        self.mapping = {}
        self.trees = {}
        self.metadata = {}
        self.edges = {}
        self.formulas = {}

    def add_tree(self, source, destination):
        self.trees[source] = destination
        for path in native_files(source):
            self.mapping[path.resolve()] = destination / path.relative_to(source)

    def locate(self, reference, source, metadata):
        executable = self.vlc / "VLC" if source.is_relative_to(self.vlc_app) else source
        if reference.startswith("@rpath/"):
            suffix = reference[len("@rpath/"):]
            candidates = [Path(expand(root, source, executable)) / suffix for root in metadata["rpaths"]]
            # VLC's distribution expects its launcher to supply the lib directory.
            candidates += [source.parent / suffix, self.vlc / "lib" / suffix, self.vlc_app / "Contents/Frameworks" / suffix]
        else:
            candidates = [Path(expand(reference, source, executable))]
        matches = [candidate.resolve() for candidate in candidates if candidate.is_file()]
        if not matches:
            raise RuntimeError(f"Cannot resolve {reference} loaded by {source}")
        return matches[0]

    def add_dependency(self, source):
        source = source.resolve()
        if source in self.mapping:
            return
        for parent in source.parents:
            if parent.suffix == ".framework":
                self.add_tree(parent, Path("Frameworks") / parent.name)
                return
        # Formula-qualified paths avoid collisions between unrelated dylibs.
        formula = self.formula_root(source)
        if formula is None:
            raise RuntimeError(f"Unexpected dependency outside Homebrew/VLC: {source}")
        self.mapping[source] = Path("lib") / formula.parent.name / source.name

    @staticmethod
    def formula_root(path):
        parts = path.parts
        if "Cellar" not in parts:
            return None
        index = parts.index("Cellar")
        return Path(*parts[:index + 3])

    def discover(self):
        for name in TOOLS:
            source = (self.brew_bin / name).resolve(strict=True)
            self.mapping[source] = Path("bin") / name
        with (self.vlc_app / "Contents/Info.plist").open("rb") as stream:
            self.vlc_version = plistlib.load(stream)["CFBundleShortVersionString"]
        if not self.vlc_version.startswith("3."):
            raise RuntimeError(f"LibVLC 3 is required; found {self.vlc_version}")
        for directory in ("lib", "plugins", "share"):
            source = self.vlc / directory
            if not source.is_dir():
                raise RuntimeError(f"Missing VLC runtime directory: {source}")
            self.add_tree(source, Path("vlc") / directory)
        while pending := sorted(set(self.mapping) - set(self.metadata)):
            for source in pending:
                metadata = inspect(source)
                self.metadata[source] = metadata
                edges = {}
                for reference in metadata["dependencies"]:
                    if system_reference(reference):
                        continue
                    target = self.locate(reference, source, metadata)
                    self.add_dependency(target)
                    edges[reference] = target
                self.edges[source] = edges
        destinations = list(self.mapping.values())
        if len(set(destinations)) != len(destinations):
            raise RuntimeError("Two source binaries map to the same bundle path")
        for source in self.mapping:
            formula = self.formula_root(source)
            if formula:
                self.formulas[formula] = Path("Licenses/Homebrew") / formula.parent.name / formula.name
        # Preserve MKVToolNix translations, including their copyright notices.
        mkv_root = self.formula_root((self.brew_bin / "mkvmerge").resolve())
        self.trees[mkv_root / "share/locale"] = Path("share/locale")

    def sources(self):
        sources = {str(source): {"sha256": sha256(source)} for source in self.mapping}
        for source in self.trees:
            sources[str(source)] = inventory(source)
        for root in self.formulas:
            for path in self.notice_files(root):
                sources[str(path)] = {"sha256": sha256(path)}
        sources["bundler"] = sha256(Path(__file__))
        sources["notices"] = inventory(REPO / "docs/licenses/macos")
        sources["third-party-notice"] = sha256(REPO / "docs/macos-third-party.txt")
        sources["vlc-version"] = self.vlc_version
        return sources

    @staticmethod
    def notice_files(root):
        # Retain the installed formula and bottle/source checksums, not just an upstream homepage.
        files = [path for path in root.iterdir() if path.is_file() and (path.name.upper().startswith(("COPYING", "LICENSE", "NOTICE", "AUTHORS", "COPYRIGHT", "README")) or path.name in {"INSTALL_RECEIPT.json", "sbom.spdx.json"})]
        files.extend((root / ".brew").glob("*.rb"))
        return sorted(files)

    def prepare(self, output, fingerprint):
        for source, destination in self.trees.items():
            copy(source, output / destination)
        for source, destination in self.mapping.items():
            if not (output / destination).exists():
                copy(source, output / destination)
        for root, destination in self.formulas.items():
            for source in self.notice_files(root):
                copy(source, output / destination / source.name)
        copy(REPO / "docs/licenses/macos", output / "Licenses/Upstream")
        copy(REPO / "docs/macos-third-party.txt", output / "Licenses/READ ME.txt")
        # Cached plugin indexes embed offsets from the original signed binaries. Let LibVLC rescan.
        for path in output.rglob("plugins.dat"):
            path.unlink()
        for path in sorted(output.rglob("_CodeSignature"), reverse=True):
            if path.is_dir():
                shutil.rmtree(path)
        records = []
        for source, destination in sorted(self.mapping.items()):
            target = output / destination
            target.chmod(target.stat().st_mode | 0o200)
            metadata = self.metadata[source]
            if metadata["architectures"] != ["arm64"]:
                thin = target.with_name(target.name + ".arm64")
                run("/usr/bin/lipo", target, "-thin", "arm64", "-output", thin)
                thin.replace(target)
            arguments = []
            if metadata["id"]:
                arguments += ["-id", "@loader_path/" + target.name]
            for reference, dependency in self.edges[source].items():
                relocated = "@loader_path/" + os.path.relpath(output / self.mapping[dependency], target.parent)
                arguments += ["-change", reference, relocated]
            # All non-system dependencies are now loader-relative; remove build-machine search paths.
            for rpath in set(metadata["rpaths"]):
                arguments += ["-delete_rpath", rpath]
            if arguments:
                run("/usr/bin/install_name_tool", *arguments, target)
            records.append({"path": str(destination), "source": str(source), "source_sha256": sha256(source), **metadata})
        manifest = {
            "architecture": "arm64", "minimum_macos": "15.0", "cache_key": fingerprint,
            "vlc": {"version": self.vlc_version, "source": str(self.vlc_app), "upstream": "https://download.videolan.org/pub/videolan/vlc/" + self.vlc_version + "/"},
            "tools": {},
            "homebrew_components": [{"name": root.parent.name, "version": root.name, "provenance": str(destination)} for root, destination in sorted(self.formulas.items())],
            "native_files": records,
            "redistribution": "Local build only. Collected notices and source URLs are not a complete corresponding-source distribution. See Licenses/READ ME.txt.",
        }
        sign(output)
        for name in TOOLS:
            manifest["tools"][name] = run(output / "bin" / name, "-version" if name.startswith("ff") else "--version").splitlines()[0]
        (output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
        audit(output)


def audit(root):
    """Fail closed on external references, wrong architecture/OS, or escaping symlinks."""
    root = root.resolve()
    for path in root.rglob("*"):
        if path.is_symlink() and (not path.exists() or not path.resolve().is_relative_to(root)):
            raise RuntimeError(f"Broken or escaping symlink: {path}")
    binaries = native_files(root)
    executable = root / "Contents/MacOS/HomeVideoLibrary" if root.suffix == ".app" else root / "bin/ffmpeg"
    for path in binaries:
        metadata = inspect(path)
        if metadata["architectures"] != ["arm64"]:
            raise RuntimeError(f"Bundle must contain arm64 only: {path}: {metadata['architectures']}")
        for rpath in metadata["rpaths"]:
            resolved = Path(expand(rpath, path, executable)).resolve()
            if "@" in str(resolved) or not resolved.is_relative_to(root):
                raise RuntimeError(f"External runtime search path in {path}: {rpath}")
        for reference in metadata["dependencies"]:
            if system_reference(reference):
                continue
            if reference.startswith("@rpath/"):
                candidates = [Path(expand(rpath, path, executable)) / reference[len("@rpath/"):] for rpath in metadata["rpaths"]]
            elif reference.startswith(("@loader_path/", "@executable_path/")):
                candidates = [Path(expand(reference, path, executable))]
            else:
                raise RuntimeError(f"Non-relocatable dependency in {path}: {reference}")
            if not any(candidate.is_file() and candidate.resolve().is_relative_to(root) for candidate in candidates):
                raise RuntimeError(f"Unresolved bundled dependency in {path}: {reference}")
    print(f"Audited {len(binaries)} native files: arm64, macOS <= 15.0, no external non-system dependencies.", flush=True)


def sign(root):
    # Sign leaves first. Do not rely on --deep to discover nested executable helpers in Resources.
    for path in native_files(root):
        # Signing a bundle's main executable implicitly signs the whole enclosing bundle.
        # Defer it until its nested libraries/frameworks have been signed.
        if path.parent.name == "MacOS" and (path.parent.parent / "Info.plist").is_file():
            with (path.parent.parent / "Info.plist").open("rb") as stream:
                if plistlib.load(stream).get("CFBundleExecutable") == path.name:
                    continue
        run("/usr/bin/codesign", "--force", "--sign", "-", path)
    containers = [path for path in root.rglob("*") if path.is_dir() and not path.is_symlink() and path.suffix in {".framework", ".app", ".xpc", ".bundle"}]
    for container in sorted(containers, key=lambda path: len(path.parts), reverse=True):
        run("/usr/bin/codesign", "--force", "--sign", "-", container)
    if root.suffix == ".app":
        run("/usr/bin/codesign", "--force", "--deep", "--sign", "-", root)
    for path in native_files(root):
        run("/usr/bin/codesign", "--verify", "--strict", path)
    if root.suffix == ".app":
        run("/usr/bin/codesign", "--verify", "--deep", "--strict", root)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("destination", type=Path)
    parser.add_argument("--brew-bin", type=Path, default=Path("/opt/homebrew/bin"))
    parser.add_argument("--vlc-app", type=Path, default=Path("/Applications/VLC.app"))
    parser.add_argument("--cache", type=Path, default=REPO / "artifacts/macos/native-cache")
    parser.add_argument("--audit", action="store_true")
    parser.add_argument("--sign", action="store_true")
    parser.add_argument("--thin", action="store_true", help="Thin universal NuGet natives in the assembled app before auditing/signing")
    args = parser.parse_args()
    if sys.platform != "darwin":
        raise RuntimeError("Build and audit macOS bundles on macOS")
    if args.audit or args.sign or args.thin:
        if args.thin:
            for path in native_files(args.destination):
                if inspect(path)["architectures"] != ["arm64"]:
                    thin = path.with_name(path.name + ".arm64")
                    run("/usr/bin/lipo", path, "-thin", "arm64", "-output", thin)
                    thin.replace(path)
        if args.audit:
            audit(args.destination)
        if args.sign:
            sign(args.destination)
        return
    if args.destination.exists():
        raise RuntimeError(f"Destination already exists: {args.destination}")
    print("Inspecting native dependency closure and hashing build inputs…", flush=True)
    bundle = Bundle(args.brew_bin, args.vlc_app)
    bundle.discover()
    sources = bundle.sources()
    fingerprint = hashlib.sha256(json.dumps(sources, sort_keys=True).encode()).hexdigest()
    entry = args.cache / fingerprint
    payload = entry / "Native"
    valid = payload.is_dir() and (entry / "inventory.json").is_file()
    if valid:
        valid = inventory(payload) == json.loads((entry / "inventory.json").read_text())
    if valid:
        print(f"Reusing content-verified native cache {fingerprint[:16]}", flush=True)
    else:
        args.cache.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix=".prepare-", dir=args.cache) as temporary:
            stage = Path(temporary)
            bundle.prepare(stage / "Native", fingerprint)
            (stage / "sources.json").write_text(json.dumps(sources, indent=2) + "\n")
            (stage / "inventory.json").write_text(json.dumps(inventory(stage / "Native"), indent=2) + "\n")
            if entry.exists():
                shutil.rmtree(entry)
            stage.rename(entry)
        print(f"Prepared native cache {fingerprint[:16]}", flush=True)
    copy(payload, args.destination)


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, OSError, ValueError) as exception:
        print(f"Native packaging failed: {exception}", file=sys.stderr)
        sys.exit(1)
