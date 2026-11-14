"""Read-only look at the chat records in every mirrored chat folder.

    python3 -I records.py snapshot <file>   write down every record's digest
    python3 -I records.py diff <file>       what appeared, changed or went since

Opens nothing for writing except the snapshot file it is given.
"""
import hashlib
import json
import os
import sys

SUPPORT = os.path.expanduser("~/Library/Application Support")
FIELDS = ["sessionId", "title", "titleSource", "isArchived", "lastAssistantUuid",
          "cliSessionId", "lastActivityAt"]


def folders():
    with open(os.path.join(SUPPORT, "ClaudeGraft/mirrored-chats.json")) as f:
        pairs = json.load(f).get("pairs", {})
    found = set()
    for key in pairs:
        for half in key.split("\x00"):
            if "/claude-code-sessions/" in half:
                found.add(half)
    return sorted(found)


def records():
    result = {}
    for folder in folders():
        for name in sorted(os.listdir(folder)):
            if name.startswith("local_") and name.endswith(".json"):
                path = os.path.join(folder, name)
                with open(path, "rb") as f:
                    result[path] = hashlib.sha256(f.read()).hexdigest()
    return result


def describe(path):
    try:
        with open(path) as f:
            record = json.load(f)
    except Exception as error:
        return "  unreadable: %s" % error
    return "\n".join("  %-18s %s" % (k, json.dumps(record.get(k))) for k in FIELDS)


def short(path):
    profile = path[len(SUPPORT) + 1:].split("/")[0]
    return "%s: %s" % (profile, os.path.basename(path))


if __name__ == "__main__":
    action, file = sys.argv[1], sys.argv[2]
    now = records()
    if action == "snapshot":
        with open(file, "w") as f:
            json.dump(now, f, indent=1, sort_keys=True)
        print("%d records in %s" % (len(now), ", ".join(folders())))
    elif action == "diff":
        with open(file) as f:
            then = json.load(f)
        added = [p for p in now if p not in then]
        changed = [p for p in now if p in then and now[p] != then[p]]
        gone = [p for p in then if p not in now]
        print("added %d, changed %d, gone %d, unchanged %d"
              % (len(added), len(changed), len(gone), len(now) - len(added) - len(changed)))
        for label, paths in (("ADDED", added), ("CHANGED", changed)):
            for path in sorted(paths, key=short):
                print("%s %s\n%s" % (label, short(path), describe(path)))
        for path in sorted(gone, key=short):
            print("GONE %s" % short(path))
