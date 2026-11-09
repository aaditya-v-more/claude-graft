//! What every part of Graft on Linux shares: where things live, how the host
//! is reached from inside the sandbox, which processes are Claude, the shortcut
//! model, the locks and the diagnostics log.

pub mod claude;
pub mod config;
pub mod diagnostics;
pub mod files;
pub mod host;
pub mod lock;
pub mod paths;
pub mod testing;

pub use config::{GraftConfig, IconPreset, Shortcut, Source};
