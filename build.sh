#!/bin/bash
# Builds Claude Graft.app into ./build.noindex. No Xcode project involved;
# swiftc is enough for a single-window SwiftUI app plus the small launcher
# binary.
#
#   ./build.sh                    one slice, for this machine
#   GRAFT_UNIVERSAL=1 ./build.sh  arm64 and x86_64, for shipping
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
# Spotlight skips any directory whose name ends in .noindex, and that suffix is
# the whole reason this one is spelled that way. A development build is a
# complete, launchable Claude Graft.app, so an ordinary build directory puts a
# second one in Spotlight beside the installed copy — indistinguishable, and
# the wrong one to open. Two instances then poll the same profiles, race for
# the same keychain prompt, and Sparkle updates a bundle nobody installed.
BUILD="$ROOT/build.noindex"
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

# Whoever signs, signs everything: a framework left carrying the ad-hoc
# signature inside an app signed with a Developer ID is a mixed bundle, which
# notarisation rejects. release.sh sets these; a plain build gets ad-hoc.
IDENTITY="${GRAFT_SIGNING_IDENTITY:--}"
SIGN_FLAGS="${GRAFT_SIGN_FLAGS:-}"

"$ROOT/Tools/fetch-sparkle.sh"

rm -rf "$BUILD"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources" "$APP/Contents/Frameworks"
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
    "$ROOT/Sources/Shared/Diagnostics.swift" \
    "$ROOT/Sources/Launcher/main.swift"

echo "building app"
compile "$APP/Contents/MacOS/ClaudeGraft" \
    -F "$ROOT/vendor" -framework Sparkle \
    "$ROOT/Sources/Shared/GraftCore.swift" \
    "$ROOT/Sources/Shared/Diagnostics.swift" \
    "$ROOT"/Sources/App/*.swift

echo "embedding Sparkle"
FRAMEWORK="$APP/Contents/Frameworks/Sparkle.framework"
ditto "$ROOT/vendor/Sparkle.framework" "$FRAMEWORK"

# Graft is not sandboxed, so Sparkle's XPC services do nothing for it, and
# removing them avoids the nested-entitlements dance and the launchd refusals
# that non-sandboxed apps hit with them present. The version letter is globbed
# because Sparkle has not always used B, and a silent no-op here would leave
# nested XPC for a --deep sign to trip over.
rm -rf "$FRAMEWORK"/Versions/*/XPCServices
rm -f "$FRAMEWORK/XPCServices"

# The binary was linked against @rpath/Sparkle.framework; this is what makes it
# resolve inside the bundle rather than wherever it was built from.
install_name_tool -add_rpath "@executable_path/../Frameworks" \
    "$APP/Contents/MacOS/ClaudeGraft" 2>/dev/null

# --deep is right here and wrong on the outer bundle: the framework's own nested
# helpers (Autoupdate, Updater.app) need signing, and doing it now means the app
# signature below seals a framework that is already settled.
codesign --force --deep $SIGN_FLAGS --sign "$IDENTITY" "$FRAMEWORK" >/dev/null

cp "$ROOT/Resources/Info.plist" "$APP/Contents/Info.plist"
/usr/libexec/PlistBuddy \
    -c "Set :CFBundleShortVersionString $VERSION" \
    -c "Set :CFBundleVersion $VERSION" \
    "$APP/Contents/Info.plist" >/dev/null
if [ -f "$ROOT/Resources/AppIcon.icns" ]; then
    cp "$ROOT/Resources/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"
fi
cp "$ROOT/Resources/ClaudeRuntime.entitlements" \
    "$APP/Contents/Resources/ClaudeRuntime.entitlements"
for localization in "$ROOT"/Resources/*.lproj; do
    ditto "$localization" "$APP/Contents/Resources/$(basename "$localization")"
done

codesign --force $SIGN_FLAGS --sign "$IDENTITY" "$APP" >/dev/null

echo "built $APP ($VERSION, $ARCHES)"
