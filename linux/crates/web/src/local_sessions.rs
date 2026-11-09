//! This machine's Code transcripts, by the ids claude.ai lists their sessions
//! under.
//!
//! The facts in a transcript, the sweep's cache of them and the test for
//! whether a session's folder could be on this machine all belong to the chat
//! mirror, so they come in through `Transcripts`, which the app fills from
//! `graft-chats`. The walk is here, and it only reads: what the sweep already
//! parsed comes out of its cache, and a transcript it has not reached yet is
//! parsed without being added to it, so nothing the sweep relies on is written
//! from a search.

use crate::projects::LocalCodeSession;
use std::collections::HashSet;
use std::os::unix::fs::MetadataExt;
use std::path::{Path, PathBuf};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

/// What the sweep reads out of one transcript, in the shape a search needs.
#[derive(Debug, Clone, PartialEq)]
pub struct TranscriptFacts {
    /// Every `bridgeSessionId` its `bridge-session` lines carry.
    pub bridge_ids: Vec<String>,
    /// `ownerAccountUuid`.
    pub owner_account: String,
    /// `ownerOrganizationUuid`.
    pub owner_organization: String,
    pub title: String,
    pub cwd: String,
    /// Milliseconds since 1970, as the sweep keeps it.
    pub last_activity_ms: f64,
}

/// What the chat mirror knows, lent to a search. Every method only reads.
pub trait Transcripts: Send + Sync {
    /// `~/.claude/projects`, or the suite's stand-in for it.
    fn projects_root(&self) -> PathBuf {
        graft_core::paths::home().join(".claude/projects")
    }

    /// The sweep's cached facts for a transcript, when its entry still matches
    /// the file's modification time (seconds since 1970) and size.
    fn cached(&self, transcript: &Path, modified: f64, size: u64) -> Option<TranscriptFacts>;

    /// The facts of a transcript the sweep has not reached, parsed by the
    /// sweep's own reader and kept nowhere.
    fn parse(&self, transcript: &Path, cli_session_id: &str) -> Option<TranscriptFacts>;

    /// The sweep's `sessionFolderIsHere`: whether the session's home folder or
    /// drive exists on this machine, never the folder itself.
    fn folder_is_here(&self, cwd: &str) -> bool;

    /// The command line sessions a conversation's record has taken over
    /// (`priorCliSessionIds`, the sweep's `superseded`).
    fn carried_on(&self) -> HashSet<String>;
}

/// For a build with no chat mirror to ask, and for tests: no transcripts.
pub struct NoTranscripts;

impl Transcripts for NoTranscripts {
    fn cached(&self, _: &Path, _: f64, _: u64) -> Option<TranscriptFacts> {
        None
    }
    fn parse(&self, _: &Path, _: &str) -> Option<TranscriptFacts> {
        None
    }
    fn folder_is_here(&self, _: &str) -> bool {
        false
    }
    fn carried_on(&self) -> HashSet<String> {
        HashSet::new()
    }
    fn projects_root(&self) -> PathBuf {
        PathBuf::from("/nonexistent")
    }
}

pub fn read(source: &dyn Transcripts) -> Vec<LocalCodeSession> {
    let root = source.projects_root();
    let carried_on = source.carried_on();
    let mut sessions = Vec::new();
    let Some(folders) = graft_core::files::listing(&root) else { return sessions };
    for folder in folders {
        let directory = root.join(&folder);
        let Some(names) = graft_core::files::listing(&directory) else { continue };
        for name in names {
            let Some(cli) = name.strip_suffix(".jsonl") else { continue };
            let path = directory.join(&name);
            let Ok(meta) = std::fs::metadata(&path) else { continue };
            let modified = meta.mtime() as f64 + meta.mtime_nsec() as f64 / 1e9;
            let facts = source.cached(&path, modified, meta.size()).or_else(|| source.parse(&path, cli));
            let Some(facts) = facts else { continue };
            sessions.push(LocalCodeSession {
                bridge_ids: facts.bridge_ids,
                owner: facts.owner_account,
                organization: facts.owner_organization,
                title: facts.title,
                folder_is_here: source.folder_is_here(&facts.cwd),
                folder: facts.cwd,
                transcript: path,
                last_active: UNIX_EPOCH + Duration::from_secs_f64((facts.last_activity_ms / 1000.0).max(0.0)),
                carried_on: carried_on.contains(cli),
            });
        }
    }
    sessions
}

/// Kept so a search's read can be dated against the list it stands beside.
#[derive(Debug, Clone)]
pub struct LocalRead {
    pub read: SystemTime,
    pub sessions: Vec<LocalCodeSession>,
}
