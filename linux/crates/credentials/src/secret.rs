//! The one read Graft makes of the desktop keyring: the password Chromium
//! keeps for Claude's safe storage.
//!
//! It goes to the host's Secret Service over the session bus, not through the
//! Flatpak secret portal. The portal hands each sandboxed app a keyring of its
//! own, and Claude's password is in the desktop's.

use zbus::blocking::{Connection, Proxy};
use zvariant::{OwnedObjectPath, OwnedValue, Value};
use std::collections::HashMap;

const SERVICE: &str = "org.freedesktop.secrets";
const SERVICE_PATH: &str = "/org/freedesktop/secrets";
const SERVICE_IFACE: &str = "org.freedesktop.Secret.Service";
const PROMPT_IFACE: &str = "org.freedesktop.Secret.Prompt";

/// What one read of the keyring came back with.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum KeyRead {
    Password(Vec<u8>),
    /// No such item at all: Claude has never stored one on this machine, or
    /// there is no keyring and Claude uses the fixed `v10` key.
    Missing,
    /// The item is there and the keyring holding it is locked.
    Shut,
}

/// The attribute sets Chromium has filed the password under. Electron names
/// the application after the app, and the attribute's spelling has changed
/// across Chromium versions; `GRAFT_SAFE_STORAGE_APP` names another for a
/// package that renamed its app.
fn queries() -> Vec<HashMap<&'static str, String>> {
    let mut apps: Vec<String> = vec!["Claude".into(), "claude".into(), "Claude Desktop".into()];
    if let Ok(extra) = std::env::var("GRAFT_SAFE_STORAGE_APP") {
        if !extra.is_empty() {
            apps.insert(0, extra);
        }
    }
    let mut out = Vec::new();
    for schema in ["chrome_libsecret_os_crypt_password_v2", "chrome_libsecret_os_crypt_password"] {
        for app in &apps {
            let mut q = HashMap::new();
            q.insert("xdg:schema", schema.to_string());
            q.insert("application", app.clone());
            out.push(q);
        }
    }
    out
}

pub fn read_password(allow_dialog: bool) -> KeyRead {
    let Ok(conn) = Connection::session() else { return KeyRead::Missing };
    let Ok(service) = Proxy::new(&conn, SERVICE, SERVICE_PATH, SERVICE_IFACE) else {
        return KeyRead::Missing;
    };
    let mut locked_seen: Vec<OwnedObjectPath> = Vec::new();
    for query in queries() {
        let found: Result<(Vec<OwnedObjectPath>, Vec<OwnedObjectPath>), _> =
            service.call("SearchItems", &(query,));
        let Ok((unlocked, locked)) = found else { continue };
        if let Some(item) = unlocked.first() {
            if let Some(password) = secret_of(&conn, &service, item) {
                return KeyRead::Password(password);
            }
        }
        locked_seen.extend(locked);
    }
    if locked_seen.is_empty() {
        return KeyRead::Missing;
    }
    if !allow_dialog {
        return KeyRead::Shut;
    }
    if !unlock(&conn, &service, &locked_seen) {
        return KeyRead::Shut;
    }
    for item in &locked_seen {
        if let Some(password) = secret_of(&conn, &service, item) {
            return KeyRead::Password(password);
        }
    }
    KeyRead::Shut
}

fn secret_of(conn: &Connection, service: &Proxy<'_>, item: &OwnedObjectPath) -> Option<Vec<u8>> {
    let _ = conn;
    let opened: (OwnedValue, OwnedObjectPath) =
        service.call("OpenSession", &("plain", Value::from(""))).ok()?;
    let session = opened.1;
    let secrets: HashMap<OwnedObjectPath, (OwnedObjectPath, Vec<u8>, Vec<u8>, String)> =
        service.call("GetSecrets", &(vec![item.clone()], session.clone())).ok()?;
    let _ = Proxy::new(conn, SERVICE, session.as_str(), "org.freedesktop.Secret.Session")
        .and_then(|p| p.call::<_, _, ()>("Close", &()));
    secrets.get(item).map(|s| s.2.clone()).filter(|p| !p.is_empty())
}

/// Ask the keyring to unlock, which is what shows the desktop's dialog, and
/// wait for the answer.
fn unlock(conn: &Connection, service: &Proxy<'_>, items: &[OwnedObjectPath]) -> bool {
    let answer: Result<(Vec<OwnedObjectPath>, OwnedObjectPath), _> =
        service.call("Unlock", &(items.to_vec(),));
    let Ok((unlocked, prompt)) = answer else { return false };
    if !unlocked.is_empty() {
        return true;
    }
    if prompt.as_str() == "/" {
        return false;
    }
    let Ok(proxy) = Proxy::new(conn, SERVICE, prompt.as_str(), PROMPT_IFACE) else { return false };
    let Ok(mut completed) = proxy.receive_signal("Completed") else { return false };
    if proxy.call::<_, _, ()>("Prompt", &("",)).is_err() {
        return false;
    }
    match completed.next() {
        Some(message) => {
            let body = message.body();
            let parsed: Result<(bool, OwnedValue), _> = body.deserialize();
            matches!(parsed, Ok((false, _)))
        }
        None => false,
    }
}
