//! Sharing Claude Code chats between profiles, and keeping their session
//! records whole.
//!
//! Grafting gives a profile its own real copy of another profile's chat
//! folder and keeps the two in step, because a profile will not write a record
//! into a folder that resolves outside itself. The sweep files the records a
//! grafted profile never wrote, from the transcripts Claude Code leaves in
//! `~/.claude/projects`. `notes/chat-sharing.md` and `notes/session-records.md`
//! say why each rule here is shaped the way it is.

pub mod cache;
pub mod elsewhere;
pub mod fsx;
pub mod graft;
pub mod mirror;
pub mod records;
pub mod report;
pub mod running;
pub mod transcript;

#[cfg(test)]
mod tests;

pub use cache::{transcript_cache_file, CachedTranscript, RecordSessions, TRANSCRIPT_READING_VERSION};
pub use elsewhere::{adopt_chats, chats_elsewhere, Adoption, ChatsElsewhere, ElsewhereChat};
pub use fsx::{counterpart_directory, is_stashed_away, newest_child, relink, stash_path};
pub use graft::{
    apply, copy_appearance, copy_desktop_servers, drop_borrowed_copies, graft, mirror_chat_stores,
    open_for_mirror, seed_own_chats, stashed_counterpart, ungraft, unmirror_chat_stores,
};
pub use mirror::{
    digest, forget_mirrors, forget_stale_pairs, is_mirrored, load_mirror_state, mirror_chat_folders,
    mirror_decision, mirror_known_pairs, mirror_pairs_borrowed_by, mirror_pairs_under, mirror_state_file,
    pair_folders, pair_halves, pair_key, save_mirror_state, set_on_pairs_saved, MirrorAction, MirrorState,
};
pub use records::{
    claude_projects, file_missing_session_records, file_missing_session_records_at, load_session_record_state,
    may_file_records, owner_profile, resumed_copies, save_session_record_state, session_filing,
    session_folder_is_here, session_record, session_record_state_file, session_store_contents,
    session_update_decision, SessionFiling, SessionRecordState, SessionUpdate, StoreContents,
};
pub use report::{
    command_line_login, device_identifier, state_findings, state_report, state_report_text, write_state_report,
    FolderReport, ProfileReport,
};
pub use running::{running_claudes, set_processes_override};
pub use transcript::{
    prompt_fingerprint, prompts_typed, read_transcript, session_facts, ConversationTrace, SessionFacts,
};

use graft_core::{lock, paths, Shortcut};
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};

/// Files a second profile can share wholesale. Everything absent from this
/// list is either credential material, per-organization cache, or a store two
/// running instances cannot both hold open.
pub const SHARED_ITEMS: [&str; 7] = [
    "Claude Extensions",
    "Claude Extensions Settings",
    "extensions-installations.json",
    "window-state.json",
    "git-worktrees.json",
    "claude-ssh-remote",
    "ssh_configs.json",
];

/// Chat history lives in these two, keyed by `<accountUuid>/<orgUuid>`, except
/// where a store spells those two shorter, which `counterpart_directory` is
/// the rule for.
pub const CHAT_STORES: [&str; 2] = ["claude-code-sessions", "local-agent-mode-sessions"];

/// Sits beside the account directories in a chat store without being one. It
/// is keyed by organization, so a walk that took it for an account reported
/// its organization folders as chat stores of their own.
pub const NON_ACCOUNT_STORE_ITEMS: [&str; 1] = ["skills-plugin"];

/// Keys copied out of config.json. The rest of that file is this profile's own
/// credentials, so it is never linked.
pub const APPEARANCE_KEYS: [&str; 2] = ["userThemeMode", "locale"];

/// Suffix for anything a profile owned before it was grafted.
pub const STASH_SUFFIX: &str = ".graft-own";

/// Mirror, file, then write down what the pass left behind.
///
/// The three go together and in this order. Mirroring runs first because a
/// record arriving that way has to be on disk before the sweep decides whether
/// anyone has filed one. The report runs last because it is the file somebody
/// reads after an incident, and a pass that leaves it describing the previous
/// one is a pass that cannot be reconstructed.
pub fn square_up(filing_into: &[PathBuf], checking_running: bool) -> Vec<SessionFacts> {
    lock::with_state_lock(|| {
        mirror_known_pairs();
        let filed = file_missing_session_records(filing_into);
        write_state_report(checking_running);
        filed
    })
}

/// The profiles a pass from the window files into: Claude's own and every
/// shortcut that has been installed. The window is the only pass that covers
/// a Claude opened straight from the dock or the app grid, which runs no
/// launcher and asks Graft nothing.
pub fn sweep_filing_profiles(shortcuts: &[Shortcut]) -> Vec<PathBuf> {
    let mut filing = vec![paths::main_profile()];
    filing.extend(shortcuts.iter().filter(|s| s.installed_name.is_some()).map(Shortcut::profile_dir));
    filing
}

/// Keeps session records whole from the window's side.
///
/// There is no timer. A record is read when a Claude starts and at no other
/// moment, so the pass that matters is the one a shortcut's launcher runs on
/// its way to opening one, in a process the window never sees. What is left
/// here are the two times somebody is looking at Graft itself: when it starts,
/// and when the tray's window opens.
#[derive(Default)]
pub struct SessionRecords {
    in_flight: AtomicBool,
}

impl SessionRecords {
    pub const fn new() -> SessionRecords {
        SessionRecords { in_flight: AtomicBool::new(false) }
    }

    /// One pass, run on whatever thread calls it; the window calls it off its
    /// main loop, since transcripts run to megabytes. A pass still running
    /// when another is asked for is left to finish and this one answers
    /// `None`. The titles filed are for one line in the window, saying what
    /// just happened.
    pub fn sweep(&self, shortcuts: &[Shortcut]) -> Option<Vec<String>> {
        if self.in_flight.swap(true, Ordering::SeqCst) {
            return None;
        }
        let filed = square_up(&sweep_filing_profiles(shortcuts), true);
        self.in_flight.store(false, Ordering::SeqCst);
        Some(filed.into_iter().map(|f| f.title).collect())
    }
}
