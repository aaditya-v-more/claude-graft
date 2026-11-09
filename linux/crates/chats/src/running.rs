//! Which Claudes are up, read through a listing the suite can replace.
//!
//! Nothing in a temporary folder can be made to run, and a suite that asked
//! the host would pass or fail on whether somebody had Claude open while it
//! ran. Worse, on this machine a Claude started with no `--user-data-dir` is
//! the main profile wherever `~/.config` happens to point, so the sandbox's
//! own Claude profile would read as open whenever the real one is.

use graft_core::claude::{self, Process};
use graft_core::paths;
use std::path::{Path, PathBuf};
use std::sync::RwLock;

static OVERRIDE: RwLock<Option<Vec<Process>>> = RwLock::new(None);

/// Stand a fixed process listing in for the host's. `None` goes back to
/// asking `ps`, except in this crate's own suite, which never asks.
pub fn set_processes_override(listing: Option<Vec<Process>>) {
    *OVERRIDE.write().unwrap_or_else(|e| e.into_inner()) = listing;
}

/// The host's processes, or the listing standing in for them.
pub fn processes() -> Vec<Process> {
    if let Some(listing) = OVERRIDE.read().unwrap_or_else(|e| e.into_inner()).clone() {
        return listing;
    }
    if cfg!(test) {
        return Vec::new();
    }
    claude::processes()
}

pub fn is_running(profile: &Path) -> bool {
    claude::is_running_in(&processes(), profile)
}

/// Every profile on the machine with a Claude on it, Claude's own included.
///
/// Any Claude at all, not only the ones in `~/.config` with a chat store: an
/// instance anywhere may be sharing these folders through a graft, and the
/// question this answers is whether anything is about to write over records
/// landing underneath it.
pub fn running_claudes() -> Vec<PathBuf> {
    claude::running_profiles_in(&processes())
}

/// A stand-in Claude on a profile, for a listing handed to the override.
pub fn stand_in(pid: i32, profile: &Path) -> Process {
    let command = if paths::is_main_profile(profile) {
        "/usr/lib/claude-desktop/claude-desktop".to_string()
    } else {
        format!("/usr/lib/claude-desktop/claude-desktop --user-data-dir={}", paths::text(profile))
    };
    Process { pid, command }
}
