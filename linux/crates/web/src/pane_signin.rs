//! Signs an account in through the browser so it can lend its usage without a
//! Claude of its own: the same sign-in Claude Code's long-lived login uses, a
//! year-long token for model requests and nothing else.
//!
//! The browser comes back to a listener on loopback with a one-time code, and
//! the code is exchanged for the token at Anthropic's sign-in service. That
//! exchange is the one request here that does not go to `api.anthropic.com`,
//! and what it sends is the code, never a token. The service hands back a
//! second, renewing token beside it, which is never read: a year is the whole
//! of what is wanted, and holding one would be holding a login that renews
//! itself.
//!
//! What to do with the login once it is sealed belongs to the usage pool; this
//! hands it back as a `PaneLogin` and keeps nothing.

use base64::Engine;
use gtk::glib;
use serde_json::{json, Map, Value};
use sha2::{Digest, Sha256};
use std::io::{Read, Write};
use std::net::{Ipv4Addr, Ipv6Addr, SocketAddr, TcpListener, TcpStream};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant, SystemTime};

pub const CLIENT_ID: &str = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
pub const AUTHORIZE: &str = "https://claude.com/cai/oauth/authorize";
pub const EXCHANGE: &str = "https://platform.claude.com/v1/oauth/token";
pub const SCOPE: &str = "user:inference";
pub const LIFETIME: u64 = 365 * 24 * 60 * 60;
/// Long enough to sign in from scratch, two-step verification included.
pub const PATIENCE: Duration = Duration::from_secs(10 * 60);
/// What the usage pool names a signed-in account by, before its UUID.
pub const ACCOUNT_PREFIX: &str = "account:";

/// A login signed in here, sealed the way the profile's own token cache
/// seals one, ready for the usage pool to keep.
#[derive(Debug, Clone, PartialEq)]
pub struct PaneLogin {
    pub id: String,
    pub account_uuid: Option<String>,
    pub email: Option<String>,
    pub organization_name: Option<String>,
    pub scopes: Vec<String>,
    pub expires: SystemTime,
    pub added: SystemTime,
    pub sealed: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Failure {
    Cancelled,
    TimedOut,
    Listener,
    Declined(Option<String>),
    Refused(u16, Option<String>),
    Unreadable,
    OwnAccount,
    Keyring,
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
            Failure::Keyring => "Graft needs the keyring to keep this login. Try again and allow it.".into(),
        }
    }
}

/// How the login is sealed: the caller's, since the key is the profile's and
/// asking the keyring is the usage pool's business. `interactive` is whether a
/// person pressed the button that started this, and so may be asked.
pub type Seal = Box<dyn Fn(&[u8], bool) -> Option<String> + Send>;

/// Thirty-two random bytes, written the way an address can carry them.
pub fn random_token() -> String {
    let mut bytes = [0u8; 32];
    let mut filled = 0;
    while filled < bytes.len() {
        let rest = &mut bytes[filled..];
        // SAFETY: the pointer and length name the unfilled tail of `bytes`.
        let got = unsafe { libc::getrandom(rest.as_mut_ptr().cast(), rest.len(), 0) };
        if got > 0 {
            filled += got as usize;
        }
    }
    base64url(&bytes)
}

fn base64url(bytes: &[u8]) -> String {
    base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(bytes)
}

pub fn challenge(verifier: &str) -> String {
    base64url(&Sha256::digest(verifier.as_bytes()))
}

pub fn authorize_url(redirect: &str, verifier: &str, state: &str) -> String {
    let challenge = challenge(verifier);
    let items = [
        ("code", "true"),
        ("client_id", CLIENT_ID),
        ("response_type", "code"),
        ("redirect_uri", redirect),
        ("scope", SCOPE),
        ("code_challenge", challenge.as_str()),
        ("code_challenge_method", "S256"),
        ("state", state),
    ];
    // `+` would be read as a space, and a colon or slash left bare in a value
    // is a thing some servers parse and others do not.
    let query: Vec<String> = items.iter().map(|(k, v)| format!("{k}={}", strict(v))).collect();
    format!("{AUTHORIZE}?{}", query.join("&"))
}

fn strict(text: &str) -> String {
    let mut out = String::new();
    for byte in text.bytes() {
        if byte.is_ascii_alphanumeric() || b"!$'()*,-.;@_~".contains(&byte) {
            out.push(byte as char);
        } else {
            out.push_str(&format!("%{byte:02X}"));
        }
    }
    out
}

/// The page the browser comes back to: text, never markup.
pub fn page(message: &str) -> String {
    let escaped = message.replace('&', "&amp;").replace('<', "&lt;").replace('>', "&gt;");
    format!(
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>Claude Graft</title>\
         <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\
         <style>:root {{ color-scheme: light dark; }}\
         body {{ font: 15px system-ui, sans-serif; margin: 0; min-height: 100vh; display: grid; \
         place-items: center; background: Canvas; color: CanvasText; }}\
         p {{ max-width: 28em; padding: 0 24px; line-height: 1.45; text-align: center; }}</style>\
         </head><body><p>{escaped}</p></body></html>"
    )
}

/// Listens on both loopback addresses at one port. The redirect names
/// `localhost`, and which of the two a browser tries first is the browser's
/// business.
pub struct Loopback {
    pub port: u16,
    listeners: Vec<TcpListener>,
}

impl Loopback {
    pub fn new() -> Option<Loopback> {
        let first = TcpListener::bind(SocketAddr::from((Ipv4Addr::LOCALHOST, 0))).ok()?;
        let port = first.local_addr().ok()?.port();
        let mut listeners = vec![first];
        if let Ok(second) = TcpListener::bind(SocketAddr::from((Ipv6Addr::LOCALHOST, port))) {
            listeners.push(second);
        }
        for listener in &listeners {
            listener.set_nonblocking(true).ok()?;
        }
        Some(Loopback { port, listeners })
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
}

/// The query of a `GET /callback`, or `None` for any other request: a browser
/// asks for a favicon on the same port.
pub fn callback_query(head: &str) -> Option<Map<String, Value>> {
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
    let mut out = Map::new();
    for pair in query.split('&').filter(|p| !p.is_empty()) {
        let (key, value) = pair.split_once('=').unwrap_or((pair, ""));
        let (Some(key), Some(value)) = (unescape(key), unescape(value)) else { continue };
        out.entry(key).or_insert(Value::String(value));
    }
    Some(out)
}

fn unescape(text: &str) -> Option<String> {
    let bytes = text.as_bytes();
    let mut out = Vec::new();
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == b'%' {
            out.push(u8::from_str_radix(std::str::from_utf8(bytes.get(i + 1..i + 3)?).ok()?, 16).ok()?);
            i += 3;
        } else {
            out.push(bytes[i]);
            i += 1;
        }
    }
    String::from_utf8(out).ok()
}

fn read_head(stream: &mut TcpStream) -> String {
    let mut collected = Vec::new();
    let mut buffer = [0u8; 4096];
    while collected.len() < 16_384 {
        match stream.read(&mut buffer) {
            Ok(0) | Err(_) => break,
            Ok(n) => {
                collected.extend_from_slice(&buffer[..n]);
                if collected.windows(4).any(|w| w == b"\r\n\r\n") {
                    break;
                }
            }
        }
    }
    String::from_utf8_lossy(&collected).into_owned()
}

fn respond(stream: &mut TcpStream, status: u16, body: Option<&str>) {
    let body = body.unwrap_or("");
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
    let _ = stream.write_all(head.as_bytes());
    let _ = stream.write_all(body.as_bytes());
}

/// What a sign-in already under way can be asked to stop.
#[derive(Clone, Default)]
pub struct Cancel(Arc<AtomicBool>);

impl Cancel {
    pub fn cancel(&self) {
        self.0.store(true, Ordering::SeqCst);
    }

    fn is_cancelled(&self) -> bool {
        self.0.load(Ordering::SeqCst)
    }
}

/// Opens the browser at the sign-in and waits, off the main thread, for it to
/// come back. Only ever from a button: sealing the login may need the
/// keyring, and it is a person waiting on the answer who is asked.
///
/// `own_account` is the account the Claude being lent to already uses, which
/// has nothing to add to itself.
pub async fn sign_in(
    own_account: Option<String>,
    interactive: bool,
    seal: Seal,
    cancel: Cancel,
    parent: Option<&gtk::Window>,
) -> Result<PaneLogin, Failure> {
    let listener = Loopback::new().ok_or(Failure::Listener)?;
    let verifier = random_token();
    let state = random_token();
    let redirect = format!("http://localhost:{}/callback", listener.port);
    let url = authorize_url(&redirect, &verifier, &state);
    let launcher = gtk::UriLauncher::new(&url);
    if launcher.launch_future(parent).await.is_err() {
        return Err(Failure::Listener);
    }
    let (done, wait) = futures_channel::oneshot::channel();
    std::thread::spawn(move || {
        let result = run(&listener, &redirect, &verifier, &state, own_account.as_deref(), interactive, &seal, &cancel);
        let _ = done.send(result);
    });
    wait.await.unwrap_or(Err(Failure::Cancelled))
}

#[allow(clippy::too_many_arguments)]
fn run(
    listener: &Loopback,
    redirect: &str,
    verifier: &str,
    state: &str,
    own_account: Option<&str>,
    interactive: bool,
    seal: &Seal,
    cancel: &Cancel,
) -> Result<PaneLogin, Failure> {
    let deadline = Instant::now() + PATIENCE;
    loop {
        if cancel.is_cancelled() {
            return Err(Failure::Cancelled);
        }
        if Instant::now() >= deadline {
            return Err(Failure::TimedOut);
        }
        let Some(mut stream) = listener.accept() else {
            std::thread::sleep(Duration::from_millis(250));
            continue;
        };
        let head = read_head(&mut stream);
        let Some(query) = callback_query(&head) else {
            respond(&mut stream, 404, None);
            continue;
        };
        let get = |key: &str| query.get(key).and_then(Value::as_str).map(str::to_string);
        // Anything without the state this sign-in opened with came from
        // somewhere else: a stale tab, or a page poking at the port.
        if get("state").as_deref() != Some(state) {
            respond(&mut stream, 400, Some(&page("This sign-in link is out of date. Start again from Claude Graft.")));
            continue;
        }
        let Some(code) = get("code") else {
            let failure = Failure::Declined(get("error_description").or_else(|| get("error")));
            respond(&mut stream, 200, Some(&page(&failure.message())));
            return Err(failure);
        };
        let result = finish(&code, redirect, verifier, state, own_account, interactive, seal);
        match &result {
            Ok(login) => respond(
                &mut stream,
                200,
                Some(&page(&format!(
                    "{} is signed in. You can close this tab and go back to Claude Graft.",
                    login.email.clone().unwrap_or_else(|| "The account".into())
                ))),
            ),
            Err(failure) => respond(&mut stream, 200, Some(&page(&failure.message()))),
        }
        return result;
    }
}

/// The code for the token, at Anthropic's sign-in service, through the same
/// HTTP library WebKit itself uses, on this thread alone.
fn finish(
    code: &str,
    redirect: &str,
    verifier: &str,
    state: &str,
    own_account: Option<&str>,
    interactive: bool,
    seal: &Seal,
) -> Result<PaneLogin, Failure> {
    let body = json!({
        "grant_type": "authorization_code",
        "code": code,
        "redirect_uri": redirect,
        "client_id": CLIENT_ID,
        "code_verifier": verifier,
        "state": state,
        "expires_in": LIFETIME,
    });
    let context = glib::MainContext::new();
    let (status, payload) = context
        .with_thread_default(|| exchange(&body))
        .map_err(|_| Failure::Refused(0, None))?;
    let object: Option<Map<String, Value>> = serde_json::from_slice(&payload).ok();
    if status != 200 {
        let reason = object.as_ref().and_then(|o| {
            o.get("error_description")
                .and_then(Value::as_str)
                .or_else(|| o.get("error").and_then(|e| e.get("message")).and_then(Value::as_str))
                .map(str::to_string)
        });
        return Err(Failure::Refused(status, reason));
    }
    let object = object.ok_or(Failure::Unreadable)?;
    let token = object.get("access_token").and_then(Value::as_str).filter(|t| !t.is_empty()).ok_or(Failure::Unreadable)?;
    let account = object.get("account").and_then(Value::as_object);
    let uuid = account.and_then(|a| a.get("uuid")).and_then(Value::as_str).map(str::to_string);
    if uuid.is_some() && uuid.as_deref() == own_account {
        return Err(Failure::OwnAccount);
    }
    let sealed = seal(token.as_bytes(), interactive).ok_or(Failure::Keyring)?;
    let lifetime = object.get("expires_in").and_then(Value::as_f64).unwrap_or(LIFETIME as f64);
    let scopes = object
        .get("scope")
        .and_then(Value::as_str)
        .map(|s| s.split(' ').filter(|p| !p.is_empty()).map(str::to_string).collect())
        .unwrap_or_else(|| vec![SCOPE.to_string()]);
    let now = SystemTime::now();
    Ok(PaneLogin {
        id: format!("{ACCOUNT_PREFIX}{}", uuid.clone().unwrap_or_else(|| uuid::Uuid::new_v4().to_string())),
        account_uuid: uuid,
        email: account.and_then(|a| a.get("email_address")).and_then(Value::as_str).map(str::to_string),
        organization_name: object.get("organization").and_then(|o| o.get("name")).and_then(Value::as_str).map(str::to_string),
        scopes,
        expires: now + Duration::from_secs_f64(lifetime.max(0.0)),
        added: now,
        sealed,
    })
}

fn exchange(body: &Value) -> (u16, Vec<u8>) {
    use webkit::soup::prelude::*;
    let session = webkit::soup::Session::new();
    session.set_timeout(30);
    let Some(message) = webkit::soup::Message::new("POST", EXCHANGE).ok() else { return (0, Vec::new()) };
    let bytes = glib::Bytes::from_owned(body.to_string().into_bytes());
    message.set_request_body_from_bytes(Some("application/json"), Some(&bytes));
    if let Some(headers) = message.request_headers() {
        headers.append("Accept", "application/json");
    }
    let payload = session.send_and_read(&message, None::<&gtk::gio::Cancellable>);
    let status = message.status().into_glib() as u16;
    (status, payload.map(|b| b.to_vec()).unwrap_or_default())
}

use glib::translate::IntoGlib;
