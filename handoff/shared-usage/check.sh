#!/bin/bash
# Read-only. Prints what each step of HANDOFF.md is checked against. Prints no
# token and no sealed value, only whether one is where it should be. Pass the
# two profile folders if they are not Claude and Claude-2.
SUPPORT="$HOME/Library/Application Support"
echo "== Grafts running"
ps -axo pid,command | grep "Claude Graft.app/Contents/MacOS" | grep -v grep | sed 's/^ *//'
echo "== Relay"
pgrep -fl graft-relay >/dev/null && pgrep -fl graft-relay | cut -c1-120 || echo "no relay process"
lsof -nP -iTCP:47823 -sTCP:LISTEN 2>/dev/null | tail -n +2 | awk '{print $1, $2, $9}' || true
echo "== Code environments"
python3 -I - "$SUPPORT" "$@" <<'PY'
import json, os, sys
support = sys.argv[1]
graft = os.path.join(support, "ClaudeGraft")
def load(name, default):
    try:
        return json.load(open(os.path.join(graft, name)))
    except (OSError, ValueError):
        return default
journal = load("pool/environment.json", {})
print("journal relay:", journal.get("relay"))
for folder in sys.argv[2:] or ("Claude", "Claude-2"):
    profile = os.path.join(support, folder)
    path = os.path.join(profile, "ccd-environment-config.json")
    entry = journal.get("profiles", {}).get(profile)
    try:
        outer = json.load(open(path))
        present = "present"
    except FileNotFoundError:
        outer, present = {}, "absent"
    except ValueError:
        outer, present = {}, "UNREADABLE"
    value = outer.get("envVars")
    if entry is None:
        state = "no journal entry"
    elif value == entry["pooled"]:
        state = "HOLDS THE LINES"
    elif value == entry.get("plain"):
        state = "without the lines"
    else:
        state = "changed since Graft read it"
    wanted = "-" if entry is None else entry["wanted"]
    journaled = os.path.exists(path + ".journal")
    print(f"{folder}: file {present}, envVars {'set' if value else 'none'}, "
          f"scopedEnvVars {'set' if outer.get('scopedEnvVars') else 'none'}, "
          f"wanted={wanted}, {state}{', MID-WRITE' if journaled else ''}")
sharing = load("usage-sharing.json", {})
print("lenders:", json.dumps(sharing.get("lenders", {})))
print("assignments:", json.dumps({k: v.get("to") for k, v in sharing.get("assignments", {}).items()}))
accounts = load("pool/accounts.json", [])
for account in accounts if isinstance(accounts, list) else []:
    shown = {k: account.get(k) for k in ("id", "email", "accountUuid", "scopes", "expires")}
    shown["sealed"] = "present" if account.get("sealed") else "MISSING"
    print("account:", json.dumps(shown))
raw = open(os.path.join(graft, "pool/accounts.json")).read() if os.path.exists(os.path.join(graft, "pool/accounts.json")) else ""
print("refresh token stored:", "YES" if "refresh" in raw.lower() else "no")
PY
echo "== Last pool lines in the diagnostics log"
grep -E '"(pool\.|relay)' "$SUPPORT/ClaudeGraft/diagnostics.log" 2>/dev/null | tail -n 8 | cut -c1-200
