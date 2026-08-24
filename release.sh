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

# Sign with a Developer ID when one is available, otherwise ad-hoc. Resolved
# here but applied by build.sh, which signs the embedded framework as well —
# an ad-hoc framework inside a Developer ID app is a mixed bundle, and
# notarisation rejects those.
IDENTITY="${GRAFT_SIGNING_IDENTITY:-}"
if [ -z "$IDENTITY" ]; then
    IDENTITY="$(security find-identity -v -p codesigning 2>/dev/null \
        | awk -F'"' '/Developer ID Application/ {print $2; exit}')"
fi
if [ -n "$IDENTITY" ]; then
    echo "signing as $IDENTITY"
    export GRAFT_SIGN_FLAGS="--options runtime --timestamp"
else
    echo "no Developer ID found; ad-hoc signature only"
    IDENTITY="-"
fi
export GRAFT_SIGNING_IDENTITY="$IDENTITY"

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

codesign --verify --deep --strict "$APP"
echo "signature verified, framework included"

rm -rf "$DIST"
mkdir -p "$DIST"
mkdir -p "$DIST/sparkle"
ZIP="$DIST/sparkle/ClaudeGraft-$VERSION.zip"
ditto -c -k --keepParent "$APP" "$ZIP"
echo "packaged $ZIP"

# The zip is for Sparkle, which unpacks it correctly. People get a disk image.
# A zip has to be unarchived, and an unarchiver that drops the framework's
# symlinks or extended attributes leaves a bundle whose seal no longer matches
# what it claims — macOS calls that damaged and offers no way past it, unlike an
# unnotarised app, which at least has Open Anyway. A disk image is copied rather
# than extracted, so there is nothing for a third-party tool to get wrong.
DMG="$DIST/ClaudeGraft-$VERSION.dmg"
STAGE="$(mktemp -d)/stage"
mkdir -p "$STAGE"
ditto "$APP" "$STAGE/Claude Graft.app"
ln -s /Applications "$STAGE/Applications"
hdiutil create -volname "Claude Graft" -srcfolder "$STAGE" -ov -format UDZO -quiet "$DMG"
rm -rf "$(dirname "$STAGE")"

# A disk image whose contents do not verify is worse than no disk image: it is
# the failure this one exists to prevent.
MOUNT="$(hdiutil attach "$DMG" -nobrowse -readonly | sed -n 's|.*\(/Volumes/.*\)|\1|p')"
if ! codesign --verify --deep --strict "$MOUNT/Claude Graft.app" 2>/dev/null; then
    hdiutil detach "$MOUNT" -quiet
    echo "the app inside $DMG does not verify." >&2
    exit 1
fi
hdiutil detach "$MOUNT" -quiet
echo "packaged $DMG"

# The appcast is a file in docs/, which Pages serves straight off main. No
# branch to juggle, no CI secret: the signing key stays in this machine's
# keychain and generate_appcast reads it from there.
echo "updating the appcast"
mkdir -p "$ROOT/docs"
"$ROOT/vendor/bin/generate_appcast" \
    --download-url-prefix "https://github.com/aaditya-v-more/claude-graft/releases/download/$TAG/" \
    --link "https://github.com/aaditya-v-more/claude-graft" \
    -o "$ROOT/docs/appcast.xml" \
    "$DIST/sparkle"

# generate_appcast drops the signature silently when the key it finds does not
# match SUPublicEDKey, and an unsigned enclosure is one every client refuses.
# Better to hear about it here than from a user who cannot update.
grep -q "sparkle:edSignature" "$ROOT/docs/appcast.xml" \
    || { echo "the new appcast entry carries no EdDSA signature." >&2; exit 1; }
grep -q "ClaudeGraft-$VERSION.zip" "$ROOT/docs/appcast.xml" \
    || { echo "the appcast does not mention $VERSION." >&2; exit 1; }
echo "appcast has a signed entry for $VERSION"

if [ "${1:-}" = "--install" ]; then
    rm -rf "/Applications/Claude Graft.app"
    ditto "$APP" "/Applications/Claude Graft.app"
    echo "installed /Applications/Claude Graft.app"
fi

cat <<NOTE

To publish $TAG:
  git add docs/appcast.xml && git commit -m "Release $VERSION"
  git tag -a $TAG -m "Claude Graft $VERSION"
  git push origin main --tags
  gh release create $TAG "$ZIP" --title "Claude Graft $VERSION" --notes-file -

The appcast must be pushed for the feed to serve it, and the release must exist
for the URL inside it to resolve. Do both before telling anyone.

To notarise first (needs a Developer ID and an app-specific password):
  xcrun notarytool submit "$ZIP" --apple-id <you> --team-id <team> \\
      --password <app-specific-password> --wait
  xcrun stapler staple "$APP"
NOTE
