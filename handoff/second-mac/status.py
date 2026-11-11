# Read-only. Asks the running relay what it carried and prints each request in
# order: when, which model, the status, the account that answered and any
# account passed over. Prints no token. Times are UTC.
import datetime, json, os, socket, sys
path = os.path.expanduser("~/Library/Application Support/ClaudeGraft/pool/relay.sock")
s = socket.socket(socket.AF_UNIX); s.settimeout(5)
try:
    s.connect(path)
except OSError as error:
    print("no relay to ask:", error); sys.exit(0)
s.sendall(b"{}\n")
data = b""
while not data.endswith(b"\n"):
    chunk = s.recv(65536)
    if not chunk: break
    data += chunk
status = json.loads(data)
if len(sys.argv) > 1:
    open(sys.argv[1], "w").write(json.dumps(status, indent=1))
def when(t):
    return datetime.datetime.fromtimestamp(t + 978307200, datetime.timezone.utc).strftime("%H:%M:%S")
names = {m["id"]: m["id"][:8] for m in status.get("members", [])}
print("== recent requests")
for r in status.get("recent", []):
    print(when(r["at"]), (r.get("model") or "?").ljust(18), r["status"], "answered by", names.get(r["servedBy"], r["servedBy"]),
          "passed over", [(names.get(p["id"], p["id"]), p["reason"]) for p in r.get("passedOver", [])],
          "session", (r.get("session") or "-")[:8])
print("== conversations")
for c in status.get("sessions", []):
    print(c["id"][:8], "from", names.get(c["from"], c["from"]), "held by", names.get(c.get("servedBy"), c.get("servedBy")),
          "model", c.get("model"), "requests", c.get("requests"))
