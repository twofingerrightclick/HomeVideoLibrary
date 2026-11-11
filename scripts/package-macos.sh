#!/bin/bash
# Build a self-contained Apple Silicon .app. Installed native dependencies are build inputs only.
set -euo pipefail

if [[ "$(uname -s)" != Darwin ]]; then
  echo "Build the macOS package on a Mac (plutil, codesign and ditto are required)." >&2
  exit 1
fi
rid="${1:-osx-arm64}"
version="${2:-0.1.1}"
if [[ $# -gt 2 || "$rid" != osx-arm64 || ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ || "$(uname -m)" != arm64 ]]; then
  echo "Usage on Apple Silicon: bash scripts/package-macos.sh [osx-arm64] [version, e.g. 0.1.1]" >&2
  exit 1
fi
script_dir="$(cd -- "$(dirname -- "$0")" && pwd)"
repo_dir="$(cd -- "$script_dir/.." && pwd)"
cd "$repo_dir"
if ! command -v dotnet >/dev/null; then
  echo "Install the .NET 10 SDK on the build machine: https://dotnet.microsoft.com/download/dotnet/10.0" >&2
  exit 1
fi
output_root="$repo_dir/artifacts/macos"
package_name="HomeVideoLibrary-$version-$rid"
destination="$output_root/$package_name"
if [[ -e "$destination" || -e "$destination.zip" ]]; then
  echo "Output already exists: $destination (or its ZIP). Move it aside before rebuilding." >&2
  exit 1
fi
mkdir -p "$output_root"
stage="$(mktemp -d "$output_root/.package.XXXXXX")"
trap 'rm -rf -- "$stage"' EXIT
app="$stage/$package_name/Home Video Library.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

# The hash-validated cache avoids copying/relocating dependencies on every source build.
python3 "$script_dir/bundle-macos-native.py" "$app/Contents/Resources/Native"

# Keep native libraries loose for Avalonia, SQLite and libVLC interop; trimming is unverified.
dotnet publish src/HomeVideoLibrary.Desktop/HomeVideoLibrary.Desktop.csproj \
  -c Release -r "$rid" --self-contained true \
  -p:UseAppHost=true -p:PublishSingleFile=false -p:PublishTrimmed=false \
  -p:Version="$version" -o "$app/Contents/MacOS"

cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleExecutable</key><string>HomeVideoLibrary</string>
  <key>CFBundleIdentifier</key><string>org.homevideolibrary.desktop</string>
  <key>CFBundleName</key><string>Home Video</string>
  <key>CFBundleDisplayName</key><string>Home Video Library</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>LSMinimumSystemVersion</key><string>15.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.video</string>
</dict></plist>
PLIST
bash "$script_dir/install-macos-icon.sh" "$app"
chmod +x "$app/Contents/MacOS/HomeVideoLibrary"
# Retain the resolved managed dependency versions for release/source compliance work.
python3 "$script_dir/collect-macos-managed-notices.py" \
  src/HomeVideoLibrary.Desktop/obj/project.assets.json "$app/Contents/Resources/ManagedNotices"
cp "$repo_dir/docs/macos-third-party.txt" "$stage/$package_name/THIRD PARTY.txt"
cp "$repo_dir/docs/macos-install.txt" "$stage/$package_name/READ ME.txt"
# Audit the entire publish output, then sign every nested native file before the outer app.
python3 "$script_dir/bundle-macos-native.py" "$app" --thin --audit --sign
mv "$stage/$package_name" "$destination"
/usr/bin/ditto -c -k --sequesterRsrc --keepParent "$destination" "$destination.zip"
echo "Built: $destination"
echo "Archive: $destination.zip"
echo "Local ad-hoc build; Developer ID signing and notarization are still required for public distribution."
