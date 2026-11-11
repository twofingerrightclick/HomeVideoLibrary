#!/bin/bash
# Tests a relocated copy; never modifies the shipping app, installed dependencies, or user settings.
set -euo pipefail
if [[ $# -ne 1 || ! -d "$1/Contents/MacOS" ]]; then
  echo 'Usage: bash scripts/smoke-test-macos-package.sh "/path/Home Video Library.app"' >&2
  exit 1
fi
script_dir="$(cd -- "$(dirname -- "$0")" && pwd)"
source_app="$(cd -- "$1" && pwd)"
scratch="$(mktemp -d "${TMPDIR:-/tmp}/hvl-package-smoke.XXXXXX")"
trap 'rm -rf -- "$scratch"' EXIT
app="$scratch/Relocated Library.app"
/usr/bin/ditto "$source_app" "$app"
macos="$app/Contents/MacOS"
dotnet build "$script_dir/macos-package-smoke/PackageSmoke.csproj" -c Release \
  -p:BundleMacOS="$macos" -p:UseSharedCompilation=false -m:1 -nodeReuse:false -o "$scratch/harness"
cp "$scratch/harness/PackageSmoke" "$scratch/harness/PackageSmoke.dll" "$macos/"
# The harness uses exactly the app's runtime and dependency graph, not an installed .NET runtime.
cp "$macos/HomeVideoLibrary.deps.json" "$macos/PackageSmoke.deps.json"
cp "$macos/HomeVideoLibrary.runtimeconfig.json" "$macos/PackageSmoke.runtimeconfig.json"
/usr/bin/codesign --force --sign - "$macos/PackageSmoke"
mkdir -p "$scratch/home" "$scratch/work"
cd "$scratch/work"
/usr/bin/env -i HOME="$scratch/home" PATH=/usr/bin:/bin:/usr/sbin:/sbin \
  TMPDIR="$scratch/" LANG=en_US.UTF-8 DOTNET_MULTILEVEL_LOOKUP=0 \
  "$macos/PackageSmoke" "$scratch/work"
