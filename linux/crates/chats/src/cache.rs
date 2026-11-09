//! What has already been read, so a pass pays only for what changed.

use crate::fsx;
use crate::transcript::{self, ConversationTrace, SessionFacts};
use graft_core::paths;
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::collections::{BTreeMap, HashMap, HashSet};
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::SystemTime;

/// One transcript's parse, remembered against the file it came from.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct CachedTranscript {
    /// Seconds since the epoch, as the file's own modification time.
    pub modified: f64,
    pub size: i64,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub facts: Option<SessionFacts>,
    /// Optional so a cache written before traces were kept still loads; an
    /// entry without one is read again, once, rather than trusted.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub trace: Option<ConversationTrace>,
    /// Which reading of a transcript made this entry. A transcript that names
    /// no permission mode was once read as `auto`, and an entry from then
    /// would go on saying so for as long as the file sat still.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub reading: Option<i64>,
}

pub const TRANSCRIPT_READING_VERSION: i64 = 2;

pub fn transcript_cache_file() -> PathBuf {
    paths::graft_file("transcript-cache.json")
}

/// Parsed transcripts, kept on disk rather than only in memory.
///
/// A shortcut files records on its way to opening Claude, and a launcher is a
/// process that exits the moment it has handed over, so a cache that lived
/// only in memory would be cold every single time, and cold is two hundred
/// megabytes of transcript standing between a click and a window. On disk it
/// is paid once, by whoever pays it first.
struct TranscriptCache {
    /// The file the entries were loaded from. The suite moves every path
    /// between tests, and entries learned in one world mean nothing in the
    /// next.
    loaded_from: Option<PathBuf>,
    entries: BTreeMap<String, CachedTranscript>,
    dirty: bool,
}

static TRANSCRIPTS: Mutex<TranscriptCache> =
    Mutex::new(TranscriptCache { loaded_from: None, entries: BTreeMap::new(), dirty: false });

fn read_cache_file(path: &Path) -> Option<BTreeMap<String, CachedTranscript>> {
    let bytes = std::fs::read(path).ok()?;
    serde_json::from_slice(&bytes).ok()
}

fn loaded(cache: &mut TranscriptCache) {
    let file = transcript_cache_file();
    if cache.loaded_from.as_deref() == Some(file.as_path()) {
        return;
    }
    cache.loaded_from = Some(file.clone());
    cache.dirty = false;
    cache.entries = read_cache_file(&file).unwrap_or_default();
}

fn stamp_and_size(path: &Path) -> (f64, i64) {
    match std::fs::symlink_metadata(path) {
        Ok(meta) => (fsx::seconds(fsx::modified(path)), meta.len() as i64),
        Err(_) => (fsx::seconds(std::time::UNIX_EPOCH), 0),
    }
}

/// A transcript's facts and trace, from the cache when the file has not moved
/// since it was read.
pub fn transcript_reading(path: &Path, cli_session_id: &str) -> (Option<SessionFacts>, ConversationTrace) {
    let (stamp, size) = stamp_and_size(path);
    let key = paths::text(path);
    {
        let mut cache = TRANSCRIPTS.lock().unwrap_or_else(|e| e.into_inner());
        loaded(&mut cache);
        if let Some(entry) = cache.entries.get(&key) {
            if entry.modified == stamp
                && entry.size == size
                && entry.reading == Some(TRANSCRIPT_READING_VERSION)
            {
                if let Some(trace) = &entry.trace {
                    return (entry.facts.clone(), trace.clone());
                }
            }
        }
    }
    let reading = transcript::read_transcript(path, cli_session_id);
    let mut cache = TRANSCRIPTS.lock().unwrap_or_else(|e| e.into_inner());
    loaded(&mut cache);
    cache.entries.insert(
        key,
        CachedTranscript {
            modified: stamp,
            size,
            facts: reading.0.clone(),
            trace: Some(reading.1.clone()),
            reading: Some(TRANSCRIPT_READING_VERSION),
        },
    );
    cache.dirty = true;
    reading
}

/// Writes the cache back, keeping only what this pass looked at, and folding
/// in anything another process learned meanwhile. Two Grafts can be walking
/// this at once, a launcher opening a shortcut while the app runs a pass of
/// its own, and the worst a lost entry costs is one transcript parsed twice.
pub fn save_transcript_cache(visited: &HashSet<String>) {
    let mut cache = TRANSCRIPTS.lock().unwrap_or_else(|e| e.into_inner());
    loaded(&mut cache);
    if !cache.dirty {
        return;
    }
    cache.dirty = false;
    cache.entries.retain(|path, _| visited.contains(path));
    let mut merged = cache.entries.clone();
    if let Some(on_disk) = read_cache_file(&transcript_cache_file()) {
        for (path, entry) in on_disk {
            if !merged.contains_key(&path) && visited.contains(&path) {
                merged.insert(path, entry);
            }
        }
    }
    let Ok(bytes) = serde_json::to_vec(&merged) else { return };
    let _ = graft_core::files::atomic_write(&transcript_cache_file(), &bytes, 0o644);
}

/// Which command line sessions one record speaks for.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct RecordSessions {
    /// The session it holds now.
    pub cli_session_id: Option<String>,
    /// The sessions it grew out of. A conversation carried on past a
    /// compaction, or resumed, gets a new command line session and the record
    /// keeps the old ids here.
    pub prior: Vec<String>,
    /// The record it was forked from, by name. A fork gets a record of its
    /// own, where a resume keeps the one it had.
    pub forked_from: Option<String>,
}

/// Parsed records, keyed by file and re-read only when the file changes. A
/// sweep of several stores walks hundreds of these every time, and each one is
/// small; reading them all is what the budget is for, not the parsing.
static RECORDS: Mutex<Option<HashMap<String, (SystemTime, u64, RecordSessions)>>> = Mutex::new(None);

pub fn sessions_of_record(path: &Path) -> RecordSessions {
    let meta = std::fs::metadata(path).ok();
    let stamp = meta.as_ref().and_then(|m| m.modified().ok()).unwrap_or(std::time::UNIX_EPOCH);
    let size = meta.map(|m| m.len()).unwrap_or(0);
    let key = paths::text(path);
    {
        let cache = RECORDS.lock().unwrap_or_else(|e| e.into_inner());
        if let Some((s, z, sessions)) = cache.as_ref().and_then(|c| c.get(&key)) {
            if *s == stamp && *z == size {
                return sessions.clone();
            }
        }
    }
    let record: Option<Value> = std::fs::read(path).ok().and_then(|b| serde_json::from_slice(&b).ok());
    let field = |name: &str| record.as_ref().and_then(|r| r.get(name)).and_then(Value::as_str).map(str::to_string);
    let sessions = RecordSessions {
        cli_session_id: field("cliSessionId"),
        prior: record
            .as_ref()
            .and_then(|r| r.get("priorCliSessionIds"))
            .and_then(Value::as_array)
            .map(|a| a.iter().filter_map(Value::as_str).map(str::to_string).collect())
            .unwrap_or_default(),
        forked_from: field("forkedFromSessionId"),
    };
    let mut cache = RECORDS.lock().unwrap_or_else(|e| e.into_inner());
    cache.get_or_insert_with(HashMap::new).insert(key, (stamp, size, sessions.clone()));
    sessions
}

/// Drop what a walk did not find. Claude re-files a session under a new record
/// name as it goes, so a cache keyed by path grows by one entry every time it
/// does, and the app runs for weeks and walks the stores on every dropdown. A
/// launcher would never notice; the app that is always running is the one
/// that pays.
pub fn forget_records_outside_of(walked: &HashSet<String>) {
    let mut cache = RECORDS.lock().unwrap_or_else(|e| e.into_inner());
    if let Some(map) = cache.as_mut() {
        map.retain(|path, _| walked.contains(path));
    }
}
