#!/bin/bash
# Source/build-machine setup only. Release packages already include these dependencies.
set -euo pipefail

finish() {
  local result=$?
  if [[ $result -ne 0 ]]; then
    echo "Setup did not complete. See the error above; you can run this script again." >&2
  fi
  if [[ -t 0 && -t 1 ]]; then
    read -r -p "Press Return to close…" _ || true
  fi
}
trap finish EXIT

if [[ "$(uname -s)" != Darwin || $# -ne 0 ]]; then
  echo "Run this script on macOS without arguments." >&2
  exit 1
fi
if [[ $EUID -eq 0 ]]; then
  echo "Run this as your normal user, without sudo. Homebrew will request privileges if needed." >&2
  exit 1
fi

# Finder does not inherit the shell's Homebrew PATH.
brew_path="$(command -v brew || true)"
if [[ -z "$brew_path" ]]; then
  for candidate in /opt/homebrew/bin/brew /usr/local/bin/brew; do
    if [[ -x "$candidate" ]]; then brew_path="$candidate"; break; fi
  done
fi
if [[ -z "$brew_path" ]]; then
  echo "Install Homebrew from https://brew.sh, then run this script again." >&2
  echo "Homebrew's installer may also request Apple's Command Line Tools." >&2
  exit 1
fi

echo "Installing FFmpeg (including ffprobe), MKVToolNix, and VLC's native playback engine."
echo "The published Home Video Library app includes .NET; no SDK is needed."
export HOMEBREW_NO_INSTALL_UPGRADE=1
"$brew_path" install ffmpeg mkvtoolnix
# Use the default /Applications location, which the app discovers without shell configuration.
"$brew_path" install --cask --appdir=/Applications vlc

brew_prefix="$("$brew_path" --prefix)"
for tool in ffmpeg ffprobe mkvmerge mkvextract mkvpropedit; do
  if [[ ! -x "$brew_prefix/bin/$tool" ]]; then
    echo "Missing $brew_prefix/bin/$tool. Check your Homebrew installation." >&2
    exit 1
  fi
  if [[ "$tool" == ff* ]]; then
    "$brew_prefix/bin/$tool" -version
  else
    "$brew_prefix/bin/$tool" --version
  fi
done
if [[ ! -f /Applications/VLC.app/Contents/MacOS/lib/libvlc.dylib ]]; then
  echo "VLC's native library was not found in /Applications/VLC.app." >&2
  exit 1
fi
echo "Dependencies installed. Open Home Video Library.app and try importing and playing a video."
