#!/bin/bash
# Builds a release copy of Claude Graft and packages it for distribution.
#
#   ./release.sh            build, package into dist/
#   ./release.sh --install  also replace /Applications/Claude Graft.app
#
# The version comes from the VERSION file. Bump that, run this, publish what it
# names — there is nowhere else to remember to change.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
DIST="$ROOT/dist"
APP="$ROOT/build/Claude Graft.app"

VERSION="$(tr -d '[:space:]' < "$ROOT/VERSION")"
TAG="v$VERSION"

echo "Claude Graft $VERSION"

# Releasing the same version twice leaves two different binaries answering to
# one number, which nothing downstream — an update feed least of all — can tell
# apart. Caught here rather than after the upload.
if git -C "$ROOT" rev-parse "$TAG" >/dev/null 2>&1; then
    echo "$TAG already exists. Bump VERSION before releasing." >&2
    exit 1
fi

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

GRAFT_UNIVERSAL=1 "$ROOT/build.sh" >/dev/null
echo "built universal"

# What was actually produced, asked of the bundle rather than assumed. A single
# slice here would be a download that cannot run on half the Macs there are.
for binary in "$APP/Contents/MacOS/ClaudeGraft" "$APP/Contents/Resources/graft-launch"; do
    for arch in arm64 x86_64; do
        lipo -archs "$binary" | grep -qw "$arch" \
            || { echo "$(basename "$binary") is missing its $arch slice." >&2; exit 1; }
    done
done
STAMPED="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$APP/Contents/Info.plist")"
[ "$STAMPED" = "$VERSION" ] || { echo "bundle says $STAMPED, VERSION says $VERSION." >&2; exit 1; }
echo "verified $STAMPED, arm64 + x86_64"

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

To publish $TAG:
  git tag -a $TAG -m "Claude Graft $VERSION"
  git push origin $TAG
  gh release create $TAG "$ZIP" --title "Claude Graft $VERSION" --notes-file -

To notarise first (needs a Developer ID and an app-specific password):
  xcrun notarytool submit "$ZIP" --apple-id <you> --team-id <team> \\
      --password <app-specific-password> --wait
  xcrun stapler staple "$APP"
NOTE
