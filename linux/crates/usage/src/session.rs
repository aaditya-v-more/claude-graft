//! Opens a five-hour window on one account by sending it a single short
//! message. Nothing appears on screen and nothing beyond the keyring's own
//! unlock dialog is asked.
//!
//! It uses that profile's own login, borrowed the same way the usage figures
//! are: access token only, nothing written back, and the request goes to
//! Anthropic and nowhere else. That is what makes it per account: the command
//! line could only ever have started a window on whichever single account it
//! happens to be signed into.
//!
//! Nothing here may run except because somebody pressed the button. A window
//! refreshes when it appears and again on a timer, so a session wired into
//! one of those would start a five-hour window on every account, over and
//! over, without anybody asking for one. The suite counts the callers.

use crate::api::{self, ApiFailure, Session};
use graft_credentials::{Failure, Prompting, INFERENCE_SCOPE};
use std::collections::HashSet;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::Duration;

pub const MODEL: &str = "claude-haiku-4-5-20251001";
const ANTHROPIC_VERSION: &str = "2023-06-01";

/// These tokens are minted for Claude Code, and the service expects to see
/// its system prompt on requests made with them.
const SYSTEM_PROMPT: &str = "You are Claude Code, Anthropic's official CLI for Claude.";

#[derive(Debug, Clone, PartialEq)]
pub enum SessionFailure {
    NoLogin,
    InFlight,
    Credentials(Failure),
    Http { code: u16, detail: String },
    Unreachable(String),
}

impl SessionFailure {
    pub fn message(&self) -> String {
        match self {
            SessionFailure::NoLogin => "No login is stored for this profile yet. Open it once and sign in, \
                 then a session can be started from here."
                .into(),
            SessionFailure::InFlight => "A session is already being started for this account.".into(),
            SessionFailure::Credentials(failure) => failure.message().into(),
            SessionFailure::Http { code: 401 | 403, .. } => {
                "That profile's login was refused. Open it once so Claude can renew it.".into()
            }
            SessionFailure::Http { code: 429, .. } => "That account is rate-limited right now.".into(),
            SessionFailure::Http { code, detail } if detail.is_empty() => format!("Anthropic answered {code}."),
            SessionFailure::Http { code, detail } => format!("Anthropic answered {code}: {detail}"),
            SessionFailure::Unreachable(detail) => format!("Could not reach Anthropic: {detail}"),
        }
    }
}

impl std::fmt::Display for SessionFailure {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.message())
    }
}

impl std::error::Error for SessionFailure {}

/// Blocking; call it off the main thread. `Ok` means the window is open.
///
/// `interactive` allows the one keyring unlock dialog that may stand between
/// a press and the profile's token. Only a button may pass `true`, which is
/// what the suite checks, so a call arriving from somewhere nobody audited
/// fails quietly rather than putting a dialog in front of someone who asked
/// for nothing.
///
/// After it returns the caller drops the monitor's reading for this profile
/// (`Monitor::invalidate`) and refreshes with `Refresh::after_session_started`:
/// the window this opened is exactly what the stored reading predates.
pub fn start_session(profile: &Path, interactive: bool) -> Result<(), SessionFailure> {
    start_with_prompt(profile, interactive, "hi")
}

fn start_with_prompt(profile: &Path, interactive: bool, prompt: &str) -> Result<(), SessionFailure> {
    let Some(_claim) = Claim::take(profile) else { return Err(SessionFailure::InFlight) };

    let prompting = if interactive { Prompting::Yes } else { Prompting::No };
    let token = graft_credentials::token(profile, prompting, &[INFERENCE_SCOPE])
        .map_err(SessionFailure::Credentials)?
        .ok_or(SessionFailure::NoLogin)?;

    let body = serde_json::json!({
        "model": MODEL,
        "max_tokens": 4,
        "system": [{"type": "text", "text": SYSTEM_PROMPT}],
        "messages": [{"role": "user", "content": prompt}],
    });
    let bearer = format!("Bearer {}", token.value);
    let answer = Session::new(Duration::from_secs(30))
        .post(
            api::MESSAGES_PATH,
            &[
                ("Authorization", &bearer),
                ("anthropic-version", ANTHROPIC_VERSION),
                ("anthropic-beta", api::OAUTH_BETA),
                ("Content-Type", "application/json"),
            ],
            &serde_json::to_vec(&body).unwrap_or_default(),
        )
        .map_err(|failure| match failure {
            ApiFailure::Unreachable(detail) => SessionFailure::Unreachable(detail),
            other => SessionFailure::Unreachable(other.message()),
        })?;
    if answer.status != 200 {
        return Err(SessionFailure::Http { code: answer.status, detail: message_in(&answer.body) });
    }
    Ok(())
}

/// Anthropic returns `{ "error": { "message": ... } }` on a refusal.
fn message_in(body: &[u8]) -> String {
    serde_json::from_slice::<serde_json::Value>(body)
        .ok()
        .and_then(|v| v.pointer("/error/message").and_then(|m| m.as_str()).map(str::to_string))
        .unwrap_or_default()
}

/// The window and the tray each carry a Start Session button for the same
/// account, and each only knows about its own press. A second one landing
/// while the first is still open sends a second message to start a window
/// that is already starting.
static CLAIMED: Mutex<Option<HashSet<PathBuf>>> = Mutex::new(None);

struct Claim(PathBuf);

impl Claim {
    fn take(profile: &Path) -> Option<Claim> {
        let key = graft_core::paths::normalize(profile);
        let mut claimed = CLAIMED.lock().unwrap_or_else(|e| e.into_inner());
        // Made only when the account was free: a claim built and dropped on
        // the refused path would let go of the press that holds it.
        claimed.get_or_insert_with(HashSet::new).insert(key.clone()).then(|| Claim(key))
    }
}

impl Drop for Claim {
    fn drop(&mut self) {
        let mut claimed = CLAIMED.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(set) = claimed.as_mut() {
            set.remove(&self.0);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::testing::{signed_in, tomorrow, StandIn};
    use graft_core::testing::Sandbox;

    #[test]
    fn sessions_start_against_anthropic_over_https_on_the_cheapest_model() {
        assert_eq!(api::MESSAGES_PATH, "/v1/messages");
        assert!(api::usage_endpoint().starts_with("https://api.anthropic.com/"), "sessions share the one base, over https");
        assert!(MODEL.starts_with("claude-haiku"), "and the cheapest way to open a window");
    }

    #[test]
    fn a_profile_with_no_login_cannot_start_a_session() {
        let sandbox = Sandbox::new();
        let bare = sandbox.config().join("Claude-NoLogin");
        std::fs::create_dir_all(&bare).unwrap();
        assert_eq!(start_session(&bare, false), Err(SessionFailure::NoLogin));
    }

    #[test]
    fn a_second_press_on_the_same_account_is_turned_away_until_the_first_finishes() {
        let sandbox = Sandbox::new();
        let account = sandbox.config().join("Claude-Session");
        let first = Claim::take(&account);
        assert!(first.is_some(), "the first press takes the account");
        assert!(Claim::take(&account).is_none(), "a second press while that one is still open is turned away");
        assert_eq!(start_session(&account, false), Err(SessionFailure::InFlight));
        drop(first);
        assert!(Claim::take(&account).is_some(), "and the account is free again once it finishes");
    }

    #[test]
    fn a_session_is_one_short_message_with_the_profiles_own_inference_login() {
        let sandbox = Sandbox::new();
        let profile = sandbox.config().join("Claude-2");
        signed_in(&profile, &[("usage-only", "user:profile", tomorrow()), ("can-run", "user:profile user:inference", tomorrow())]);
        let stand_in = StandIn::start();
        stand_in.answer(200, &[], "{}");
        assert_eq!(start_session(&profile, false), Ok(()));

        let seen = stand_in.requests();
        assert_eq!(seen.len(), 1);
        assert_eq!((seen[0].method.as_str(), seen[0].path.as_str()), ("POST", "/v1/messages"));
        assert_eq!(seen[0].header("authorization").as_deref(), Some("Bearer can-run"), "the token that may run the model is the one used");
        assert_eq!(seen[0].header("anthropic-version").as_deref(), Some(ANTHROPIC_VERSION));
        let body: serde_json::Value = serde_json::from_slice(&seen[0].body).unwrap();
        assert_eq!(body["model"], MODEL);
        assert_eq!(body["max_tokens"], 4);
        assert_eq!(body["system"][0]["text"], SYSTEM_PROMPT);
    }

    #[test]
    fn a_refusal_says_what_the_service_said() {
        let sandbox = Sandbox::new();
        let profile = sandbox.config().join("Claude-2");
        signed_in(&profile, &[("t", "user:profile user:inference", tomorrow())]);
        let stand_in = StandIn::start();
        stand_in.answer(400, &[], r#"{"error":{"message":"bad things"}}"#);
        let failure = start_session(&profile, false).unwrap_err();
        assert_eq!(failure, SessionFailure::Http { code: 400, detail: "bad things".into() });
        assert_eq!(failure.message(), "Anthropic answered 400: bad things");
        stand_in.answer(401, &[], "{}");
        assert!(start_session(&profile, false).unwrap_err().message().contains("refused"));
    }

    #[test]
    fn an_expired_login_fails_before_anything_is_sent() {
        let sandbox = Sandbox::new();
        let profile = sandbox.config().join("Claude-2");
        signed_in(&profile, &[("old", "user:profile user:inference", 1_000.0)]);
        let stand_in = StandIn::start();
        assert_eq!(start_session(&profile, false), Err(SessionFailure::Credentials(Failure::Expired)));
        assert!(stand_in.requests().is_empty());
    }

    /// The Rust sources of this crate, with the test modules cut off, so a
    /// check reads only what ships.
    fn shipped_sources() -> Vec<(String, String)> {
        let dir = Path::new(env!("CARGO_MANIFEST_DIR")).join("src");
        let mut out = Vec::new();
        for entry in std::fs::read_dir(&dir).unwrap() {
            let path = entry.unwrap().path();
            if path.extension().and_then(|e| e.to_str()) != Some("rs") || path.ends_with("testing.rs") {
                continue;
            }
            let text = std::fs::read_to_string(&path).unwrap();
            let shipped = text.split("#[cfg(test)]\nmod tests").next().unwrap().to_string();
            out.push((path.file_name().unwrap().to_string_lossy().into_owned(), shipped));
        }
        out.sort();
        out
    }

    #[test]
    fn nothing_in_this_crate_starts_a_session_by_itself() {
        let sources = shipped_sources();
        assert!(sources.len() >= 4, "the crate's own source is there to be read");
        let mut callers = Vec::new();
        for (file, text) in &sources {
            for line in text.lines() {
                let code = line.trim_start();
                if code.starts_with("//") || code.starts_with("pub fn start_session(") {
                    continue;
                }
                // `may_start_session(` is a question about a figure, not a call.
                let calls = code.match_indices("start_session(").any(|(at, _)| {
                    at == 0 || !matches!(code.as_bytes()[at - 1], b'a'..=b'z' | b'A'..=b'Z' | b'0'..=b'9' | b'_')
                });
                if calls {
                    callers.push(file.clone());
                }
            }
        }
        assert!(callers.is_empty(), "only a button in the app starts a session, never this crate: {callers:?}");
    }

    #[test]
    fn nothing_reaches_for_the_unconditional_keyring_dialog_by_hand() {
        // `Prompting::Yes` asks whatever has gone before. It is reached by
        // saying a person pressed something, which is auditable, and named in
        // exactly the two places that turn a press into it.
        let named: Vec<(String, usize)> = shipped_sources()
            .into_iter()
            .map(|(file, text)| {
                let count = text.lines().filter(|l| !l.trim_start().starts_with("//") && l.contains("Prompting::Yes")).count();
                (file, count)
            })
            .filter(|(_, n)| *n > 0)
            .collect();
        assert_eq!(named, vec![("monitor.rs".to_string(), 1), ("session.rs".to_string(), 1)]);
    }

    #[test]
    fn nothing_holds_one_client_for_the_whole_process() {
        // Every token-bearing request goes out through a `Session` its caller
        // made and drops. A client in a static is a client every caller
        // shares, and the next person to add a cache to it adds it for all.
        for (file, text) in shipped_sources() {
            for line in text.lines() {
                let code = line.trim_start();
                if code.starts_with("static") || code.starts_with("pub static") {
                    assert!(
                        !code.contains("Agent") && !code.contains("Session"),
                        "{file} keeps a client in a static: {code}"
                    );
                }
            }
            assert!(!text.contains("ureq::get(") && !text.contains("ureq::post("), "{file} uses ureq's own default client");
            if file != "api.rs" {
                assert!(!text.contains("ureq::Agent"), "{file} makes a client of its own outside api.rs");
            }
        }
    }
}
