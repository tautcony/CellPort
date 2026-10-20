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
brew_bin="$(command -v brew || true)"
if [[ "$libusb_arch" == arm64 && -x /opt/homebrew/bin/brew ]]; then
  brew_bin=/opt/homebrew/bin/brew
elif [[ "$libusb_arch" == x86_64 && -x /usr/local/bin/brew ]]; then
  brew_bin=/usr/local/bin/brew
fi
brew_prefix="${LIBUSB_PREFIX:-}"
brew_cellar="${LIBUSB_CELLAR:-}"
if [[ -n "$brew_bin" ]]; then
  [[ -n "$brew_prefix" ]] || brew_prefix="$($brew_bin --prefix libusb 2>/dev/null || true)"
  [[ -n "$brew_cellar" ]] || brew_cellar="$($brew_bin --cellar libusb 2>/dev/null || true)"
fi
libusb_source=""
candidate_paths=(
  "$brew_prefix/lib/libusb-1.0.dylib" \
  "$brew_prefix/lib/libusb-1.0.0.dylib" \
  /opt/homebrew/opt/libusb/lib/libusb-1.0.dylib \
  /opt/homebrew/opt/libusb/lib/libusb-1.0.0.dylib \
  /usr/local/opt/libusb/lib/libusb-1.0.dylib \
  /usr/local/opt/libusb/lib/libusb-1.0.0.dylib \
  /opt/homebrew/lib/libusb-1.0.dylib \
  /usr/local/lib/libusb-1.0.dylib
)
for candidate in "${candidate_paths[@]}"; do
  if [[ -f "$candidate" ]] && lipo -verify_arch "$libusb_arch" "$candidate" >/dev/null 2>&1; then
    libusb_source="$candidate"
    break
  fi
done
if [[ -z "$libusb_source" ]]; then
  # Homebrew's opt symlink can be absent on a fresh or partially linked runner.
  # Search the versioned cellar directly instead of assuming a fixed keg path.
  for cellar in "$brew_cellar" /opt/homebrew/Cellar/libusb /usr/local/Cellar/libusb; do
    [[ -d "$cellar" ]] || continue
    while IFS= read -r -d '' candidate; do
      if lipo -verify_arch "$libusb_arch" "$candidate" >/dev/null 2>&1; then
        libusb_source="$candidate"
        break 2
      fi
    done < <(find -L "$cellar" -type f \( -name 'libusb-1.0.dylib' -o -name 'libusb-1.0.0.dylib' \) -print0)
  done
fi
if [[ -z "$libusb_source" ]]; then
  echo "Could not find an architecture-matched libusb-1.0.dylib ($libusb_arch)." >&2
  echo "brew: ${brew_bin:-not found}; prefix: ${brew_prefix:-not found}; cellar: ${brew_cellar:-not found}" >&2
  for candidate in "${candidate_paths[@]}"; do
    if [[ -e "$candidate" ]]; then
      echo "Candidate $candidate has architectures: $(lipo -archs "$candidate" 2>/dev/null || echo unknown)" >&2
    fi
  done
  echo "Install libusb for the app architecture and retry." >&2
  exit 1
fi
echo "Using libusb ($libusb_arch): $libusb_source"
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
