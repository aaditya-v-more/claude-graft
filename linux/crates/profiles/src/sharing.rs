//! The two steps of chat sharing a shortcut's life calls on.
//!
//! Sharing is an area of its own, built on top of this one, so this crate is
//! handed the two steps rather than reaching for them: putting a shortcut's
//! description into effect when it is installed with its profile closed, and
//! dropping every mirror pair a profile is part of when nothing will run its
//! launcher again.

use graft_core::GraftConfig;
use std::path::Path;

pub trait Sharing {
    /// Put a description into effect: graft from its source, or hand the
    /// chats back when it has none. Only ever called with the profile closed.
    fn apply(&self, _config: &GraftConfig) {}

    /// Stop squaring this profile up against anything. A pair outliving the
    /// shortcut that made it goes on syncing whenever any other profile
    /// opens, for ever, with nothing on screen saying so.
    fn forget_mirrors(&self, _profile: &Path) {}
}

/// For a caller with no sharing to do, and for the suite.
pub struct NoSharing;

impl Sharing for NoSharing {}
