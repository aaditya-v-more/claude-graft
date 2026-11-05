#!/bin/bash
# On the maintainer's Mac, after reviewing the Windows artifacts for this tag.
# Only detached signatures are written. Publishing remains a separate release step.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
version="$(tr -d '[:space:]' < "$root/VERSION")"
expected="$(/usr/libexec/PlistBuddy -c 'Print :SUPublicEDKey' "$root/Resources/Info.plist")"
actual="$("$root/vendor/bin/generate_keys" -p)"
[ "$actual" = "$expected" ] || { echo "The signing key does not match Graft's embedded release key." >&2; exit 1; }
cd "${1:?Pass the directory containing the reviewed Windows setup files}"
for arch in x64 arm64; do
    setup="ClaudeGraft-$version-windows-$arch-setup.exe"
    [ -s "$setup" ] || { echo "Missing $setup" >&2; exit 1; }
    # Recompute on the signing machine. The signed filename also authenticates
    # the architecture and version, so a signed older build cannot be replayed.
    shasum -a 256 "$setup" > "$setup.sha256"
    signature="$("$root/vendor/bin/sign_update" -p "$setup.sha256")"
    "$root/vendor/bin/sign_update" --verify "$setup.sha256" "$signature"
    printf '%s\n' "$signature" > "$setup.sha256.sig"
done
