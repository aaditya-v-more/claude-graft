#!/bin/bash
# Builds Claude Graft.app into ./build. No Xcode project involved; swiftc is
# enough for a single-window SwiftUI app plus the small launcher binary.
#
#   ./build.sh                    one slice, for this machine
#   GRAFT_UNIVERSAL=1 ./build.sh  arm64 and x86_64, for shipping
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
BUILD="$ROOT/build"
APP="$BUILD/Claude Graft.app"
DEPLOYMENT_TARGET="${MACOS_DEPLOYMENT_TARGET:-13.0}"

# The one place the number is written down. Everything else — the bundle, the
# zip, the tag — reads it from here.
VERSION="$(tr -d '[:space:]' < "$ROOT/VERSION")"

# A build for this machine only has to run on it. A build being shipped has to
# run on machines nobody here can test, and an Intel Mac given an arm64-only
# bundle reports nothing more useful than a bounce in the Dock.
if [ -n "${GRAFT_UNIVERSAL:-}" ]; then
    ARCHES="arm64 x86_64"
else
    ARCHES="$(uname -m)"
fi

rm -rf "$BUILD"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
SLICES="$(mktemp -d)"
trap 'rm -rf "$SLICES"' EXIT

# Builds one binary per architecture and joins them. lipo is skipped for a
# single slice so an ordinary build stays one compile.
compile() {
    local output="$1"; shift
    local built=()
    for arch in $ARCHES; do
        local slice="$SLICES/$(basename "$output").$arch"
        swiftc -O -swift-version 5 \
            -target "$arch-apple-macos$DEPLOYMENT_TARGET" \
            "$@" -o "$slice"
        built+=("$slice")
    done
    if [ "${#built[@]}" -eq 1 ]; then
        mv "${built[0]}" "$output"
    else
        lipo -create "${built[@]}" -output "$output"
    fi
}

echo "building launcher"
compile "$APP/Contents/Resources/graft-launch" \
    "$ROOT/Sources/Shared/GraftCore.swift" \
    "$ROOT/Sources/Launcher/main.swift"

echo "building app"
compile "$APP/Contents/MacOS/ClaudeGraft" \
    "$ROOT/Sources/Shared/GraftCore.swift" \
    "$ROOT"/Sources/App/*.swift

cp "$ROOT/Resources/Info.plist" "$APP/Contents/Info.plist"
/usr/libexec/PlistBuddy \
    -c "Set :CFBundleShortVersionString $VERSION" \
    -c "Set :CFBundleVersion $VERSION" \
    "$APP/Contents/Info.plist" >/dev/null
if [ -f "$ROOT/Resources/AppIcon.icns" ]; then
    cp "$ROOT/Resources/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"
fi

codesign --force --sign - "$APP" >/dev/null 2>&1 || true

echo "built $APP ($VERSION, $ARCHES)"
