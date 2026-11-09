//! What the chat mirror knows about this machine's transcripts, lent to
//! Search Chats. Every method only reads: the sweep's cache is read from its
//! file and never written from here, so a search can never leave the sweep
//! trusting an entry it did not make.

use graft_chats::{CachedTranscript, SessionFacts};
use graft_web::{TranscriptFacts, Transcripts};
use std::collections::{BTreeMap, HashSet};
use std::path::Path;
use std::sync::Mutex;
use std::time::SystemTime;

type Cache = BTreeMap<String, CachedTranscript>;

#[derive(Default)]
pub struct ChatTranscripts {
    /// The cache file as last read, against its own modification time, so a
    /// search over hundreds of transcripts reads it once.
    loaded: Mutex<Option<(Option<SystemTime>, Cache)>>,
}

fn facts(facts: &SessionFacts) -> TranscriptFacts {
    TranscriptFacts {
        bridge_ids: facts.bridge_ids.clone(),
        owner_account: facts.owner_account.clone(),
        owner_organization: facts.owner_organization.clone(),
        title: facts.title.clone(),
        cwd: facts.cwd.clone(),
        last_activity_ms: facts.last_activity_at,
    }
}

/// An entry stands for the file only while the file is as it was read. The
/// sweep keeps the time as seconds in a float, and the search works it out
/// from the seconds and nanoseconds apart, so the two are compared to the
/// millisecond rather than for equality.
pub fn entry_matches(entry: &CachedTranscript, modified: f64, size: u64) -> bool {
    (entry.modified - modified).abs() < 0.001 && entry.size == size as i64
}

impl Transcripts for ChatTranscripts {
    fn cached(&self, transcript: &Path, modified: f64, size: u64) -> Option<TranscriptFacts> {
        let file = graft_chats::transcript_cache_file();
        let stamp = graft_core::files::modification_time(&file);
        let mut loaded = self.loaded.lock().unwrap_or_else(|e| e.into_inner());
        if loaded.as_ref().map(|(s, _)| *s != stamp).unwrap_or(true) {
            let cache: Cache = std::fs::read(&file).ok().and_then(|b| serde_json::from_slice(&b).ok()).unwrap_or_default();
            *loaded = Some((stamp, cache));
        }
        let (_, cache) = loaded.as_ref()?;
        let entry = cache.get(&graft_core::paths::text(transcript))?;
        if !entry_matches(entry, modified, size) {
            return None;
        }
        entry.facts.as_ref().map(facts)
    }

    fn parse(&self, transcript: &Path, cli_session_id: &str) -> Option<TranscriptFacts> {
        graft_chats::read_transcript(transcript, cli_session_id).0.as_ref().map(facts)
    }

    fn folder_is_here(&self, cwd: &str) -> bool {
        graft_chats::session_folder_is_here(cwd)
    }

    fn carried_on(&self) -> HashSet<String> {
        graft_chats::session_store_contents().superseded.into_iter().collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_cached_entry_stands_only_for_the_file_as_it_was_read() {
        let entry = CachedTranscript { modified: 1_700_000_000.25, size: 512, facts: None, trace: None, reading: Some(2) };
        assert!(entry_matches(&entry, 1_700_000_000.2500004, 512), "the same time, worked out another way, still matches");
        assert!(!entry_matches(&entry, 1_700_000_001.25, 512), "a file written since is read again");
        assert!(!entry_matches(&entry, 1_700_000_000.25, 513), "and so is one that grew");
    }
}
