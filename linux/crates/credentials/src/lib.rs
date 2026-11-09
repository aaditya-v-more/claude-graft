//! Borrows the access token Claude Desktop already holds for a profile, so
//! usage can be read for that account from Anthropic's own API.
//!
//! Deliberately narrow: only the access token is decoded, never the refresh
//! token, because Anthropic rotates those and using one would sign Claude
//! out; nothing is ever written back to Claude's login, cookies or keyring;
//! the key seals one thing, the lines in a Claude's Code environment that
//! sharing usage needs, written the way Claude writes that field itself.
//!
//! On Linux, Electron's safe storage is Chromium's: a value is `v10` or `v11`,
//! then AES-128-CBC with sixteen spaces for an IV, under a key derived with
//! one round of PBKDF2-SHA1 over the salt `saltysalt`. `v10` derives from the
//! fixed password `peanuts`, which is what Chromium falls back to on a desktop
//! with no keyring; `v11` derives from a password Chromium keeps in the
//! desktop's Secret Service, which is where the dialog can come from.

mod secret;

use base64::Engine;
use graft_core::files;
use serde_json::Value;
use std::path::Path;
use std::sync::Mutex;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

pub use secret::KeyRead;

pub const USAGE_SCOPE: &str = "user:profile";
pub const INFERENCE_SCOPE: &str = "user:inference";
const CACHE_KEYS: [&str; 2] = ["oauth:tokenCacheV2", "oauth:tokenCache"];
const IV: [u8; 16] = [b' '; 16];

/// One access token from a profile's cache.
#[derive(Debug, Clone, PartialEq)]
pub struct Token {
    pub value: String,
    pub expires: SystemTime,
    pub plan: Option<String>,
    pub scopes: Vec<String>,
}

impl Token {
    /// Two minutes of slack, so a token is not handed to a request that will
    /// arrive after it has expired.
    pub fn is_current(&self) -> bool {
        self.expires > SystemTime::now() + Duration::from_secs(120)
    }

    /// An entry whose scopes could not be read from its cache key is not ruled
    /// out: the key's shape is Claude's to change, and the service refuses the
    /// request anyway if the token really is insufficient.
    pub fn covers(&self, required: &[&str]) -> bool {
        self.scopes.is_empty() || required.iter().all(|r| self.scopes.iter().any(|s| s == r))
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Failure {
    /// The keyring is locked and this read was not allowed to ask.
    NoKeyringAccess,
    /// The keyring asked and the answer was no.
    KeyringDeclined,
    NotSignedIn,
    Expired,
    /// `config.json` is there and did not parse, which is Claude part way
    /// through renaming a new one over it. Not the same as no login: the next
    /// read finds the file whole.
    ConfigUnreadable,
}

impl Failure {
    pub fn message(self) -> &'static str {
        match self {
            Failure::NoKeyringAccess => {
                "The keyring holding Claude's login key is locked. Unlock it when it asks, and live usage starts working."
            }
            Failure::KeyringDeclined => {
                "Unlocking the keyring was declined, so usage is coming from disk. Refresh Usage asks again."
            }
            Failure::NotSignedIn => "No Claude Desktop login was found for this profile.",
            Failure::Expired => "That profile's login has expired. Open it once so Claude can renew it.",
            Failure::ConfigUnreadable => {
                "That profile's settings file could not be read just now. The next refresh tries again."
            }
        }
    }
}

impl std::fmt::Display for Failure {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(self.message())
    }
}

impl std::error::Error for Failure {}

/// Who may raise the keyring's unlock dialog on a given read. Every case tries
/// the silent read first, so none of them prompts while the keyring is open.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Prompting {
    /// Never. Whatever is on disk is shown instead.
    No,
    /// Once for the life of the process, and never again after a decline. A
    /// timer must not turn one locked keyring into a dialog every pass.
    OnceIfShut,
    /// Someone pressed something and is waiting for the answer.
    Yes,
}

/// Whether a read that nobody asked for is still allowed to open a dialog.
pub fn may_raise_dialog(prompting: Prompting, already_asked: bool, declined: bool) -> bool {
    match prompting {
        Prompting::No => false,
        Prompting::OnceIfShut => !already_asked && !declined,
        Prompting::Yes => true,
    }
}

#[derive(Default)]
struct KeyState {
    v11: Option<[u8; 16]>,
    asked_unprompted: bool,
    declined: bool,
}

/// Held across the dialog on purpose: two profiles finding the keyring shut in
/// one pass would otherwise stack two dialogs for one keyring.
static KEY: Mutex<KeyState> =
    Mutex::new(KeyState { v11: None, asked_unprompted: false, declined: false });

/// Chromium's Linux derivation: one round, not the 1003 a Mac uses.
pub fn derive(password: &[u8]) -> [u8; 16] {
    let mut key = [0u8; 16];
    pbkdf2::pbkdf2_hmac::<sha1::Sha1>(password, b"saltysalt", 1, &mut key);
    key
}

pub fn peanuts_key() -> [u8; 16] {
    derive(b"peanuts")
}

/// The `v11` key, read from the Secret Service once per process.
pub fn safe_storage_key(prompting: Prompting) -> Result<Option<[u8; 16]>, Failure> {
    let mut state = KEY.lock().unwrap_or_else(|e| e.into_inner());
    if let Some(key) = state.v11 {
        return Ok(Some(key));
    }
    match secret::read_password(false) {
        KeyRead::Password(p) => {
            let key = derive(&p);
            state.v11 = Some(key);
            return Ok(Some(key));
        }
        KeyRead::Missing => return Ok(None),
        KeyRead::Shut => {}
    }
    if !may_raise_dialog(prompting, state.asked_unprompted, state.declined) {
        return Err(if state.declined { Failure::KeyringDeclined } else { Failure::NoKeyringAccess });
    }
    if prompting == Prompting::OnceIfShut {
        state.asked_unprompted = true;
    }
    match secret::read_password(true) {
        KeyRead::Password(p) => {
            state.declined = false;
            let key = derive(&p);
            state.v11 = Some(key);
            Ok(Some(key))
        }
        KeyRead::Missing => Ok(None),
        KeyRead::Shut => {
            // The dialog was up and the answer was no. Asking again on the
            // next tick is the nagging this whole path exists to avoid.
            state.declined = true;
            Err(Failure::KeyringDeclined)
        }
    }
}

type Decryptor = cbc::Decryptor<aes::Aes128>;
type Encryptor = cbc::Encryptor<aes::Aes128>;

/// Which key a sealed value was made with.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Scheme {
    V10,
    V11,
}

pub fn scheme_of(blob: &[u8]) -> Option<Scheme> {
    match blob.get(..3) {
        Some(b"v10") => Some(Scheme::V10),
        Some(b"v11") => Some(Scheme::V11),
        _ => None,
    }
}

pub fn decrypt(blob: &[u8], key: &[u8; 16]) -> Option<Vec<u8>> {
    use cbc::cipher::{block_padding::Pkcs7, BlockDecryptMut, KeyIvInit};
    scheme_of(blob)?;
    let payload = &blob[3..];
    Decryptor::new(key.into(), &IV.into()).decrypt_padded_vec_mut::<Pkcs7>(payload).ok()
}

/// What Electron's safe storage makes of a value: the scheme's prefix, then
/// AES-128-CBC under its key with the same IV every time. One plaintext
/// therefore always seals to one value, which is what lets a launcher
/// recognise a value Graft prepared without being able to read it.
pub fn seal(plain: &[u8], key: &[u8; 16], scheme: Scheme) -> String {
    use cbc::cipher::{block_padding::Pkcs7, BlockEncryptMut, KeyIvInit};
    let sealed = Encryptor::new(key.into(), &IV.into()).encrypt_padded_vec_mut::<Pkcs7>(plain);
    let mut out = match scheme {
        Scheme::V10 => b"v10".to_vec(),
        Scheme::V11 => b"v11".to_vec(),
    };
    out.extend_from_slice(&sealed);
    base64::engine::general_purpose::STANDARD.encode(out)
}

pub fn unseal(sealed: &str, key: &[u8; 16]) -> Option<Vec<u8>> {
    let blob = base64::engine::general_purpose::STANDARD.decode(sealed).ok()?;
    decrypt(&blob, key)
}

/// The key for a blob of a given scheme. `v10` needs no keyring at all.
pub fn key_for(scheme: Scheme, prompting: Prompting) -> Result<Option<[u8; 16]>, Failure> {
    match scheme {
        Scheme::V10 => Ok(Some(peanuts_key())),
        Scheme::V11 => safe_storage_key(prompting),
    }
}

/// The scheme and key a profile seals with, judged by its own token cache,
/// for writing a value Claude will read back. `None` when it has no login yet.
pub fn sealing_key(profile: &Path, prompting: Prompting) -> Result<Option<(Scheme, [u8; 16])>, Failure> {
    let config = files::readable_config_json(profile).ok_or(Failure::ConfigUnreadable)?;
    let Some(blob) = cache_blob(&config) else { return Ok(None) };
    let Some(scheme) = scheme_of(&blob) else { return Ok(None) };
    Ok(key_for(scheme, prompting)?.map(|key| (scheme, key)))
}

fn cache_blob(config: &serde_json::Map<String, Value>) -> Option<Vec<u8>> {
    let encoded = CACHE_KEYS.iter().find_map(|k| config.get(*k).and_then(Value::as_str))?;
    base64::engine::general_purpose::STANDARD.decode(encoded).ok()
}

/// The best current token covering `scopes`, `None` when the profile has no
/// login cached at all.
pub fn token(profile: &Path, prompting: Prompting, scopes: &[&str]) -> Result<Option<Token>, Failure> {
    let Some(all) = tokens(profile, prompting)? else { return Ok(None) };
    all.into_iter()
        .filter(|t| t.is_current() && t.covers(scopes))
        .max_by_key(|t| t.expires)
        .map(Some)
        .ok_or(Failure::Expired)
}

/// Every entry the profile's cache holds, current or not. The usage pool needs
/// the lot: Claude hands Claude Code one of these, and which one is Claude's
/// business, so a request is recognised by any of them.
///
/// A config caught mid-write is an error rather than no login: taken for
/// none, it left a lender with nothing to be recognised by for a pass.
pub fn tokens(profile: &Path, prompting: Prompting) -> Result<Option<Vec<Token>>, Failure> {
    let config = files::readable_config_json(profile).ok_or(Failure::ConfigUnreadable)?;
    let Some(blob) = cache_blob(&config) else { return Ok(None) };
    let Some(scheme) = scheme_of(&blob) else { return Err(Failure::NotSignedIn) };
    let key = key_for(scheme, prompting)?.ok_or(Failure::NoKeyringAccess)?;
    let plain = decrypt(&blob, &key).ok_or(Failure::NotSignedIn)?;
    let cache: serde_json::Map<String, Value> =
        serde_json::from_slice(&plain).map_err(|_| Failure::NotSignedIn)?;
    Ok(Some(parse_cache(&cache)))
}

/// The cache holds one entry per client, organization and scope set. The key
/// carries the scopes; the entry carries the token itself.
pub fn parse_cache(cache: &serde_json::Map<String, Value>) -> Vec<Token> {
    cache
        .iter()
        .filter_map(|(composite, value)| {
            let token = value.get("token")?.as_str()?.to_string();
            let expires = value.get("expiresAt")?.as_f64()?;
            Some(Token {
                value: token,
                expires: UNIX_EPOCH + Duration::from_millis(expires.max(0.0) as u64),
                plan: value.get("subscriptionType").and_then(Value::as_str).map(str::to_string),
                scopes: scopes_in(composite),
            })
        })
        .collect()
}

/// Scopes are appended to the cache key after the api host.
fn scopes_in(composite: &str) -> Vec<String> {
    let Some(marker) = composite.find("https://") else { return Vec::new() };
    let tail = &composite[marker + "https://".len()..];
    let Some(separator) = tail.find(':') else { return Vec::new() };
    tail[separator + 1..].split(' ').filter(|s| !s.is_empty()).map(str::to_string).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_sealed_value_opens_with_the_same_key() {
        let key = derive(b"a password");
        let sealed = seal(b"HTTPS_PROXY=http://127.0.0.1:47823", &key, Scheme::V11);
        assert_eq!(unseal(&sealed, &key).as_deref(), Some(&b"HTTPS_PROXY=http://127.0.0.1:47823"[..]));
        assert_eq!(seal(b"x", &key, Scheme::V11), seal(b"x", &key, Scheme::V11), "one plaintext seals to one value");
    }

    #[test]
    fn the_peanuts_key_matches_chromium() {
        // Chromium's own test vector for the Linux fallback key.
        assert_eq!(hex(&peanuts_key()), "fd621fe5a2b402539dfa147ca9272778");
    }

    fn hex(bytes: &[u8]) -> String {
        bytes.iter().map(|b| format!("{b:02x}")).collect()
    }

    #[test]
    fn a_read_nobody_asked_for_asks_once_and_never_after_no() {
        assert!(may_raise_dialog(Prompting::OnceIfShut, false, false));
        assert!(!may_raise_dialog(Prompting::OnceIfShut, true, false), "a second unasked-for read stays quiet");
        assert!(!may_raise_dialog(Prompting::OnceIfShut, false, true), "a decline is not asked again");
        assert!(!may_raise_dialog(Prompting::No, false, false));
        assert!(may_raise_dialog(Prompting::Yes, true, true), "a press may always ask");
    }

    #[test]
    fn scopes_come_from_the_cache_key() {
        let mut cache = serde_json::Map::new();
        cache.insert(
            "client:org:https://api.anthropic.com:user:profile user:inference".into(),
            serde_json::json!({"token": "t", "expiresAt": 4_102_444_800_000.0_f64, "subscriptionType": "max"}),
        );
        let tokens = parse_cache(&cache);
        assert_eq!(tokens[0].scopes, vec!["user:profile", "user:inference"]);
        assert!(tokens[0].covers(&[INFERENCE_SCOPE]));
        assert_eq!(tokens[0].plan.as_deref(), Some("max"));
    }
}
