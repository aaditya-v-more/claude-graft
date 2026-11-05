#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
BUILD="$ROOT/build.noindex/sidebar-tests"
mkdir -p "$BUILD"
cd "$ROOT"
node Tests/sidebar-storage.cjs
swiftc -swift-version 5 -target "$(uname -m)-apple-macos${MACOS_DEPLOYMENT_TARGET:-13.0}" \
    "$ROOT"/Sources/Shared/*.swift "$ROOT/Tests/Sidebar/main.swift" -o "$BUILD/tests"
"$BUILD/tests"
