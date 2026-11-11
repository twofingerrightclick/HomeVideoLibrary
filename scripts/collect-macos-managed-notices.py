#!/usr/bin/env python3
"""Collect exact resolved NuGet versions and available notices without downloading packages."""
import json
from pathlib import Path
import shutil
import sys
import xml.etree.ElementTree as ET


assets_path, destination = map(Path, sys.argv[1:])
assets = json.loads(assets_path.read_text())
destination.mkdir(parents=True, exist_ok=True)
packages = []
for identity, library in sorted(assets["libraries"].items()):
    if library["type"] != "package":
        continue
    roots = [Path(root) / library["path"] for root in assets["packageFolders"]]
    root = next((candidate for candidate in roots if candidate.is_dir()), None)
    if root is None:
        raise RuntimeError(f"Resolved package is missing: {identity}")
    record = {"package": identity, "sha512": library.get("sha512"), "notices": []}
    for name in library.get("files", []):
        source = root / name
        basename = source.name.lower()
        if source.is_file() and (basename.endswith(".nuspec") or any(term in basename for term in ("license", "copying", "notice", "copyright"))):
            relative = Path(identity) / name
            target = destination / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, target)
            record["notices"].append(str(relative))
            if basename.endswith(".nuspec"):
                document = ET.parse(source)
                for element in document.iter():
                    tag = element.tag.rsplit("}", 1)[-1]
                    if tag in {"license", "licenseUrl", "projectUrl", "copyright"}:
                        record[tag] = {"value": element.text, **element.attrib}
    packages.append(record)
(destination / "manifest.json").write_text(json.dumps(packages, indent=2) + "\n")
print(f"Collected provenance and available notices for {len(packages)} NuGet packages.")
