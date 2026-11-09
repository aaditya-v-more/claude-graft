//! Drives a built graft-relay against a stand-in for the service on loopback:
//! which account each request reached, what came back, and how the relay
//! steps aside. Nothing here touches a real profile, port 47823 or the
//! network.

use graft_core::testing::tempfile_lite::TempDir;
use graft_pool::environment;
use serde_json::{json, Value};
use std::collections::HashMap;
use std::io::{BufRead, BufReader, Read, Write};
use std::net::{TcpListener, TcpStream};
use std::os::unix::net::UnixStream;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

const RELAY: &str = env!("CARGO_BIN_EXE_graft-relay");
const STREAM: [&[u8]; 3] = [
    b"event: message_start\ndata: {\"n\":1}\n\n",
    b"event: content_block_delta\ndata: {\"n\":2}\n\n",
    b"event: message_stop\ndata: {\"n\":3}\n\n",
];

fn now() -> f64 {
    SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_secs_f64()
}

fn streamed() -> Vec<u8> {
    STREAM.concat()
}

struct Checks {
    count: usize,
    failures: Vec<String>,
}

impl Checks {
    fn new() -> Checks {
        Checks { count: 0, failures: Vec::new() }
    }

    fn check(&mut self, ok: bool, what: &str) {
        self.count += 1;
        if !ok {
            eprintln!("  FAIL {what}");
            self.failures.push(what.to_string());
        }
    }

    fn finish(self) {
        eprintln!("{} relay checks, {} failed", self.count, self.failures.len());
        assert!(self.failures.is_empty(), "failed: {:#?}", self.failures);
    }
}

fn free_port() -> u16 {
    TcpListener::bind("127.0.0.1:0").unwrap().local_addr().unwrap().port()
}

// The stand-in for the service.

#[derive(Clone)]
struct AccountState {
    five: f64,
    week: f64,
    week_reset: f64,
    reject: bool,
    refuse: Option<u16>,
}

fn state(week_reset: f64) -> AccountState {
    AccountState { five: 0.1, week: 0.1, week_reset, reject: false, refuse: None }
}

#[derive(Clone, Debug, Default)]
struct Seen {
    token: String,
    session: Option<String>,
    connection: Option<String>,
    chunked: bool,
    accept: Option<String>,
}

struct World {
    accounts: HashMap<String, AccountState>,
    seen: Vec<Seen>,
    /// Which account holds each thread the stand-in has been told about. A
    /// thread is continued only from the account that holds it, as the
    /// service does.
    threads: HashMap<String, String>,
    start: f64,
}

type Service = Arc<Mutex<World>>;

fn stand_in(start: f64) -> (Service, u16) {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let port = listener.local_addr().unwrap().port();
    let world = Arc::new(Mutex::new(World { accounts: HashMap::new(), seen: Vec::new(), threads: HashMap::new(), start }));
    let shared = world.clone();
    std::thread::spawn(move || {
        for stream in listener.incoming().flatten() {
            let world = shared.clone();
            std::thread::spawn(move || {
                // The relay drops a connection rather than drain an answer it
                // will not use, so a failed write here is expected.
                let _ = answer(stream, world);
            });
        }
    });
    (world, port)
}

fn reply(writer: &mut TcpStream, status: u16, reason: &str, headers: &[(String, String)], body: &[u8]) -> std::io::Result<()> {
    let mut head = format!("HTTP/1.1 {status} {reason}\r\n");
    for (name, value) in headers {
        head.push_str(&format!("{name}: {value}\r\n"));
    }
    head.push_str(&format!("Content-Length: {}\r\n\r\n", body.len()));
    writer.write_all(head.as_bytes())?;
    writer.write_all(body)?;
    writer.flush()
}

fn h(name: &str, value: impl ToString) -> (String, String) {
    (name.to_string(), value.to_string())
}

fn answer(stream: TcpStream, world: Service) -> std::io::Result<()> {
    let mut reader = BufReader::new(stream.try_clone()?);
    let mut writer = stream;
    loop {
        let mut line = String::new();
        if reader.read_line(&mut line)? == 0 {
            return Ok(());
        }
        let parts: Vec<&str> = line.trim_end().splitn(3, ' ').collect();
        if parts.len() < 2 {
            return Ok(());
        }
        let (method, path) = (parts[0].to_string(), parts[1].to_string());
        let mut headers: Vec<(String, String)> = Vec::new();
        loop {
            let mut field = String::new();
            if reader.read_line(&mut field)? == 0 {
                return Ok(());
            }
            let field = field.trim_end();
            if field.is_empty() {
                break;
            }
            if let Some((n, v)) = field.split_once(':') {
                headers.push((n.trim().to_string(), v.trim().to_string()));
            }
        }
        let header = |name: &str| headers.iter().find(|(n, _)| n.eq_ignore_ascii_case(name)).map(|(_, v)| v.clone());
        let token = header("Authorization").map(|a| a.get(7..).unwrap_or("").to_string()).unwrap_or_default();

        if header("Upgrade").is_some_and(|u| u.eq_ignore_ascii_case("websocket")) {
            world.lock().unwrap().seen.push(Seen { token, connection: header("Connection"), ..Seen::default() });
            writer.write_all(b"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n")?;
            writer.flush()?;
            let mut chunk = [0u8; 65536];
            loop {
                let got = reader.read(&mut chunk)?;
                if got == 0 {
                    return Ok(());
                }
                writer.write_all(&chunk[..got])?;
                writer.flush()?;
            }
        }
        if header("Transfer-Encoding").is_some_and(|t| t.eq_ignore_ascii_case("chunked")) {
            // Answers each piece as it lands, the way a stream that waits on
            // what comes back would need.
            world.lock().unwrap().seen.push(Seen { token, chunked: true, ..Seen::default() });
            writer.write_all(b"HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n")?;
            writer.flush()?;
            loop {
                let mut size = String::new();
                reader.read_line(&mut size)?;
                let size = usize::from_str_radix(size.trim(), 16).unwrap_or(0);
                if size == 0 {
                    let mut trailer = String::new();
                    reader.read_line(&mut trailer)?;
                    break;
                }
                let mut data = vec![0u8; size];
                reader.read_exact(&mut data)?;
                let mut end = String::new();
                reader.read_line(&mut end)?;
                let mut got = b"got ".to_vec();
                got.extend_from_slice(&data);
                writer.write_all(format!("{:x}\r\n", got.len()).as_bytes())?;
                writer.write_all(&got)?;
                writer.write_all(b"\r\n")?;
                writer.flush()?;
            }
            writer.write_all(b"0\r\n\r\n")?;
            writer.flush()?;
            continue;
        }
        let length: usize = header("Content-Length").and_then(|l| l.parse().ok()).unwrap_or(0);
        let mut body = vec![0u8; length];
        reader.read_exact(&mut body)?;
        let mut world_now = world.lock().unwrap();
        world_now.seen.push(Seen { token: token.clone(), session: header("X-Claude-Code-Session-Id"), ..Seen::default() });
        let account = world_now.accounts.get(&token).cloned();
        let start = world_now.start;
        let account = match account {
            Some(account) if path.split('?').next() == Some("/v1/messages") && method == "POST" => account,
            _ => {
                drop(world_now);
                let payload = format!("{{\"token\": \"{token}\", \"path\": \"{path}\"}}");
                reply(&mut writer, 200, "OK", &[h("Content-Type", "application/json")], payload.as_bytes())?;
                continue;
            }
        };
        if let Some(refusal) = account.refuse {
            drop(world_now);
            reply(&mut writer, refusal, "Refused", &[], br#"{"type":"error","error":{"type":"authentication_error"}}"#)?;
            continue;
        }
        let limits = vec![
            h("anthropic-ratelimit-unified-5h-utilization", account.five),
            h("anthropic-ratelimit-unified-7d-utilization", account.week),
            h("anthropic-ratelimit-unified-5h-reset", (start + 3600.0) as i64),
            h("anthropic-ratelimit-unified-7d-reset", account.week_reset as i64),
        ];
        if account.reject {
            drop(world_now);
            let mut fields = limits.clone();
            fields.push(h("anthropic-ratelimit-unified-status", "rejected"));
            fields.push(h("anthropic-ratelimit-unified-reset", (start + 1800.0) as i64));
            reply(&mut writer, 429, "Too Many Requests", &fields, br#"{"type":"error","error":{"type":"rate_limit_error"}}"#)?;
            continue;
        }
        let accept = header("Accept-Encoding");
        if let Some(last) = world_now.seen.last_mut() {
            last.accept = accept.clone();
        }
        let marker = br#""thread":{"type":"continue","previous_message_id":""#;
        let continued = body.windows(marker.len()).position(|w| w == marker).map(|at| {
            let rest = &body[at + marker.len()..];
            let end = rest.iter().position(|b| *b == b'"').unwrap_or(rest.len());
            String::from_utf8_lossy(&rest[..end]).into_owned()
        });
        if let Some(previous) = continued {
            if world_now.threads.get(&previous) != Some(&token) {
                drop(world_now);
                let mut payload = br#"{"type":"error","error":{"type":"not_found_error","message":"thread_not_found"}}"#.to_vec();
                let mut head = "HTTP/1.1 404 Not Found\r\n".to_string();
                // As the service does: even an error is compressed for a
                // client that accepts it.
                if accept.as_deref().unwrap_or("").contains("gzip") {
                    let mut encoder = flate2::write::GzEncoder::new(Vec::new(), flate2::Compression::default());
                    encoder.write_all(&payload)?;
                    payload = encoder.finish()?;
                    head.push_str("Content-Encoding: gzip\r\n");
                }
                head.push_str("Content-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n");
                writer.write_all(head.as_bytes())?;
                writer.write_all(format!("{:x}\r\n", payload.len()).as_bytes())?;
                writer.write_all(&payload)?;
                writer.write_all(b"\r\n0\r\n\r\n")?;
                writer.flush()?;
                continue;
            }
        }
        drop(world_now);
        let mut head = "HTTP/1.1 200 OK\r\n".to_string();
        for (name, value) in &limits {
            head.push_str(&format!("{name}: {value}\r\n"));
        }
        head.push_str("anthropic-ratelimit-unified-status: allowed\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\n\r\n");
        writer.write_all(head.as_bytes())?;
        writer.flush()?;
        for event in STREAM {
            writer.write_all(format!("{:x}\r\n", event.len()).as_bytes())?;
            writer.write_all(event)?;
            writer.write_all(b"\r\n")?;
            writer.flush()?;
            std::thread::sleep(Duration::from_millis(20));
        }
        writer.write_all(b"0\r\n\r\n")?;
        writer.flush()?;
    }
}

// Claude Code's side of the relay.

fn tls_config(ca: &Path) -> Arc<rustls::ClientConfig> {
    let mut roots = rustls::RootCertStore::empty();
    for (_, der) in graft_relay::certificates::pem_blocks(&std::fs::read_to_string(ca).unwrap()) {
        roots.add(rustls::pki_types::CertificateDer::from(der)).unwrap();
    }
    let provider = Arc::new(rustls::crypto::ring::default_provider());
    let mut config = rustls::ClientConfig::builder_with_provider(provider)
        .with_safe_default_protocol_versions()
        .unwrap()
        .with_root_certificates(roots)
        .with_no_client_auth();
    config.alpn_protocols = vec![b"http/1.1".to_vec()];
    Arc::new(config)
}

type Tls = rustls::StreamOwned<rustls::ClientConnection, TcpStream>;

/// A tunnel through the relay to the service's name, taken apart by the
/// relay's own authority.
fn tunnel(port: u16, ca: &Path) -> Tls {
    let mut raw = TcpStream::connect(("127.0.0.1", port)).unwrap();
    raw.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
    raw.write_all(b"CONNECT api.anthropic.com:443 HTTP/1.1\r\nHost: api.anthropic.com:443\r\n\r\n").unwrap();
    let mut reply = Vec::new();
    let mut byte = [0u8; 1];
    while !reply.ends_with(b"\r\n\r\n") {
        if raw.read(&mut byte).unwrap() == 0 {
            break;
        }
        reply.push(byte[0]);
    }
    assert!(reply.starts_with(b"HTTP/1.1 200"), "the relay opens a tunnel for the service");
    let name = rustls::pki_types::ServerName::try_from("api.anthropic.com").unwrap();
    let connection = rustls::ClientConnection::new(tls_config(ca), name).unwrap();
    rustls::StreamOwned::new(connection, raw)
}

struct Response {
    status: u16,
    headers: Vec<(String, String)>,
    body: Vec<u8>,
}

impl Response {
    fn header(&self, name: &str) -> Option<&str> {
        self.headers.iter().find(|(n, _)| n.eq_ignore_ascii_case(name)).map(|(_, v)| v.as_str())
    }
}

struct Client {
    stream: Tls,
    buffer: Vec<u8>,
}

impl Client {
    fn connect(port: u16, ca: &Path) -> Client {
        Client { stream: tunnel(port, ca), buffer: Vec::new() }
    }

    fn fill(&mut self) -> bool {
        let mut chunk = [0u8; 65536];
        match self.stream.read(&mut chunk) {
            Ok(0) | Err(_) => false,
            Ok(got) => {
                self.buffer.extend_from_slice(&chunk[..got]);
                true
            }
        }
    }

    fn line(&mut self) -> Vec<u8> {
        loop {
            if let Some(at) = self.buffer.windows(2).position(|w| w == b"\r\n") {
                let line = self.buffer[..at].to_vec();
                self.buffer.drain(..at + 2);
                return line;
            }
            if !self.fill() {
                return std::mem::take(&mut self.buffer);
            }
        }
    }

    fn exactly(&mut self, count: usize) -> Vec<u8> {
        while self.buffer.len() < count {
            if !self.fill() {
                break;
            }
        }
        let count = count.min(self.buffer.len());
        self.buffer.drain(..count).collect()
    }

    /// As Python's client sends one: a Host, `Accept-Encoding: identity`
    /// unless the caller names another, and a length for a body.
    fn request(&mut self, method: &str, path: &str, headers: &[(String, String)], body: Option<&[u8]>) -> Response {
        let mut text = format!("{method} {path} HTTP/1.1\r\nHost: api.anthropic.com\r\n");
        if !headers.iter().any(|(n, _)| n.eq_ignore_ascii_case("accept-encoding")) {
            text.push_str("Accept-Encoding: identity\r\n");
        }
        for (name, value) in headers {
            text.push_str(&format!("{name}: {value}\r\n"));
        }
        if let Some(body) = body {
            text.push_str(&format!("Content-Length: {}\r\n", body.len()));
        }
        text.push_str("\r\n");
        let mut out = text.into_bytes();
        out.extend_from_slice(body.unwrap_or(b""));
        self.stream.write_all(&out).unwrap();
        self.stream.flush().unwrap();

        let status_line = String::from_utf8_lossy(&self.line()).into_owned();
        let status: u16 = status_line.split(' ').nth(1).and_then(|s| s.parse().ok()).unwrap_or(0);
        let mut fields = Vec::new();
        loop {
            let line = String::from_utf8_lossy(&self.line()).into_owned();
            if line.is_empty() {
                break;
            }
            if let Some((n, v)) = line.split_once(':') {
                fields.push((n.trim().to_string(), v.trim().to_string()));
            }
        }
        let field = |name: &str| fields.iter().find(|(n, _)| n.eq_ignore_ascii_case(name)).map(|(_, v)| v.clone());
        let mut body = Vec::new();
        if field("Transfer-Encoding").is_some_and(|t| t.eq_ignore_ascii_case("chunked")) {
            loop {
                let size = String::from_utf8_lossy(&self.line()).into_owned();
                let size = usize::from_str_radix(size.split(';').next().unwrap_or("").trim(), 16).unwrap_or(0);
                if size == 0 {
                    let _ = self.line();
                    break;
                }
                body.extend(self.exactly(size));
                let _ = self.line();
            }
        } else if let Some(length) = field("Content-Length").and_then(|l| l.parse().ok()) {
            body = self.exactly(length);
        }
        Response { status, headers: fields, body }
    }
}

fn read_until(stream: &mut Tls, marker: &[u8]) -> Vec<u8> {
    let mut data = Vec::new();
    let mut chunk = [0u8; 65536];
    while !data.windows(marker.len()).any(|w| w == marker) {
        match stream.read(&mut chunk) {
            Ok(0) | Err(_) => break,
            Ok(got) => data.extend_from_slice(&chunk[..got]),
        }
    }
    data
}

fn control(path: &Path, command: Value) -> Value {
    let mut stream = UnixStream::connect(path).unwrap();
    stream.set_read_timeout(Some(Duration::from_secs(5))).unwrap();
    let mut line = serde_json::to_vec(&command).unwrap();
    line.push(b'\n');
    stream.write_all(&line).unwrap();
    let mut data = Vec::new();
    let mut chunk = [0u8; 65536];
    while !data.ends_with(b"\n") {
        match stream.read(&mut chunk) {
            Ok(0) | Err(_) => break,
            Ok(got) => data.extend_from_slice(&chunk[..got]),
        }
    }
    serde_json::from_slice(&data).unwrap_or(Value::Null)
}

fn member(id: &str, token: &str, lend: bool, borrows: Option<&[&str]>, account: Option<&str>) -> Value {
    let mut entry = json!({"id": id, "name": id, "identify": [graft_pool::digest(token)], "token": if lend { json!(token) } else { Value::Null }});
    if let Some(borrows) = borrows {
        entry["borrowsFrom"] = json!(borrows);
    }
    if let Some(account) = account {
        entry["account"] = json!(account);
    }
    entry
}

fn lends(id: &str, token: &str) -> Value {
    member(id, token, true, None, None)
}

fn borrows(id: &str, token: &str, from: &[&str]) -> Value {
    member(id, token, true, Some(from), None)
}

fn limited(mut entry: Value, limits: Value) -> Value {
    entry["limits"] = limits;
    entry
}

/// Limits as Graft hands them over, newer than anything the relay has heard.
fn pushed(reset: f64, observed: f64) -> Value {
    json!({"fiveHour": 0.1, "week": 0.1, "observed": now() + observed, "weekReset": reset})
}

fn configure(path: &Path, members: Vec<Value>, assignments: Value) -> Value {
    control(path, json!({"configure": {"enabled": true, "members": members, "assignments": assignments}}))
}

fn wait_for(path: &Path, present: bool) -> bool {
    let deadline = Instant::now() + Duration::from_secs(10);
    while Instant::now() < deadline {
        if path.exists() == present {
            return true;
        }
        std::thread::sleep(Duration::from_millis(50));
    }
    false
}

fn wait_exit(child: &mut Child, patience: Duration) -> Option<i32> {
    let deadline = Instant::now() + patience;
    while Instant::now() < deadline {
        if let Ok(Some(status)) = child.try_wait() {
            return status.code();
        }
        std::thread::sleep(Duration::from_millis(50));
    }
    let _ = child.kill();
    let _ = child.wait();
    None
}

fn start_relay(binary: &str, work: &Path, config: &Path, port: u16, socket: &Path, upstream: u16, extra: &[&str]) -> Child {
    Command::new(binary)
        .args(["--config-home", &config.to_string_lossy(), "--home", &work.join("home").to_string_lossy()])
        .args(["--port", &port.to_string(), "--socket", &socket.to_string_lossy()])
        .args(["--upstream-loopback-port", &upstream.to_string()])
        .args(extra)
        .current_dir(work)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .unwrap()
}

struct Run {
    world: Service,
    socket: PathBuf,
    ca: PathBuf,
}

impl Run {
    fn ask(&self, conn: &mut Client, token: &str, session: Option<&str>, body: &[u8]) -> (Response, Vec<String>) {
        self.ask_with(conn, token, session, "POST", "/v1/messages?beta=true", Some(body), &[])
    }

    #[allow(clippy::too_many_arguments)]
    fn ask_with(
        &self,
        conn: &mut Client,
        token: &str,
        session: Option<&str>,
        method: &str,
        path: &str,
        body: Option<&[u8]>,
        extra: &[(String, String)],
    ) -> (Response, Vec<String>) {
        let before = self.world.lock().unwrap().seen.len();
        let mut headers = vec![h("Authorization", format!("Bearer {token}")), h("Content-Type", "application/json")];
        if let Some(session) = session {
            headers.push(h("X-Claude-Code-Session-Id", session));
        }
        headers.extend_from_slice(extra);
        let response = conn.request(method, path, &headers, if method == "POST" { body } else { None });
        let went = self.world.lock().unwrap().seen[before..].iter().map(|s| s.token.clone()).collect();
        (response, went)
    }

    fn seen_since(&self, before: usize) -> Vec<Seen> {
        self.world.lock().unwrap().seen[before..].to_vec()
    }

    fn seen_count(&self) -> usize {
        self.world.lock().unwrap().seen.len()
    }

    fn sessions(&self) -> HashMap<String, Value> {
        let status = control(&self.socket, json!({}));
        status["sessions"].as_array().cloned().unwrap_or_default().into_iter().map(|s| (s["id"].as_str().unwrap_or("").to_string(), s)).collect()
    }

    fn holder(&self, session: &str) -> Option<String> {
        self.sessions().get(session).and_then(|s| s["servedBy"].as_str().map(str::to_string))
    }

    fn set(&self, token: &str, account: AccountState) {
        self.world.lock().unwrap().accounts.insert(token.to_string(), account);
    }

    fn reject(&self, token: &str, reject: bool) {
        self.world.lock().unwrap().accounts.get_mut(token).unwrap().reject = reject;
    }
}

fn toks(list: &[&str]) -> Vec<String> {
    list.iter().map(|s| s.to_string()).collect()
}

const DEFAULT_BODY: &[u8] = br#"{"model":"claude-test","messages":[]}"#;

#[test]
fn the_relay_sends_each_request_to_the_account_with_room_and_hands_over_to_the_next() {
    let mut c = Checks::new();
    let start = now();
    let (world, upstream) = stand_in(start);
    let run_dir = TempDir::new();
    let work = run_dir.path().to_path_buf();
    let config = work.join("config");
    std::fs::create_dir_all(&config).unwrap();
    let port = free_port();
    let socket = work.join("r.sock");
    let mut relay = start_relay(RELAY, &work, &config, port, &socket, upstream, &[]);
    c.check(wait_for(&socket, true), "the relay opens its control socket once it is listening");
    let pool = config.join("ClaudeGraft/pool");
    let run = Run { world, socket: socket.clone(), ca: pool.join("relay-ca.pem") };
    {
        let mut w = run.world.lock().unwrap();
        w.accounts.insert("tok-A".into(), AccountState { five: 0.10, week: 0.30, week_reset: start + 5.0 * 86400.0, reject: false, refuse: None });
        w.accounts.insert("tok-B".into(), AccountState { five: 0.20, week: 0.40, week_reset: start + 2.0 * 86400.0, reject: false, refuse: None });
        w.accounts.insert("tok-C".into(), AccountState { five: 0.20, week: 0.40, week_reset: start + 3.0 * 86400.0, reject: false, refuse: Some(401) });
    }

    let names: Vec<String> = std::fs::read_dir(&pool).unwrap().flatten().map(|e| e.file_name().to_string_lossy().into_owned()).collect();
    c.check(!names.iter().any(|n| n.ends_with(".key")), "no private key is left on disk beside the authority");
    c.check(run.ca.exists() && pool.join("relay-leaf.pem").exists(), "the authority and the leaf it signed are kept with Graft's state");

    let mut conn = Client::connect(port, &run.ca);
    let (response, went) = run.ask(&mut conn, "tok-A", Some("s1"), DEFAULT_BODY);
    c.check(response.status == 200 && went == toks(&["tok-A"]), "an unconfigured relay passes a request through with the token it came with");
    c.check(response.body == streamed(), "a streamed answer comes back byte for byte");

    let status = configure(&run.socket, vec![borrows("A", "tok-A", &["B", "C"]), lends("B", "tok-B")], Value::Null);
    c.check(
        status["configured"] == json!(true) && status["enabled"] == json!(true) && status["members"].as_array().map(Vec::len) == Some(2),
        "the relay reports the pool it was given",
    );
    let (response, went) = run.ask(&mut conn, "tok-A", Some("s1"), DEFAULT_BODY);
    c.check(response.status == 200 && went == toks(&["tok-A"]), "a request with no limits known yet stays with the account that sent it");
    let (_, went) = run.ask(&mut conn, "tok-A", Some("s2"), DEFAULT_BODY);
    c.check(went == toks(&["tok-A"]), "an account's limits are learned from its own answers, not guessed");

    // B has been heard from only as a lender, which tells the relay nothing
    // until it answers; push its limits the way Graft does.
    let b_reset = start + 2.0 * 86400.0;
    configure(
        &run.socket,
        vec![borrows("A", "tok-A", &["B", "C"]), limited(lends("B", "tok-B"), json!({"fiveHour": 0.2, "week": 0.4, "observed": now(), "weekReset": b_reset}))],
        Value::Null,
    );
    let before = run.seen_count();
    let (response, went) = run.ask(&mut conn, "tok-A", Some("s3"), DEFAULT_BODY);
    c.check(went == toks(&["tok-B"]), "the account whose week resets soonest answers another member's request");
    c.check(response.body == streamed(), "a borrowed answer streams back unchanged");
    c.check(run.seen_since(before).first().and_then(|s| s.session.clone()).as_deref() == Some("s3"), "everything but the token goes out exactly as Claude Code sent it");

    for (path, method, body, what) in [
        ("/v1/messages/count_tokens?beta=true", "POST", Some(&br#"{"model":"x"}"#[..]), "counting tokens"),
        ("/api/oauth/profile", "GET", None, "a profile lookup"),
        ("/api/frame/abc", "GET", None, "an artifact"),
    ] {
        let (_, went) = run.ask_with(&mut conn, "tok-A", Some("s1"), method, path, body, &[]);
        c.check(went == toks(&["tok-A"]), &format!("{what} keeps the token it was sent with"));
    }
    for marker in [&br#""file_id":"f1""#[..], br#""container":"c1""#, br#""container":{"id":"c1","skills":[]}"#, br#""mcp_servers":[{"type":"url","url":"u"}]"#] {
        let mut body = br#"{"model":"x","messages":["#.to_vec();
        body.extend_from_slice(marker);
        body.extend_from_slice(b"]}");
        let (_, went) = run.ask(&mut conn, "tok-A", None, &body);
        let name = String::from_utf8_lossy(marker).split('"').nth(1).unwrap_or("").to_string();
        c.check(went == toks(&["tok-A"]), &format!("a request naming {name} stays with the account that owns it"));
    }
    let schema = br#"{"model":"x","messages":[],"tools":[{"name":"batch","input_schema":{"type":"object","properties":{"container":{"type":"object"},"file_id":{"type":"string"},"mcp_servers":{"type":"array"}}}}]}"#;
    let (_, went) = run.ask(&mut conn, "tok-A", None, schema);
    c.check(went == toks(&["tok-B"]), "a tool whose schema has a property of the same name refers to nothing and pins nothing");
    let (_, went) = run.ask(&mut conn, "tok-A", None, br#"{"model":"x","messages":[{"content":"file_id: none"}]}"#);
    c.check(went == toks(&["tok-B"]), "the same word inside a prompt pins nothing");
    let (_, went) = run.ask(&mut conn, "tok-unknown", Some("s1"), DEFAULT_BODY);
    c.check(went == toks(&["tok-unknown"]), "a token from outside the pool is passed through untouched");

    run.reject("tok-B", true);
    let (response, went) = run.ask(&mut conn, "tok-A", Some("s4"), DEFAULT_BODY);
    c.check(response.status == 200 && went == toks(&["tok-B", "tok-A"]), "an account the service refuses for being full is passed over within the same request");
    c.check(response.body == streamed(), "Claude Code sees only the answer that was delivered");
    let (_, went) = run.ask(&mut conn, "tok-A", Some("s5"), DEFAULT_BODY);
    c.check(went == toks(&["tok-A"]), "a full account is not asked again until its window resets");
    let status = control(&run.socket, json!({}));
    let recent = status["recent"].as_array().cloned().unwrap_or_default();
    let second_last = recent.get(recent.len().saturating_sub(2)).cloned().unwrap_or(Value::Null);
    c.check(
        second_last["servedBy"] == json!("A") && second_last["passedOver"] == json!([{"id": "B", "reason": "full"}]),
        "the record says which account was passed over and why",
    );
    run.reject("tok-B", false);

    // A third account whose login the service refuses: once, then never again.
    configure(
        &run.socket,
        vec![borrows("A", "tok-A", &["B", "C"]), limited(lends("C", "tok-C"), json!({"fiveHour": 0.1, "week": 0.1, "observed": now(), "weekReset": start + 86400.0}))],
        Value::Null,
    );
    let (response, went) = run.ask(&mut conn, "tok-A", Some("s6"), DEFAULT_BODY);
    c.check(response.status == 200 && went == toks(&["tok-C", "tok-A"]), "a lent login the service refuses falls back without Claude Code seeing the refusal");
    let (_, went) = run.ask(&mut conn, "tok-A", Some("s7"), DEFAULT_BODY);
    c.check(went == toks(&["tok-A"]), "a refused login is not lent again until it changes");
    c.check(control(&run.socket, json!({}))["members"][1]["tokenRefused"] == json!(true), "the relay says which login was refused");

    configure(
        &run.socket,
        vec![borrows("A", "tok-A", &["B", "C"]), limited(lends("B", "tok-B"), json!({"fiveHour": 0.2, "week": 0.4, "observed": now() + 5.0, "weekReset": b_reset}))],
        Value::Null,
    );
    let (_, went) = run.ask(&mut conn, "tok-A", Some("s8"), DEFAULT_BODY);
    c.check(went == toks(&["tok-B"]), "a member taken out of the pool is forgotten with it");
    run.world.lock().unwrap().accounts.get_mut("tok-A").unwrap().week_reset = start + 86400.0;
    let (_, went) = run.ask(&mut conn, "tok-A", Some("s9"), DEFAULT_BODY);
    c.check(went == toks(&["tok-B"]), "an account's limits stand as last heard until it answers again or Graft reads them");
    configure(
        &run.socket,
        vec![
            limited(borrows("A", "tok-A", &["B", "C"]), json!({"fiveHour": 0.1, "week": 0.3, "observed": now() + 10.0, "weekReset": start + 86400.0})),
            lends("B", "tok-B"),
        ],
        Value::Null,
    );
    let (_, went) = run.ask(&mut conn, "tok-A", Some("s11"), DEFAULT_BODY);
    c.check(went == toks(&["tok-A"]), "a newer reading from Graft moves the order");
    let (_, went) = run.ask(&mut conn, "tok-A", Some("s8"), DEFAULT_BODY);
    c.check(went == toks(&["tok-B"]), "a conversation stays on the account holding its cache");

    // Each Claude has a list of its own, and lending runs one way.
    run.set("tok-D", state(start + 3600.0));
    configure(
        &run.socket,
        vec![limited(borrows("A", "tok-A", &[]), pushed(start + 86400.0, 20.0)), limited(borrows("B", "tok-B", &["A"]), pushed(start + 4.0 * 86400.0, 20.0))],
        Value::Null,
    );
    let (_, went) = run.ask(&mut conn, "tok-A", Some("s12"), DEFAULT_BODY);
    c.check(went == toks(&["tok-A"]), "a Claude given no accounts to draw on keeps its own requests");
    let (_, went) = run.ask(&mut conn, "tok-B", Some("s13"), DEFAULT_BODY);
    c.check(went == toks(&["tok-A"]), "while one given an account draws on it, even from a Claude that borrows nothing");
    configure(
        &run.socket,
        vec![
            limited(member("B", "tok-B", true, Some(&["D"]), Some("acct-B")), pushed(start + 4.0 * 86400.0, 30.0)),
            limited(member("D", "tok-D", true, None, Some("acct-B")), pushed(start + 3600.0, 30.0)),
        ],
        Value::Null,
    );
    let (_, went) = run.ask(&mut conn, "tok-B", Some("s14"), DEFAULT_BODY);
    c.check(went == toks(&["tok-B"]), "an account a Claude is already signed into is not borrowed from under another name");

    configure(
        &run.socket,
        vec![limited(borrows("A", "tok-A", &["B"]), pushed(start + 86400.0, 40.0)), limited(lends("B", "tok-B"), pushed(start + 4.0 * 86400.0, 40.0))],
        json!({"s15": "B"}),
    );
    let (_, went) = run.ask(&mut conn, "tok-A", Some("s15"), DEFAULT_BODY);
    c.check(went == toks(&["tok-B"]), "a conversation moved by hand is answered by the account it was moved to");
    let (_, went) = run.ask(&mut conn, "tok-A", Some("s16"), DEFAULT_BODY);
    c.check(went == toks(&["tok-A"]), "and every other one goes where the ranking says");
    configure(
        &run.socket,
        vec![limited(borrows("A", "tok-A", &["B"]), pushed(start + 4.0 * 86400.0, 50.0)), limited(lends("B", "tok-B"), pushed(start + 86400.0, 50.0))],
        json!({"s15": "B", "s17": "A"}),
    );
    let (_, went) = run.ask(&mut conn, "tok-A", Some("s17"), DEFAULT_BODY);
    c.check(went == toks(&["tok-A"]), "a conversation moved back to its own account stays there");
    run.ask(&mut conn, "tok-A", Some("s18"), br#"{"model":"x","system":[{"cache_control":{"type":"ephemeral","ttl":"1h"}}]}"#);
    let sessions = run.sessions();
    c.check(
        sessions.get("s15").map(|s| (s["servedBy"].clone(), s["from"].clone())) == Some((json!("B"), json!("A")))
            && sessions.get("s17").map(|s| s["servedBy"].clone()) == Some(json!("A")),
        "the relay lists each conversation with the account answering it",
    );
    let lifetime = |s: Option<&Value>| s.and_then(|s| Some(s["cachedUntil"].as_f64()? - s["lastAt"].as_f64()?));
    c.check(lifetime(sessions.get("s18")).is_some_and(|l| (l - 3600.0).abs() < 1.0), "a conversation that asked for an hour of cache is held where it is for an hour");
    c.check(lifetime(sessions.get("s16")).is_some_and(|l| (l - 300.0).abs() < 1.0), "and one that did not, for five minutes");
    run.reject("tok-B", true);
    let (response, went) = run.ask(&mut conn, "tok-A", Some("s15"), DEFAULT_BODY);
    c.check(response.status == 200 && went == toks(&["tok-B", "tok-A"]), "a conversation moved to an account with no room goes where there is some");
    run.reject("tok-B", false);

    // Everything that cannot change accounts reaches the service as Claude
    // Code wrote it, a socket Remote Control holds open included. B here would
    // have its model requests answered by A, so a token arriving as A's would
    // show it.
    configure(
        &run.socket,
        vec![limited(lends("A", "tok-A"), pushed(start + 86400.0, 60.0)), limited(borrows("B", "tok-B", &["A"]), pushed(start + 4.0 * 86400.0, 60.0))],
        Value::Null,
    );
    let before = run.seen_count();
    let mut s = tunnel(port, &run.ca);
    s.sock.set_read_timeout(Some(Duration::from_secs(5))).unwrap();
    s.write_all(
        b"GET /v1/sessions/ws/abc/subscribe HTTP/1.1\r\nHost: api.anthropic.com\r\nAuthorization: Bearer tok-B\r\n\
          Connection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n",
    )
    .unwrap();
    c.check(read_until(&mut s, b"\r\n\r\n").starts_with(b"HTTP/1.1 101"), "a request to open a socket is answered by the service rather than refused on the way");
    let went = run.seen_since(before);
    c.check(
        went.len() == 1 && went[0].token == "tok-B" && went[0].connection.as_deref().unwrap_or("").to_lowercase().contains("upgrade"),
        "and reaches it still asking for the upgrade, under the token it was sent with",
    );
    s.write_all(b"\x81\x05hello").unwrap();
    c.check(read_until(&mut s, b"hello").windows(7).any(|w| w == b"\x81\x05hello"), "once open, the socket carries bytes both ways untouched");
    drop(s);

    let before = run.seen_count();
    let mut s = tunnel(port, &run.ca);
    s.sock.set_read_timeout(Some(Duration::from_secs(5))).unwrap();
    s.write_all(
        b"POST /v1/sessions/abc/events HTTP/1.1\r\nHost: api.anthropic.com\r\nAuthorization: Bearer tok-B\r\n\
          Content-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nfirst\r\n",
    )
    .unwrap();
    c.check(read_until(&mut s, b"got first").windows(9).any(|w| w == b"got first"), "a request still being written is answered as it goes, as it would be without the relay");
    s.write_all(b"6\r\nsecond\r\n0\r\n\r\n").unwrap();
    c.check(read_until(&mut s, b"0\r\n\r\n").windows(10).any(|w| w == b"got second"), "and the rest of it follows");
    let went = run.seen_since(before);
    c.check(went.len() == 1 && went[0].token == "tok-B" && went[0].chunked, "it arrives still chunked, under the token it was sent with");
    drop(s);

    let mut side = Client::connect(port, &run.ca);
    run.ask_with(&mut side, "tok-B", Some("s1"), "GET", "/api/oauth/profile", None, &[]);
    let (_, went) = run.ask(&mut side, "tok-B", Some("s21"), DEFAULT_BODY);
    c.check(went == toks(&["tok-A"]), "a model request after one passed through on the same connection still goes where the ranking says");
    drop(side);

    // Reasoning carried back in a conversation is read only by the account
    // that wrote it, for the models bound that way, so such a conversation
    // moves only when its account has no room. E borrows D, and D ranks first.
    run.set("tok-E", state(start + 5.0 * 86400.0));
    configure(
        &run.socket,
        vec![limited(lends("D", "tok-D"), pushed(start + 3600.0, 90.0)), limited(borrows("E", "tok-E", &["D"]), pushed(start + 5.0 * 86400.0, 90.0))],
        json!({"s32": "D"}),
    );
    let plain = br#"{"model":"claude-sonnet-5-5","messages":[{"role":"user","content":"hi"}]}"#;
    let reasoned = br#"{"model":"claude-sonnet-5-5","messages":[{"role":"user","content":"hi"},{"role":"assistant","content":[{"type":"thinking","thinking":"t","signature":"s"},{"type":"text","text":"x"}]},{"role":"user","content":"and?"}]}"#;
    let (_, went) = run.ask(&mut conn, "tok-E", Some("s30"), plain);
    c.check(went == toks(&["tok-D"]), "a conversation with no reasoning yet goes where the ranking says");
    let (_, went) = run.ask(&mut conn, "tok-E", Some("s31"), reasoned);
    c.check(went == toks(&["tok-E"]), "one carrying reasoning the relay never saw written stays on the account that sent it");
    let (_, went) = run.ask(&mut conn, "tok-E", Some("s31"), reasoned);
    c.check(went == toks(&["tok-E"]), "and goes on staying there, ranking or not");
    let (_, went) = run.ask(&mut conn, "tok-E", Some("s30"), reasoned);
    c.check(went == toks(&["tok-D"]), "while one whose reasoning D wrote stays with D");
    let (_, went) = run.ask(&mut conn, "tok-E", Some("s32"), reasoned);
    c.check(went == toks(&["tok-D"]), "a person moving a conversation from the dropdown is a move made knowing what it costs");
    let opus = String::from_utf8_lossy(reasoned).replace("claude-sonnet-5-5", "claude-opus-5-5");
    let (_, went) = run.ask(&mut conn, "tok-E", Some("s33"), opus.as_bytes());
    c.check(went == toks(&["tok-D"]), "and a model whose reasoning survives the move is routed as before");
    run.reject("tok-D", true);
    let (response, went) = run.ask(&mut conn, "tok-E", Some("s30"), reasoned);
    c.check(response.status == 200 && went == toks(&["tok-D", "tok-E"]), "a held conversation moves once its account has no room, rather than fail");
    run.reject("tok-D", false);
    let (_, went) = run.ask(&mut conn, "tok-E", Some("s30"), reasoned);
    c.check(went == toks(&["tok-E"]), "and stays where it went, so its reasoning is lost once");

    // A conversation kept as a thread on the service sends only its newest
    // turns, and only the account holding the thread can answer them. H
    // borrows G, and G ranks first.
    run.set("tok-G", state(start + 3600.0));
    run.set("tok-H", state(start + 5.0 * 86400.0));
    run.set("tok-F", AccountState { refuse: Some(404), ..state(start + 1800.0) });
    let thread_pool = |first: &str, observed: f64, assignments: Value| {
        let (g, hh) = if first == "G" { (start + 3600.0, start + 5.0 * 86400.0) } else { (start + 5.0 * 86400.0, start + 3600.0) };
        configure(
            &run.socket,
            vec![limited(lends("G", "tok-G"), pushed(g, observed)), limited(borrows("H", "tok-H", &["G"]), pushed(hh, observed))],
            assignments,
        );
    };
    let threaded = |kind: &str, previous: &str| -> Vec<u8> {
        let thread = if kind == "create" {
            r#"{"type":"create"}"#.to_string()
        } else {
            format!(r#"{{"type":"continue","previous_message_id":"{previous}"}}"#)
        };
        format!(r#"{{"model":"claude-opus-5-5","messages":[{{"role":"user","content":"hi"}}],"thread":{thread}}}"#).into_bytes()
    };
    let gzip = vec![h("Accept-Encoding", "gzip, deflate, br")];
    let ask_thread = |conn: &mut Client, token: &str, session: &str, body: &[u8]| -> (Response, Vec<String>) {
        let (mut response, went) = run.ask_with(conn, token, Some(session), "POST", "/v1/messages?beta=true", Some(body), &gzip);
        // Claude Code unpacks whatever the account it reached sent; so does
        // this.
        if response.header("Content-Encoding") == Some("gzip") {
            let mut plain = Vec::new();
            let _ = flate2::read::GzDecoder::new(&response.body[..]).read_to_end(&mut plain);
            response.body = plain;
        }
        (response, went)
    };
    let has = |body: &[u8], needle: &[u8]| body.windows(needle.len()).any(|w| w == needle);
    let title = br#"{"model":"claude-haiku-5-5","messages":[{"role":"user","content":"name this"}]}"#;

    thread_pool("G", 95.0, Value::Null);
    let (_, went) = ask_thread(&mut conn, "tok-H", "s40", &threaded("create", ""));
    c.check(went == toks(&["tok-G"]), "a new thread goes where the ranking says");
    run.world.lock().unwrap().threads.insert("m1".into(), "tok-G".into());
    thread_pool("H", 96.0, Value::Null);
    let (response, went) = ask_thread(&mut conn, "tok-H", "s40", &threaded("continue", "m1"));
    c.check(response.status == 200 && went == toks(&["tok-G"]), "a thread is continued on the account holding it, past a ranking that has turned");
    let (_, went) = ask_thread(&mut conn, "tok-H", "s41", &threaded("continue", "m9"));
    c.check(went == toks(&["tok-H"]), "one the relay never saw answered stays on the account that sent it");

    thread_pool("G", 97.0, json!({"s40": "H"}));
    let (response, went) = ask_thread(&mut conn, "tok-H", "s40", &threaded("continue", "m1"));
    c.check(
        response.status == 404 && has(&response.body, b"thread_not_found") && went == toks(&["tok-H"]),
        "a thread moved home from the dropdown is not sent back to the account it left",
    );
    let (_, went) = ask_thread(&mut conn, "tok-H", "s40", &threaded("create", ""));
    c.check(went == toks(&["tok-H"]), "the thread Claude Code starts again goes where it was moved");
    run.world.lock().unwrap().threads.insert("m4".into(), "tok-H".into());

    thread_pool("G", 98.0, json!({"s40": "G"}));
    let (_, went) = run.ask(&mut conn, "tok-H", Some("s40"), title);
    c.check(went == toks(&["tok-G"]), "a title asked for beside a thread is routed like any short request");
    c.check(run.holder("s40").as_deref() == Some("H"), "and answered elsewhere, does not say where the thread is");
    let before = run.seen_count();
    let (response, went) = ask_thread(&mut conn, "tok-H", "s40", &threaded("continue", "m4"));
    c.check(
        response.status == 404 && has(&response.body, b"thread_not_found") && went == toks(&["tok-G"]),
        "a borrowed account without the thread says so, and Claude Code hears it rather than the request going home",
    );
    c.check(
        run.seen_since(before).first().and_then(|s| s.accept.clone()).as_deref() == Some("identity"),
        "a thread moved to a borrowed account asks for an answer it can read, compressed errors and all",
    );
    let (response, went) = ask_thread(&mut conn, "tok-H", "s40", &threaded("create", ""));
    c.check(response.status == 200 && went == toks(&["tok-G"]), "the thread started again goes to the account chosen");
    run.world.lock().unwrap().threads.insert("m2".into(), "tok-G".into());
    let (_, went) = ask_thread(&mut conn, "tok-H", "s40", &threaded("continue", "m2"));
    c.check(went == toks(&["tok-G"]), "and is continued there");
    c.check(run.holder("s40").as_deref() == Some("G"), "the dropdown names the account holding the thread");

    let sessions = run.sessions();
    c.check(sessions.get("s40").is_some_and(|s| !s["startedAt"].is_null()), "a conversation first seen with its first message is marked as new");
    ask_thread(&mut conn, "tok-H", "s43", br#"{"model":"claude-opus-5-5","messages":[{"role":"user","content":"a"},{"role":"assistant","content":"b"},{"role":"user","content":"c"}]}"#);
    let sessions = run.sessions();
    c.check(sessions.get("s43").is_some_and(|s| s["startedAt"].is_null()), "one first seen part way through, as after the relay restarts, is not");

    // The tray follows the conversation typed into last, so only a turn a
    // person typed on a conversation's thread marks it.
    let typed_at = sessions.get("s40").and_then(|s| s["promptedAt"].as_f64());
    c.check(
        typed_at.is_some() && sessions.get("s43").is_some_and(|s| s["promptedAt"].is_null()),
        "a turn typed on a thread marks when the conversation was typed into, and a request without one does not",
    );
    let tool_answer = br#"{"model":"claude-opus-5-5","messages":[{"role":"user","content":[{"type":"tool_result","tool_use_id":"u1","content":"17 x 23"}]}],"thread":{"type":"continue","previous_message_id":"m2"}}"#;
    ask_thread(&mut conn, "tok-H", "s40", tool_answer);
    let (response, _) = ask_thread(&mut conn, "tok-H", "s40", title);
    let held = run.sessions().get("s40").cloned().unwrap_or(Value::Null);
    c.check(
        response.status == 200 && held["promptedAt"].as_f64() == typed_at && held["lastAt"].as_f64().unwrap_or(0.0) > typed_at.unwrap_or(f64::MAX),
        "Claude Code carrying on with a tool's answer, or asking for a title, leaves it as it was",
    );
    ask_thread(&mut conn, "tok-H", "s40", &threaded("continue", "m2"));
    let held = run.sessions().get("s40").cloned().unwrap_or(Value::Null);
    c.check(held["promptedAt"].as_f64().unwrap_or(0.0) > typed_at.unwrap_or(f64::MAX), "and the next turn typed marks it again");

    configure(
        &run.socket,
        vec![limited(lends("F", "tok-F"), pushed(start + 1800.0, 100.0)), limited(borrows("H", "tok-H", &["F"]), pushed(start + 5.0 * 86400.0, 100.0))],
        json!({"s42": "F"}),
    );
    run.world.lock().unwrap().threads.insert("m3".into(), "tok-H".into());
    let (response, went) = ask_thread(&mut conn, "tok-H", "s42", &threaded("continue", "m3"));
    c.check(response.status == 200 && went == toks(&["tok-F", "tok-H"]), "any other refusal of a continued thread still sends it home");

    // Whatever the lent accounts say, Claude Code hears its own account's
    // answer. Each sender here ranks ahead of what it borrows, and finds
    // itself full on what the Chat tab spent unseen.
    for (sender, lender, refusal) in [("J1", "K", 403u16), ("J2", "L", 401u16)] {
        let (st, lt) = (format!("tok-{sender}"), format!("tok-{lender}"));
        run.set(&st, AccountState { reject: true, ..state(start + 3600.0) });
        run.set(&lt, AccountState { refuse: Some(refusal), ..state(start + 3.0 * 86400.0) });
        configure(
            &run.socket,
            vec![limited(borrows(sender, &st, &[lender]), pushed(start + 3600.0, 110.0)), limited(lends(lender, &lt), pushed(start + 3.0 * 86400.0, 110.0))],
            Value::Null,
        );
        let (response, went) = run.ask(&mut conn, &st, Some(&format!("s6{sender}")), DEFAULT_BODY);
        c.check(
            response.status == 429 && went == vec![st.clone(), lt.clone(), st.clone()],
            &format!("a sender asked first and found full, whose lender answers {refusal}, is asked again and its own refusal delivered"),
        );
    }

    // Three accounts ranked lender, sender, lender: what the first cannot
    // serve goes to the sender, not on to the other lender.
    run.set("tok-P", AccountState { refuse: Some(403), ..state(start + 1800.0) });
    run.set("tok-Q", state(start + 86400.0));
    run.set("tok-R", state(start + 5.0 * 86400.0));
    configure(
        &run.socket,
        vec![
            limited(lends("P", "tok-P"), pushed(start + 1800.0, 110.0)),
            limited(borrows("Q", "tok-Q", &["P", "R"]), pushed(start + 86400.0, 110.0)),
            limited(lends("R", "tok-R"), pushed(start + 5.0 * 86400.0, 110.0)),
        ],
        Value::Null,
    );
    let (response, went) = run.ask(&mut conn, "tok-Q", Some("s62"), DEFAULT_BODY);
    c.check(response.status == 200 && went == toks(&["tok-P", "tok-Q"]), "a request one lender cannot serve goes home, even with another lender ranked after the sender");

    // A held conversation follows its account across names: Claude X's login
    // lapses, and the same account signed in here as Y lends in its place.
    run.set("tok-S", state(start + 1800.0));
    run.set("tok-X", state(start + 900.0));
    run.set("tok-Y", state(start + 5.0 * 86400.0));
    run.set("tok-W", state(start + 600.0));
    configure(
        &run.socket,
        vec![
            limited(member("S", "tok-S", true, Some(&["X", "Y"]), Some("acct-S")), pushed(start + 86400.0, 115.0)),
            limited(member("X", "tok-X", true, None, Some("acct-X")), pushed(start + 900.0, 115.0)),
            limited(member("Y", "tok-Y", true, None, Some("acct-X")), pushed(start + 900.0, 115.0)),
        ],
        Value::Null,
    );
    let (_, went) = run.ask(&mut conn, "tok-S", Some("s63"), plain);
    c.check(went == toks(&["tok-X"]), "a conversation starts on the account ranked first");
    run.ask(&mut conn, "tok-S", Some("s64"), plain);
    configure(
        &run.socket,
        vec![
            limited(member("S", "tok-S", true, Some(&["X", "Y"]), Some("acct-S")), pushed(start + 1800.0, 120.0)),
            limited(member("X", "tok-X", false, None, Some("acct-X")), pushed(start + 5.0 * 86400.0, 120.0)),
            limited(member("Y", "tok-Y", true, None, Some("acct-X")), pushed(start + 5.0 * 86400.0, 120.0)),
        ],
        Value::Null,
    );
    let (_, went) = run.ask(&mut conn, "tok-S", Some("s63"), reasoned);
    c.check(went == toks(&["tok-Y"]), "its reasoning stays with the account that wrote it, under the name that account now lends by");
    configure(
        &run.socket,
        vec![
            limited(member("S", "tok-S", true, Some(&["W"]), Some("acct-S")), pushed(start + 1800.0, 125.0)),
            limited(member("W", "tok-W", true, None, Some("acct-W")), pushed(start + 600.0, 125.0)),
        ],
        Value::Null,
    );
    let (_, went) = run.ask(&mut conn, "tok-S", Some("s64"), reasoned);
    c.check(went == toks(&["tok-S"]), "and once that account lends no more, goes home rather than wherever the ranking says");

    // Claude 2 borrowing Claude and the same account signed in here: the
    // relay counts the first, and a conversation moved to the second goes to
    // the first.
    run.set("tok-E", state(start + 3600.0));
    configure(
        &run.socket,
        vec![
            limited(member("A", "tok-A", true, Some(&["B", "E"]), Some("acct-A")), pushed(start + 86400.0, 130.0)),
            limited(member("B", "tok-B", true, None, Some("acct-B")), pushed(start + 4.0 * 86400.0, 130.0)),
            limited(member("E", "tok-E", true, None, Some("acct-B")), pushed(start + 4.0 * 86400.0, 130.0)),
        ],
        json!({"s19": "E"}),
    );
    let (_, went) = run.ask(&mut conn, "tok-A", Some("s19"), DEFAULT_BODY);
    c.check(went == toks(&["tok-B"]), "a conversation moved to an account listed twice goes to it under the name the relay counted");
    // The same pair with Claude's own login lapsed, which is what happens to
    // a Claude left closed: the account still lends, under the name signed in
    // here.
    configure(
        &run.socket,
        vec![
            limited(member("A", "tok-A", true, Some(&["B", "E"]), Some("acct-A")), pushed(start + 4.0 * 86400.0, 135.0)),
            limited(member("B", "tok-B", false, None, Some("acct-B")), pushed(start + 86400.0, 135.0)),
            limited(member("E", "tok-E", true, None, Some("acct-B")), pushed(start + 86400.0, 135.0)),
        ],
        Value::Null,
    );
    let (_, went) = run.ask(&mut conn, "tok-A", Some("s20"), DEFAULT_BODY);
    c.check(went == toks(&["tok-E"]), "an account listed twice lends under its second name when the first has no login to lend");

    control(
        &run.socket,
        json!({"configure": {"enabled": false, "members": [borrows("A", "tok-A", &["B", "C"]), lends("B", "tok-B")]}}),
    );
    let (_, went) = run.ask(&mut conn, "tok-A", Some("s10"), DEFAULT_BODY);
    c.check(went == toks(&["tok-A"]), "a pool switched off sends everything as Claude sent it");
    drop(conn);

    // A tunnel for anything else is not opened, only passed along.
    let mut raw = TcpStream::connect(("127.0.0.1", port)).unwrap();
    raw.set_read_timeout(Some(Duration::from_secs(5))).unwrap();
    raw.write_all(format!("CONNECT 127.0.0.1:{upstream} HTTP/1.1\r\nHost: x\r\n\r\n").as_bytes()).unwrap();
    let mut reply = [0u8; 4096];
    let got = raw.read(&mut reply).unwrap_or(0);
    c.check(reply[..got].starts_with(b"HTTP/1.1 200"), "a tunnel to any other host is opened");
    raw.write_all(b"GET /plain HTTP/1.1\r\nHost: x\r\nAuthorization: Bearer tok-A\r\nConnection: close\r\n\r\n").unwrap();
    let mut data = Vec::new();
    let _ = raw.read_to_end(&mut data);
    c.check(has(&data, br#""path": "/plain""#), "and what goes through it arrives untouched");
    drop(raw);

    // Without the leaf a starting relay would make a new authority and leaf;
    // one that finds another relay running must leave before it gets that far.
    let leaf = pool.join("relay-leaf.pem");
    let authority = std::fs::read(&run.ca).unwrap();
    let presented = std::fs::read(&leaf).unwrap();
    std::fs::remove_file(&leaf).unwrap();
    let mut second = start_relay(RELAY, &work, &config, port, &socket, upstream, &[]);
    let code = wait_exit(&mut second, Duration::from_secs(10));
    c.check(code == Some(0) && relay.try_wait().ok().flatten().is_none(), "a second relay leaves at once and the first keeps answering");
    c.check(std::fs::read(&run.ca).unwrap() == authority && !leaf.exists(), "and without renewing the certificate the first one presents");
    std::fs::write(&leaf, presented).unwrap();

    control(&run.socket, json!({"quit": true}));
    c.check(!socket.exists(), "a relay stepping aside takes its socket with it");
    c.check(wait_exit(&mut relay, Duration::from_secs(10)) == Some(0), "and leaves once nothing is in flight");
    let mut replacement = start_relay(RELAY, &work, &config, port, &socket, upstream, &[]);
    c.check(wait_for(&socket, true), "the relay replacing it can start straight away");
    let mut conn = Client::connect(port, &run.ca);
    let (response, went) = run.ask(&mut conn, "tok-A", Some("s1"), DEFAULT_BODY);
    c.check(response.status == 200 && went == toks(&["tok-A"]), "and starts out passing everything through until it is told otherwise");
    drop(conn);
    control(&run.socket, json!({"quit": true}));
    let _ = wait_exit(&mut replacement, Duration::from_secs(10));
    c.finish();
}

#[test]
fn a_relay_whose_graft_has_gone_puts_back_each_closed_claudes_environment_and_leaves() {
    let mut c = Checks::new();
    let gone_dir = TempDir::new();
    let gone = gone_dir.path().to_path_buf();
    let copy = gone.join("graft-relay");
    std::fs::copy(RELAY, &copy).unwrap();
    let support = gone.join("support");
    let gone_pool = support.join("ClaudeGraft/pool");
    std::fs::create_dir_all(&gone_pool).unwrap();
    let claude = |name: &str, field: &str| -> PathBuf {
        let path = gone.join(name);
        std::fs::create_dir_all(&path).unwrap();
        std::fs::write(path.join(environment::FILE_NAME), serde_json::to_vec(&json!({"envVars": field, "kept": 1})).unwrap()).unwrap();
        path
    };
    let read = |path: &Path| -> Value { serde_json::from_slice(&std::fs::read(path.join(environment::FILE_NAME)).unwrap()).unwrap() };
    let closed = claude("Closed", "pooled-1");
    let open = claude("Open", "pooled-2");
    let mut profiles = serde_json::Map::new();
    profiles.insert(closed.to_string_lossy().into_owned(), json!({"wanted": true, "plain": "plain-1", "pooled": "pooled-1"}));
    profiles.insert(open.to_string_lossy().into_owned(), json!({"wanted": true, "pooled": "pooled-2"}));
    let journal = json!({"profiles": profiles});
    std::fs::write(gone_pool.join("environment.json"), serde_json::to_vec(&journal).unwrap()).unwrap();
    // Stands for a Claude still open on that profile: the main binary's name,
    // and the profile as its data folder.
    let mut still_open = Command::new("bash")
        .arg("-c")
        .arg(format!("exec -a 'claude-desktop --user-data-dir={}' sleep 60", open.display()))
        .spawn()
        .unwrap();
    let socket = gone.join("g.sock");
    let copy_text = copy.to_string_lossy().into_owned();
    let mut left = start_relay(&copy_text, &gone, &support, free_port(), &socket, free_port(), &["--removal-check", "0.4", "--local-processes"]);
    // A development build digests its own large binary as it starts, so the
    // relay is waited for before its looks are counted.
    c.check(wait_for(&socket, true), "the relay starts");
    std::thread::sleep(Duration::from_millis(1500));
    c.check(read(&closed)["envVars"] == json!("pooled-1") && left.try_wait().ok().flatten().is_none(), "a relay whose file is still there leaves every Claude as it is");
    std::fs::remove_file(&copy).unwrap();
    std::fs::remove_dir_all(support.join("ClaudeGraft")).unwrap();
    std::thread::sleep(Duration::from_secs(2));
    c.check(
        read(&closed) == json!({"envVars": "plain-1", "kept": 1}),
        "once its file has gone, a closed Claude gets back what it held before sharing, from the journal it read last",
    );
    c.check(
        read(&open)["envVars"] == json!("pooled-2") && left.try_wait().ok().flatten().is_none(),
        "while one still open keeps the lines and the relay keeps answering it",
    );
    c.check(!support.join("ClaudeGraft").exists(), "and nothing of Graft's that was removed is made again");
    let _ = still_open.kill();
    let _ = still_open.wait();
    let code = wait_exit(&mut left, Duration::from_secs(10));
    c.check(read(&open) == json!({"kept": 1}) && code == Some(0), "once that one closes too its lines come out, and the relay leaves");
    c.finish();
}
