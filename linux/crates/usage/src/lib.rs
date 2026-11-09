//! Plan usage for every Claude on this computer: the live figure from
//! Anthropic's usage endpoint, read with each profile's own borrowed token,
//! the profile's own record on disk underneath it, the monitor that decides
//! how often either is asked, and Start Session.
//!
//! The app drives a `Monitor` and reads every figure it shows from it, so one
//! account is reported from one source wherever it appears.

pub mod api;
pub mod history;
pub mod monitor;
pub mod session;

#[cfg(test)]
mod testing;

pub use api::{ApiFailure, Reading};
pub use history::{countdown, usage_of, Headline, Usage};
pub use monitor::{
    clock, status_file, Entry, Freshness, Monitor, Refresh, Snapshot, Sources, SubscriptionId, Target, TICK,
};
pub use session::{start_session, SessionFailure};
