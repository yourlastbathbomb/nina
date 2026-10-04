#!/bin/bash
# Stage the ZWO ASI camera SDK (arm64) and libusb into mac/native/stage for the macOS build.
#
#   mac/scripts/stage-zwo.sh [path/to/ASI_linux_mac_SDK_V*.tar.bz2 | extracted SDK dir]
#
# The ZWO dylib links /opt/homebrew/opt/libusb/lib/libusb-1.0.0.dylib. We copy libusb next to it,
# rewrite the reference to @loader_path, and re-sign both ad hoc (mandatory on arm64 after
# install_name_tool). libusb is LGPL-2.1 and stays dynamically linked.
set -euo pipefail

here="$(cd "$(dirname "$0")/.." && pwd)"
native="$here/native"
stage="$native/stage"
src="${1:-}"

if [[ -z "$src" ]]; then
    src="$(ls -d "$native"/zwo/ASI_linux_mac_SDK_V* 2>/dev/null | grep -v '\.tar\.bz2$' | sort -V | tail -1 || true)"
fi
if [[ -z "$src" ]]; then
    echo "No ZWO SDK found. Put ASI_linux_mac_SDK_V*.tar.bz2 (from ZWO's ASI_Camera_SDK.zip) in $native/zwo/ or pass its path." >&2
    exit 1
fi
if [[ -f "$src" ]]; then
    mkdir -p "$native/zwo"
    tar xjf "$src" -C "$native/zwo"
    src="$native/zwo/$(basename "$src" .tar.bz2)"
fi

asi="$(ls "$src"/lib/mac_arm64/libASICamera2.dylib.* | sort -V | tail -1)"
libusb_src="$(brew --prefix libusb 2>/dev/null)/lib/libusb-1.0.0.dylib"
if [[ ! -f "$libusb_src" ]]; then
    echo "libusb not found; run: brew install libusb" >&2
    exit 1
fi

rm -rf "$stage"
mkdir -p "$stage"
cp "$asi" "$stage/libASICamera2.dylib"
cp -L "$libusb_src" "$stage/libusb-1.0.0.dylib"
cp "$src/license.txt" "$stage/ZWO-LICENSE.txt"
chmod u+w "$stage"/*.dylib

old_usb="$(otool -L "$stage/libASICamera2.dylib" | awk '/libusb-1.0/ {print $1}')"
install_name_tool -id @rpath/libASICamera2.dylib "$stage/libASICamera2.dylib"
install_name_tool -change "$old_usb" @loader_path/libusb-1.0.0.dylib "$stage/libASICamera2.dylib"
install_name_tool -id @rpath/libusb-1.0.0.dylib "$stage/libusb-1.0.0.dylib"

codesign --force --sign - "$stage/libusb-1.0.0.dylib"
codesign --force --sign - "$stage/libASICamera2.dylib"

echo "Staged into $stage:"
for f in "$stage"/*.dylib; do
    echo "  $(basename "$f"): $(lipo -archs "$f")"
    otool -L "$f" | tail -n +2 | sed 's/^/      /'
done
