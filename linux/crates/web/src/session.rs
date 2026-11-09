//! Asks claude.ai for one account's artifacts, projects and chats from inside
//! a page of claude.ai.
//!
//! The account's sign-in is a cookie in a WebKit network session of its own,
//! and the requests go out from a claude.ai page loaded into that session, the
//! way the site's own pages make them. Graft never reads the cookie, never
//! copies it into a request of its own, and has nowhere to send it but
//! claude.ai: the session is not shared with any other site, and the only page
//! ever loaded into it outside the sign-in window is claude.ai's. Nothing here
//! touches the session's cookie manager.

use crate::api::{self, ACCOUNT_PATH, HOST, ORIGIN};
use crate::files;
use futures_channel::oneshot;
use graft_core::diagnostics;
use gtk::glib;
use serde_json::{json, Value};
use std::cell::RefCell;
use std::collections::HashMap;
use std::rc::Rc;
use std::time::Duration;
use uuid::Uuid;
use webkit::prelude::*;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Failure {
    /// claude.ai did not take the sign-in, or there is none.
    SignedOut,
    Unreachable,
    Unreadable,
    /// An export file larger than what is left of its budget.
    TooLarge,
}

#[derive(Debug, Clone)]
pub struct Answer {
    pub status: u16,
    pub data: Vec<u8>,
}

impl Answer {
    pub fn ok(&self) -> bool {
        (200..300).contains(&self.status)
    }
}

/// The isolated world every request runs in, so nothing the page's own script
/// has done to `fetch` stands between the request and claude.ai.
const WORLD: &str = "claude-graft";

/// How long a page may take to load before the requests waiting on it are
/// told claude.ai did not answer. A page that never finishes and never fails
/// would otherwise hold them for good.
pub const LOAD_LIMIT: Duration = Duration::from_secs(30);

thread_local! {
    static STORES: RefCell<HashMap<Uuid, webkit::NetworkSession>> = RefCell::new(HashMap::new());
}

/// Each account its own network session, kept in a folder of Graft's, so two
/// sign-ins never meet. One object per account for the life of the process:
/// the sign-in window and the hidden page share it, and two sessions opened on
/// one folder would each think the other's files were theirs.
pub fn network_session(account: Uuid) -> webkit::NetworkSession {
    STORES.with(|stores| {
        stores
            .borrow_mut()
            .entry(account)
            .or_insert_with(|| {
                let data = files::store_data_folder(account);
                let cache = files::store_cache_folder(account);
                let _ = std::fs::create_dir_all(&data);
                let _ = std::fs::create_dir_all(&cache);
                let session = webkit::NetworkSession::new(
                    Some(&graft_core::paths::text(&data)),
                    Some(&graft_core::paths::text(&cache)),
                );
                // WebKit would otherwise keep a password typed into an HTTP
                // sign-in in the desktop's keyring, which is a credential
                // leaving the store it belongs to.
                session.set_persistent_credential_storage_enabled(false);
                session
            })
            .clone()
    })
}

/// WebKit's own default is already a Safari's, which is what claude.ai's
/// sign-in page offers its full set of ways in to; it is named here so a
/// change in WebKit's default cannot change what claude.ai is told.
pub const USER_AGENT: &str =
    "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15";

pub fn web_view(account: Uuid) -> webkit::WebView {
    let view = webkit::WebView::builder().network_session(&network_session(account)).build();
    if let Some(settings) = WebViewExt::settings(&view) {
        settings.set_user_agent(Some(USER_AGENT));
    }
    view
}

/// Lets go of an account's store and removes its folder. WebKit is asked to
/// clear what the store holds first, through the session itself, so the
/// sign-in is gone even if a file is still held open when the folder goes.
/// A folder that will not go is written down and tried again; one that still
/// will not is left to the next launch, which removes every store no account
/// is listed under.
pub async fn forget(account: Uuid) {
    let session = STORES.with(|stores| stores.borrow_mut().remove(&account));
    if let Some(session) = session {
        if let Some(manager) = session.website_data_manager() {
            let (done, wait) = oneshot::channel();
            manager.clear(webkit::WebsiteDataTypes::ALL, glib::TimeSpan(0), None::<&gtk::gio::Cancellable>, move |_| {
                let _ = done.send(());
            });
            let _ = wait.await;
        }
        drop(session);
    }
    let folder = files::store_folder(account);
    for attempt in 1..=3u64 {
        match std::fs::remove_dir_all(&folder) {
            Ok(()) => return,
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => return,
            Err(e) => {
                diagnostics::note(
                    "artifacts.store removal failed",
                    &[("attempt", json!(attempt)), ("error", json!(e.to_string()))],
                );
                glib::timeout_future(Duration::from_secs(2 * attempt)).await;
            }
        }
    }
}

struct Inner {
    view: Option<webkit::WebView>,
    /// Which page is the current one. A page dropped by `reset` can still
    /// report in afterwards, and taken at its word it would mark the new page
    /// loaded before it was.
    serial: u64,
    loading: Vec<oneshot::Sender<Result<(), Failure>>>,
    loaded: bool,
    /// Set when the account is signed out or replaced. Its store is about to
    /// be removed, and a page made on it now would keep the store, and the
    /// sign-in in it, alive.
    closed: bool,
    in_flight: usize,
    drained: Vec<oneshot::Sender<()>>,
}

pub struct WebSession {
    pub account: Uuid,
    inner: RefCell<Inner>,
}

impl WebSession {
    pub fn new(account: Uuid) -> Rc<WebSession> {
        Rc::new(WebSession {
            account,
            inner: RefCell::new(Inner {
                view: None,
                serial: 0,
                loading: Vec::new(),
                loaded: false,
                closed: false,
                in_flight: 0,
                drained: Vec::new(),
            }),
        })
    }

    pub fn is_closed(&self) -> bool {
        self.inner.borrow().closed
    }

    pub async fn send(
        self: &Rc<Self>,
        method: &str,
        path: &str,
        body: Option<&[u8]>,
        headers: &[(String, String)],
    ) -> Result<Answer, Failure> {
        let answer = self.send_once(method, path, body, headers).await?;
        if api::means_signed_out(answer.status, path) {
            return Err(Failure::SignedOut);
        }
        if api::needs_account_check(answer.status, path) {
            // Signed out there is signed out; otherwise the 403 goes back as
            // the refusal it was.
            let account = self.send_once("GET", ACCOUNT_PATH, None, &[]).await?;
            if api::means_signed_out(account.status, ACCOUNT_PATH) {
                return Err(Failure::SignedOut);
            }
        }
        Ok(answer)
    }

    async fn send_once(
        self: &Rc<Self>,
        method: &str,
        path: &str,
        body: Option<&[u8]>,
        headers: &[(String, String)],
    ) -> Result<Answer, Failure> {
        self.ready().await?;
        let view = self.inner.borrow().view.clone().ok_or(Failure::Unreachable)?;
        // The artifact service answers 404 without its own headers; the rest
        // of claude.ai's API is asked as its pages ask it, without them.
        let mut all: serde_json::Map<String, Value> =
            api::headers_for(path).into_iter().map(|(k, v)| (k, Value::String(v))).collect();
        for (k, v) in headers {
            all.insert(k.clone(), Value::String(v.clone()));
        }
        if body.is_some() {
            all.insert("Content-Type".into(), json!("application/json"));
        }
        let script = r#"
            const response = await fetch(path, {method, credentials: "same-origin",
                                                headers: JSON.parse(headers),
                                                body: body === "" ? undefined : body});
            return {status: response.status, body: await response.text()};
        "#;
        let arguments = arguments(&[
            ("path", path.to_string()),
            ("method", method.to_string()),
            ("headers", Value::Object(all).to_string()),
            ("body", body.map(|b| String::from_utf8_lossy(b).into_owned()).unwrap_or_default()),
        ]);
        let _hold = self.hold();
        let result = view.call_async_javascript_function_future(script, Some(&arguments), Some(WORLD), None).await;
        let answer = parse_result(result)?;
        let status = answer.get("status").and_then(Value::as_u64).ok_or(Failure::Unreadable)? as u16;
        let text = answer.get("body").and_then(Value::as_str).ok_or(Failure::Unreadable)?;
        Ok(Answer { status, data: text.as_bytes().to_vec() })
    }

    /// One of an artifact's files, read from the host its viewer reads it
    /// from. No cookie goes with it: the file host is not claude.ai, and the
    /// token in the address is all it takes, as it is for claude.ai's own
    /// viewer. The bytes come back as base64, the one way binary survives the
    /// trip out of a page.
    ///
    /// `limit` is what is left of the export's budget, and it is held to in
    /// the page, before the file is encoded there: a file read whole first and
    /// measured after can take the page's process down with it, and that
    /// leaves the account answering nothing.
    pub async fn download(self: &Rc<Self>, url: &str, limit: usize) -> Result<Answer, Failure> {
        self.ready().await?;
        let view = self.inner.borrow().view.clone().ok_or(Failure::Unreachable)?;
        let script = r#"
            limit = Number(limit);
            const response = await fetch(url, {credentials: "omit", cache: "no-store",
                                               referrerPolicy: "no-referrer"});
            if (!response.ok) return {status: response.status, body: ""};
            const declared = Number(response.headers.get("Content-Length") || 0);
            if (declared > limit) return {status: response.status, body: "", tooLarge: true};
            const reader = response.body.getReader();
            const chunks = [];
            let size = 0;
            for (;;) {
                const {done, value} = await reader.read();
                if (done) break;
                size += value.length;
                if (size > limit) {
                    await reader.cancel();
                    return {status: response.status, body: "", tooLarge: true};
                }
                chunks.push(value);
            }
            const bytes = new Uint8Array(size);
            let offset = 0;
            for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; }
            let binary = "";
            for (let i = 0; i < bytes.length; i += 0x8000) {
                binary += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
            }
            return {status: response.status, body: btoa(binary)};
        "#;
        let arguments = arguments(&[("url", url.to_string()), ("limit", limit.to_string())]);
        let _hold = self.hold();
        let result = view.call_async_javascript_function_future(script, Some(&arguments), Some(WORLD), None).await;
        let answer = parse_result(result)?;
        if answer.get("tooLarge").and_then(Value::as_bool) == Some(true) {
            return Err(Failure::TooLarge);
        }
        let status = answer.get("status").and_then(Value::as_u64).ok_or(Failure::Unreadable)? as u16;
        let text = answer.get("body").and_then(Value::as_str).ok_or(Failure::Unreadable)?;
        use base64::Engine;
        let data = base64::engine::general_purpose::STANDARD.decode(text).map_err(|_| Failure::Unreadable)?;
        Ok(Answer { status, data })
    }

    pub async fn identity(self: &Rc<Self>) -> Result<api::Identity, Failure> {
        let answer = self.send("GET", ACCOUNT_PATH, None, &[]).await?;
        if answer.status != 200 {
            return Err(Failure::SignedOut);
        }
        api::identity(&answer.data).ok_or(Failure::Unreadable)
    }

    /// Drops the page, so the next request loads a fresh one. A sign-in made
    /// since is only seen by a page loaded after it.
    ///
    /// Requests already waiting for the old page wait for the new one instead.
    /// Dropped with the page, they were never answered, and the next request
    /// queued behind them never started a page of its own, so every request
    /// for the account hung until Graft quit.
    pub fn reset(self: &Rc<Self>) {
        let restart = {
            let mut inner = self.inner.borrow_mut();
            inner.view = None;
            inner.serial += 1;
            inner.loaded = false;
            !inner.loading.is_empty() && !inner.closed
        };
        if restart {
            self.start_loading();
        }
    }

    /// For good. Whatever is waiting is told the account is signed out, and
    /// nothing asked of this session afterwards loads a page.
    pub fn close(&self) {
        let waiting = {
            let mut inner = self.inner.borrow_mut();
            inner.closed = true;
            inner.view = None;
            inner.serial += 1;
            inner.loaded = false;
            std::mem::take(&mut inner.loading)
        };
        for waiter in waiting {
            let _ = waiter.send(Err(Failure::SignedOut));
        }
    }

    /// Returns once every request already sent from the page has come back,
    /// which is when the last thing holding the page lets go of it.
    pub async fn released(&self) {
        let wait = {
            let mut inner = self.inner.borrow_mut();
            if inner.in_flight == 0 {
                return;
            }
            let (done, wait) = oneshot::channel();
            inner.drained.push(done);
            wait
        };
        let _ = wait.await;
    }

    fn hold(self: &Rc<Self>) -> Hold {
        self.inner.borrow_mut().in_flight += 1;
        Hold(Rc::clone(self))
    }

    fn request_ended(&self) {
        let waiting = {
            let mut inner = self.inner.borrow_mut();
            inner.in_flight -= 1;
            if inner.in_flight > 0 {
                return;
            }
            std::mem::take(&mut inner.drained)
        };
        for waiter in waiting {
            let _ = waiter.send(());
        }
    }

    async fn ready(self: &Rc<Self>) -> Result<(), Failure> {
        let wait = {
            let mut inner = self.inner.borrow_mut();
            if inner.closed {
                return Err(Failure::SignedOut);
            }
            if inner.loaded && inner.view.is_some() {
                return Ok(());
            }
            let (done, wait) = oneshot::channel();
            inner.loading.push(done);
            (inner.loading.len() == 1, wait)
        };
        if wait.0 {
            self.start_loading();
        }
        wait.1.await.unwrap_or(Err(Failure::Unreachable))
    }

    fn start_loading(self: &Rc<Self>) {
        let view = web_view(self.account);
        let serial = {
            let mut inner = self.inner.borrow_mut();
            inner.serial += 1;
            inner.view = Some(view.clone());
            inner.loaded = false;
            inner.serial
        };
        let weak = Rc::downgrade(self);
        view.connect_load_changed(move |view, event| {
            if event != webkit::LoadEvent::Finished {
                return;
            }
            let Some(this) = weak.upgrade() else { return };
            // A challenge page or a redirect elsewhere is not a claude.ai page
            // to ask from, and a request from it would carry nothing.
            let on_claude = view.uri().is_some_and(|u| host_of(&u) == Some(HOST));
            this.finish_loading(serial, if on_claude { None } else { Some(Failure::SignedOut) });
        });
        let weak = Rc::downgrade(self);
        view.connect_load_failed(move |_, _, _, _| {
            if let Some(this) = weak.upgrade() {
                this.finish_loading(serial, Some(Failure::Unreachable));
            }
            false
        });
        // A page whose process died still reads as loaded, and every request
        // sent to it fails as though claude.ai were down until something else
        // drops it. So it is dropped here, and the next request loads another.
        let weak = Rc::downgrade(self);
        view.connect_web_process_terminated(move |_, _| {
            let Some(this) = weak.upgrade() else { return };
            if this.inner.borrow().serial != serial {
                return;
            }
            diagnostics::note("artifacts.page ended", &[]);
            if this.inner.borrow().loaded {
                this.reset();
            } else {
                this.finish_loading(serial, Some(Failure::Unreachable));
            }
        });
        view.load_uri(&format!("{ORIGIN}{ACCOUNT_PATH}"));
        let weak = Rc::downgrade(self);
        glib::MainContext::default().spawn_local(async move {
            glib::timeout_future(LOAD_LIMIT).await;
            let Some(this) = weak.upgrade() else { return };
            let stuck = {
                let inner = this.inner.borrow();
                inner.serial == serial && !inner.loaded
            };
            if stuck {
                this.finish_loading(serial, Some(Failure::Unreachable));
            }
        });
    }

    /// Only the page now in use may finish a load.
    fn finish_loading(&self, serial: u64, error: Option<Failure>) {
        let waiting = {
            let mut inner = self.inner.borrow_mut();
            if inner.serial != serial || inner.view.is_none() {
                return;
            }
            inner.loaded = error.is_none();
            if error.is_some() {
                inner.view = None;
                inner.serial += 1;
            }
            std::mem::take(&mut inner.loading)
        };
        for waiter in waiting {
            let _ = waiter.send(match error {
                Some(e) => Err(e),
                None => Ok(()),
            });
        }
    }
}

/// A request counted as sent from the page until it comes back, however it
/// comes back.
struct Hold(Rc<WebSession>);

impl Drop for Hold {
    fn drop(&mut self) {
        self.0.request_ended();
    }
}

pub(crate) fn arguments(pairs: &[(&str, String)]) -> glib::Variant {
    let dict = glib::VariantDict::new(None);
    for (key, value) in pairs {
        dict.insert_value(key, &value.to_variant());
    }
    dict.end()
}

pub(crate) fn parse_result(
    result: Result<webkit::javascriptcore::Value, glib::Error>,
) -> Result<serde_json::Map<String, Value>, Failure> {
    let value = result.map_err(|_| Failure::Unreachable)?;
    let json = value.to_json(0).ok_or(Failure::Unreadable)?;
    match serde_json::from_str::<Value>(&json) {
        Ok(Value::Object(map)) => Ok(map),
        _ => Err(Failure::Unreadable),
    }
}

pub fn host_of(uri: &str) -> Option<&str> {
    let rest = uri.strip_prefix("https://").or_else(|| uri.strip_prefix("http://"))?;
    let end = rest.find(['/', '?', '#']).unwrap_or(rest.len());
    let host = &rest[..end];
    let host = host.rsplit('@').next().unwrap_or(host);
    Some(host.split(':').next().unwrap_or(host))
}
