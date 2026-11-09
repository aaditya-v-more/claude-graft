//! What opening a profile does, whoever asks: a shortcut in the dock, the
//! window's Open button or the tray. The profiles crate decides whether to
//! start or show a Claude; this is where the other areas' steps are put in
//! the order the Mac's `Graft.run` and `Graft.open` put them.

use graft_core::GraftConfig;
use graft_profiles::launch::LaunchSteps;
use graft_profiles::sharing::Sharing;
use std::collections::BTreeSet;
use std::path::{Path, PathBuf};

/// Chat sharing as the profiles crate asks for it.
pub struct ChatSharing;

impl Sharing for ChatSharing {
    fn apply(&self, config: &GraftConfig) {
        graft_chats::apply(config);
    }

    fn forget_mirrors(&self, profile: &Path) {
        graft_chats::forget_mirrors(profile);
    }
}

/// The steps around a launch.
pub struct Steps;

impl LaunchSteps for Steps {
    /// A profile already open is the one case where nothing else files its
    /// records: the sweep has no timer, and this press starts nothing.
    fn already_open(&self, filing: &[PathBuf]) {
        graft_chats::square_up(filing, false);
    }

    /// Links first, so a record filed through a graft lands where the link
    /// now points; then the mirror and the sweep, because this profile may be
    /// the source somebody borrows from and opening it is the only moment
    /// their changes have to reach the sidebar about to be built.
    fn before_launch(&self, config: Option<&GraftConfig>, filing: &[PathBuf]) {
        if let Some(config) = config {
            graft_chats::apply(config);
        }
        graft_chats::square_up(filing, false);
    }

    /// Under the launch lock with the profile found closed: the only time a
    /// Claude's own storage may be written.
    fn before_start(&self, profile: &Path) {
        graft_sidebar::synchronize_before_opening(profile);
    }
}

fn forget_sidebar_pairs(keys: &BTreeSet<String>) {
    graft_sidebar::forget_pairs(keys);
}

/// Connect the areas that cannot depend on each other. Called once at the
/// start of every process that runs a pass, the window and each launcher, so
/// a pair the mirror drops is dropped from the sidebar sync in the same pass.
pub fn wire() {
    graft_chats::set_on_pairs_saved(Some(forget_sidebar_pairs));
}
