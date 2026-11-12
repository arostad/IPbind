#!/bin/bash
# Builds IPbind.app (arm64) and IPbind-mac-preview.zip into mac/dist. Run on a Mac with Xcode 15+.
set -euo pipefail
cd "$(dirname "$0")"
swift build -c release --arch arm64
BIN="$(swift build -c release --arch arm64 --show-bin-path)/IPbind"
rm -rf dist && mkdir -p dist/IPbind.app/Contents/{MacOS,Resources}
cp "$BIN" dist/IPbind.app/Contents/MacOS/IPbind
cp Support/Info.plist dist/IPbind.app/Contents/Info.plist
ICONSET="$(mktemp -d)/AppIcon.iconset"; mkdir -p "$ICONSET"
SRC=../icons/png/IPbind-1024.png
for s in 16 32 128 256 512; do
  sips -z $s $s "$SRC" --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
  sips -z $((s*2)) $((s*2)) "$SRC" --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o dist/IPbind.app/Contents/Resources/AppIcon.icns
codesign --force --deep -s - dist/IPbind.app
codesign --verify --verbose dist/IPbind.app
(cd dist && ditto -c -k --keepParent IPbind.app IPbind-mac-preview.zip)
echo "Built $(pwd)/dist/IPbind-mac-preview.zip"
