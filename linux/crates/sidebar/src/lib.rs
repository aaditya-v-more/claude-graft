//! Pinned chats and sidebar order carried between profiles that share a chat
//! history, and the desktop settings file each profile keeps to itself.
//!
//! The pins live in Claude's browser storage, IndexedDB and local storage,
//! which Chromium keeps in LevelDB and locks while it runs. Nothing here
//! writes those files. A private copy of Claude's own Electron runtime, run
//! headless with a script of Graft's, opens them the way Claude does and
//! writes through Chromium's own storage APIs; `notes/sidebar-and-settings.md`
//! says why that copy is made the way it is on Linux.

pub mod helper;
pub mod records;
pub mod settings;
pub mod snapshot;
pub mod storage;
pub mod sync;

pub use settings::copy_desktop_servers;
pub use snapshot::{merge, replace_shared, Snapshot};
pub use sync::{forget_pairs, status, status_message, synchronize_before_opening, SyncStatus};

/// Why a pass stopped, in the words the diagnostics log uses.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Failure(pub String);

impl Failure {
    pub fn new(reason: &str) -> Failure {
        Failure(reason.to_string())
    }
}

impl std::fmt::Display for Failure {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for Failure {}
