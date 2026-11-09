//! The conversations the tray and the dropdown name, and which of them is on
//! screen: what the relay carried, laid against what each Claude's own
//! records say about it.

use crate::wire::PoolSession;
use crate::Time;
use graft_core::{files, paths};
use serde_json::Value;
use std::collections::{BTreeMap, BTreeSet, HashMap};
use std::io::{Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};

/// A conversation the tray shows.
#[derive(Debug, Clone, PartialEq)]
pub struct Chat {
    pub id: String,
    pub title: Option<String>,
    pub claude: String,
    pub served_by: Option<String>,
    pub assigned: Option<String>,
    /// Every account it may be moved to, its own first.
    pub choices: Vec<String>,
    pub last_at: Time,
    /// The account it was meant for said no on its last request.
    pub passed_over: bool,
    /// Set for one the relay saw begin; see `PoolSession::started_at`.
    pub started_at: Option<Time>,
    /// When its Claude last showed it, from the record Claude keeps.
    pub focused_at: Option<Time>,
    /// When a person last typed into it; see `PoolSession::prompted_at`.
    pub prompted_at: Option<Time>,
    /// False for one named from its record alone, which the relay has not
    /// carried since it began: no account has answered it, and none will until
    /// it is written in again.
    pub carried: bool,
}

impl Chat {
    /// The latest sign of someone looking at it. Claude writes
    /// `lastFocusedAt` the moment its window switches to a conversation,
    /// which is the one thing that says which is on screen; a Claude that
    /// reads another's chats writes no records, and there the last thing typed
    /// is the next best word.
    pub fn attention(&self) -> Option<Time> {
        [self.focused_at, self.prompted_at, self.started_at].into_iter().flatten().fold(None, |best, t| {
            Some(best.map_or(t, |b: f64| b.max(t)))
        })
    }
}

/// What the record of a conversation says about it. A conversation archived,
/// or deleted from the sidebar, is one the person has put away, and the tray
/// stops naming it.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct Filed {
    pub title: Option<String>,
    pub archived: bool,
    pub gone: bool,
    pub focused_at: Option<Time>,
}

/// The conversation Claude last brought on screen in one profile, read from
/// the `lastFocusedAt` Claude writes into the records in its store.
#[derive(Debug, Clone, PartialEq)]
pub struct Focus {
    pub session: String,
    pub at: Time,
    /// What the record says of it, which is all there is to name a
    /// conversation by when the relay has not carried it.
    pub title: Option<String>,
    /// Archived, or its record deleted.
    pub put_away: bool,
}

impl Focus {
    pub fn new(session: &str, at: Time) -> Focus {
        Focus { session: session.to_string(), at, title: None, put_away: false }
    }
}

/// The conversation on screen in the Claude in front, which the tray names
/// for as long as it is there.
///
/// Claude's own word decides: the conversation its window focused last. One
/// the relay has not carried, such as an old chat opened to read, is among
/// `chats` by its record alone (`uncarried`). A focus matching none of them,
/// because that conversation was put away, names nothing rather than the
/// conversation before it, which is the one thing certain not to be on
/// screen. A Claude whose records say nothing of focus falls back on the
/// conversation typed into last, and only when nothing says even that, on the
/// one that last heard back.
pub fn current(chats: &[Chat], focused: Option<&Focus>, now: Time) -> Option<Chat> {
    if let Some(focused) = focused {
        if let Some(shown) = chats.iter().find(|c| c.id == focused.session) {
            return Some(shown.clone());
        }
        if chats.iter().all(|c| c.attention().unwrap_or(f64::NEG_INFINITY) < focused.at) {
            return None;
        }
    }
    let mut best: Option<&Chat> = None;
    for chat in chats.iter().filter(|c| now - c.last_at < 30.0 * 60.0) {
        let better = match best {
            None => true,
            Some(b) => {
                let (x, y) = (b.attention().unwrap_or(f64::NEG_INFINITY), chat.attention().unwrap_or(f64::NEG_INFINITY));
                if x != y {
                    x < y
                } else {
                    b.last_at < chat.last_at
                }
            }
        };
        if better {
            best = Some(chat);
        }
    }
    best.cloned()
}

/// The conversation on screen when the relay has never carried it, or has
/// forgotten it after a day: a chat from before sharing began, opened to read.
/// Its record is the only thing that knows its name, and no account has
/// answered it, so the tray says the name alone. One the relay did carry is
/// left to its own row, which an archived or deleted one has already left;
/// named from its record, it would be back on the tray.
pub fn uncarried(
    focus: &Focus,
    claude: &str,
    carried: &[PoolSession],
    choices: Vec<String>,
    assigned: Option<String>,
) -> Option<Chat> {
    if focus.put_away || carried.iter().any(|s| s.id == focus.session) {
        return None;
    }
    Some(Chat {
        id: focus.session.clone(),
        title: focus.title.clone(),
        claude: claude.to_string(),
        served_by: None,
        assigned,
        choices,
        last_at: focus.at,
        passed_over: false,
        started_at: None,
        focused_at: Some(focus.at),
        prompted_at: None,
        carried: false,
    })
}

/// The account answering a conversation now: the one it was moved to, until
/// that one has no room and another answers in its place, or else the one that
/// answered last. Right after a move nothing has answered from there yet, and
/// naming the account that answered before it read as though the choice had
/// not taken.
pub fn answering_key(chat: &Chat) -> Option<String> {
    match &chat.assigned {
        Some(assigned) if !chat.passed_over => Some(assigned.clone()),
        _ => chat.served_by.clone().or_else(|| chat.assigned.clone()),
    }
}

/// Where a Claude keeps its conversations' records, as a file watch names it:
/// a link on the way is followed first, since a watch on a link watches the
/// link and not the folder it names.
pub fn record_store(profile: &Path) -> PathBuf {
    let store = profile.join("claude-code-sessions");
    std::fs::canonicalize(&store).unwrap_or(store)
}

/// Where Claude Code keeps its transcripts, one folder per project.
pub fn claude_projects() -> PathBuf {
    paths::home().join(".claude/projects")
}

const STASH_SUFFIX: &str = ".graft-own";
const NON_ACCOUNT_STORE_ITEMS: [&str; 1] = ["skills-plugin"];

#[derive(Debug, Clone, Default, PartialEq)]
pub struct Records {
    /// By session.
    pub filed: BTreeMap<String, Filed>,
    /// By Claude key.
    pub focused: BTreeMap<String, Focus>,
}

#[derive(Clone)]
struct Cached {
    stamp: Option<std::time::SystemTime>,
    session: Option<String>,
    filed: Filed,
}

/// What the walk over the records remembers from one pass to the next, which
/// is what tells a record deleted from one caught being rewritten, and a focus
/// a store wrote from one the mirror copied into it. One per window, on the
/// thread that walks.
#[derive(Default)]
pub struct RecordWatch {
    cache: HashMap<String, Cached>,
    /// Sessions a record was seen for, in their own Claude's store and in
    /// another's. One of them with no record any more was deleted from its
    /// sidebar; one never seen is too new to have one.
    ever_own: BTreeSet<String>,
    ever_elsewhere: BTreeSet<String>,
    missing_before: BTreeSet<String>,
    /// By Claude key.
    last_focus: BTreeMap<String, Focus>,
    /// Every focus a store holds, against the stores it was first seen in.
    focus_origins: HashMap<(String, u64), BTreeSet<String>>,
    /// Claudes whose last focus had no record on the previous pass.
    focus_missing_before: BTreeSet<String>,
    transcript_titles: HashMap<String, (Option<std::time::SystemTime>, Option<String>)>,
    transcript_paths: HashMap<String, PathBuf>,
    transcript_misses: HashMap<String, Time>,
}

impl RecordWatch {
    pub fn new() -> RecordWatch {
        RecordWatch::default()
    }

    /// What each conversation's record in a sidebar says, found by the session
    /// id every request carries, which is the record's `cliSessionId`. Records
    /// are read again only when their files change, and each is dated with
    /// one `lstat`, since this runs over every record of every Claude every two
    /// seconds while anything is shared.
    pub fn look_up(&mut self, sessions: &[PoolSession], profiles: &[(String, PathBuf)], now: Time) -> Records {
        // Every conversation the relay remembers, not only the last half
        // hour's: the one on screen may have been quiet for longer.
        let mut from: BTreeMap<String, String> = BTreeMap::new();
        for session in sessions {
            from.entry(session.id.clone()).or_insert_with(|| session.from.clone());
        }
        // Every record each store holds, by session: the one with the newest
        // focus where a session has a record under two accounts.
        let mut held: BTreeMap<String, BTreeMap<String, Filed>> = BTreeMap::new();
        // A conversation is put away in the Claude it is in; the same session
        // listed by another profile that shares its chats says nothing of that.
        let mut own: BTreeMap<String, Vec<Filed>> = BTreeMap::new();
        let mut elsewhere: BTreeMap<String, Vec<Filed>> = BTreeMap::new();
        let mut markers: BTreeSet<String> = BTreeSet::new();
        let mut walked: BTreeSet<String> = BTreeSet::new();
        // A store that would not open is not an empty one, and what it holds
        // is not gone; nothing is judged missing on such a pass.
        let mut unreadable = false;
        let mut list = |dir: &Path| -> Vec<String> {
            match std::fs::read_dir(dir) {
                Ok(entries) => {
                    let mut names: Vec<String> =
                        entries.flatten().map(|e| e.file_name().to_string_lossy().into_owned()).collect();
                    names.sort();
                    names
                }
                Err(e) if e.kind() == std::io::ErrorKind::NotFound => Vec::new(),
                Err(_) => {
                    unreadable = true;
                    Vec::new()
                }
            }
        };
        for (key, profile) in profiles {
            let store = profile.join("claude-code-sessions");
            // Walked as the sweep walks it: a stash holds what a profile had
            // before it was grafted, and a deleted conversation's old record
            // there would keep it on the tray.
            for account in list(&store) {
                if account.starts_with('.')
                    || account.ends_with(STASH_SUFFIX)
                    || NON_ACCOUNT_STORE_ITEMS.contains(&account.as_str())
                {
                    continue;
                }
                let account_dir = store.join(&account);
                if !account_dir.is_dir() {
                    continue;
                }
                for org in list(&account_dir) {
                    if org.starts_with('.') || org.ends_with(STASH_SUFFIX) {
                        continue;
                    }
                    let org_dir = account_dir.join(&org);
                    if !org_dir.is_dir() {
                        continue;
                    }
                    for name in list(&org_dir) {
                        if let Some(marked) = name.strip_prefix("deleted_") {
                            markers.insert(marked.to_string());
                            continue;
                        }
                        if !name.starts_with("local_") || !name.ends_with(".json") {
                            continue;
                        }
                        let path = org_dir.join(&name);
                        let text = paths::text(&path);
                        walked.insert(text.clone());
                        let stamp = files::modification_time(&path);
                        let fresh = match self.cache.get(&text) {
                            Some(c) if c.stamp == stamp => None,
                            _ => Some(read_record(&path, stamp)),
                        };
                        if let Some(fresh) = fresh {
                            self.cache.insert(text.clone(), fresh);
                        }
                        let entry = &self.cache[&text];
                        let Some(session) = entry.session.clone() else { continue };
                        let record = entry.filed.clone();
                        let newer = record.focused_at.unwrap_or(f64::NEG_INFINITY)
                            >= held.get(key).and_then(|h| h.get(&session)).and_then(|f| f.focused_at).unwrap_or(f64::NEG_INFINITY);
                        if newer {
                            held.entry(key.clone()).or_default().insert(session.clone(), record.clone());
                        }
                        let Some(owner) = from.get(&session) else { continue };
                        if owner == key {
                            own.entry(session.clone()).or_default().push(record);
                            self.ever_own.insert(session);
                        } else {
                            elsewhere.entry(session.clone()).or_default().push(record);
                            self.ever_elsewhere.insert(session);
                        }
                    }
                }
            }
        }
        self.cache.retain(|path, _| walked.contains(path));

        // A focus belongs to the store it turned up in first. The mirror
        // carries a record across with its focus, and read as this store's
        // own, the copy named the conversation another Claude had shown as the
        // one on screen here. One that several stores held when it was first
        // seen, as everything is on the first pass, counts for each.
        let mut origins: HashMap<(String, u64), BTreeSet<String>> = HashMap::new();
        for (key, records) in &held {
            for (session, record) in records {
                let Some(at) = record.focused_at else { continue };
                let stamp = (session.clone(), at.to_bits());
                if let Some(first) = self.focus_origins.get(&stamp) {
                    origins.insert(stamp, first.clone());
                } else {
                    origins.entry(stamp).or_default().insert(key.clone());
                }
            }
        }
        self.focus_origins = origins;
        let mut focused: BTreeMap<String, Focus> = BTreeMap::new();
        for (key, records) in &held {
            for (session, record) in records {
                let Some(at) = record.focused_at else { continue };
                let later = at > focused.get(key).map(|f| f.at).unwrap_or(f64::NEG_INFINITY);
                let owned = self.focus_origins.get(&(session.clone(), at.to_bits())).is_some_and(|o| o.contains(key));
                if later && owned {
                    focused.insert(
                        key.clone(),
                        Focus { session: session.clone(), at, title: record.title.clone(), put_away: record.archived },
                    );
                }
            }
        }

        // Deleting the conversation on screen takes its record, and with it
        // the latest focus in the store; the one focused before it is not what
        // Claude shows now, so the focus that went stays the word until Claude
        // writes a newer one. So does a focus the mirror wrote over with
        // another Claude's, since this Claude still shows that conversation. A
        // store that would not open keeps its last focus too.
        let mut focus_missing = BTreeSet::new();
        for (key, last) in &self.last_focus {
            if !profiles.iter().any(|(k, _)| k == key) {
                continue;
            }
            if !focused.get(key).map(|f| f.at < last.at).unwrap_or(true) {
                continue;
            }
            let mut kept = last.clone();
            if let Some(record) = held.get(key).and_then(|h| h.get(&last.session)) {
                kept.title = record.title.clone().or(kept.title);
                kept.put_away = record.archived;
            } else if !unreadable {
                // Deleted, but only once it is missing on two passes running:
                // a pass woken by Claude's write can land before the record is
                // moved into place.
                focus_missing.insert(key.clone());
                if self.focus_missing_before.contains(key) {
                    kept.put_away = true;
                }
            }
            focused.insert(key.clone(), kept);
        }
        if !unreadable {
            self.focus_missing_before = focus_missing;
        }
        let untitled: Vec<(String, String)> = focused
            .iter()
            .filter(|(_, f)| f.title.is_none() && !f.put_away)
            .map(|(k, f)| (k.clone(), f.session.clone()))
            .collect();
        for (key, session) in untitled {
            let title = self.transcript_title(&session, now);
            if let Some(focus) = focused.get_mut(&key) {
                focus.title = title;
            }
        }
        self.last_focus = focused.clone();

        let mut found = BTreeMap::new();
        let mut missing = BTreeSet::new();
        for session in from.keys() {
            // Its own Claude's record decides. A copy in a store that shares
            // its chats lingers until the next pass that mirrors them, and read
            // first it kept a deleted conversation on the tray.
            let mine = own.get(session).cloned().unwrap_or_default();
            let records = if mine.is_empty() { elsewhere.get(session).cloned().unwrap_or_default() } else { mine.clone() };
            let mut filed = Filed {
                title: records.iter().find_map(|r| r.title.clone()),
                archived: !records.is_empty() && records.iter().all(|r| r.archived),
                gone: false,
                focused_at: records.iter().filter_map(|r| r.focused_at).fold(None, |m: Option<f64>, t| Some(m.map_or(t, |m| m.max(t)))),
            };
            // A marker names a session only for a record Graft filed; one
            // Claude named itself is known gone by having been seen before and
            // missing on two passes running, since a file rewritten in place
            // can be caught between the two.
            let vanished = !unreadable
                && ((mine.is_empty() && self.ever_own.contains(session))
                    || (records.is_empty() && self.ever_elsewhere.contains(session)));
            if vanished {
                missing.insert(session.clone());
            }
            filed.gone = markers.contains(session) || (vanished && self.missing_before.contains(session));
            if filed.title.is_none() {
                filed.title = self.transcript_title(session, now);
            }
            found.insert(session.clone(), filed);
        }
        if !unreadable {
            self.missing_before = missing;
        }
        Records { filed: found, focused }
    }

    /// Read again only when the transcript has changed, since a conversation
    /// on screen that has no record would otherwise have its last quarter
    /// megabyte read every two seconds; and looked for again only once a
    /// minute when there is none, since the relay remembers a day of them.
    fn transcript_title(&mut self, session: &str, now: Time) -> Option<String> {
        let path = match self.transcript_paths.get(session) {
            Some(known) if known.exists() => known.clone(),
            _ => {
                if self.transcript_misses.get(session).is_some_and(|missed| now - missed < 60.0) {
                    return None;
                }
                let Some(found) = transcript(session) else {
                    self.transcript_misses.insert(session.to_string(), now);
                    return None;
                };
                self.transcript_misses.remove(session);
                self.transcript_paths.insert(session.to_string(), found.clone());
                found
            }
        };
        let stamp = files::modification_time(&path);
        if let Some((known, title)) = self.transcript_titles.get(session) {
            if *known == stamp {
                return title.clone();
            }
        }
        let title = transcript_title_at(&path);
        self.transcript_titles.insert(session.to_string(), (stamp, title.clone()));
        title
    }
}

fn read_record(path: &Path, stamp: Option<std::time::SystemTime>) -> Cached {
    let record = match files::read_json(path) {
        files::JsonRead::Value(Value::Object(map)) => Some(map),
        _ => None,
    };
    let get = |name: &str| record.as_ref().and_then(|r| r.get(name));
    Cached {
        stamp,
        session: get("cliSessionId").and_then(Value::as_str).map(str::to_string),
        filed: Filed {
            title: get("title").and_then(Value::as_str).filter(|t| !t.is_empty()).map(str::to_string),
            archived: get("isArchived").and_then(Value::as_bool).unwrap_or(false),
            gone: false,
            focused_at: get("lastFocusedAt").and_then(Value::as_f64).map(|ms| ms / 1000.0),
        },
    }
}

pub fn transcript(session: &str) -> Option<PathBuf> {
    let projects = claude_projects();
    let names = files::listing(&projects)?;
    names.into_iter().map(|name| projects.join(name).join(format!("{session}.jsonl"))).find(|p| p.exists())
}

/// A conversation that has no record yet still has its transcript, and the
/// command line writes the title it settles on there as it goes.
pub fn transcript_title(session: &str) -> Option<String> {
    transcript_title_at(&transcript(session)?)
}

pub fn transcript_title_at(path: &Path) -> Option<String> {
    let mut file = std::fs::File::open(path).ok()?;
    let size = file.seek(SeekFrom::End(0)).ok()?;
    let start = size.saturating_sub(262_144);
    file.seek(SeekFrom::Start(start)).ok()?;
    let mut tail = Vec::new();
    file.read_to_end(&mut tail).ok()?;
    for key in [&br#""customTitle":""#[..], br#""aiTitle":""#] {
        let Some(at) = crate::rfind(&tail, key) else { continue };
        // Up to the first quote no backslash escapes, and read as JSON: a
        // title with a quote in it was cut off at the quote, and one with a
        // dash came out as its escape.
        let open = at + key.len() - 1;
        let mut escaped = false;
        let mut close = None;
        for (offset, byte) in tail[open + 1..].iter().enumerate() {
            if !escaped && *byte == b'"' {
                close = Some(open + 1 + offset);
                break;
            }
            escaped = !escaped && *byte == b'\\';
        }
        let Some(close) = close else { continue };
        if let Ok(title) = serde_json::from_slice::<String>(&tail[open..=close]) {
            if !title.is_empty() {
                return Some(title);
            }
        }
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;
    use graft_core::testing::Sandbox;
    use serde_json::json;

    fn now() -> Time {
        crate::now()
    }

    fn chat(id: &str, quiet: f64, focused: Option<f64>, typed: Option<f64>, begun: Option<f64>, at: Time) -> Chat {
        Chat {
            id: id.into(),
            title: None,
            claude: "main".into(),
            served_by: Some("main".into()),
            assigned: None,
            choices: vec!["main".into(), "c2".into()],
            last_at: at - quiet,
            passed_over: false,
            started_at: begun.map(|b| at - b),
            focused_at: focused.map(|f| at - f),
            prompted_at: typed.map(|t| at - t),
            carried: true,
        }
    }

    fn focus(session: &str, ago: f64, at: Time) -> Focus {
        Focus::new(session, at - ago)
    }

    #[test]
    fn the_tray_names_the_conversation_claude_last_brought_on_screen_whatever_was_typed_elsewhere() {
        let t = now();
        let chats = [chat("a", 0.0, None, Some(5.0), None, t), chat("b", 900.0, Some(60.0), None, None, t)];
        assert_eq!(current(&chats, Some(&focus("b", 60.0, t)), t).unwrap().id, "b");
    }

    #[test]
    fn two_conversations_with_one_name_are_told_apart_by_their_session_never_their_title() {
        let t = now();
        let mut twin = chat("a", 0.0, Some(10.0), None, None, t);
        let mut other = chat("b", 0.0, Some(30.0), None, None, t);
        twin.title = Some("Is 391 prime?".into());
        other.title = Some("Is 391 prime?".into());
        let both = [twin, other];
        assert_eq!(current(&both, Some(&focus("a", 10.0, t)), t).unwrap().id, "a");
        assert_eq!(current(&both, Some(&focus("b", 5.0, t)), t).unwrap().id, "b");
    }

    #[test]
    fn the_tray_goes_on_naming_it_however_long_it_has_been_quiet() {
        let t = now();
        let quiet = [chat("a", 3.0 * 3600.0, Some(3.0 * 3600.0), None, None, t)];
        assert_eq!(current(&quiet, Some(&focus("a", 3.0 * 3600.0, t)), t).unwrap().id, "a");
    }

    #[test]
    fn a_conversation_on_screen_that_the_tray_may_not_name_names_nothing_rather_than_the_one_before() {
        let t = now();
        let chats = [chat("a", 0.0, Some(300.0), Some(30.0), None, t)];
        assert!(current(&chats, Some(&focus("x", 2.0, t)), t).is_none());
    }

    #[test]
    fn a_conversation_opened_that_the_relay_has_not_carried_is_named_from_its_record() {
        let t = now();
        let mut unsent = focus("old", 2.0, t);
        unsent.title = Some("Before sharing began".into());
        let alone = uncarried(&unsent, "main", &[], vec!["main".into(), "c2".into()], None).unwrap();
        assert!(alone.title.as_deref() == Some("Before sharing began") && !alone.carried);
        let chats = [chat("a", 0.0, Some(300.0), Some(30.0), None, t), alone.clone()];
        assert_eq!(current(&chats, Some(&unsent), t).unwrap().id, "old");
        assert_eq!(answering_key(&alone), None, "by its title alone, since no account has answered it");

        let mut archived = unsent.clone();
        archived.put_away = true;
        let carried_old = PoolSession {
            id: "old".into(),
            from: "main".into(),
            served_by: Some("main".into()),
            last_at: t,
            requests: 1,
            model: None,
            cached_until: None,
            passed_over: vec![],
            started_at: None,
            prompted_at: None,
        };
        assert!(
            uncarried(&archived, "main", &[], vec![], None).is_none()
                && uncarried(&unsent, "main", &[carried_old], vec![], None).is_none(),
            "but not once it is put away, nor when the relay carried it and its own row decides"
        );
    }

    #[test]
    fn with_no_word_from_claude_the_conversation_typed_into_last_is_named() {
        let t = now();
        assert_eq!(current(&[chat("a", 0.0, None, Some(5.0), None, t)], Some(&focus("x", 600.0, t)), t).unwrap().id, "a");
        let chats = [chat("a", 0.0, None, Some(120.0), None, t), chat("b", 0.0, None, None, Some(30.0), t)];
        assert_eq!(current(&chats, None, t).unwrap().id, "b", "or begun last");
        let chats = [chat("a", 60.0, None, None, None, t), chat("b", 10.0, None, None, None, t)];
        assert_eq!(current(&chats, None, t).unwrap().id, "b", "and with nothing to say even that, the one that last heard back");
        let chats = [chat("a", 31.0 * 60.0, None, Some(31.0 * 60.0), None, t)];
        assert!(current(&chats, None, t).is_none(), "a conversation quiet for half an hour is not guessed to be on screen");
    }

    #[test]
    fn a_conversation_just_moved_names_the_account_it_was_moved_to() {
        let t = now();
        let mut moved = chat("a", 0.0, None, None, None, t);
        moved.assigned = Some("c2".into());
        assert_eq!(answering_key(&moved).as_deref(), Some("c2"));
        moved.passed_over = true;
        assert_eq!(answering_key(&moved).as_deref(), Some("main"), "and the one that answered in its place once it had no room");
        assert_eq!(answering_key(&chat("a", 0.0, None, None, None, t)).as_deref(), Some("main"));
    }

    struct Stores {
        main: PathBuf,
        other: PathBuf,
    }

    const ACCOUNT: &str = "acct-1";
    const ORG: &str = "org-1";

    fn store(profile: &Path) -> PathBuf {
        let dir = profile.join("claude-code-sessions").join(ACCOUNT).join(ORG);
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    fn record(profile: &Path, name: &str, session: &str, title: Option<&str>, archived: bool, focused: Option<f64>, at: Time) {
        let mut object = json!({"sessionId": format!("local_{name}"), "cliSessionId": session, "isArchived": archived});
        if let Some(title) = title {
            object["title"] = json!(title);
        }
        if let Some(ago) = focused {
            object["lastFocusedAt"] = json!(((at - ago) * 1000.0) as i64);
        }
        let path = store(profile).join(format!("local_{name}.json"));
        std::fs::write(&path, serde_json::to_vec(&object).unwrap()).unwrap();
        // A second write within one tick of the clock would keep its date, and
        // the walk would take it for the record it already read.
        bump(&path);
    }

    fn bump(path: &Path) {
        use std::sync::atomic::{AtomicU64, Ordering};
        static TICK: AtomicU64 = AtomicU64::new(1);
        let n = TICK.fetch_add(1, Ordering::SeqCst);
        let when = std::time::UNIX_EPOCH + std::time::Duration::from_secs(1_700_000_000 + n);
        let file = std::fs::File::options().write(true).open(path).unwrap();
        file.set_modified(when).unwrap();
    }

    fn gone(profile: &Path, name: &str) {
        let _ = std::fs::remove_file(store(profile).join(format!("local_{name}.json")));
    }

    fn session(id: &str, at: Time) -> PoolSession {
        PoolSession {
            id: id.into(),
            from: "main".into(),
            served_by: Some("main".into()),
            last_at: at,
            requests: 1,
            model: None,
            cached_until: None,
            passed_over: vec![],
            started_at: None,
            prompted_at: None,
        }
    }

    fn laid_out(sandbox: &Sandbox, t: Time) -> Stores {
        let stores = Stores { main: sandbox.root.path().join("on-screen/Claude"), other: sandbox.root.path().join("on-screen/Claude-2") };
        record(&stores.main, "r-open", "s-open", Some("Is 391 prime?"), false, Some(60.0), t);
        record(&stores.other, "r-open", "s-open", Some("Is 391 prime?"), true, None, t);
        record(&stores.main, "r-earlier", "s-earlier", Some("Earlier"), false, Some(300.0), t);
        record(&stores.main, "r-archived", "s-archived", Some("Old idea"), true, Some(600.0), t);
        record(&stores.main, "s-marked", "s-marked", Some("Filed by Graft"), false, None, t);
        std::fs::write(store(&stores.main).join("deleted_s-marked"), b"1762600000000").unwrap();
        record(&stores.main, "r-deleted", "s-deleted", Some("Throwaway"), false, None, t);
        record(&stores.main, "r-mirrored", "s-mirrored", Some("Mirrored"), false, None, t);
        record(&stores.other, "r-mirrored", "s-mirrored", Some("Mirrored"), false, None, t);
        let stash = stores.main.join("claude-code-sessions").join(ACCOUNT).join(format!(".{ORG}{STASH_SUFFIX}"));
        std::fs::create_dir_all(&stash).unwrap();
        std::fs::write(stash.join("local_r-stashed.json"), br#"{"cliSessionId":"s-deleted","title":"Stashed"}"#).unwrap();
        let projects = claude_projects().join("-home-test-on-screen");
        std::fs::create_dir_all(&projects).unwrap();
        std::fs::write(
            projects.join("s-untitled.jsonl"),
            concat!(
                r#"{"type":"user","message":{"role":"user","content":"hi"},"sessionId":"s-untitled"}"#,
                "\n",
                r#"{"type":"custom-title","customTitle":"Prime \"numbers\" — checked","sessionId":"s-untitled"}"#
            ),
        )
        .unwrap();
        stores
    }

    #[test]
    fn a_conversation_is_named_by_the_record_its_claude_keeps() {
        let sandbox = Sandbox::new();
        let t = now();
        let stores = laid_out(&sandbox, t);
        let sessions: Vec<PoolSession> = ["s-open", "s-earlier", "s-archived", "s-marked", "s-deleted", "s-mirrored", "s-untitled"]
            .iter()
            .map(|s| session(s, t))
            .collect();
        let profiles = vec![("main".to_string(), stores.main.clone()), ("c2".to_string(), stores.other.clone())];
        let mut watch = RecordWatch::new();
        let first = watch.look_up(&sessions, &profiles, t);
        let open = &first.filed["s-open"];
        assert!(open.title.as_deref() == Some("Is 391 prime?") && !open.archived && !open.gone);
        assert!(
            (open.focused_at.unwrap() - (t - 60.0)).abs() < 0.01
                && first.focused["main"]
                    == Focus { session: "s-open".into(), at: open.focused_at.unwrap(), title: Some("Is 391 prime?".into()), put_away: false },
            "and the one Claude focused last is read from the milliseconds Claude writes, with its title"
        );
        assert!(first.filed["s-archived"].archived, "a conversation archived in its sidebar is put away");
        assert!(!open.archived, "a copy archived in a Claude that shares the chats does not put away the one in its own");
        assert!(first.filed["s-marked"].gone, "a record of Graft's with a deletion marker beside it is gone at once");
        assert_eq!(
            first.filed["s-untitled"].title.as_deref(),
            Some("Prime \"numbers\" \u{2014} checked"),
            "a conversation with no record yet takes its title from the transcript, quotes and dashes whole"
        );
        assert_eq!(first.filed["s-deleted"].title.as_deref(), Some("Throwaway"), "a stash beside the store is not read as a record");

        gone(&stores.main, "r-deleted");
        gone(&stores.main, "r-mirrored");
        let second = watch.look_up(&sessions, &profiles, t);
        assert!(!second.filed["s-deleted"].gone, "a record caught missing once may be mid-rewrite, so nothing is gone yet");
        let third = watch.look_up(&sessions, &profiles, t);
        assert!(third.filed["s-deleted"].gone, "a record seen before and missing on two passes was deleted from its sidebar");
        assert!(third.filed["s-mirrored"].gone, "and a copy still in a Claude that shares the chats does not keep it");

        gone(&stores.main, "r-open");
        let after = watch.look_up(&sessions, &profiles, t);
        let earlier = [chat("s-earlier", 0.0, Some(300.0), None, None, t)];
        assert!(
            after.focused["main"].session == "s-open" && current(&earlier, after.focused.get("main"), t).is_none(),
            "deleting the conversation on screen names nothing, never the one focused before it"
        );
    }

    #[test]
    fn a_store_that_will_not_open_hides_nothing() {
        use std::os::unix::fs::PermissionsExt;
        let sandbox = Sandbox::new();
        let t = now();
        let stores = laid_out(&sandbox, t);
        let sessions: Vec<PoolSession> = ["s-open", "s-earlier"].iter().map(|s| session(s, t)).collect();
        let profiles = vec![("main".to_string(), stores.main.clone()), ("c2".to_string(), stores.other.clone())];
        let mut watch = RecordWatch::new();
        let _ = watch.look_up(&sessions, &profiles, t);
        gone(&stores.main, "r-earlier");
        let org = store(&stores.main);
        std::fs::set_permissions(&org, std::fs::Permissions::from_mode(0o000)).unwrap();
        let readable = std::fs::read_dir(&org).is_ok();
        let _ = watch.look_up(&sessions, &profiles, t);
        let unopened = watch.look_up(&sessions, &profiles, t);
        std::fs::set_permissions(&org, std::fs::Permissions::from_mode(0o755)).unwrap();
        if readable {
            // Run as root, where no permission keeps a folder shut, there is
            // nothing to check.
            return;
        }
        assert!(
            !unopened.filed["s-earlier"].gone && unopened.focused["main"].session == "s-open",
            "and what it held last still stands"
        );
    }

    #[test]
    fn a_focus_the_mirror_copies_in_from_another_claude_is_that_claudes() {
        let sandbox = Sandbox::new();
        let t = now();
        let here = sandbox.root.path().join("mirror-focus/Claude");
        let there = sandbox.root.path().join("mirror-focus/Claude-2");
        let pair = vec![("main".to_string(), here.clone()), ("c2".to_string(), there.clone())];
        let mut watch = RecordWatch::new();
        record(&here, "m-a", "m-a", Some("Here"), false, Some(120.0), t);
        record(&there, "m-a", "m-a", Some("Here"), false, Some(120.0), t);
        let _ = watch.look_up(&[], &pair, t);
        record(&there, "m-b", "m-b", Some("There"), false, Some(30.0), t);
        let seen_there = watch.look_up(&[], &pair, t);
        record(&here, "m-b", "m-b", Some("There"), false, Some(30.0), t);
        let copied_here = watch.look_up(&[], &pair, t);
        assert!(
            seen_there.focused["c2"].session == "m-b"
                && copied_here.focused["c2"].session == "m-b"
                && copied_here.focused["main"].session == "m-a",
            "and this one still shows its own"
        );
        record(&there, "m-a", "m-a", Some("Here"), false, Some(20.0), t);
        let _ = watch.look_up(&[], &pair, t);
        record(&here, "m-a", "m-a", Some("Here, renamed"), false, Some(20.0), t);
        let overwritten = watch.look_up(&[], &pair, t);
        assert!(
            overwritten.focused["main"].session == "m-a"
                && !overwritten.focused["main"].put_away
                && overwritten.focused["main"].title.as_deref() == Some("Here, renamed"),
            "and keeps showing it when the mirror writes another Claude's focus over its record"
        );
        record(&here, "m-c", "m-c", Some("Back here"), false, Some(1.0), t);
        let back = watch.look_up(&[], &pair, t);
        assert!(
            back.focused["main"].session == "m-c" && back.focused["main"].title.as_deref() == Some("Back here"),
            "a conversation this Claude brings on screen afterwards is its own again"
        );
        gone(&here, "m-c");
        let once = watch.look_up(&[], &pair, t);
        let twice = watch.look_up(&[], &pair, t);
        assert!(
            once.focused["main"].session == "m-c"
                && !once.focused["main"].put_away
                && twice.focused["main"].session == "m-c"
                && twice.focused["main"].put_away,
            "the conversation on screen is deleted once its record is missing on two passes, not one caught mid-write"
        );
        let projects = claude_projects().join("-home-test");
        std::fs::create_dir_all(&projects).unwrap();
        std::fs::write(projects.join("s-untitled.jsonl"), r#"{"customTitle":"From the transcript"}"#).unwrap();
        record(&here, "m-d", "s-untitled", None, false, Some(0.5), t);
        assert_eq!(
            watch.look_up(&[], &pair, t).focused["main"].title.as_deref(),
            Some("From the transcript"),
            "one on screen whose record has no title yet takes it from the transcript"
        );
    }

    #[test]
    fn a_records_date_read_with_one_lstat_is_a_links_own_date_and_not_its_files() {
        let dir = graft_core::testing::tempfile_lite::TempDir::new();
        let file = dir.path().join("stamped.json");
        let link = dir.path().join("stamped-link.json");
        std::fs::write(&file, b"{}").unwrap();
        bump(&file);
        std::os::unix::fs::symlink(&file, &link).unwrap();
        assert_ne!(files::modification_time(&link), files::modification_time(&file));
        assert!(files::modification_time(&dir.path().join("never-stamped.json")).is_none());
    }
}
