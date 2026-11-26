# Drives a built graft-relay against a stand-in for the service on loopback:
# which account each request reached, what came back, and how the relay steps
# aside. Nothing here touches a real profile, port 47823 or the network.
import gzip, hashlib, http.client, http.server, json, os, re, shutil, socket, ssl, subprocess, sys, tempfile, threading, time

RELAY = os.path.abspath(sys.argv[1])
failures = 0
checks = 0

def check(condition, what):
    global failures, checks
    checks += 1
    if not condition:
        failures += 1
        print("  FAIL", what)

def free_port():
    s = socket.socket(); s.bind(("127.0.0.1", 0)); port = s.getsockname()[1]; s.close(); return port

def digest(token):
    return hashlib.sha256(token.encode()).hexdigest()

now = time.time()
# What the stand-in reports for each account, changed by the checks as they go.
accounts = {
    "tok-A": {"5h": 0.10, "7d": 0.30, "7d-reset": now + 5 * 86400, "reject": False},
    "tok-B": {"5h": 0.20, "7d": 0.40, "7d-reset": now + 2 * 86400, "reject": False},
    "tok-C": {"5h": 0.20, "7d": 0.40, "7d-reset": now + 3 * 86400, "reject": False, "refuse": 401},
}
seen = []
# Which account holds each thread the stand-in has been told about. A thread is
# continued only from the account that holds it, as the service does.
threads = {}
STREAM = [b"event: message_start\ndata: {\"n\":1}\n\n", b"event: content_block_delta\ndata: {\"n\":2}\n\n",
          b"event: message_stop\ndata: {\"n\":3}\n\n"]

class Service(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    def log_message(self, *args): pass

    def answer(self):
        token = (self.headers.get("Authorization") or "")[7:]
        if (self.headers.get("Upgrade") or "").lower() == "websocket":
            seen.append({"method": self.command, "path": self.path, "token": token, "body": b"",
                         "session": None, "connection": self.headers.get("Connection")})
            self.send_response(101)
            self.send_header("Upgrade", "websocket")
            self.send_header("Connection", "Upgrade")
            self.end_headers(); self.wfile.flush()
            while True:
                data = self.rfile.read1(65536)
                if not data: break
                self.wfile.write(data); self.wfile.flush()
            self.close_connection = True
            return
        if (self.headers.get("Transfer-Encoding") or "").lower() == "chunked":
            # Answers each piece as it lands, the way a stream that waits on
            # what comes back would need.
            seen.append({"method": self.command, "path": self.path, "token": token, "body": b"",
                         "session": None, "chunked": True})
            self.send_response(200)
            self.send_header("Transfer-Encoding", "chunked")
            self.end_headers(); self.wfile.flush()
            while True:
                size = int(self.rfile.readline().strip() or b"0", 16)
                if size == 0:
                    self.rfile.readline(); break
                data = self.rfile.read(size); self.rfile.readline()
                reply = b"got " + data
                self.wfile.write(b"%x\r\n%s\r\n" % (len(reply), reply)); self.wfile.flush()
            self.wfile.write(b"0\r\n\r\n"); self.wfile.flush()
            return
        length = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(length) if length else b""
        seen.append({"method": self.command, "path": self.path, "token": token, "body": body,
                     "session": self.headers.get("X-Claude-Code-Session-Id")})
        state = accounts.get(token)
        if self.path.split("?")[0] != "/v1/messages" or self.command != "POST" or state is None:
            payload = json.dumps({"token": token, "path": self.path}).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers(); self.wfile.write(payload); return
        if state.get("refuse"):
            payload = b'{"type":"error","error":{"type":"authentication_error"}}'
            self.send_response(state["refuse"])
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers(); self.wfile.write(payload); return
        limits = {"anthropic-ratelimit-unified-5h-utilization": str(state["5h"]),
                  "anthropic-ratelimit-unified-7d-utilization": str(state["7d"]),
                  "anthropic-ratelimit-unified-5h-reset": str(int(now + 3600)),
                  "anthropic-ratelimit-unified-7d-reset": str(int(state["7d-reset"]))}
        model = re.search(rb'"model":"([^"]*)"', body)
        model = model.group(1) if model else b""
        if any(name in model for name in state.get("lacks", [])):
            payload = b'{"type":"error","error":{"type":"permission_error"}}'
            self.send_response(403)
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers(); self.wfile.write(payload); return
        if any(name in model for name in state.get("credits", [])):
            # A plan that sells the model apart: no limit named, and
            # compressed like any other error.
            payload = (b'{"type":"error","error":{"type":"rate_limit_error",'
                       b'"details":{"error_code":"credits_required"}}}')
            self.send_response(429)
            if "gzip" in (self.headers.get("Accept-Encoding") or ""):
                payload = gzip.compress(payload)
                self.send_header("Content-Encoding", "gzip")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers(); self.wfile.write(payload); return
        if state.get("busy"):
            # Too many requests at once: no limit named, only how long to wait.
            state["busy"] -= 1
            payload = b'{"type":"error","error":{"type":"rate_limit_error"}}'
            self.send_response(429)
            self.send_header("Retry-After", "1")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers(); self.wfile.write(payload); return
        spent = any(name in model for name in state.get("spent", []))
        if state["reject"] or spent:
            payload = b'{"type":"error","error":{"type":"rate_limit_error"}}'
            self.send_response(429)
            for k, v in limits.items(): self.send_header(k, v)
            self.send_header("anthropic-ratelimit-unified-status", "rejected")
            self.send_header("anthropic-ratelimit-unified-reset", str(int(now + (2 * 86400 if spent else 1800))))
            if spent:
                self.send_header("anthropic-ratelimit-unified-representative-claim", "seven_day_overage_included")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers(); self.wfile.write(payload); return
        seen[-1]["accept"] = self.headers.get("Accept-Encoding")
        continued = re.search(rb'"thread":\{"type":"continue","previous_message_id":"([^"]+)"', body)
        if continued and threads.get(continued.group(1).decode()) != token:
            payload = b'{"type":"error","error":{"type":"not_found_error","message":"thread_not_found"}}'
            self.send_response(404)
            # As the service does: even an error is compressed for a client
            # that accepts it.
            if "gzip" in (self.headers.get("Accept-Encoding") or ""):
                payload = gzip.compress(payload)
                self.send_header("Content-Encoding", "gzip")
            self.send_header("Content-Type", "application/json")
            self.send_header("Transfer-Encoding", "chunked")
            self.end_headers()
            self.wfile.write(b"%x\r\n%s\r\n0\r\n\r\n" % (len(payload), payload)); self.wfile.flush()
            return
        self.send_response(200)
        for k, v in limits.items(): self.send_header(k, v)
        self.send_header("anthropic-ratelimit-unified-status", "allowed")
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Transfer-Encoding", "chunked")
        self.end_headers()
        for event in STREAM:
            self.wfile.write(b"%x\r\n%s\r\n" % (len(event), event)); self.wfile.flush()
            time.sleep(0.02)
        self.wfile.write(b"0\r\n\r\n"); self.wfile.flush()

    do_GET = answer
    do_POST = answer

service = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Service)
# The relay drops a connection rather than drain an answer it will not use.
service.handle_error = lambda *args: None
threading.Thread(target=service.serve_forever, daemon=True).start()
upstream_port = service.server_address[1]

work = tempfile.mkdtemp(prefix="relay-")
support = os.path.join(work, "support")
os.makedirs(support)
port = free_port()
# Whatever started this script stands in for Claude, which makes the script
# Claude Code and anything it starts a program run from inside a session.
parent = subprocess.run(["ps", "-o", "comm=", "-p", str(os.getppid())], capture_output=True, text=True).stdout.strip()
args = [RELAY, "--support", support, "--port", str(port), "--socket", "r.sock",
        "--upstream-loopback-port", str(upstream_port), "--claude", parent]
relay = subprocess.Popen(args, cwd=work, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
socket_path = os.path.join(work, "r.sock")
for _ in range(100):
    if os.path.exists(socket_path): break
    time.sleep(0.05)
check(os.path.exists(socket_path), "the relay opens its control socket once it is listening")
pool = os.path.join(support, "ClaudeGraft", "pool")
ca = os.path.join(pool, "relay-ca.pem")

def control(command):
    s = socket.socket(socket.AF_UNIX); s.settimeout(5)
    os.chdir(work); s.connect("r.sock")
    s.sendall(json.dumps(command).encode() + b"\n")
    data = b""
    while not data.endswith(b"\n"):
        chunk = s.recv(65536)
        if not chunk: break
        data += chunk
    s.close()
    return json.loads(data)

def connection():
    context = ssl.create_default_context(cafile=ca)
    conn = http.client.HTTPSConnection("127.0.0.1", port, context=context, timeout=10)
    conn.set_tunnel("api.anthropic.com", 443)
    return conn

def ask(conn, token, body=b'{"model":"claude-test","messages":[]}', path="/v1/messages?beta=true",
        method="POST", session="s1", extra=None):
    before = len(seen)
    headers = {"Authorization": "Bearer " + token, "Content-Type": "application/json"}
    if session: headers["X-Claude-Code-Session-Id"] = session
    headers.update(extra or {})
    conn.request(method, path, body=body if method == "POST" else None, headers=headers)
    response = conn.getresponse()
    payload = response.read()
    return response, payload, seen[before:]

def member(id, token, lend=True, borrows=None, account=None):
    entry = {"id": id, "name": id, "identify": [digest(token)], "token": token if lend else None}
    if borrows is not None: entry["borrowsFrom"] = borrows
    if account is not None: entry["account"] = account
    return entry

def pushed(reset, observed=20):
    """Limits as Graft hands them over, newer than anything the relay has heard."""
    return {"fiveHour": 0.1, "week": 0.1, "observed": time.time() + observed - 978307200,
            "weekReset": reset - 978307200}

out = subprocess.run(["openssl", "x509", "-in", ca, "-noout", "-text"], capture_output=True, text=True).stdout
check("Permitted:" in out and "DNS:api.anthropic.com" in out,
      "the authority Claude Code is told to trust can vouch for the service's name alone")
check(subprocess.run(["openssl", "verify", "-CAfile", ca, os.path.join(pool, "relay-leaf.pem")],
                     capture_output=True).returncode == 0,
      "the certificate the relay presents is signed by that authority")
check(not any(name.endswith(".key") for name in os.listdir(pool)),
      "no private key is left on disk beside the authority")

conn = connection()
response, payload, went = ask(conn, "tok-A")
check(response.status == 200 and [w["token"] for w in went] == ["tok-A"],
      "an unconfigured relay passes a request through with the token it came with")
check(payload == b"".join(STREAM), "a streamed answer comes back byte for byte")

status = control({"configure": {"enabled": True, "members": [member("A", "tok-A", borrows=["B", "C"]), member("B", "tok-B")]}})
check(status["configured"] and status["enabled"] and len(status["members"]) == 2,
      "the relay reports the pool it was given")

response, payload, went = ask(conn, "tok-A")
check(response.status == 200 and [w["token"] for w in went] == ["tok-A"],
      "a request with no limits known yet stays with the account that sent it")
response, payload, went = ask(conn, "tok-A", session="s2")
check([w["token"] for w in went] == ["tok-A"],
      "an account's limits are learned from its own answers, not guessed")

# B has been heard from only as a lender, which tells the relay nothing until it
# answers; push its limits the way Graft does.
status = control({"configure": {"enabled": True, "members": [
    member("A", "tok-A", borrows=["B", "C"]),
    dict(member("B", "tok-B"), limits={"fiveHour": 0.2, "week": 0.4, "observed": time.time() - 978307200,
                                       "weekReset": accounts["tok-B"]["7d-reset"] - 978307200})]}})
response, payload, went = ask(conn, "tok-A", session="s3")
check([w["token"] for w in went] == ["tok-B"],
      "the account whose week resets soonest answers another member's request")
check(payload == b"".join(STREAM), "a borrowed answer streams back unchanged")
check(went[0]["session"] == "s3", "everything but the token goes out exactly as Claude Code sent it")

for path, method, body, what in [
    ("/v1/messages/count_tokens?beta=true", "POST", b'{"model":"x"}', "counting tokens"),
    ("/api/oauth/profile", "GET", None, "a profile lookup"),
    ("/api/frame/abc", "GET", None, "an artifact"),
]:
    response, payload, went = ask(conn, "tok-A", path=path, method=method, body=body)
    check([w["token"] for w in went] == ["tok-A"], "%s keeps the token it was sent with" % what)

for marker in [b'"file_id":"f1"', b'"container":"c1"', b'"container":{"id":"c1","skills":[]}',
               b'"mcp_servers":[{"type":"url","url":"u"}]']:
    response, payload, went = ask(conn, "tok-A", body=b'{"model":"x","messages":[],' + marker + b'}', session=None)
    check([w["token"] for w in went] == ["tok-A"],
          "a request naming %s stays with the account that owns it" % marker.split(b'"')[1].decode())
artifact = {"model": "x", "messages": [{"role": "assistant", "content": [
    {"type": "tool_use", "id": "t1", "name": "artifact", "input": {"container": "{\"kind\":\"project\",\"id\":\"p1\"}"}}]}]}
response, payload, went = ask(conn, "tok-A", body=json.dumps(artifact, separators=(",", ":")).encode(), session=None)
check([w["token"] for w in went] == ["tok-B"],
      "an artifact tool's container argument leaves inference available to the other account")
schema = (b'{"model":"x","messages":[],"tools":[{"name":"batch","input_schema":{"type":"object","properties":'
          b'{"container":{"type":"object"},"file_id":{"type":"string"},"mcp_servers":{"type":"array"}}}}]}')
response, payload, went = ask(conn, "tok-A", body=schema, session=None)
check([w["token"] for w in went] == ["tok-B"],
      "a tool whose schema has a property of the same name refers to nothing and pins nothing")
response, payload, went = ask(conn, "tok-A", body=b'{"model":"x","messages":[{"content":"file_id: none"}]}',
                              session=None)
check([w["token"] for w in went] == ["tok-B"], "the same word inside a prompt pins nothing")

response, payload, went = ask(conn, "tok-unknown")
check([w["token"] for w in went] == ["tok-unknown"], "a token from outside the pool is passed through untouched")

accounts["tok-B"]["reject"] = True
response, payload, went = ask(conn, "tok-A", session="s4")
check(response.status == 200 and [w["token"] for w in went] == ["tok-B", "tok-A"],
      "an account the service refuses for being full is passed over within the same request")
check(payload == b"".join(STREAM), "Claude Code sees only the answer that was delivered")
response, payload, went = ask(conn, "tok-A", session="s5")
check([w["token"] for w in went] == ["tok-A"], "a full account is not asked again until its window resets")
status = control({})
recent = status["recent"][-2]
check(recent["servedBy"] == "A" and recent["passedOver"] == [{"id": "B", "reason": "full"}],
      "the record says which account was passed over and why")
accounts["tok-B"]["reject"] = False

# A third account whose login the service refuses: once, then never again.
status = control({"configure": {"enabled": True, "members": [
    member("A", "tok-A", borrows=["B", "C"]),
    dict(member("C", "tok-C"), limits={"fiveHour": 0.1, "week": 0.1, "observed": time.time() - 978307200,
                                       "weekReset": now + 86400 - 978307200})]}})
response, payload, went = ask(conn, "tok-A", session="s6")
check(response.status == 200 and [w["token"] for w in went] == ["tok-C", "tok-A"],
      "a lent login the service refuses falls back without Claude Code seeing the refusal")
response, payload, went = ask(conn, "tok-A", session="s7")
check([w["token"] for w in went] == ["tok-A"], "a refused login is not lent again until it changes")
check(control({})["members"][1]["tokenRefused"], "the relay says which login was refused")

control({"configure": {"enabled": True, "members": [member("A", "tok-A", borrows=["B", "C"]),
    dict(member("B", "tok-B"), limits={"fiveHour": 0.2, "week": 0.4, "observed": time.time() - 978307200 + 5,
                                       "weekReset": accounts["tok-B"]["7d-reset"] - 978307200})]}})
response, payload, went = ask(conn, "tok-A", session="s8")
check([w["token"] for w in went] == ["tok-B"], "a member taken out of the pool is forgotten with it")
accounts["tok-A"]["7d-reset"] = now + 86400
response, payload, went = ask(conn, "tok-A", session="s9")
check([w["token"] for w in went] == ["tok-B"],
      "an account's limits stand as last heard until it answers again or Graft reads them")
control({"configure": {"enabled": True, "members": [
    dict(member("A", "tok-A", borrows=["B", "C"]), limits={"fiveHour": 0.1, "week": 0.3, "observed": time.time() - 978307200 + 10,
                                       "weekReset": now + 86400 - 978307200}),
    member("B", "tok-B")]}})
response, payload, went = ask(conn, "tok-A", session="s11")
check([w["token"] for w in went] == ["tok-A"], "a newer reading from Graft moves the order")
response, payload, went = ask(conn, "tok-A", session="s8")
check([w["token"] for w in went] == ["tok-B"],
      "a conversation stays on the account that answered it, whatever the ranking says since")

# Each Claude has a list of its own, and lending runs one way.
accounts["tok-D"] = {"5h": 0.1, "7d": 0.1, "7d-reset": now + 3600, "reject": False}
control({"configure": {"enabled": True, "members": [
    dict(member("A", "tok-A", borrows=[]), limits=pushed(now + 86400)),
    dict(member("B", "tok-B", borrows=["A"]), limits=pushed(now + 4 * 86400))]}})
response, payload, went = ask(conn, "tok-A", session="s12")
check([w["token"] for w in went] == ["tok-A"], "a Claude given no accounts to draw on keeps its own requests")
response, payload, went = ask(conn, "tok-B", session="s13")
check([w["token"] for w in went] == ["tok-A"], "while one given an account draws on it, even from a Claude that borrows nothing")
control({"configure": {"enabled": True, "members": [
    dict(member("B", "tok-B", borrows=["D"], account="acct-B"), limits=pushed(now + 4 * 86400, 30)),
    dict(member("D", "tok-D", account="acct-B"), limits=pushed(now + 3600, 30))]}})
response, payload, went = ask(conn, "tok-B", session="s14")
check([w["token"] for w in went] == ["tok-B"],
      "an account a Claude is already signed into is not borrowed from under another name")

control({"configure": {"enabled": True, "members": [
    dict(member("A", "tok-A", borrows=["B"]), limits=pushed(now + 86400, 40)),
    dict(member("B", "tok-B"), limits=pushed(now + 4 * 86400, 40))],
    "assignments": {"s15": "B"}}})
response, payload, went = ask(conn, "tok-A", session="s15")
check([w["token"] for w in went] == ["tok-B"], "a conversation moved by hand is answered by the account it was moved to")
response, payload, went = ask(conn, "tok-A", session="s16")
check([w["token"] for w in went] == ["tok-A"], "and every other one goes where the ranking says")
control({"configure": {"enabled": True, "members": [
    dict(member("A", "tok-A", borrows=["B"]), limits=pushed(now + 4 * 86400, 50)),
    dict(member("B", "tok-B"), limits=pushed(now + 86400, 50))],
    "assignments": {"s15": "B", "s17": "A"}}})
response, payload, went = ask(conn, "tok-A", session="s17")
check([w["token"] for w in went] == ["tok-A"], "a conversation moved back to its own account stays there")
response, payload, went = ask(conn, "tok-A", session="s16")
check([w["token"] for w in went] == ["tok-A"],
      "a conversation the ranking has turned away from stays with the account that answered it")
sessions = {entry["id"]: entry for entry in control({})["sessions"]}
check(sessions.get("s15", {}).get("servedBy") == "B" and sessions["s15"]["from"] == "A"
      and sessions.get("s17", {}).get("servedBy") == "A",
      "the relay lists each conversation with the account answering it")
# A relay that has started since holds no record of a conversation, and Graft
# hands over where each was answered as the relay last reported it.
control({"configure": {"enabled": True, "members": [
    dict(member("A", "tok-A", borrows=["B"]), limits=pushed(now + 4 * 86400, 50)),
    dict(member("B", "tok-B"), limits=pushed(now + 86400, 50))],
    "assignments": {"s15": "B", "s17": "A"}, "holders": {"s18": "A", "s16": "B"}}})
response, payload, went = ask(conn, "tok-A", session="s18")
check([w["token"] for w in went] == ["tok-A"],
      "a conversation the relay never saw goes to the account Graft says answered it last")
response, payload, went = ask(conn, "tok-A", session="s16")
check([w["token"] for w in went] == ["tok-A"],
      "and what the relay saw answered itself comes before what Graft remembers")
accounts["tok-B"]["reject"] = True
response, payload, went = ask(conn, "tok-A", session="s15")
check(response.status == 200 and [w["token"] for w in went] == ["tok-B", "tok-A"],
      "a conversation moved to an account with no room goes where there is some")
accounts["tok-B"]["reject"] = False

# Everything that cannot change accounts reaches the service as Claude Code
# wrote it, a socket Remote Control holds open included. B here would have its
# model requests answered by A, so a token arriving as A's would show it. (B is
# the one borrowing because it has just been found full.)
control({"configure": {"enabled": True, "members": [
    dict(member("A", "tok-A"), limits=pushed(now + 86400, 60)),
    dict(member("B", "tok-B", borrows=["A"]), limits=pushed(now + 4 * 86400, 60))]}})

def tunnel():
    raw = socket.create_connection(("127.0.0.1", port), timeout=10)
    raw.sendall(b"CONNECT api.anthropic.com:443 HTTP/1.1\r\nHost: api.anthropic.com:443\r\n\r\n")
    reply = b""
    while b"\r\n\r\n" not in reply:
        chunk = raw.recv(4096)
        if not chunk: break
        reply += chunk
    return ssl.create_default_context(cafile=ca).wrap_socket(raw, server_hostname="api.anthropic.com")

def read_until(s, marker):
    data = b""
    try:
        while marker not in data:
            chunk = s.recv(65536)
            if not chunk: break
            data += chunk
    except OSError:
        pass
    return data

before = len(seen)
s = tunnel()
s.settimeout(5)
s.sendall(b"GET /v1/sessions/ws/abc/subscribe HTTP/1.1\r\nHost: api.anthropic.com\r\n"
          b"Authorization: Bearer tok-B\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n"
          b"Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n")
check(read_until(s, b"\r\n\r\n").startswith(b"HTTP/1.1 101"),
      "a request to open a socket is answered by the service rather than refused on the way")
went = seen[before:]
check([w["token"] for w in went] == ["tok-B"] and "upgrade" in (went[0].get("connection") or "").lower(),
      "and reaches it still asking for the upgrade, under the token it was sent with")
s.sendall(b"\x81\x05hello")
check(b"\x81\x05hello" in read_until(s, b"hello"), "once open, the socket carries bytes both ways untouched")
s.close()

before = len(seen)
s = tunnel()
s.settimeout(5)
s.sendall(b"POST /v1/sessions/abc/events HTTP/1.1\r\nHost: api.anthropic.com\r\nAuthorization: Bearer tok-B\r\n"
          b"Content-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nfirst\r\n")
check(b"got first" in read_until(s, b"got first"),
      "a request still being written is answered as it goes, as it would be without the relay")
s.sendall(b"6\r\nsecond\r\n0\r\n\r\n")
check(b"got second" in read_until(s, b"0\r\n\r\n"), "and the rest of it follows")
went = seen[before:]
check([w["token"] for w in went] == ["tok-B"] and went[0].get("chunked"),
      "it arrives still chunked, under the token it was sent with")
s.close()

side = connection()
ask(side, "tok-B", path="/api/oauth/profile", method="GET")
response, payload, went = ask(side, "tok-B", session="s21")
check([w["token"] for w in went] == ["tok-A"],
      "a model request after one passed through on the same connection still goes where the ranking says")
side.close()

# Reasoning carried back in a conversation is read only by the account that
# wrote it, for the models bound that way, so such a conversation moves only
# when its account has no room. Two accounts nothing above has refused: E
# borrows D, and D ranks first.
accounts["tok-E"] = {"5h": 0.1, "7d": 0.1, "7d-reset": now + 5 * 86400, "reject": False}
control({"configure": {"enabled": True, "members": [
    dict(member("D", "tok-D"), limits=pushed(now + 3600, 90)),
    dict(member("E", "tok-E", borrows=["D"]), limits=pushed(now + 5 * 86400, 90))],
    "assignments": {"s32": "D"}}})
plain = b'{"model":"claude-sonnet-5-5","messages":[{"role":"user","content":"hi"}]}'
reasoned = (b'{"model":"claude-sonnet-5-5","messages":[{"role":"user","content":"hi"},'
            b'{"role":"assistant","content":[{"type":"thinking","thinking":"t","signature":"s"},'
            b'{"type":"text","text":"x"}]},{"role":"user","content":"and?"}]}')
response, payload, went = ask(conn, "tok-E", session="s30", body=plain)
check([w["token"] for w in went] == ["tok-D"], "a conversation with no reasoning yet goes where the ranking says")
response, payload, went = ask(conn, "tok-E", session="s31", body=reasoned)
check([w["token"] for w in went] == ["tok-E"],
      "one carrying reasoning the relay never saw written stays on the account that sent it")
response, payload, went = ask(conn, "tok-E", session="s31", body=reasoned)
check([w["token"] for w in went] == ["tok-E"], "and goes on staying there, ranking or not")
response, payload, went = ask(conn, "tok-E", session="s30", body=reasoned)
check([w["token"] for w in went] == ["tok-D"], "while one whose reasoning D wrote stays with D")
response, payload, went = ask(conn, "tok-E", session="s32", body=reasoned)
check([w["token"] for w in went] == ["tok-D"],
      "a person moving a conversation from the dropdown is a move made knowing what it costs")
opus = reasoned.replace(b"claude-sonnet-5-5", b"claude-opus-5-5")
response, payload, went = ask(conn, "tok-E", session="s33", body=opus)
check([w["token"] for w in went] == ["tok-D"],
      "a model whose reasoning survives the move, seen for the first time, goes where the ranking says")
accounts["tok-D"]["7d-reset"] = now + 6 * 86400
control({"configure": {"enabled": True, "members": [
    dict(member("D", "tok-D"), limits=pushed(now + 6 * 86400, 91)),
    dict(member("E", "tok-E", borrows=["D"]), limits=pushed(now + 5 * 86400, 91))],
    "assignments": {"s32": "D"}}})
response, payload, went = ask(conn, "tok-E", session="s33", body=opus)
check([w["token"] for w in went] == ["tok-D"],
      "and stays with the account that answered it like any other, once the ranking turns")
response, payload, went = ask(conn, "tok-E", session="s34", body=opus)
check([w["token"] for w in went] == ["tok-E"], "while a conversation beginning then goes by the new ranking")
accounts["tok-D"]["reject"] = True
response, payload, went = ask(conn, "tok-E", session="s30", body=reasoned)
check(response.status == 200 and [w["token"] for w in went] == ["tok-D", "tok-E"],
      "a held conversation moves once its account has no room, rather than fail")
accounts["tok-D"]["reject"] = False
response, payload, went = ask(conn, "tok-E", session="s30", body=reasoned)
check([w["token"] for w in went] == ["tok-E"], "and stays where it went, so its reasoning is lost once")

# A conversation kept as a thread on the service sends only its newest turns,
# and only the account holding the thread can answer them. H borrows G, and G
# ranks first.
accounts["tok-G"] = {"5h": 0.1, "7d": 0.1, "7d-reset": now + 3600, "reject": False}
accounts["tok-H"] = {"5h": 0.1, "7d": 0.1, "7d-reset": now + 5 * 86400, "reject": False}
accounts["tok-F"] = {"5h": 0.1, "7d": 0.1, "7d-reset": now + 1800, "reject": False, "refuse": 404}
def thread_pool(first, second, observed, assignments=None):
    """G and H, with the week of `first` resetting sooner."""
    resets = {first: now + 3600, second: now + 5 * 86400}
    control({"configure": {"enabled": True, "members": [
        dict(member("G", "tok-G"), limits=pushed(resets["G"], observed)),
        dict(member("H", "tok-H", borrows=["G"]), limits=pushed(resets["H"], observed))],
        "assignments": assignments or {}}})
def threaded(kind, previous=None):
    thread = b'{"type":"create"}' if kind == "create" else \
        b'{"type":"continue","previous_message_id":"' + previous.encode() + b'"}'
    return b'{"model":"claude-opus-5-5","messages":[{"role":"user","content":"hi"}],"thread":' + thread + b'}'
def ask_thread(token, session, body):
    response, payload, went = ask(conn, token, session=session, body=body,
                                  extra={"Accept-Encoding": "gzip, deflate, br"})
    # Claude Code unpacks whatever the account it reached sent; so does this.
    if response.getheader("Content-Encoding") == "gzip": payload = gzip.decompress(payload)
    return response, payload, went
def holder(session):
    return {entry["id"]: entry for entry in control({})["sessions"]}.get(session, {}).get("servedBy")
title = b'{"model":"claude-haiku-5-5","messages":[{"role":"user","content":"name this"}]}'

thread_pool("G", "H", 95)
response, payload, went = ask_thread("tok-H", "s40", threaded("create"))
check([w["token"] for w in went] == ["tok-G"], "a new thread goes where the ranking says")
threads["m1"] = "tok-G"
thread_pool("H", "G", 96)
response, payload, went = ask_thread("tok-H", "s40", threaded("continue", "m1"))
check(response.status == 200 and [w["token"] for w in went] == ["tok-G"],
      "a thread is continued on the account holding it, past a ranking that has turned")
response, payload, went = ask_thread("tok-H", "s41", threaded("continue", "m9"))
check([w["token"] for w in went] == ["tok-H"],
      "one the relay never saw answered stays on the account that sent it")

thread_pool("G", "H", 97, {"s40": "H"})
response, payload, went = ask_thread("tok-H", "s40", threaded("continue", "m1"))
check(response.status == 404 and b"thread_not_found" in payload and [w["token"] for w in went] == ["tok-H"],
      "a thread moved home from the dropdown is not sent back to the account it left")
response, payload, went = ask_thread("tok-H", "s40", threaded("create"))
check([w["token"] for w in went] == ["tok-H"], "the thread Claude Code starts again goes where it was moved")
threads["m4"] = "tok-H"

thread_pool("G", "H", 98, {"s40": "G"})
response, payload, went = ask(conn, "tok-H", session="s40", body=title)
check([w["token"] for w in went] == ["tok-G"], "a title asked for beside a thread is routed like any short request")
check(holder("s40") == "H", "and answered elsewhere, does not say where the thread is")
response, payload, went = ask_thread("tok-H", "s40", threaded("continue", "m4"))
check(response.status == 404 and b"thread_not_found" in payload and [w["token"] for w in went] == ["tok-G"],
      "a borrowed account without the thread says so, and Claude Code hears it rather than the request going home")
check(went and went[0].get("accept") == "identity",
      "a thread moved to a borrowed account asks for an answer it can read, compressed errors and all")
response, payload, went = ask_thread("tok-H", "s40", threaded("create"))
check(response.status == 200 and [w["token"] for w in went] == ["tok-G"],
      "the thread started again goes to the account chosen")
threads["m2"] = "tok-G"
response, payload, went = ask_thread("tok-H", "s40", threaded("continue", "m2"))
check([w["token"] for w in went] == ["tok-G"], "and is continued there")
check(holder("s40") == "G", "the dropdown names the account holding the thread")

sessions = {entry["id"]: entry for entry in control({})["sessions"]}
check(sessions.get("s40", {}).get("startedAt") is not None,
      "a conversation first seen with its first message is marked as new")
response, payload, went = ask_thread("tok-H", "s43", b'{"model":"claude-opus-5-5","messages":[{"role":"user","content":"a"},'
                                     b'{"role":"assistant","content":"b"},{"role":"user","content":"c"}]}')
sessions = {entry["id"]: entry for entry in control({})["sessions"]}
check("s43" in sessions and sessions["s43"].get("startedAt") is None,
      "one first seen part way through, as after the relay restarts, is not")

# The menu bar follows the conversation typed into last, so only a turn a
# person typed on a conversation's thread marks it; a tool's answer carried on,
# or a title asked for beside it, does not.
typed_at = sessions.get("s40", {}).get("promptedAt")
check(typed_at is not None and sessions["s43"].get("promptedAt") is None,
      "a turn typed on a thread marks when the conversation was typed into, and a request without one does not")
tool_answer = (b'{"model":"claude-opus-5-5","messages":[{"role":"user","content":[{"type":"tool_result",'
               b'"tool_use_id":"u1","content":"17 x 23"}]}],"thread":{"type":"continue","previous_message_id":"m2"}}')
response, payload, went = ask_thread("tok-H", "s40", tool_answer)
response, payload, went = ask_thread("tok-H", "s40", title)
held = {entry["id"]: entry for entry in control({})["sessions"]}.get("s40", {})
check(response.status == 200 and held.get("promptedAt") == typed_at and held.get("lastAt", 0) > typed_at,
      "Claude Code carrying on with a tool's answer, or asking for a title, leaves it as it was")
response, payload, went = ask_thread("tok-H", "s40", threaded("continue", "m2"))
held = {entry["id"]: entry for entry in control({})["sessions"]}.get("s40", {})
check(held.get("promptedAt", 0) > typed_at, "and the next turn typed marks it again")

control({"configure": {"enabled": True, "members": [
    dict(member("F", "tok-F"), limits=pushed(now + 1800, 100)),
    dict(member("H", "tok-H", borrows=["F"]), limits=pushed(now + 5 * 86400, 100))],
    "assignments": {"s42": "F"}}})
threads["m3"] = "tok-H"
response, payload, went = ask_thread("tok-H", "s42", threaded("continue", "m3"))
check(response.status == 200 and [w["token"] for w in went] == ["tok-F", "tok-H"],
      "any other refusal of a continued thread still sends it home")

# Whatever the lent accounts say, Claude Code hears its own account's answer.
# Each sender here ranks ahead of what it borrows, so the last account in the
# ranking is a lender, and finds itself full on what the Chat tab spent unseen.
for sender, lender, refusal in [("J1", "K", 403), ("J2", "L", 401)]:
    accounts["tok-" + sender] = {"5h": 0.1, "7d": 0.1, "7d-reset": now + 3600, "reject": True}
    accounts["tok-" + lender] = {"5h": 0.1, "7d": 0.1, "7d-reset": now + 3 * 86400, "reject": False,
                                 "refuse": refusal}
    control({"configure": {"enabled": True, "members": [
        dict(member(sender, "tok-" + sender, borrows=[lender]), limits=pushed(now + 3600, 110)),
        dict(member(lender, "tok-" + lender), limits=pushed(now + 3 * 86400, 110))]}})
    response, payload, went = ask(conn, "tok-" + sender, session="s6" + sender)
    check(response.status == 429
          and [w["token"] for w in went] == ["tok-" + sender, "tok-" + lender, "tok-" + sender],
          "a sender asked first and found full, whose lender answers %d, is asked again and its own refusal delivered"
          % refusal)

# Three accounts ranked lender, sender, lender: what the first cannot serve
# goes to the sender, not on to the other lender.
accounts["tok-P"] = {"5h": 0.1, "7d": 0.1, "7d-reset": now + 1800, "reject": False, "refuse": 403}
accounts["tok-Q"] = {"5h": 0.1, "7d": 0.1, "7d-reset": now + 86400, "reject": False}
accounts["tok-R"] = {"5h": 0.1, "7d": 0.1, "7d-reset": now + 5 * 86400, "reject": False}
control({"configure": {"enabled": True, "members": [
    dict(member("P", "tok-P"), limits=pushed(now + 1800, 110)),
    dict(member("Q", "tok-Q", borrows=["P", "R"]), limits=pushed(now + 86400, 110)),
    dict(member("R", "tok-R"), limits=pushed(now + 5 * 86400, 110))]}})
response, payload, went = ask(conn, "tok-Q", session="s62")
check(response.status == 200 and [w["token"] for w in went] == ["tok-P", "tok-Q"],
      "a request one lender cannot serve goes home, even with another lender ranked after the sender")

# A held conversation follows its account across names: Claude X's login
# lapses, and the same account signed in here as Y lends in its place.
accounts["tok-S"] = {"5h": 0.1, "7d": 0.1, "7d-reset": now + 1800, "reject": False}
accounts["tok-X"] = {"5h": 0.1, "7d": 0.1, "7d-reset": now + 900, "reject": False}
accounts["tok-Y"] = {"5h": 0.1, "7d": 0.1, "7d-reset": now + 5 * 86400, "reject": False}
accounts["tok-W"] = {"5h": 0.1, "7d": 0.1, "7d-reset": now + 600, "reject": False}
control({"configure": {"enabled": True, "members": [
    dict(member("S", "tok-S", borrows=["X", "Y"], account="acct-S"), limits=pushed(now + 86400, 115)),
    dict(member("X", "tok-X", account="acct-X"), limits=pushed(now + 900, 115)),
    dict(member("Y", "tok-Y", account="acct-X"), limits=pushed(now + 900, 115))]}})
response, payload, went = ask(conn, "tok-S", session="s63", body=plain)
check([w["token"] for w in went] == ["tok-X"], "a conversation starts on the account ranked first")
response, payload, went = ask(conn, "tok-S", session="s64", body=plain)
control({"configure": {"enabled": True, "members": [
    dict(member("S", "tok-S", borrows=["X", "Y"], account="acct-S"), limits=pushed(now + 1800, 120)),
    dict(member("X", "tok-X", lend=False, account="acct-X"), limits=pushed(now + 5 * 86400, 120)),
    dict(member("Y", "tok-Y", account="acct-X"), limits=pushed(now + 5 * 86400, 120))]}})
response, payload, went = ask(conn, "tok-S", session="s63", body=reasoned)
check([w["token"] for w in went] == ["tok-Y"],
      "its reasoning stays with the account that wrote it, under the name that account now lends by")
control({"configure": {"enabled": True, "members": [
    dict(member("S", "tok-S", borrows=["W"], account="acct-S"), limits=pushed(now + 1800, 125)),
    dict(member("W", "tok-W", account="acct-W"), limits=pushed(now + 600, 125))]}})
response, payload, went = ask(conn, "tok-S", session="s64", body=reasoned)
check([w["token"] for w in went] == ["tok-S"],
      "and once that account lends no more, goes home rather than wherever the ranking says")

# Claude 2 borrowing Claude and the same account signed in here: the relay
# counts the first, and a conversation moved to the second goes to the first.
accounts["tok-E"] = {"5h": 0.1, "7d": 0.1, "7d-reset": now + 3600, "reject": False}
control({"configure": {"enabled": True, "members": [
    dict(member("A", "tok-A", borrows=["B", "E"], account="acct-A"), limits=pushed(now + 86400, 60)),
    dict(member("B", "tok-B", account="acct-B"), limits=pushed(now + 4 * 86400, 60)),
    dict(member("E", "tok-E", account="acct-B"), limits=pushed(now + 4 * 86400, 60))],
    "assignments": {"s19": "E"}}})
response, payload, went = ask(conn, "tok-A", session="s19")
check([w["token"] for w in went] == ["tok-B"],
      "a conversation moved to an account listed twice goes to it under the name the relay counted")

# The same pair with Claude's own login lapsed, which is what happens to a
# Claude left closed: the account still lends, under the name signed in here.
control({"configure": {"enabled": True, "members": [
    dict(member("A", "tok-A", borrows=["B", "E"], account="acct-A"), limits=pushed(now + 4 * 86400, 60)),
    dict(member("B", "tok-B", lend=False, account="acct-B"), limits=pushed(now + 86400, 60)),
    dict(member("E", "tok-E", account="acct-B"), limits=pushed(now + 86400, 60))]}})
response, payload, went = ask(conn, "tok-A", session="s20")
check([w["token"] for w in went] == ["tok-E"],
      "an account listed twice lends under its second name when the first has no login to lend")

# Some limits count one family of models alone, and some plans lack a model.
# T borrows U and V: U's week resets soonest, then V's, then T's own. U has
# spent its week of one model and V's plan is without it.
accounts["tok-T"] = {"5h": 0.1, "7d": 0.1, "7d-reset": now + 5 * 86400, "reject": False}
accounts["tok-U"] = {"5h": 0.1, "7d": 0.1, "7d-reset": now + 1800, "reject": False, "spent": [b"fable"]}
accounts["tok-V"] = {"5h": 0.1, "7d": 0.1, "7d-reset": now + 86400, "reject": False, "lacks": [b"fable"]}
def model_pool(observed, u=None, **more):
    control({"configure": dict({"enabled": True, "members": [
        dict(member("T", "tok-T", borrows=["U", "V"]), limits=pushed(now + 5 * 86400, observed)),
        dict(member("U", "tok-U"), limits=dict(pushed(now + 1800, observed), **(u or {}))),
        dict(member("V", "tok-V"), limits=pushed(now + 86400, observed))]}, **more)})
def first_turn(model):
    return b'{"model":"' + model + b'","messages":[{"role":"user","content":"hi"}]}'
model_pool(130)
response, payload, went = ask(conn, "tok-T", session="s70", body=first_turn(b"claude-fable-5-2"))
check(response.status == 200 and [w["token"] for w in went] == ["tok-U", "tok-V", "tok-T"],
      "an account out of one model is passed over for it, and one whose plan lacks it sends the request home")
response, payload, went = ask(conn, "tok-T", session="s71", body=first_turn(b"claude-fable-5-2[1m]"))
check([w["token"] for w in went] == ["tok-T"],
      "neither is asked for that model again, whatever its version, while what it said stands")
response, payload, went = ask(conn, "tok-T", session="s72", body=first_turn(b"claude-opus-5-5"))
check([w["token"] for w in went] == ["tok-U"],
      "and every other model still starts on the account that is out of one")
accounts["tok-U"]["busy"] = 1
response, payload, went = ask(conn, "tok-T", session="s72", body=first_turn(b"claude-opus-5-5"))
check(response.status == 200 and [w["token"] for w in went] == ["tok-U", "tok-V"],
      "an account too busy for a moment is passed over for that one request")
time.sleep(1.2)
response, payload, went = ask(conn, "tok-T", session="s72", body=first_turn(b"claude-opus-5-5"))
check([w["token"] for w in went] == ["tok-U"],
      "and its conversation comes back to it, since being busy says nothing of its room")
# Graft reads the weeks one model has from the usage endpoint and says so.
model_pool(135, u={"models": {"sonnet": {"used": 1.0, "reset": now + 2 * 86400 - 978307200}}})
response, payload, went = ask(conn, "tok-T", session="s73", body=first_turn(b"claude-sonnet-5-5"))
check([w["token"] for w in went] == ["tok-V"],
      "an account Graft read as out of one model is not asked for it at all")
response, payload, went = ask(conn, "tok-T", session="s74", body=first_turn(b"claude-opus-5-5"))
check([w["token"] for w in went] == ["tok-U"], "and is still asked first for the rest")

# The order somebody asked for comes before the resets.
model_pool(140, first=["T"])
response, payload, went = ask(conn, "tok-T", session="s75", body=first_turn(b"claude-opus-5-5"))
check([w["token"] for w in went] == ["tok-T"],
      "an account somebody put first is used before one whose week resets sooner")
model_pool(141, first=["V", "T"])
response, payload, went = ask(conn, "tok-T", session="s76", body=first_turn(b"claude-opus-5-5"))
check([w["token"] for w in went] == ["tok-V"], "the order given is kept among the accounts it names")
response, payload, went = ask(conn, "tok-T", session="s75", body=first_turn(b"claude-opus-5-5"))
check([w["token"] for w in went] == ["tok-T"],
      "and a conversation under way stays where it is when the order changes")
model_pool(142, first=["U", "V", "T"])
response, payload, went = ask(conn, "tok-T", session="s77", body=first_turn(b"claude-fable-5-2"))
check([w["token"] for w in went] == ["tok-T"],
      "an account put first and out of one model is skipped for that model")
response, payload, went = ask(conn, "tok-T", session="s78", body=first_turn(b"claude-haiku-5-5"))
check([w["token"] for w in went] == ["tok-U"], "and comes first for every other")

# A plan can sell one model apart, and says so in a 429 that names no limit.
accounts["tok-W"] = {"5h": 0.1, "7d": 0.1, "7d-reset": now + 5 * 86400, "reject": False}
accounts["tok-X"] = {"5h": 0.1, "7d": 0.1, "7d-reset": now + 1800, "reject": False, "credits": [b"fable"]}
control({"configure": {"enabled": True, "members": [
    dict(member("W", "tok-W", borrows=["X"]), limits=pushed(now + 5 * 86400, 143)),
    dict(member("X", "tok-X"), limits=pushed(now + 1800, 143))]}})
packed = {"Accept-Encoding": "gzip, deflate, br"}
response, payload, went = ask(conn, "tok-W", session="s80", body=first_turn(b"claude-fable-5-2"), extra=packed)
check(response.status == 200 and [w["token"] for w in went] == ["tok-X", "tok-W"],
      "an account whose plan sells the model apart sends the request home")
response, payload, went = ask(conn, "tok-W", session="s81", body=first_turn(b"claude-opus-5-5"), extra=packed)
check([w["token"] for w in went] == ["tok-X"],
      "and is asked at once for every other model, where a busy one would have been left a minute")
response, payload, went = ask(conn, "tok-W", session="s82", body=first_turn(b"claude-fable-5-2"), extra=packed)
check([w["token"] for w in went] == ["tok-W"], "and is not asked for that model again while what it said stands")

# A sender with no room is sure of nothing, so what one lender cannot serve
# goes on to the next lender before it goes home.
control({"configure": {"enabled": True, "members": [
    dict(member("P", "tok-P"), limits=pushed(now + 1800, 150)),
    dict(member("Q", "tok-Q", borrows=["P", "R"]),
         limits=dict(pushed(now + 86400, 150), fiveHour=0.99, fiveHourReset=now + 3600 - 978307200)),
    dict(member("R", "tok-R"), limits=pushed(now + 5 * 86400, 150))]}})
response, payload, went = ask(conn, "tok-Q", session="s80", body=first_turn(b"claude-opus-5-5"))
check(response.status == 200 and [w["token"] for w in went] == ["tok-P", "tok-R"],
      "a request one lender cannot serve goes on to the next when its sender has no room")

# Twenty accounts in one pool, each drawing on all the others.
crowd = ["tok-N%02d" % n for n in range(20)]
for n, token in enumerate(crowd):
    accounts[token] = {"5h": 0.1, "7d": 0.1, "7d-reset": now + (20 - n) * 3600, "reject": False}
def crowd_pool(observed, **more):
    control({"configure": dict({"enabled": True, "members": [
        dict(member("N%02d" % n, crowd[n], borrows=["N%02d" % m for m in range(20) if m != n]),
             limits=pushed(now + (20 - n) * 3600, observed)) for n in range(20)]}, **more)})
crowd_pool(160)
response, payload, went = ask(conn, crowd[0], session="s90", body=first_turn(b"claude-opus-5-5"))
check(response.status == 200 and [w["token"] for w in went] == [crowd[19]],
      "of twenty accounts, a new conversation starts on the one whose week resets soonest")
for token in crowd[2:]: accounts[token]["reject"] = True
response, payload, went = ask(conn, crowd[0], session="s91", body=first_turn(b"claude-opus-5-5"))
check(response.status == 200 and [w["token"] for w in went] == crowd[:0:-1],
      "with eighteen of them full it asks each once, soonest reset first, down to the one with room")
response, payload, went = ask(conn, crowd[0], session="s92", body=first_turn(b"claude-opus-5-5"))
check([w["token"] for w in went] == [crowd[1]], "and the next conversation goes straight to that one")
response, payload, went = ask(conn, crowd[7], session="s93", body=first_turn(b"claude-opus-5-5"))
check([w["token"] for w in went] == [crowd[1]], "from whichever of the twenty it is sent")
crowd_pool(161, first=["N00", "N01"])
response, payload, went = ask(conn, crowd[7], session="s94", body=first_turn(b"claude-opus-5-5"))
check([w["token"] for w in went] == [crowd[0]], "and an order set over twenty is kept")
check(len(control({})["members"]) == 20, "the relay reports on all twenty")

control({"configure": {"enabled": False, "members": [member("A", "tok-A", borrows=["B", "C"]), member("B", "tok-B")]}})
response, payload, went = ask(conn, "tok-A", session="s10")
check([w["token"] for w in went] == ["tok-A"], "a pool switched off sends everything as Claude sent it")
conn.close()

# A tunnel for anything else is not opened, only passed along.
raw = socket.create_connection(("127.0.0.1", port))
raw.sendall(b"CONNECT 127.0.0.1:%d HTTP/1.1\r\nHost: x\r\n\r\n" % upstream_port)
reply = raw.recv(4096)
check(reply.startswith(b"HTTP/1.1 200"), "a tunnel to any other host is opened")
raw.sendall(b"GET /plain HTTP/1.1\r\nHost: x\r\nAuthorization: Bearer tok-A\r\nConnection: close\r\n\r\n")
data = b""
while True:
    chunk = raw.recv(65536)
    if not chunk: break
    data += chunk
check(b'"path": "/plain"' in data, "and what goes through it arrives untouched")
raw.close()

# A program started from inside a session has the proxy and not the authority.
def from_inside(program):
    child = subprocess.Popen(program + ["127.0.0.1", str(port)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                             stderr=subprocess.DEVNULL)
    child.stdin.write(b"CONNECT api.anthropic.com:443 HTTP/1.1\r\nHost: api.anthropic.com:443\r\n\r\n"); child.stdin.flush()
    reply = os.read(child.stdout.fileno(), 4096)
    child.stdin.write(b"GET /plain HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n"); child.stdin.flush()
    data = b""
    def drain():
        nonlocal data
        while True:
            chunk = os.read(child.stdout.fileno(), 65536)
            if not chunk: break
            data += chunk
    reader = threading.Thread(target=drain, daemon=True); reader.start(); reader.join(3)
    child.kill(); child.wait()
    return reply, data

reply, data = from_inside(["/usr/bin/nc"])
check(reply.startswith(b"HTTP/1.1 200") and b'"path": "/plain"' in data,
      "a program Claude Code started reaches the service through a tunnel left unopened")
relayed = "import socket,sys,os\ns=socket.create_connection((sys.argv[1],int(sys.argv[2])))\n" \
          "import threading\nthreading.Thread(target=lambda:[s.sendall(c) for c in iter(lambda:os.read(0,65536),b'')],daemon=True).start()\n" \
          "while True:\n c=s.recv(65536)\n if not c: break\n os.write(1,c)\n"
reply, data = from_inside([sys.executable, "-c", relayed])
check(reply.startswith(b"HTTP/1.1 200") and b'"path": "/plain"' not in data,
      "and Claude Code started by Claude Code still has its tunnel opened")

# Without the leaf a starting relay would make a new authority and leaf; one
# that finds another relay running must leave before it gets that far.
leaf = os.path.join(pool, "relay-leaf.pem")
authority, presented = open(ca, "rb").read(), open(leaf, "rb").read()
os.remove(leaf)
second = subprocess.run(args, cwd=work, stdin=subprocess.DEVNULL, capture_output=True, timeout=10)
check(second.returncode == 0 and relay.poll() is None, "a second relay leaves at once and the first keeps answering")
check(open(ca, "rb").read() == authority and not os.path.exists(leaf),
      "and without renewing the certificate the first one presents")
open(leaf, "wb").write(presented)

control({"quit": True})
check(not os.path.exists(socket_path), "a relay stepping aside takes its socket with it")
try:
    relay.wait(timeout=10)
except subprocess.TimeoutExpired:
    relay.kill()
check(relay.returncode == 0, "and leaves once nothing is in flight")
replacement = subprocess.Popen(args, cwd=work, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
for _ in range(100):
    if os.path.exists(socket_path): break
    time.sleep(0.05)
check(os.path.exists(socket_path), "the relay replacing it can start straight away")
conn = connection()
response, payload, went = ask(conn, "tok-A")
check(response.status == 200 and [w["token"] for w in went] == ["tok-A"],
      "and starts out passing everything through until it is told otherwise")
conn.close()
control({"quit": True})
try:
    replacement.wait(timeout=10)
except subprocess.TimeoutExpired:
    replacement.kill()

# A relay whose Graft has been removed, and the folder Graft kept with it, as
# `brew uninstall --zap` leaves things: one borrowing Claude closed and one
# still open, both holding the lines.
gone = tempfile.mkdtemp(prefix="relay-gone-")
copy = os.path.join(gone, "graft-relay")
with open(RELAY, "rb") as source, open(copy, "wb") as target: target.write(source.read())
os.chmod(copy, 0o755)
gone_support = os.path.join(gone, "support")
gone_pool = os.path.join(gone_support, "ClaudeGraft", "pool")
os.makedirs(gone_pool)
def claude(name, field):
    path = os.path.join(gone, name)
    os.makedirs(path)
    with open(os.path.join(path, "ccd-environment-config.json"), "w") as f: json.dump({"envVars": field, "kept": 1}, f)
    return path
def environment(path):
    with open(os.path.join(path, "ccd-environment-config.json")) as f: return json.load(f)
closed_claude, open_claude = claude("Closed", "pooled-1"), claude("Open", "pooled-2")
with open(os.path.join(gone_pool, "environment.json"), "w") as f:
    json.dump({"profiles": {closed_claude: {"wanted": True, "plain": "plain-1", "pooled": "pooled-1"},
                            open_claude: {"wanted": True, "pooled": "pooled-2"}}}, f)
still_open = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)", "--user-data-dir=" + open_claude])
gone_args = [copy, "--support", gone_support, "--port", str(free_port()), "--socket", "g.sock",
             "--upstream-loopback-port", str(upstream_port), "--removal-check", "0.4"]
left = subprocess.Popen(gone_args, cwd=gone, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                        stderr=subprocess.DEVNULL)
time.sleep(1.5)
check(environment(closed_claude)["envVars"] == "pooled-1" and left.poll() is None,
      "a relay whose file is still there leaves every Claude as it is")
os.remove(copy)
shutil.rmtree(os.path.join(gone_support, "ClaudeGraft"))
time.sleep(2)
check(environment(closed_claude) == {"envVars": "plain-1", "kept": 1},
      "once its file has gone, a closed Claude gets back what it held before sharing, from the journal it read last")
check(environment(open_claude)["envVars"] == "pooled-2" and left.poll() is None,
      "while one still open keeps the lines and the relay keeps answering it")
check(not os.path.exists(os.path.join(gone_support, "ClaudeGraft")),
      "and nothing of Graft's that was removed is made again")
still_open.kill(); still_open.wait()
try:
    left.wait(timeout=10)
except subprocess.TimeoutExpired:
    left.kill()
check(environment(open_claude) == {"kept": 1} and left.returncode == 0,
      "once that one closes too its lines come out, and the relay leaves")

service.shutdown()

print("%d relay checks, %d failed" % (checks, failures))
sys.exit(1 if failures else 0)
