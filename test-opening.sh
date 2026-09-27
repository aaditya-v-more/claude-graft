#!/bin/bash
# Drives the app's Open action and Claude Desktop with disposable profiles.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
BUILD="$ROOT/build.noindex/opening-tests"
APP="$BUILD/Opening Checks.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources" "$BUILD/launcher"

# The launcher uses the normal entry point, with its storage redirected before
# it runs. No real histories, credentials or shortcut registrations are used.
sed '/^Graft.run(config)$/i\
Graft.applicationSupportOverride = URL(fileURLWithPath: config.profileDir).deletingLastPathComponent()\
Graft.claudeProjectsOverride = Graft.applicationSupport.appending(path: "empty-projects")\
Graft.claudeConfigFileOverride = Graft.applicationSupport.appending(path: "empty-login.json")\
' "$ROOT/Sources/Launcher/main.swift" > "$BUILD/launcher/main.swift"
swiftc -swift-version 5 \
    -target "$(uname -m)-apple-macos${MACOS_DEPLOYMENT_TARGET:-13.0}" \
    "$ROOT"/Sources/Shared/*.swift "$BUILD/launcher/main.swift" \
    -o "$APP/Contents/Resources/graft-launch"

swiftc -swift-version 5 \
    -target "$(uname -m)-apple-macos${MACOS_DEPLOYMENT_TARGET:-13.0}" \
    "$ROOT"/Sources/Shared/*.swift \
    "$ROOT/Sources/App/Model.swift" "$ROOT/Sources/App/Installer.swift" \
    "$ROOT/Sources/App/ProfileOpener.swift" "$ROOT/Sources/App/SessionStarter.swift" \
    "$ROOT/Sources/App/ClaudeCredentials.swift" "$ROOT/Tests/Opening/main.swift" \
    -o "$APP/Contents/MacOS/OpeningChecks"
cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>graft.opening-checks</string>
<key>CFBundleExecutable</key><string>OpeningChecks</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleDevelopmentRegion</key><string>en</string>
<key>LSUIElement</key><true/>
</dict></plist>
PLIST
codesign --force --sign - "$APP" >/dev/null
"$APP/Contents/MacOS/OpeningChecks" -AppleLanguages '(en)'
