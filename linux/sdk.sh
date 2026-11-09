#!/usr/bin/env bash
# Runs a command inside the GNOME SDK with the Rust extension, so nothing has
# to be installed on the host. The cargo home sits in the tree, because the
# sandbox sees the tree and not the rest of the home folder.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
exec flatpak run --share=network --filesystem="$here" --cwd="$here" \
    --env=CARGO_HOME="$here/.cargo-home" --env=CARGO_TARGET_DIR="$here/target" \
    --command=bash org.gnome.Sdk//51 -c 'source /usr/lib/sdk/rust-stable/enable.sh && "$@"' sdk "$@"
