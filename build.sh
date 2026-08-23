#!/bin/bash
# Builds Claude Graft.app into ./build. No Xcode project involved; swiftc is
# enough for a single-window SwiftUI app plus the small launcher binary.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
BUILD="$ROOT/build"
APP="$BUILD/Claude Graft.app"
DEPLOYMENT_TARGET="${MACOS_DEPLOYMENT_TARGET:-13.0}"

rm -rf "$BUILD"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

echo "building launcher"
swiftc -O -swift-version 5 \
    -target "$(uname -m)-apple-macos$DEPLOYMENT_TARGET" \
    "$ROOT/Sources/Shared/GraftCore.swift" \
    "$ROOT/Sources/Launcher/main.swift" \
    -o "$APP/Contents/Resources/graft-launch"

echo "building app"
swiftc -O -swift-version 5 \
    -target "$(uname -m)-apple-macos$DEPLOYMENT_TARGET" \
    "$ROOT/Sources/Shared/GraftCore.swift" \
    "$ROOT"/Sources/App/*.swift \
    -o "$APP/Contents/MacOS/ClaudeGraft"

cp "$ROOT/Resources/Info.plist" "$APP/Contents/Info.plist"
if [ -f "$ROOT/Resources/AppIcon.icns" ]; then
    cp "$ROOT/Resources/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"
fi

codesign --force --sign - "$APP" >/dev/null 2>&1 || true

echo "built $APP"
