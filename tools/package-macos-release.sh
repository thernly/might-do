#!/usr/bin/env bash
set -euo pipefail

# Build and package might-do for macOS.
#
# What this script does:
# 1) Publishes the .NET app for a macOS runtime identifier (RID).
# 2) Converts the PNG app icon into an ICNS file.
# 3) Wraps publish output into a standard .app bundle layout.
# 4) Writes Info.plist metadata required by macOS app bundles.
# 5) Produces a DMG installer image with create-dmg.

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"
APP_PROJECT="$REPO_ROOT/src/MightDo.App/MightDo.App.csproj"
APP_ASSETS_DIR="$REPO_ROOT/src/MightDo.App/Assets"
ICON_PNG="$APP_ASSETS_DIR/might-do-icon.png"
ICON_ICNS="$APP_ASSETS_DIR/might-do-icon.icns"
OUT_DIR="$REPO_ROOT/dist"
APP_NAME="might-do"
EXECUTABLE_NAME="MightDo.App"
TARGET_ARCH="${1:-$(uname -m)}"

# Map CPU architecture names to .NET runtime identifiers.
case "$TARGET_ARCH" in
  x86_64|amd64)
    RID="osx-x64"
    ;;
  arm64|aarch64)
    RID="osx-arm64"
    ;;
  *)
    echo "Unsupported architecture: $TARGET_ARCH. Use x86_64/amd64 or arm64/aarch64."
    exit 1
    ;;
esac

if [[ ! -f "$ICON_PNG" ]]; then
  echo "Missing icon source: $ICON_PNG"
  exit 1
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet is not installed or not on PATH."
  exit 1
fi

if ! command -v sips >/dev/null 2>&1; then
  echo "sips is required for macOS icon generation but was not found."
  exit 1
fi

if ! command -v create-dmg >/dev/null 2>&1; then
  echo "create-dmg is not installed. Install it with: brew install create-dmg"
  exit 1
fi

PUBLISH_DIR="$OUT_DIR/publish-$RID"
BUNDLE_DIR="$OUT_DIR/$APP_NAME.app"
BUNDLE_MACOS_DIR="$BUNDLE_DIR/Contents/MacOS"
BUNDLE_RES_DIR="$BUNDLE_DIR/Contents/Resources"
DMG_PATH="$OUT_DIR/$APP_NAME-$RID.dmg"

mkdir -p "$OUT_DIR"

# macOS app bundles use ICNS for dock/finder icons.
echo "Generating macOS icon from $ICON_PNG"
sips -s format icns "$ICON_PNG" --out "$ICON_ICNS" >/dev/null

echo "Publishing $APP_PROJECT for $RID"
dotnet publish "$APP_PROJECT" -c Release -r "$RID" --self-contained false -o "$PUBLISH_DIR"

# Bundle layout:
# - Contents/MacOS: executable + dependent binaries
# - Contents/Resources: icons and other resources
rm -rf "$BUNDLE_DIR"
mkdir -p "$BUNDLE_MACOS_DIR" "$BUNDLE_RES_DIR"

cp -R "$PUBLISH_DIR"/. "$BUNDLE_MACOS_DIR/"
cp "$ICON_ICNS" "$BUNDLE_RES_DIR/"

# Info.plist provides the metadata Finder and LaunchServices read.
cat > "$BUNDLE_DIR/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleExecutable</key>
  <string>MightDo.App</string>
  <key>CFBundleName</key>
  <string>might-do</string>
  <key>CFBundleDisplayName</key>
  <string>might-do</string>
  <key>CFBundleIdentifier</key>
  <string>com.might-do.app</string>
  <key>CFBundleVersion</key>
  <string>1.0</string>
  <key>CFBundleShortVersionString</key>
  <string>1.0</string>
  <key>LSMinimumSystemVersion</key>
  <string>10.15</string>
  <key>CFBundleIconFile</key>
  <string>might-do-icon.icns</string>
</dict>
</plist>
EOF

if [[ -f "$BUNDLE_MACOS_DIR/$EXECUTABLE_NAME" ]]; then
  chmod +x "$BUNDLE_MACOS_DIR/$EXECUTABLE_NAME"
fi

rm -f "$DMG_PATH"

echo "Packaging DMG from $BUNDLE_DIR"
# create-dmg options below mainly control Finder window presentation.
create-dmg \
  --volname "$APP_NAME" \
  --volicon "$ICON_ICNS" \
  --window-pos 200 120 \
  --window-size 800 500 \
  --icon-size 128 \
  --icon "$APP_NAME.app" 200 190 \
  --hide-extension "$APP_NAME.app" \
  --app-drop-link 600 185 \
  "$DMG_PATH" \
  "$OUT_DIR"

echo "Created app bundle: $BUNDLE_DIR"
echo "Created DMG: $DMG_PATH"
