#!/bin/bash
# Runs the test suite. Everything happens inside a temporary directory; the
# suite redirects Application Support and Applications before the first check.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
BUILD="$ROOT/build/tests"

mkdir -p "$BUILD"

swiftc -swift-version 5 \
    -target "$(uname -m)-apple-macos${MACOS_DEPLOYMENT_TARGET:-13.0}" \
    "$ROOT/Sources/Shared/GraftCore.swift" \
    "$ROOT/Sources/App/Model.swift" \
    "$ROOT/Sources/App/Installer.swift" \
    "$ROOT/Sources/App/Settings.swift" \
    "$ROOT/Sources/App/UsageMonitor.swift" \
    "$ROOT/Tests/main.swift" \
    -o "$BUILD/tests"

# Installer copies this into every bundle it builds, and finds it beside the
# running executable.
swiftc -swift-version 5 \
    -target "$(uname -m)-apple-macos${MACOS_DEPLOYMENT_TARGET:-13.0}" \
    "$ROOT/Sources/Shared/GraftCore.swift" \
    "$ROOT/Sources/Launcher/main.swift" \
    -o "$BUILD/graft-launch"

"$BUILD/tests"
