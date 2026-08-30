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
APP="$ROOT/build.noindex/Claude Graft.app"

VERSION="$(tr -d '[:space:]' < "$ROOT/VERSION")"
TAG="v$VERSION"

# The first version whose upgrade has to be agreed to rather than installed
# while nobody is looking, because it rearranges chats on disk in a way that
# cannot be undone. Sparkle prompts anyone below this and updates anyone at or
# above it silently, so it is a floor rather than a mark on one release: every
# entry from here on carries it. Drop it from a later 1.1.x and that release
# installs itself on a 1.0.x machine, skipping the very question 1.1.0 exists
# to ask — the same person, the same merge, no dialog.
CONFIRM_BELOW="1.1.0"

echo "Claude Graft $VERSION"

# Releasing the same version twice leaves two different binaries answering to
# one number, which nothing downstream — an update feed least of all — can tell
# apart. Caught here rather than after the upload.
if git -C "$ROOT" rev-parse "$TAG" >/dev/null 2>&1; then
    echo "$TAG already exists. Bump VERSION before releasing." >&2
    exit 1
fi

# The icon is drawn rather than stored, so a clean checkout builds the same one.
# The site's copy comes out of the same run: two files drawn by one program
# cannot end up showing different marks.
if [ ! -f "$ROOT/Resources/AppIcon.icns" ]; then
    echo "drawing the icon"
    ICONSET="$(mktemp -d)/AppIcon.iconset"
    swiftc -swift-version 5 -O "$ROOT/Tools/make-icon.swift" -o "$(dirname "$ICONSET")/make-icon"
    "$(dirname "$ICONSET")/make-icon" "$ICONSET" "$(dirname "$ICONSET")/icon.png" >/dev/null
    iconutil -c icns "$ICONSET" -o "$ROOT/Resources/AppIcon.icns"
    sips -Z 180 "$(dirname "$ICONSET")/icon.png" --out "$ROOT/docs/assets/icon.png" >/dev/null
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

# generate_appcast reads release notes from a file named after the archive, and
# embeds them when they carry no DOCTYPE or body tags. An update that stops to
# ask has to have something to say in the dialog it puts up, so the release the
# floor is set at must bring its own note; every other release may.
NOTES="$ROOT/docs/release-notes/$VERSION.html"
if [ -f "$NOTES" ]; then
    cp "$NOTES" "$DIST/sparkle/ClaudeGraft-$VERSION.html"
    echo "release notes from docs/release-notes/$VERSION.html"
elif [ "$VERSION" = "$CONFIRM_BELOW" ]; then
    echo "$VERSION is the version that stops to ask, and docs/release-notes/$VERSION.html is missing." >&2
    exit 1
fi

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

# generate_appcast merges into an appcast it finds beside the archives, and the
# download prefix it is given applies only to the build it has not seen before.
# Without seeding it with the live feed, this run would rewrite the file from
# the one new zip and every earlier version would vanish along with its URL.
BEFORE=0
ASKED_BEFORE=0
if [ -f "$ROOT/docs/appcast.xml" ]; then
    cp "$ROOT/docs/appcast.xml" "$DIST/sparkle/appcast.xml"
    BEFORE="$(grep -c "<item>" "$ROOT/docs/appcast.xml" || true)"
    ASKED_BEFORE="$(grep -c "sparkle:minimumAutoupdateVersion" "$ROOT/docs/appcast.xml" || true)"
fi

# --maximum-versions 0 keeps every entry. The default prunes to a handful per
# branch point, which quietly dropped the oldest release on each run — 1.0.0 and
# 1.0.1 had already gone this way before anyone noticed, and they were recovered
# out of git history rather than regenerated, since the signature in an entry is
# over an archive that is not built again.
"$ROOT/vendor/bin/generate_appcast" \
    --maximum-versions 0 \
    --download-url-prefix "https://github.com/aaditya-v-more/claude-graft/releases/download/$TAG/" \
    --link "https://github.com/aaditya-v-more/claude-graft" \
    -o "$ROOT/docs/appcast.xml" \
    "$DIST/sparkle"

# One new entry and not one fewer of the old ones. The check used to be
# "no fewer than before", which is satisfied exactly as well by adding this
# release and dropping the oldest — which is what was happening.
AFTER="$(grep -c "<item>" "$ROOT/docs/appcast.xml" || true)"
[ "$AFTER" -eq "$((BEFORE + 1))" ] \
    || { echo "the appcast should have gone $BEFORE -> $((BEFORE + 1)), went $BEFORE -> $AFTER." >&2; exit 1; }

# generate_appcast has no option for the floor, so it goes in afterwards. Safe
# to write by hand because nothing here signs the appcast itself — the
# signatures in it are over the archives, and adding an element beside them
# leaves every one of those untouched.
awk -v v="$VERSION" -v floor="$CONFIRM_BELOW" '
    { print }
    $0 ~ "<sparkle:shortVersionString>" v "</sparkle:shortVersionString>" {
        print "            <sparkle:minimumAutoupdateVersion>" floor "</sparkle:minimumAutoupdateVersion>"
    }
' "$ROOT/docs/appcast.xml" > "$ROOT/docs/appcast.xml.new"
mv "$ROOT/docs/appcast.xml.new" "$ROOT/docs/appcast.xml"

# One more than before, counted the same way the items are and for the same
# reason. This catches the floor failing to go into the new entry, and it
# catches generate_appcast dropping it from an older one on a later run — an
# entry that lost it is an entry that installs itself on a 1.0.x machine
# without asking, which is silent and would be found by nobody.
ASKED_AFTER="$(grep -c "sparkle:minimumAutoupdateVersion" "$ROOT/docs/appcast.xml" || true)"
[ "$ASKED_AFTER" -eq "$((ASKED_BEFORE + 1))" ] \
    || { echo "entries asking before they install should have gone $ASKED_BEFORE -> $((ASKED_BEFORE + 1)), went $ASKED_BEFORE -> $ASKED_AFTER." >&2; exit 1; }

# generate_appcast drops the signature silently when the key it finds does not
# match SUPublicEDKey, and an unsigned enclosure is one every client refuses.
# Better to hear about it here than from a user who cannot update.
grep -q "sparkle:edSignature" "$ROOT/docs/appcast.xml" \
    || { echo "the new appcast entry carries no EdDSA signature." >&2; exit 1; }
grep -q "ClaudeGraft-$VERSION.zip" "$ROOT/docs/appcast.xml" \
    || { echo "the appcast does not mention $VERSION." >&2; exit 1; }
echo "appcast has a signed entry for $VERSION"

# The version is written down twice, and the second one is a different
# repository. Nothing here pushes to it — this script builds and packages, and
# a person publishes — but the cask was the half nobody remembered, and it sat
# at 1.0.6 while the feed served 1.0.10. Prepared here, with the checksum taken
# from the disk image this run actually built, so publishing it is a push
# rather than an edit made by hand against a number read off a screen.
TAP_REPO="https://github.com/aaditya-v-more/homebrew-claude-graft.git"
TAP="$DIST/homebrew-claude-graft"
CASK="$TAP/Casks/claude-graft.rb"
DMG_SHA="$(shasum -a 256 "$DMG" | awk '{print $1}')"
TAP_READY=""
if git clone -q "$TAP_REPO" "$TAP" 2>/dev/null; then
    # What the cask said before this run. Anything other than the release
    # before this one means a previous release never reached the tap, which is
    # worth hearing about even though this run is about to correct it.
    WAS="$(sed -n 's/^  version "\(.*\)"$/\1/p' "$CASK")"
    /usr/bin/sed -i "" \
        -e "s|^  version \".*\"$|  version \"$VERSION\"|" \
        -e "s|^  sha256 \".*\"$|  sha256 \"$DMG_SHA\"|" "$CASK"

    grep -q "version \"$VERSION\"" "$CASK" && grep -q "sha256 \"$DMG_SHA\"" "$CASK" \
        || { echo "the cask in $TAP was not rewritten for $VERSION." >&2; exit 1; }

    git -C "$TAP" commit -q -am "Point the cask at $VERSION"
    TAP_READY="yes"
    echo "cask updated to $VERSION in $TAP (was $WAS)"
else
    echo "could not reach the tap; the cask must be updated by hand:" >&2
    echo "  version \"$VERSION\"  sha256 \"$DMG_SHA\"" >&2
fi

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
  gh release create $TAG "$ZIP" "$DMG" --title "Claude Graft $VERSION" --notes-file -
  git -C "$TAP" push origin HEAD

The appcast must be pushed for the feed to serve it, and the release must exist
for the URL inside it to resolve. Do both before telling anyone. The cask push
is the third: it is a separate repository, it is what \`brew install\` reads, and
it is the one that was forgotten for four releases running.

Then check the feed actually rebuilt. A push does not reliably queue a Pages
build — one has already errored here, and another never started — and a feed
still serving the old file means nobody is offered the update:

  curl -s https://aaditya-v-more.github.io/claude-graft/appcast.xml | grep shortVersionString

If it does not name this version, ask for a build and wait for it:

  gh api -X POST repos/aaditya-v-more/claude-graft/pages/builds

To notarise first (needs a Developer ID and an app-specific password):
  xcrun notarytool submit "$ZIP" --apple-id <you> --team-id <team> \\
      --password <app-specific-password> --wait
  xcrun stapler staple "$APP"
NOTE
