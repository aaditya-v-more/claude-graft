#!/bin/bash
# On the maintainer's Mac, after reviewing the Windows artifacts for this tag.
# Only detached signatures are written. Publishing remains a separate release step.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
version="$(tr -d '[:space:]' < "$root/VERSION")"
cd "${1:?Pass the directory containing the reviewed Windows setup files}"
publisher="${GRAFT_WINDOWS_PUBLISHER_THUMBPRINT:?Set the independently verified Windows publisher certificate thumbprint.}"
[[ "$publisher" =~ ^[0-9A-Fa-f]{40}$ ]] || { echo "Expected a 40-digit Windows publisher thumbprint." >&2; exit 1; }
trust=()
checksums=()
if [ -n "${GRAFT_AUTHENTICODE_CA_FILE:-}" ]; then
    bundle="$GRAFT_AUTHENTICODE_CA_FILE"
    [ -r "$bundle" ] || { echo "Cannot read $bundle" >&2; exit 1; }
    trust=(-CAfile "$bundle" -TSA-CAfile "$bundle")
fi
for arch in x64 arm64; do
    setup="ClaudeGraft-$version-windows-$arch-setup.exe"
    [ -s "$setup" ] || { echo "Missing $setup" >&2; exit 1; }
    checksum="$(python3 - "$setup" "$arch" "$version" "$publisher" <<'PY'
import hashlib
import json
import sys
path, arch, version, publisher = sys.argv[1:]
digest = hashlib.sha256()
with open(path, "rb") as file:
    for chunk in iter(lambda: file.read(1024 * 1024), b""):
        digest.update(chunk)
with open(path + ".build.json", encoding="utf-8-sig") as file:
    manifest = json.load(file)
signed = manifest.get("authenticode", {})
if (manifest.get("architecture") != arch or manifest.get("version") != version
        or manifest.get("installerSha256", "").lower() != digest.hexdigest()
        or signed.get("verified") is not True
        or signed.get("signerThumbprint", "").upper() != publisher.upper()):
    sys.exit(f"Rejected {path}: expected a matching publisher-verified production build.")
print(f"{digest.hexdigest()}  {path}")
PY
)"
    # Verify the actual executable against the independently chosen signer.
    # A fabricated or stale build manifest cannot authorize it.
    osslsigncode verify ${trust[@]+"${trust[@]}"} -require-leaf-hash "sha1:$publisher" -in "$setup"
    checksums+=("$checksum")
done
expected="$(/usr/libexec/PlistBuddy -c 'Print :SUPublicEDKey' "$root/Resources/Info.plist")"
actual="$("$root/vendor/bin/generate_keys" -p)"
[ "$actual" = "$expected" ] || { echo "The signing key does not match Graft's embedded release key." >&2; exit 1; }
for checksum in "${checksums[@]}"; do
    setup="${checksum#*  }"
    # Sign only the reviewed digest, even if the file is subsequently replaced.
    printf '%s\n' "$checksum" > "$setup.sha256"
    signature="$("$root/vendor/bin/sign_update" -p "$setup.sha256")"
    "$root/vendor/bin/sign_update" --verify "$setup.sha256" "$signature"
    printf '%s\n' "$signature" > "$setup.sha256.sig"
done
