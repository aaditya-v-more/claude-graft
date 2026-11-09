#!/usr/bin/env bash
# Builds the Flatpak into a local repository and a single-file bundle in
# dist/linux, which is ignored, so nothing built here is ever committed.
#
#   ./build.sh            build and bundle
#   ./build.sh --install  also install the bundle for this user
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(dirname "$here")"
id=io.github.aaditya_v_more.ClaudeGraft
out="$root/dist/linux"
mkdir -p "$out"

flatpak run --filesystem="$root" --share=network org.flatpak.Builder \
    --force-clean --disable-rofiles-fuse \
    --state-dir="$here/.flatpak-builder" --repo="$here/repo" \
    "$here/flatpak-build" "$here/flatpak/$id.yml"
flatpak build-bundle "$here/repo" "$out/claude-graft.flatpak" "$id"
echo "Bundle: $out/claude-graft.flatpak"

if [[ "${1:-}" == "--install" ]]; then
    flatpak install --user -y --bundle "$out/claude-graft.flatpak"
fi
