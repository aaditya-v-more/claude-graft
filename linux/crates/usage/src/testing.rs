//! A stand-in for Anthropic's service on loopback, so the suite can drive
//! every answer the real one gives without a request leaving the machine.

use std::collections::HashMap;
use std::io::{BufRead, BufReader, Read, Write};
use std::net::{TcpListener, TcpStream};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex, MutexGuard};
use std::time::Duration;

/// One stand-in at a time, since where requests go is process-wide.
static SERIAL: Mutex<()> = Mutex::new(());

#[derive(Clone)]
struct Reply {
    status: u16,
    headers: Vec<(String, String)>,
    body: String,
}

#[derive(Debug, Clone)]
pub struct Seen {
    pub method: String,
    pub path: String,
    pub headers: Vec<(String, String)>,
    pub body: Vec<u8>,
}

impl Seen {
    pub fn header(&self, name: &str) -> Option<String> {
        self.headers.iter().find(|(n, _)| n.eq_ignore_ascii_case(name)).map(|(_, v)| v.clone())
    }
}

#[derive(Default)]
struct State {
    reply: Option<Reply>,
    by_token: HashMap<String, Reply>,
    seen: Vec<Seen>,
    delay: Duration,
    hang_up: bool,
}

pub struct StandIn {
    port: u16,
    state: Arc<Mutex<State>>,
    stop: Arc<AtomicBool>,
    connections: Arc<AtomicUsize>,
    routes: bool,
    _serial: Option<MutexGuard<'static, ()>>,
}

impl StandIn {
    /// A stand-in every request in this process now goes to.
    pub fn start() -> StandIn {
        let serial = SERIAL.lock().unwrap_or_else(|e| e.into_inner());
        let stand_in = StandIn::bind(true, Some(serial));
        crate::api::stand_in::set(Some(format!("http://127.0.0.1:{}", stand_in.port)));
        stand_in
    }

    /// Holds every stand-in off for as long as the guard lives, so a test can
    /// see where a request goes with none of them answering.
    pub fn none() -> MutexGuard<'static, ()> {
        SERIAL.lock().unwrap_or_else(|e| e.into_inner())
    }

    /// A listener that only counts who connects to it, for proving nothing
    /// did.
    pub fn listen_only() -> StandIn {
        StandIn::bind(false, None)
    }

    fn bind(routes: bool, serial: Option<MutexGuard<'static, ()>>) -> StandIn {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = listener.local_addr().unwrap().port();
        let state = Arc::new(Mutex::new(State::default()));
        let stop = Arc::new(AtomicBool::new(false));
        let connections = Arc::new(AtomicUsize::new(0));
        {
            let state = state.clone();
            let stop = stop.clone();
            let connections = connections.clone();
            std::thread::spawn(move || {
                for stream in listener.incoming() {
                    if stop.load(Ordering::SeqCst) {
                        break;
                    }
                    let Ok(stream) = stream else { continue };
                    connections.fetch_add(1, Ordering::SeqCst);
                    if routes {
                        let state = state.clone();
                        std::thread::spawn(move || serve(stream, &state));
                    }
                }
            });
        }
        StandIn { port, state, stop, connections, routes, _serial: serial }
    }

    pub fn port(&self) -> u16 {
        self.port
    }

    pub fn connections(&self) -> usize {
        self.connections.load(Ordering::SeqCst)
    }

    /// The answer every request gets from now on, unless its token has one
    /// of its own.
    pub fn answer(&self, status: u16, headers: &[(&str, &str)], body: &str) {
        let mut state = self.state.lock().unwrap();
        state.hang_up = false;
        state.reply = Some(reply(status, headers, body));
    }

    pub fn answer_token(&self, token: &str, status: u16, headers: &[(&str, &str)], body: &str) {
        self.state.lock().unwrap().by_token.insert(token.to_string(), reply(status, headers, body));
    }

    pub fn delay(&self, by: Duration) {
        self.state.lock().unwrap().delay = by;
    }

    pub fn hang_up(&self) {
        self.state.lock().unwrap().hang_up = true;
    }

    pub fn requests(&self) -> Vec<Seen> {
        self.state.lock().unwrap().seen.clone()
    }

    pub fn requests_with(&self, token: &str) -> usize {
        let bearer = format!("Bearer {token}");
        self.requests().iter().filter(|s| s.header("authorization").as_deref() == Some(&bearer)).count()
    }
}

impl Drop for StandIn {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::SeqCst);
        let _ = TcpStream::connect(("127.0.0.1", self.port));
        if self.routes {
            crate::api::stand_in::set(None);
        }
    }
}

fn reply(status: u16, headers: &[(&str, &str)], body: &str) -> Reply {
    Reply {
        status,
        headers: headers.iter().map(|(n, v)| (n.to_string(), v.to_string())).collect(),
        body: body.to_string(),
    }
}

fn serve(stream: TcpStream, state: &Mutex<State>) {
    let _ = stream.set_read_timeout(Some(Duration::from_secs(5)));
    let mut reader = BufReader::new(stream.try_clone().unwrap());
    let mut line = String::new();
    if reader.read_line(&mut line).unwrap_or(0) == 0 {
        return;
    }
    let mut parts = line.split_whitespace();
    let method = parts.next().unwrap_or("").to_string();
    let path = parts.next().unwrap_or("").to_string();
    let mut headers = Vec::new();
    loop {
        let mut header = String::new();
        if reader.read_line(&mut header).unwrap_or(0) == 0 || header == "\r\n" || header == "\n" {
            break;
        }
        if let Some((name, value)) = header.trim_end().split_once(':') {
            headers.push((name.trim().to_string(), value.trim().to_string()));
        }
    }
    let length = headers
        .iter()
        .find(|(n, _)| n.eq_ignore_ascii_case("content-length"))
        .and_then(|(_, v)| v.parse::<usize>().ok())
        .unwrap_or(0);
    let mut body = vec![0u8; length];
    let _ = reader.read_exact(&mut body);
    let seen = Seen { method, path, headers, body };
    let bearer = seen.header("authorization").and_then(|v| v.strip_prefix("Bearer ").map(str::to_string));

    let (reply, delay, hang_up) = {
        let mut s = state.lock().unwrap();
        s.seen.push(seen);
        let reply = bearer.and_then(|t| s.by_token.get(&t).cloned()).or_else(|| s.reply.clone());
        (reply, s.delay, s.hang_up)
    };
    std::thread::sleep(delay);
    let mut stream = stream;
    if hang_up {
        return;
    }
    let reply = reply.unwrap_or_else(|| self::reply(404, &[], "{}"));
    let mut out = format!("HTTP/1.1 {} Stand-in\r\nContent-Length: {}\r\nConnection: close\r\n", reply.status, reply.body.len());
    for (name, value) in &reply.headers {
        out.push_str(&format!("{name}: {value}\r\n"));
    }
    out.push_str("\r\n");
    out.push_str(&reply.body);
    let _ = stream.write_all(out.as_bytes());
    let _ = stream.flush();
}

/// A profile signed in with the given tokens, sealed the way a desktop with no
/// keyring seals them (`v10`), so reading them back never asks the real one.
pub fn signed_in(profile: &std::path::Path, tokens: &[(&str, &str, f64)]) {
    use base64::Engine;
    let mut cache = serde_json::Map::new();
    for (index, (token, scopes, expires)) in tokens.iter().enumerate() {
        cache.insert(
            format!("client{index}:org:https://api.anthropic.com:{scopes}"),
            serde_json::json!({"token": token, "expiresAt": expires, "subscriptionType": "max"}),
        );
    }
    let plain = serde_json::to_vec(&cache).unwrap();
    let sealed = graft_credentials::seal(&plain, &graft_credentials::peanuts_key(), graft_credentials::Scheme::V10);
    let blob = base64::engine::general_purpose::STANDARD.decode(&sealed).unwrap();
    assert_eq!(&blob[..3], b"v10");
    std::fs::create_dir_all(profile).unwrap();
    let config = serde_json::json!({"oauth:tokenCacheV2": sealed, "lastKnownAccountUuid": "AAAA"});
    std::fs::write(profile.join("config.json"), serde_json::to_vec(&config).unwrap()).unwrap();
}

/// A login good for a day.
pub fn tomorrow() -> f64 {
    let now = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_millis() as f64;
    now + 24.0 * 3_600_000.0
}
