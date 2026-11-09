#!/usr/bin/env bash
# The whole Linux suite, inside the GNOME SDK so nothing is installed on the
# host. Every test redirects its paths through graft_core::testing::Sandbox,
# so none of them reaches a real profile, keyring or Claude.
#
#   ./test.sh                 every crate
#   ./test.sh -p graft-app    one crate, or any other cargo test arguments
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
cd "$here"
if [[ $# -eq 0 ]]; then
    exec ./sdk.sh cargo test --workspace
fi
exec ./sdk.sh cargo test "$@"
