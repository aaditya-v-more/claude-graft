//! Shortcuts: how each extra Claude is made, found again, kept in step with
//! the list, opened and deleted.
//!
//! On Linux a shortcut is a `.desktop` file in `~/.local/share/applications`
//! that Graft wrote, carrying the path of its own `graft.json`, plus the icon
//! it names. The desktop runs it and asks Graft nothing, which is why the
//! file, not the list the window edits, is what a shortcut actually does.

pub mod conflict;
pub mod desktop;
pub mod icon;
pub mod installer;
pub mod launch;
pub mod opener;
pub mod profile;
pub mod sharing;
pub mod store;

pub use installer::InstallError;
pub use launch::{reveal, start_claude, LaunchError, LaunchSteps, LaunchTarget, NoSteps};
pub use opener::{Destination, OpenFailure};
pub use profile::{delete_profile, ProfileError};
pub use sharing::{NoSharing, Sharing};
pub use store::{Neighbour, ShortcutStore};

#[cfg(test)]
pub(crate) mod testing;
