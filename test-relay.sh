#!/bin/bash
# Builds the usage pool's relay and runs it against a stand-in for the service
# on loopback: which account each request reached, what came back, and how one
# relay hands over to the next. Needs python3; touches no profile, no real
# port and no network.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
BUILD="$ROOT/build.noindex/relay-tests"
mkdir -p "$BUILD"
swiftc -swift-version 5 -target "$(uname -m)-apple-macos${MACOS_DEPLOYMENT_TARGET:-13.0}" \
    "$ROOT"/Sources/Shared/*.swift "$ROOT"/Sources/Relay/*.swift -o "$BUILD/graft-relay"
python3 "$ROOT/Tests/relay.py" "$BUILD/graft-relay"
