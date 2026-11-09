//! Signs an account in through the browser so it can lend its usage without a
//! Claude of its own: the same sign-in Claude Code's long-lived login uses, a
//! year-long token for model requests and nothing else.
//!
//! The browser comes back to a listener on loopback with a one-time code, and
//! the code is exchanged for the token at Anthropic's sign-in service. That
//! exchange is the one request here that does not go to `api.anthropic.com`,
//! and what it sends is the code, never a token. The other credential the
//! service returns beside the token is dropped unread: a year is the whole of
//! what is wanted, and holding one would be holding a login that renews
//! itself.
//!
//! Opening the browser is the window's to do, through the desktop's portal;
//! this hands it the address and waits for what comes back.

use crate::code_environment::Sealer;
use crate::controller::{PoolAccount, ACCOUNT_PREFIX};
use crate::{now, Time};
use base64::Engine;
use graft_credentials::Prompting;
use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use std::collections::BTreeMap;
use std::io::{Read, Write};
use std::net::{Ipv4Addr, Ipv6Addr, SocketAddr, TcpListener, TcpStream, ToSocketAddrs};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};

pub const CLIENT_ID: &str = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
pub const AUTHORIZE: &str = "https://claude.com/cai/oauth/authorize";
pub const EXCHANGE_HOST: &str = "platform.claude.com";
pub const EXCHANGE_PATH: &str = "/v1/oauth/token";
pub const SCOPE: &str = "user:inference";
pub const LIFETIME: u64 = 365 * 24 * 60 * 60;
/// Long enough to sign in from scratch, two-step verification included.
pub const PATIENCE: Duration = Duration::from_secs(10 * 60);

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Failure {
    Cancelled,
    TimedOut,
    Listener,
    Declined(Option<String>),
    Refused(u16, Option<String>),
    Unreadable,
    OwnAccount,
    Keychain,
}

impl Failure {
    pub fn message(&self) -> String {
        match self {
            Failure::Cancelled => String::new(),
            Failure::TimedOut => "Nothing came back from the browser, so the sign-in was abandoned.".into(),
            Failure::Listener => "Graft could not listen for the browser's answer on this computer.".into(),
            Failure::Declined(Some(reason)) => format!("The sign-in was not completed: {reason}"),
            Failure::Declined(None) => "The sign-in was not completed.".into(),
            Failure::Refused(_, Some(reason)) => format!("Anthropic did not accept the sign-in: {reason}"),
            Failure::Refused(status, None) => format!("Anthropic did not accept the sign-in ({status})."),
            Failure::Unreadable => "Anthropic's answer to the sign-in could not be read.".into(),
            Failure::OwnAccount => "That is the account this Claude already uses, so it has nothing to add. \
                 Switch accounts in the browser, then try again."
                .into(),
            Failure::Keychain => "Graft needs the keyring unlocked to keep this login. Try again and allow it.".into(),
        }
    }
}

/// Thirty-two random bytes, written the way a URL can carry them.
pub fn random_token() -> String {
    let mut bytes = [0u8; 32];
    let mut filled = 0;
    while filled < bytes.len() {
        // SAFETY: writes at most the remaining length into the buffer.
        let got = unsafe { libc::getrandom(bytes[filled..].as_mut_ptr().cast(), bytes.len() - filled, 0) };
        if got > 0 {
            filled += got as usize;
        }
    }
    base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(bytes)
}

/// `+` would be read as a space, and a colon or slash left bare in a value is
/// a thing some servers parse and others do not.
fn encode(value: &str) -> String {
    let mut out = String::new();
    for byte in value.bytes() {
        if byte.is_ascii_alphanumeric() || b"!$'()*,-.;@_~".contains(&byte) {
            out.push(byte as char);
        } else {
            out.push_str(&format!("%{byte:02X}"));
        }
    }
    out
}

fn decode(value: &str) -> String {
    let bytes = value.as_bytes();
    let mut out = Vec::with_capacity(bytes.len());
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == b'%' && i + 2 < bytes.len() {
            let digits = std::str::from_utf8(&bytes[i + 1..i + 3]).unwrap_or("");
            if let Ok(b) = u8::from_str_radix(digits, 16) {
                out.push(b);
                i += 3;
                continue;
            }
        }
        out.push(bytes[i]);
        i += 1;
    }
    String::from_utf8_lossy(&out).into_owned()
}

pub fn authorize_url(redirect: &str, verifier: &str, state: &str) -> String {
    let challenge = base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(Sha256::digest(verifier.as_bytes()));
    let items = [
        ("code", "true"),
        ("client_id", CLIENT_ID),
        ("response_type", "code"),
        ("redirect_uri", redirect),
        ("scope", SCOPE),
        ("code_challenge", &challenge),
        ("code_challenge_method", "S256"),
        ("state", state),
    ];
    let query: Vec<String> = items.iter().map(|(k, v)| format!("{k}={}", encode(v))).collect();
    format!("{AUTHORIZE}?{}", query.join("&"))
}

/// The page the browser comes back to: text, never markup.
pub fn page(message: &str) -> String {
    let escaped = message.replace('&', "&amp;").replace('<', "&lt;").replace('>', "&gt;");
    format!(
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>Claude Graft</title>\n\
         <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n\
         <style>\n:root {{ color-scheme: light dark; }}\n\
         body {{ font: 15px system-ui, sans-serif; margin: 0; min-height: 100vh;\n\
                display: grid; place-items: center; background: Canvas; color: CanvasText; }}\n\
         p {{ max-width: 28em; padding: 0 24px; line-height: 1.45; text-align: center; }}\n\
         </style></head><body><p>{escaped}</p></body></html>"
    )
}

/// One sign-in under way: a listener on both loopback addresses at one port,
/// since the redirect names `localhost` and which of the two a browser tries
/// first is the browser's business.
pub struct SignIn {
    listeners: Vec<TcpListener>,
    pub port: u16,
    verifier: String,
    state: String,
    /// The address for the browser.
    pub url: String,
    cancelled: Arc<AtomicBool>,
}

impl SignIn {
    pub fn begin() -> Result<SignIn, Failure> {
        let first = TcpListener::bind(SocketAddr::from((Ipv4Addr::LOCALHOST, 0))).map_err(|_| Failure::Listener)?;
        let port = first.local_addr().map_err(|_| Failure::Listener)?.port();
        let mut listeners = vec![first];
        if let Ok(second) = TcpListener::bind(SocketAddr::from((Ipv6Addr::LOCALHOST, port))) {
            listeners.push(second);
        }
        for listener in &listeners {
            listener.set_nonblocking(true).map_err(|_| Failure::Listener)?;
        }
        let verifier = random_token();
        let state = random_token();
        let url = authorize_url(&Self::redirect_for(port), &verifier, &state);
        Ok(SignIn { listeners, port, verifier, state, url, cancelled: Arc::new(AtomicBool::new(false)) })
    }

    fn redirect_for(port: u16) -> String {
        format!("http://localhost:{port}/callback")
    }

    /// Set from the window to give up; `wait` notices within a quarter second.
    pub fn canceller(&self) -> Arc<AtomicBool> {
        self.cancelled.clone()
    }

    /// Blocks until the browser comes back, the sign-in is cancelled or ten
    /// minutes pass. `own_account` is the account of the Claude this is for,
    /// which has nothing to add to itself. Only ever from a button: sealing
    /// the login may need the keyring, and it is a person waiting on the
    /// answer who is asked.
    pub fn wait(&self, own_account: Option<&str>, prompting: Prompting) -> Result<PoolAccount, Failure> {
        let deadline = Instant::now() + PATIENCE;
        loop {
            if self.cancelled.load(Ordering::SeqCst) {
                return Err(Failure::Cancelled);
            }
            if Instant::now() >= deadline {
                return Err(Failure::TimedOut);
            }
            let Some(mut connection) = self.accept() else {
                std::thread::sleep(Duration::from_millis(250));
                continue;
            };
            let Some(query) = callback_query(&mut connection) else {
                respond(&mut connection, 404, None);
                continue;
            };
            // Anything without the state this sign-in opened with came from
            // somewhere else: a stale tab, or a page poking at the port.
            if query.get("state") != Some(&self.state) {
                respond(&mut connection, 400, Some(&page("This sign-in link is out of date. Start again from Claude Graft.")));
                continue;
            }
            let Some(code) = query.get("code") else {
                let failure = Failure::Declined(query.get("error_description").or(query.get("error")).cloned());
                respond(&mut connection, 200, Some(&page(&failure.message())));
                return Err(failure);
            };
            let result = self.finish(code, own_account, prompting);
            let message = match &result {
                Ok(account) => format!(
                    "{} is signed in. You can close this tab and go back to Claude Graft.",
                    account.email.as_deref().unwrap_or("The account")
                ),
                Err(failure) => failure.message(),
            };
            respond(&mut connection, 200, Some(&page(&message)));
            return result;
        }
    }

    fn accept(&self) -> Option<TcpStream> {
        for listener in &self.listeners {
            if let Ok((stream, _)) = listener.accept() {
                let _ = stream.set_nonblocking(false);
                let _ = stream.set_read_timeout(Some(Duration::from_secs(5)));
                return Some(stream);
            }
        }
        None
    }

    fn finish(&self, code: &str, own_account: Option<&str>, prompting: Prompting) -> Result<PoolAccount, Failure> {
        let body = json!({
            "grant_type": "authorization_code",
            "code": code,
            "redirect_uri": Self::redirect_for(self.port),
            "client_id": CLIENT_ID,
            "code_verifier": self.verifier,
            "state": self.state,
            "expires_in": LIFETIME,
        });
        let (status, payload) = post_json(EXCHANGE_HOST, EXCHANGE_PATH, &serde_json::to_vec(&body).unwrap_or_default())
            .unwrap_or((0, Vec::new()));
        let object: Option<Value> = serde_json::from_slice(&payload).ok();
        account_from(status, object.as_ref(), own_account, prompting, now())
    }
}

/// What the exchange's answer makes: an account to lend, or why not.
pub fn account_from(
    status: u16,
    object: Option<&Value>,
    own_account: Option<&str>,
    prompting: Prompting,
    now: Time,
) -> Result<PoolAccount, Failure> {
    if status != 200 {
        let reason = object
            .and_then(|o| o.get("error_description").and_then(Value::as_str).or_else(|| o["error"]["message"].as_str()))
            .map(str::to_string);
        return Err(Failure::Refused(status, reason));
    }
    let object = object.ok_or(Failure::Unreadable)?;
    let token = object.get("access_token").and_then(Value::as_str).filter(|t| !t.is_empty()).ok_or(Failure::Unreadable)?;
    let uuid = object["account"]["uuid"].as_str().map(str::to_string);
    if uuid.is_some() && uuid.as_deref() == own_account {
        return Err(Failure::OwnAccount);
    }
    let sealer = Sealer::for_graft(prompting).map_err(|_| Failure::Keychain)?;
    let sealed = sealer.seal(token.as_bytes());
    let lifetime = object.get("expires_in").and_then(Value::as_f64).unwrap_or(LIFETIME as f64);
    let scopes = object
        .get("scope")
        .and_then(Value::as_str)
        .map(|s| s.split(' ').filter(|x| !x.is_empty()).map(str::to_string).collect())
        .unwrap_or_else(|| vec![SCOPE.to_string()]);
    let id = format!("{ACCOUNT_PREFIX}{}", uuid.clone().unwrap_or_else(|| uuid::Uuid::new_v4().to_string()));
    Ok(PoolAccount {
        id,
        account_uuid: uuid,
        email: object["account"]["email_address"].as_str().map(str::to_string),
        organization_name: object["organization"]["name"].as_str().map(str::to_string),
        scopes,
        expires: now + lifetime,
        added: now,
        sealed,
        icon_preset: None,
    })
}

/// The query of a `GET /callback`, or `None` for any other request: a browser
/// asks for a favicon on the same port.
pub fn callback_query(connection: &mut impl Read) -> Option<BTreeMap<String, String>> {
    let mut collected = Vec::new();
    let mut buffer = [0u8; 4096];
    while collected.len() < 16_384 {
        match connection.read(&mut buffer) {
            Ok(0) | Err(_) => break,
            Ok(count) => collected.extend_from_slice(&buffer[..count]),
        }
        if crate::contains(&collected, b"\r\n\r\n") {
            break;
        }
    }
    let head = String::from_utf8_lossy(&collected);
    let line = head.split("\r\n").next()?;
    let mut parts = line.split(' ');
    if parts.next()? != "GET" {
        return None;
    }
    let target = parts.next()?;
    let (path, query) = target.split_once('?').unwrap_or((target, ""));
    if path != "/callback" {
        return None;
    }
    let mut found = BTreeMap::new();
    for item in query.split('&').filter(|i| !i.is_empty()) {
        let (name, value) = item.split_once('=').unwrap_or((item, ""));
        found.entry(decode(name)).or_insert_with(|| decode(value));
    }
    Some(found)
}

pub fn respond(connection: &mut impl Write, status: u16, page: Option<&str>) {
    let body = page.unwrap_or("").as_bytes();
    let reason = match status {
        200 => "OK",
        400 => "Bad Request",
        _ => "Not Found",
    };
    let head = format!(
        "HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {}\r\n\
         Cache-Control: no-store\r\nConnection: close\r\n\r\n",
        body.len()
    );
    let _ = connection.write_all(head.as_bytes());
    let _ = connection.write_all(body);
}

/// One POST over TLS, answered with its status and body. Nothing is cached
/// and nothing is kept: the connection is the request's alone.
fn post_json(host: &str, path: &str, body: &[u8]) -> Option<(u16, Vec<u8>)> {
    let roots = rustls::RootCertStore { roots: webpki_roots::TLS_SERVER_ROOTS.to_vec() };
    let provider = Arc::new(rustls::crypto::ring::default_provider());
    let config = rustls::ClientConfig::builder_with_provider(provider)
        .with_safe_default_protocol_versions()
        .ok()?
        .with_root_certificates(roots)
        .with_no_client_auth();
    let name = rustls::pki_types::ServerName::try_from(host.to_string()).ok()?;
    let connection = rustls::ClientConnection::new(Arc::new(config), name).ok()?;
    let address = (host, 443).to_socket_addrs().ok()?.next()?;
    let socket = TcpStream::connect_timeout(&address, Duration::from_secs(30)).ok()?;
    let _ = socket.set_read_timeout(Some(Duration::from_secs(35)));
    let _ = socket.set_write_timeout(Some(Duration::from_secs(35)));
    let mut stream = rustls::StreamOwned::new(connection, socket);
    let head = format!(
        "POST {path} HTTP/1.1\r\nHost: {host}\r\nContent-Type: application/json\r\nAccept: application/json\r\n\
         Content-Length: {}\r\nConnection: close\r\n\r\n",
        body.len()
    );
    stream.write_all(head.as_bytes()).ok()?;
    stream.write_all(body).ok()?;
    let mut answer = Vec::new();
    let _ = stream.read_to_end(&mut answer);
    parse_answer(&answer)
}

/// The status and body of a whole answer, its chunks put back together.
fn parse_answer(answer: &[u8]) -> Option<(u16, Vec<u8>)> {
    let end = crate::find(answer, b"\r\n\r\n")?;
    let head = String::from_utf8_lossy(&answer[..end]);
    let status: u16 = head.split(' ').nth(1)?.parse().ok()?;
    let rest = &answer[end + 4..];
    let chunked = head.lines().any(|l| {
        let lower = l.to_ascii_lowercase();
        lower.starts_with("transfer-encoding:") && lower.contains("chunked")
    });
    if !chunked {
        return Some((status, rest.to_vec()));
    }
    let mut body = Vec::new();
    let mut at = 0;
    loop {
        let line_end = at + crate::find(&rest[at..], b"\r\n")?;
        let size_text = String::from_utf8_lossy(&rest[at..line_end]);
        let size = usize::from_str_radix(size_text.split(';').next()?.trim(), 16).ok()?;
        at = line_end + 2;
        if size == 0 {
            return Some((status, body));
        }
        body.extend_from_slice(rest.get(at..at + size)?);
        at += size + 2;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_sign_ins_secrets_are_thirty_two_random_bytes_written_the_way_a_url_can_carry_them() {
        let verifier = random_token();
        assert!(verifier.len() == 43 && !verifier.contains(['+', '/', '=']));
        assert_ne!(verifier, random_token());
    }

    #[test]
    fn an_account_is_signed_in_for_model_requests_alone_back_to_this_computer() {
        let verifier = random_token();
        let url = authorize_url("http://localhost:4321/callback", &verifier, "st");
        let (base, query) = url.split_once('?').unwrap();
        assert_eq!(base, "https://claude.com/cai/oauth/authorize");
        let items: BTreeMap<String, String> = query
            .split('&')
            .map(|i| {
                let (k, v) = i.split_once('=').unwrap();
                (k.to_string(), decode(v))
            })
            .collect();
        assert!(
            items["scope"] == "user:inference"
                && items["redirect_uri"] == "http://localhost:4321/callback"
                && items["state"] == "st"
                && items["client_id"] == CLIENT_ID
        );
        let challenge = base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(Sha256::digest(verifier.as_bytes()));
        assert!(
            items["code_challenge"] == challenge && items["code_challenge_method"] == "S256",
            "and the code that comes back is worth nothing without the secret that stayed here"
        );
        assert!(!url.contains(&verifier), "which never leaves this computer");
        assert!(!query.contains(':') && !query.contains('/'), "and nothing in a value is left for a server to parse");
    }

    #[test]
    fn the_page_the_browser_comes_back_to_shows_text_never_markup() {
        assert!(page("<b>&").contains("&lt;b&gt;&amp;"));
    }

    #[test]
    fn only_the_callback_is_read_and_its_query_is_decoded() {
        let mut asked = &b"GET /callback?code=a%2Fb&state=st&state=other HTTP/1.1\r\nHost: localhost\r\n\r\n"[..];
        let query = callback_query(&mut asked).unwrap();
        assert!(query["code"] == "a/b" && query["state"] == "st", "the first of a name counts");
        let mut favicon = &b"GET /favicon.ico HTTP/1.1\r\n\r\n"[..];
        assert!(callback_query(&mut favicon).is_none());
    }

    #[test]
    fn the_borrowing_claudes_own_account_is_refused_at_sign_in() {
        let answer = json!({"access_token": "t", "account": {"uuid": "mine"}});
        assert_eq!(account_from(200, Some(&answer), Some("mine"), Prompting::No, 0.0), Err(Failure::OwnAccount));
        let refused = json!({"error_description": "bad code"});
        assert_eq!(
            account_from(400, Some(&refused), None, Prompting::No, 0.0),
            Err(Failure::Refused(400, Some("bad code".into())))
        );
    }

    #[test]
    fn an_answer_in_chunks_is_read_whole() {
        let answer = b"HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n3\r\n{\"a\r\n4\r\n\":1}\r\n0\r\n\r\n";
        assert_eq!(parse_answer(answer), Some((200, br#"{"a":1}"#.to_vec())));
    }

    #[test]
    fn the_other_credential_a_sign_in_returns_is_never_read() {
        let source = include_str!("sign_in.rs");
        let needle = ["\"refresh", "_token\""].concat();
        assert!(!source.contains(&needle));
    }
}
