#!/bin/bash
# Add the icon before signing a macOS app bundle.
set -euo pipefail
if [[ $# -ne 1 || ! -f "$1/Contents/Info.plist" ]]; then
  echo 'Usage: bash scripts/install-macos-icon.sh "/path/to/Home Video Library.app"' >&2
  exit 1
fi
script_dir="$(cd -- "$(dirname -- "$0")" && pwd)"
icon="$script_dir/../src/HomeVideoLibrary.Desktop/Assets/app-icon.icns"
resources="$1/Contents/Resources"
plist="$1/Contents/Info.plist"
mkdir -p "$resources"
cp "$icon" "$resources/app-icon.icns"
/usr/bin/plutil -replace CFBundleIconFile -string app-icon.icns "$plist"
/usr/bin/plutil -lint "$plist"
touch "$1"
