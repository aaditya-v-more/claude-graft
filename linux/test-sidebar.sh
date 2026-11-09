#!/usr/bin/env bash
# Sidebar sync against real Chromium storage, in disposable profiles.
#
# Needs a person at the machine and an installed Claude for Linux. It builds
# the crate's checks inside the SDK, then on the host copies the installed
# Claude's Electron runtime (about 220 MB, removed afterwards) and runs that
# copy headless, with no display, no session bus and no keyring, over
# profiles it makes under linux/target/sidebar-tests. It never reads or
# writes ~/.config/Claude or any other real profile, and it starts, shows and
# quits no Claude, but it does run Claude's own binary, which is why it is
# not part of the suite.
#
# GRAFT_KEEP_SIDEBAR_FIXTURES=1 leaves the profiles behind to look at.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
cd "$here"

./sdk.sh cargo test -p graft-sidebar
./sdk.sh cargo build -p graft-sidebar --example sidebar-integration

root="$here/target/sidebar-tests/run-$(date +%s)-$$"
mkdir -p "$root"
cleanup() {
    if [[ -z ${GRAFT_KEEP_SIDEBAR_FIXTURES:-} ]]; then
        rm -rf "$root"
    else
        echo "fixtures kept in $root"
    fi
}
trap cleanup EXIT

# Built in the SDK, run on the host, where Claude's runtime is.
"$here/target/debug/examples/sidebar-integration" "$root"
