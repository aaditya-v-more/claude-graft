#!/bin/bash
# Fetches the Sparkle framework the app links against, into vendor/.
#
# Vendored rather than committed. It is fifteen megabytes of binary that nothing
# here edits, and a pinned version with a checksum says everything a copy in git
# would say about which build is in use, without carrying it in every clone.
# Same reasoning as the icon: a clean checkout reproduces it rather than storing
# it. build.sh calls this on its own when vendor/ is empty.
set -euo pipefail

VERSION="2.9.6"
SHA256="52bf9e88cdd972fc0c81501377a880e90d47031bd8ca5462488f843e2609e192"

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VENDOR="$ROOT/vendor"
STAMP="$VENDOR/.sparkle-version"

if [ -d "$VENDOR/Sparkle.framework" ] && [ "$(cat "$STAMP" 2>/dev/null)" = "$VERSION" ]; then
    exit 0
fi

echo "fetching Sparkle $VERSION"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
curl -fsSL -o "$WORK/sparkle.tar.xz" \
    "https://github.com/sparkle-project/Sparkle/releases/download/$VERSION/Sparkle-$VERSION.tar.xz"

# An update framework fetched over the wire and then trusted to verify every
# future update is worth checking once itself.
ACTUAL="$(shasum -a 256 "$WORK/sparkle.tar.xz" | awk '{print $1}')"
if [ "$ACTUAL" != "$SHA256" ]; then
    echo "Sparkle $VERSION checksum mismatch." >&2
    echo "  expected $SHA256" >&2
    echo "  got      $ACTUAL" >&2
    exit 1
fi

tar -xJf "$WORK/sparkle.tar.xz" -C "$WORK"
rm -rf "$VENDOR"
mkdir -p "$VENDOR"
# ditto rather than cp -R: the framework's version symlinks and its own
# signature layout do not survive a flattening copy.
ditto "$WORK/Sparkle.framework" "$VENDOR/Sparkle.framework"
ditto "$WORK/bin" "$VENDOR/bin"
printf '%s\n' "$VERSION" > "$STAMP"
echo "vendored Sparkle $VERSION into vendor/"
