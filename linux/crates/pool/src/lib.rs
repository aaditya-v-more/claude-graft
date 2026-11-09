//! Shared usage: one Claude goes on being itself, its chats, artifacts,
//! connectors and Claude in Chrome all staying with the account it is signed
//! into, while the model requests its Claude Code makes are answered by
//! whichever of the accounts it was given has room.
//!
//! The route in is the one setting Claude passes through to Claude Code
//! untouched: a proxy. Claude replaces the base URL and the token every time it
//! starts a session, so neither can be pointed anywhere, but `HTTPS_PROXY` and
//! `NODE_EXTRA_CA_CERTS` survive, and both live in Claude's own Code
//! environment, where Graft puts them. The relay answers that proxy, opens only
//! the traffic meant for `api.anthropic.com`, and changes exactly one thing in
//! it: the `Authorization` line of `POST /v1/messages`. Every other request
//! leaves with the token it arrived with, which is why the Claude doing the
//! work keeps its own identity everywhere else.
//!
//! This crate is what the window, the launcher and the relay share: the
//! model the relay is told, the rules for which account answers, the journal
//! a launcher swaps from, and the window's side of keeping it all in step.

pub mod chats;
pub mod code_environment;
pub mod controller;
pub mod environment;
pub mod limits;
pub mod policy;
pub mod request;
pub mod sign_in;
pub mod wire;

pub use limits::PoolLimits;
pub use wire::{
    PassReason, PoolCommand, PoolConfiguration, PoolMember, PoolMemberStatus, PoolPass, PoolRouting,
    PoolSession, PoolStatus,
};

use graft_core::paths;
use std::path::PathBuf;
use std::time::{SystemTime, UNIX_EPOCH};

/// Fixed, because it is written into a Claude's environment while that
/// Claude is closed, and a session already running goes on using whatever it
/// read when it started.
pub const PORT: u16 = 47823;
pub const HOST: &str = "api.anthropic.com";

/// The scope the token Claude hands Claude Code carries, which makes it the
/// one to lend: the service has already accepted it for exactly this.
pub const LENDING_SCOPE: &str = "user:sessions:claude_code";

/// Seconds since 1970. Every time in the pool's model is one of these, the
/// way the service's own headers carry its resets.
pub type Time = f64;

pub fn now() -> Time {
    SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_secs_f64()).unwrap_or(0.0)
}

/// Short on purpose: the control socket lives here, and a Unix socket path
/// longer than 107 bytes cannot be bound at all.
pub fn directory() -> PathBuf {
    paths::graft_folder().join("pool")
}

pub fn certificate_file() -> PathBuf {
    directory().join("relay-ca.pem")
}

pub fn leaf_file() -> PathBuf {
    directory().join("relay-leaf.pem")
}

/// The leaf's private key. It is the only key kept: the authority's is never
/// written anywhere, so what Node trusts can sign nothing more.
pub fn identity_file() -> PathBuf {
    directory().join("relay-identity.pem")
}

pub fn socket_file() -> PathBuf {
    directory().join("relay.sock")
}

pub fn relay_lock_file() -> PathBuf {
    directory().join("relay.lock")
}

/// Which Claude borrows from which, and the conversations moved by hand.
pub fn settings_file() -> PathBuf {
    paths::graft_file("usage-sharing.json")
}

/// Accounts signed in through Graft, their tokens sealed.
pub fn accounts_file() -> PathBuf {
    directory().join("accounts.json")
}

/// Tokens are recognised by digest, so the relay can say which member a
/// request came from without keeping a list of anybody's tokens it was not
/// handed to lend.
pub fn digest(token: &str) -> String {
    graft_core::files::digest(token.as_bytes())
}

/// Whether `needle` occurs anywhere in `haystack`. Bodies are searched as
/// bytes, never decoded, because a request is forwarded exactly as it came.
pub(crate) fn contains(haystack: &[u8], needle: &[u8]) -> bool {
    find(haystack, needle).is_some()
}

pub(crate) fn find(haystack: &[u8], needle: &[u8]) -> Option<usize> {
    if needle.is_empty() {
        return Some(0);
    }
    haystack.windows(needle.len()).position(|w| w == needle)
}

pub(crate) fn rfind(haystack: &[u8], needle: &[u8]) -> Option<usize> {
    if needle.is_empty() || needle.len() > haystack.len() {
        return None;
    }
    haystack.windows(needle.len()).rposition(|w| w == needle)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_login_is_known_to_the_relay_by_its_sha256_alone() {
        assert_eq!(digest("abc"), "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    #[test]
    fn the_certificate_is_kept_with_grafts_state() {
        let _sandbox = graft_core::testing::Sandbox::new();
        assert!(certificate_file().ends_with("ClaudeGraft/pool/relay-ca.pem"));
        assert!(settings_file().ends_with("ClaudeGraft/usage-sharing.json"));
    }
}
