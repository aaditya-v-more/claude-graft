#!/bin/bash
# Builds a release copy of Claude Graft and packages it for distribution.
#
#   ./release.sh            build, package into dist/
#   ./release.sh --install  also replace /Applications/Claude Graft.app
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
DIST="$ROOT/dist"
APP="$ROOT/build/Claude Graft.app"

VERSION="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$ROOT/Resources/Info.plist")"

echo "Claude Graft $VERSION"

# The icon is drawn rather than stored, so a clean checkout builds the same one.
if [ ! -f "$ROOT/Resources/AppIcon.icns" ]; then
    echo "drawing the icon"
    ICONSET="$(mktemp -d)/AppIcon.iconset"
    swiftc -swift-version 5 -O "$ROOT/Tools/make-icon.swift" -o "$(dirname "$ICONSET")/make-icon"
    "$(dirname "$ICONSET")/make-icon" "$ICONSET" >/dev/null
    iconutil -c icns "$ICONSET" -o "$ROOT/Resources/AppIcon.icns"
fi

"$ROOT/test.sh" >/dev/null
echo "tests passed"

"$ROOT/build.sh" >/dev/null
echo "built"

# Sign with a Developer ID when one is available, otherwise ad-hoc. An ad-hoc
# build runs on this machine; anywhere else Gatekeeper wants right-click Open.
IDENTITY="${GRAFT_SIGNING_IDENTITY:-}"
if [ -z "$IDENTITY" ]; then
    IDENTITY="$(security find-identity -v -p codesigning 2>/dev/null \
        | awk -F'"' '/Developer ID Application/ {print $2; exit}')"
fi
if [ -n "$IDENTITY" ]; then
    echo "signing as $IDENTITY"
    codesign --force --deep --options runtime --timestamp \
        --sign "$IDENTITY" "$APP"
else
    echo "no Developer ID found; ad-hoc signature only"
    codesign --force --deep --sign - "$APP"
fi
codesign --verify --deep --strict "$APP"

rm -rf "$DIST"
mkdir -p "$DIST"
ZIP="$DIST/ClaudeGraft-$VERSION.zip"
ditto -c -k --keepParent "$APP" "$ZIP"
echo "packaged $ZIP"

if [ "${1:-}" = "--install" ]; then
    rm -rf "/Applications/Claude Graft.app"
    ditto "$APP" "/Applications/Claude Graft.app"
    echo "installed /Applications/Claude Graft.app"
fi

cat <<NOTE

To notarise (needs a Developer ID and an app-specific password):
  xcrun notarytool submit "$ZIP" --apple-id <you> --team-id <team> \\
      --password <app-specific-password> --wait
  xcrun stapler staple "$APP"
NOTE
