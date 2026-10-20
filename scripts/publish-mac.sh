#!/bin/bash
set -euo pipefail
repo_dir="$(cd "$(dirname "$0")/.." && pwd)"
if [[ "$(uname -s)" != Darwin ]]; then
  echo "Run this script on macOS (uses iconutil and codesign)." >&2
  exit 1
fi
default_rid=osx-arm64
if [[ "$(uname -m)" == x86_64 ]]; then default_rid=osx-x64; fi
rid="${1:-$default_rid}"
case "$rid" in
  osx-arm64|osx-x64) ;;
  *) echo "Usage: $0 [osx-arm64|osx-x64]" >&2; exit 1 ;;
esac

# Stage separately so a failed publish does not replace the previous app.
mkdir -p "$repo_dir/dist"
stage_dir="$(mktemp -d "$repo_dir/dist/.mac-build.XXXXXX")"
trap 'rm -rf "$stage_dir"' EXIT
bundle="$stage_dir/CellPort.app"
mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"
dotnet publish "$repo_dir/src/CellPort.App/CellPort.App.csproj" \
  -c Release -r "$rid" --self-contained true -p:UseAppHost=true \
  -o "$bundle/Contents/MacOS"
# The direct USB AT transport loads this adjacent dylib via NativeLibrary.
libusb_arch=arm64
if [[ "$rid" == osx-x64 ]]; then libusb_arch=x86_64; fi
libusb_source=""
for candidate in \
  "$(brew --prefix libusb 2>/dev/null || true)/lib/libusb-1.0.dylib" \
  /opt/homebrew/lib/libusb-1.0.dylib \
  /usr/local/lib/libusb-1.0.dylib; do
  if [[ -f "$candidate" ]] && lipo -verify_arch "$libusb_arch" "$candidate" >/dev/null 2>&1; then
    libusb_source="$candidate"
    break
  fi
done
if [[ -z "$libusb_source" ]]; then
  echo "Could not find an architecture-matched libusb-1.0.dylib ($libusb_arch). Install libusb for this architecture." >&2
  exit 1
fi
cp "$libusb_source" "$bundle/Contents/MacOS/libusb-1.0.dylib"
install_name_tool -id @loader_path/libusb-1.0.dylib "$bundle/Contents/MacOS/libusb-1.0.dylib"
codesign --force --sign - "$bundle/Contents/MacOS/libusb-1.0.dylib"
cp "$repo_dir/installer/macos/Info.plist" "$bundle/Contents/Info.plist"

iconset="$stage_dir/CellPort.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$repo_dir/docs/cellport-icon.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z "$double" "$double" "$repo_dir/docs/cellport-icon.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$bundle/Contents/Resources/CellPort.icns"
cp "$repo_dir/LICENSE" "$repo_dir/THIRD-PARTY-NOTICES.md" "$bundle/Contents/Resources/"
chmod +x "$bundle/Contents/MacOS/CellPort"
# Local ad-hoc signing. Public distribution still needs Developer ID / notarization.
codesign --force --deep --sign - "$bundle"
codesign --verify --deep --strict "$bundle"
output_dir="$repo_dir/dist/$rid"
mkdir -p "$output_dir"
rm -rf "$output_dir/CellPort.app"
mv "$bundle" "$output_dir/CellPort.app"
echo "Built: $output_dir/CellPort.app"
echo "Launch: open \"$output_dir/CellPort.app\""
