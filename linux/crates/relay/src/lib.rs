//! The relay that lets one Claude's Claude Code draw on other accounts'
//! allowance: a proxy on loopback, a TLS listener that takes apart only the
//! traffic for `api.anthropic.com`, and the rules for which account answers.
//! `graft_pool` holds what it shares with the window and the launcher.

pub mod certificates;
pub mod conn;
pub mod exchange;
pub mod http;
pub mod relay;
pub mod state;

pub use exchange::Upstream;
pub use relay::{run, Options};
