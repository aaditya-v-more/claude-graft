#!/bin/bash
# Exercises the real window with temporary profiles and mocked update replies.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
BUILD="$ROOT/build.noindex/layout-tests"
APP="$BUILD/Layout Checks.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
"$ROOT/Tools/fetch-sparkle.sh"

# Keep Shared and the app's views, but let the test own the application lifecycle:
# starting Graft's delegate would start monitoring the user's profiles.
sed -e '/^@main$/d' \
    -e 's/static let claudeUpdates = ClaudeDesktopUpdater()/static let claudeUpdates = ClaudeDesktopUpdater(session: layoutSession, version: { "1.49585.0" })/' \
    "$ROOT/Sources/App/ClaudeGraftApp.swift" > "$BUILD/ClaudeGraftApp.swift"
sources=()
for source in "$ROOT"/Sources/App/*.swift; do
    [[ "$source" == */ClaudeGraftApp.swift ]] || sources+=("$source")
done
swiftc -swift-version 5 \
    -target "$(uname -m)-apple-macos${MACOS_DEPLOYMENT_TARGET:-13.0}" \
    -F "$ROOT/vendor" -framework Sparkle \
    "$ROOT"/Sources/Shared/*.swift "${sources[@]}" \
    "$BUILD/ClaudeGraftApp.swift" "$ROOT/Tests/Layout/main.swift" \
    -Xlinker -rpath -Xlinker "$ROOT/vendor" -o "$APP/Contents/MacOS/LayoutChecks"
cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>graft.layout-checks</string>
<key>CFBundleExecutable</key><string>LayoutChecks</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleDevelopmentRegion</key><string>en</string>
<key>LSUIElement</key><true/>
</dict></plist>
PLIST
for localization in "$ROOT"/Resources/*.lproj; do
    ditto "$localization" "$APP/Contents/Resources/$(basename "$localization")"
done
codesign --force --sign - "$APP" >/dev/null
for language in en ru; do
    "$APP/Contents/MacOS/LayoutChecks" -AppleLanguages "($language)"
done
