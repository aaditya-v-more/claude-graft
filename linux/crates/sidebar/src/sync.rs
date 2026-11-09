//! Bringing the sidebars of profiles that share a chat history to agree,
//! just before one of them opens.
//!
//! Runs only with every profile connected to that history closed, and only
//! inside `graft_core::lock::with_launch_lock`, which the launchers and the
//! window both take from deciding a profile is closed to having started
//! Claude on it. Otherwise two shortcuts could each observe closed profiles
//! and open the same databases at once.

use crate::snapshot::{merge, Snapshot};
use crate::{storage, Failure};
use graft_core::files::{self, JsonRead};
use graft_core::{claude, diagnostics, lock, paths};
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};
use std::collections::{BTreeMap, BTreeSet, HashMap};
use std::fs::OpenOptions;
use std::os::unix::io::AsRawFd;
use std::path::{Path, PathBuf};

const STATE_FILE: &str = "sidebar-sync.json";
const STATUS_FILE: &str = "sidebar-sync-status.json";

/// Written by the chat sharing, read here and never written: the pairs of
/// folders whose histories are mirrored, each key the borrowing folder and
/// the source joined by a NUL, which no path can contain.
const MIRROR_FILE: &str = "mirrored-chats.json";
const PAIR_SEPARATOR: char = '\0';

#[derive(Debug, Clone, Serialize, Deserialize)]
struct State {
    #[serde(default = "first_version")]
    version: u32,
    #[serde(default)]
    pairs: BTreeMap<String, Snapshot>,
}

fn first_version() -> u32 {
    1
}

impl Default for State {
    fn default() -> State {
        State { version: 1, pairs: BTreeMap::new() }
    }
}

struct Pair {
    key: String,
    borrower: PathBuf,
    source: PathBuf,
    borrower_store: PathBuf,
    source_store: PathBuf,
    shared: BTreeSet<String>,
}

/// What the window says about a profile's last sidebar pass.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SyncStatus {
    Synced,
    Waiting,
    Retry,
}

impl SyncStatus {
    fn word(self) -> &'static str {
        match self {
            SyncStatus::Synced => "synced",
            SyncStatus::Waiting => "waiting",
            SyncStatus::Retry => "retry",
        }
    }

    pub fn message(self) -> &'static str {
        match self {
            SyncStatus::Synced => "Pinned chats and sort order are synced.",
            SyncStatus::Waiting => "Quit the linked Claude apps, then open a shortcut to sync the sidebar.",
            SyncStatus::Retry => "Sidebar sync could not finish. It will retry when you next open a shortcut.",
        }
    }
}

#[cfg(test)]
pub(crate) mod hooks {
    use super::*;
    use std::sync::Mutex;

    pub type Storage = Box<
        dyn FnMut(&[PathBuf], Option<&HashMap<String, Snapshot>>, &HashMap<String, BTreeSet<String>>)
                -> Result<HashMap<String, Snapshot>, Failure>
            + Send,
    >;

    /// Nothing in a temporary folder can be made to run, and a test that
    /// asked the real process list would answer differently depending on
    /// whether somebody had Claude open at the time.
    pub static RUNNING: Mutex<Option<Vec<PathBuf>>> = Mutex::new(None);
    pub static STORAGE: Mutex<Option<Storage>> = Mutex::new(None);
}

fn running() -> Vec<PathBuf> {
    #[cfg(test)]
    if let Some(list) = hooks::RUNNING.lock().unwrap().clone() {
        return list;
    }
    claude::running_profiles()
}

fn storage(
    profiles: &[PathBuf],
    changes: Option<&HashMap<String, Snapshot>>,
    shared: &HashMap<String, BTreeSet<String>>,
) -> Result<HashMap<String, Snapshot>, Failure> {
    #[cfg(test)]
    if let Some(stand_in) = hooks::STORAGE.lock().unwrap().as_mut() {
        return stand_in(profiles, changes, shared);
    }
    storage::storage(profiles, changes, shared)
}

/// Whether some process holds the launch lock. A pass is only safe inside
/// it, and this cannot tell its own caller from another process holding it,
/// but it does catch the call made from outside the lock altogether, which
/// is the one that would open databases a launcher is about to hand Claude.
fn launch_lock_is_held() -> bool {
    let Ok(file) = OpenOptions::new()
        .read(true)
        .write(true)
        .create(true)
        .truncate(false)
        .open(lock::launch_lock_file())
    else {
        return false;
    };
    // SAFETY: flock on a descriptor this function owns, let go before it closes.
    unsafe {
        if libc::flock(file.as_raw_fd(), libc::LOCK_EX | libc::LOCK_NB) == 0 {
            libc::flock(file.as_raw_fd(), libc::LOCK_UN);
            return false;
        }
    }
    std::io::Error::last_os_error().raw_os_error() == Some(libc::EWOULDBLOCK)
}

fn mirror_pair_keys() -> Vec<String> {
    match files::read_json(&paths::graft_file(MIRROR_FILE)) {
        JsonRead::Value(value) => {
            value.get("pairs").and_then(Value::as_object).map(|m| m.keys().cloned().collect()).unwrap_or_default()
        }
        _ => Vec::new(),
    }
}

fn pair_folders(key: &str) -> Option<(PathBuf, PathBuf)> {
    let halves: Vec<&str> = key.split(PAIR_SEPARATOR).collect();
    if halves.len() != 2 || !halves.iter().all(|h| h.starts_with('/')) {
        return None;
    }
    Some((PathBuf::from(halves[0]), PathBuf::from(halves[1])))
}

fn session_ids(directory: &Path) -> Result<BTreeSet<String>, Failure> {
    let linked = || Failure::new("linked-chat-store");
    let profile = directory.parent().and_then(Path::parent).and_then(Path::parent).ok_or_else(linked)?;
    let store_root = paths::normalize(&profile.join("claude-code-sessions"));
    let real = paths::normalize(directory);
    if files::is_symlink(directory) || !real.starts_with(&store_root) || real == store_root {
        return Err(linked());
    }
    let entries = std::fs::read_dir(directory).map_err(|_| Failure::new("unreadable-session"))?;
    Ok(entries
        .filter_map(Result::ok)
        .map(|e| e.file_name().to_string_lossy().into_owned())
        .filter(|n| n.starts_with("local_") && n.ends_with(".json"))
        .map(|n| n[..n.len() - 5].to_string())
        .collect())
}

/// The profile a chat store belongs to, when it is one Claude would read:
/// `<profile>/claude-code-sessions/<account>/<org>`, signed into that
/// account, and sitting beside the other profiles.
fn profile_of_store(store: &Path) -> Option<PathBuf> {
    let account_dir = store.parent()?;
    let code = account_dir.parent()?;
    let profile = code.parent()?;
    let account = files::account_of(profile)?;
    let fits = code.file_name()? == "claude-code-sessions"
        && account_dir.file_name()?.to_string_lossy() == account
        && paths::same_path(profile.parent()?, &paths::config_home());
    fits.then(|| paths::normalize(profile))
}

fn candidates() -> Vec<Pair> {
    let mut keys = mirror_pair_keys();
    keys.sort();
    keys.into_iter()
        .filter_map(|key| {
            let (one, other) = pair_folders(&key)?;
            let borrower = profile_of_store(&one)?;
            let source = profile_of_store(&other)?;
            if borrower == source {
                return None;
            }
            let first = session_ids(&one).ok()?;
            let second = session_ids(&other).ok()?;
            let shared: BTreeSet<String> = first.intersection(&second).cloned().collect();
            if shared.is_empty() {
                return None;
            }
            Some(Pair { key, borrower, source, borrower_store: one, source_store: other, shared })
        })
        .collect()
}

fn is_running_among(running: &[PathBuf], profiles: &[PathBuf]) -> bool {
    profiles.iter().any(|p| running.iter().any(|r| paths::same_path(r, p)))
}

/// Bring the sidebar of `profile`, and of every profile its chat history
/// connects it to, to agree before Claude opens it.
///
/// Call it inside `graft_core::lock::with_launch_lock`, after deciding the
/// profile is closed and before starting Claude. It never fails the opening:
/// a pass that cannot run, or cannot finish, is noted and retried at the
/// next opening, and Claude opens either way.
pub fn synchronize_before_opening(profile: &Path) {
    if !launch_lock_is_held() {
        diagnostics::note("sidebar.sync-skipped", &[("reason", "launch-lock-not-held".into())]);
        return;
    }
    let mut pairs = candidates();
    let mut connected: BTreeSet<PathBuf> = BTreeSet::from([paths::normalize(profile)]);
    for _ in 0..pairs.len() {
        for pair in &pairs {
            if connected.contains(&pair.borrower) || connected.contains(&pair.source) {
                connected.insert(pair.borrower.clone());
                connected.insert(pair.source.clone());
            }
        }
    }
    pairs.retain(|p| connected.contains(&p.borrower) && connected.contains(&p.source));
    if pairs.is_empty() {
        return;
    }
    let profiles: Vec<PathBuf> = connected.into_iter().collect();
    if is_running_among(&running(), &profiles) {
        record_status(SyncStatus::Waiting, &profiles);
        return;
    }
    match pass(&profiles, pairs) {
        Ok(affected) => record_status(SyncStatus::Synced, &affected),
        Err(failure) => {
            diagnostics::note("sidebar.sync-skipped", &[("reason", failure.0.clone().into())]);
            record_status(SyncStatus::Retry, &profiles);
        }
    }
}

fn scope_matches(snapshot: Option<&Snapshot>, store: &Path) -> bool {
    let account = store.parent().and_then(Path::file_name).map(|n| n.to_string_lossy().into_owned());
    let org = store.file_name().map(|n| n.to_string_lossy().into_owned());
    match (snapshot.and_then(|s| s.scope.as_deref()), account, org) {
        (Some(scope), Some(account), Some(org)) => scope == format!("{account}/{org}"),
        _ => false,
    }
}

fn pass(profiles: &[PathBuf], mut pairs: Vec<Pair>) -> Result<Vec<PathBuf>, Failure> {
    let state_file = paths::graft_file(STATE_FILE);
    let mut state: State = match files::read_json(&state_file) {
        JsonRead::Missing => State::default(),
        JsonRead::Value(value) => serde_json::from_value(value).map_err(|_| Failure::new("unreadable-baseline"))?,
        JsonRead::Unreadable => return Err(Failure::new("unreadable-baseline")),
    };
    if state.version != 1 {
        return Err(Failure::new("unsupported-baseline"));
    }
    let key = |p: &Path| paths::text(p);
    let original = storage(profiles, None, &HashMap::new())?;
    pairs.retain(|p| {
        scope_matches(original.get(&key(&p.borrower)), &p.borrower_store)
            && scope_matches(original.get(&key(&p.source)), &p.source_store)
    });
    if pairs.is_empty() {
        return Err(Failure::new("account-not-ready"));
    }
    let mut shared: HashMap<String, BTreeSet<String>> = HashMap::new();
    for pair in &pairs {
        shared.entry(key(&pair.borrower)).or_default().extend(pair.shared.iter().cloned());
        shared.entry(key(&pair.source)).or_default().extend(pair.shared.iter().cloned());
    }
    let mut wanted = original.clone();
    for _ in 0..=pairs.len() {
        let mut changed = false;
        for pair in &pairs {
            let missing = || Failure::new("missing-profile");
            let one = wanted.get(&key(&pair.borrower)).ok_or_else(missing)?.clone();
            let two = wanted.get(&key(&pair.source)).ok_or_else(missing)?.clone();
            let a = one.restricted(&pair.shared);
            let b = two.restricted(&pair.shared);
            let baseline = state.pairs.get(&pair.key).map(|s| s.restricted(&pair.shared));
            let merged = merge(&a, &b, baseline.as_ref());
            changed = changed || !a.same_choices(&merged) || !b.same_choices(&merged);
            wanted.insert(key(&pair.borrower), one.replacing(&pair.shared, &merged));
            wanted.insert(key(&pair.source), two.replacing(&pair.shared, &merged));
            state.pairs.insert(pair.key.clone(), merged);
        }
        if !changed {
            break;
        }
    }
    let affected: Vec<PathBuf> = profiles.iter().filter(|p| shared.contains_key(&key(p))).cloned().collect();
    for pair in &pairs {
        for folder in [&pair.borrower_store, &pair.source_store] {
            for id in &pair.shared {
                read_session(&folder.join(format!("{id}.json")))?;
            }
        }
    }
    let changed = affected.iter().any(|p| match (original.get(&key(p)), wanted.get(&key(p))) {
        (Some(before), Some(after)) => !before.same_choices(after),
        _ => true,
    });
    if changed {
        if is_running_among(&running(), profiles) {
            return Err(Failure::new("profile-running"));
        }
        let verified = storage(&affected, Some(&wanted), &shared)?;
        for item in &affected {
            let ids = &shared[&key(item)];
            let agrees = match (verified.get(&key(item)), wanted.get(&key(item))) {
                (Some(result), Some(want)) => result.restricted(ids).same_choices(&want.restricted(ids)),
                _ => false,
            };
            if !agrees {
                return Err(Failure::new("verification-failed"));
            }
        }
    }
    for pair in &pairs {
        let pins = |p: &Path| -> BTreeSet<String> {
            wanted.get(&key(p)).map(|s| s.pins.iter().cloned().collect()).unwrap_or_default()
        };
        update_record_flags(&pair.borrower_store, &pair.shared, &pins(&pair.borrower))?;
        update_record_flags(&pair.source_store, &pair.shared, &pins(&pair.source))?;
    }
    let live: BTreeSet<String> = mirror_pair_keys().into_iter().collect();
    state.pairs.retain(|k, _| live.contains(k));
    let value = serde_json::to_value(&state).map_err(|_| Failure::new("state-write-failed"))?;
    files::write_json(&state_file, &value).map_err(|_| Failure::new("state-write-failed"))?;
    Ok(affected)
}

fn read_session(file: &Path) -> Result<Map<String, Value>, Failure> {
    if files::is_symlink(file) {
        return Err(Failure::new("unreadable-session"));
    }
    match files::read_json(file) {
        JsonRead::Value(Value::Object(record)) => Ok(record),
        _ => Err(Failure::new("unreadable-session")),
    }
}

/// `isStarred` in a session record is a derivative of the pin set, kept in
/// step so nothing that reads the record disagrees with the sidebar. Only
/// that one key changes.
fn update_record_flags(folder: &Path, shared: &BTreeSet<String>, pins: &BTreeSet<String>) -> Result<(), Failure> {
    for id in shared {
        let file = folder.join(format!("{id}.json"));
        let mut record = read_session(&file)?;
        let pinned = pins.contains(id);
        if record.get("isStarred") == Some(&Value::Bool(pinned)) {
            continue;
        }
        record.insert("isStarred".into(), Value::Bool(pinned));
        files::write_json(&file, &Value::Object(record)).map_err(|_| Failure::new("write-failed"))?;
    }
    Ok(())
}

fn read_statuses() -> Map<String, Value> {
    match files::read_json(&paths::graft_file(STATUS_FILE)) {
        JsonRead::Value(Value::Object(map)) => map,
        _ => Map::new(),
    }
}

fn record_status(status: SyncStatus, profiles: &[PathBuf]) {
    let mut states = read_statuses();
    for profile in profiles {
        states.insert(paths::text(profile), status.word().into());
    }
    let _ = files::write_json(&paths::graft_file(STATUS_FILE), &Value::Object(states));
    let names: Vec<String> =
        profiles.iter().map(|p| p.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default()).collect();
    diagnostics::note("sidebar.sync", &[("status", status.word().into()), ("profiles", names.join(", ").into())]);
}

/// How the last pass over this profile ended, if one has run.
pub fn status(profile: &Path) -> Option<SyncStatus> {
    let states = read_statuses();
    let word = states.get(&paths::text(&paths::normalize(profile))).or_else(|| states.get(&paths::text(profile)))?;
    Some(match word.as_str()? {
        "synced" => SyncStatus::Synced,
        "waiting" => SyncStatus::Waiting,
        _ => SyncStatus::Retry,
    })
}

pub fn status_message(profile: &Path) -> Option<&'static str> {
    status(profile).map(SyncStatus::message)
}

/// Drop the agreement of every pair no longer mirrored. The chat sharing
/// calls this whenever it saves `mirrored-chats.json`, so profiles that go
/// back to separate histories forget what their sidebars once agreed on.
pub fn forget_pairs(keep: &BTreeSet<String>) {
    let file = paths::graft_file(STATE_FILE);
    let JsonRead::Value(value) = files::read_json(&file) else { return };
    let Ok(mut state) = serde_json::from_value::<State>(value) else { return };
    if state.version != 1 {
        return;
    }
    let before = state.pairs.len();
    state.pairs.retain(|k, _| keep.contains(k));
    if state.pairs.len() == before {
        return;
    }
    if let Ok(value) = serde_json::to_value(&state) {
        let _ = files::write_json(&file, &value);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use graft_core::testing::Sandbox;
    use serde_json::json;
    use std::sync::{Arc, Mutex};

    #[derive(Default)]
    struct Disk {
        profiles: HashMap<String, Snapshot>,
        reads: usize,
        writes: usize,
        refuse: bool,
    }

    struct Fixture {
        _sandbox: Sandbox,
        one: PathBuf,
        two: PathBuf,
        store_one: PathBuf,
        disk: Arc<Mutex<Disk>>,
    }

    impl Drop for Fixture {
        fn drop(&mut self) {
            *hooks::STORAGE.lock().unwrap() = None;
            *hooks::RUNNING.lock().unwrap() = None;
        }
    }

    fn write_mirror(keys: &[String]) {
        let pairs: Map<String, Value> = keys.iter().map(|k| (k.clone(), json!({}))).collect();
        files::write_json(&paths::graft_file(MIRROR_FILE), &json!({"pairs": pairs})).unwrap();
    }

    fn fixture() -> Fixture {
        let sandbox = Sandbox::new();
        let config = sandbox.config();
        let one = config.join("One");
        let two = config.join("Two");
        let store_one = one.join("claude-code-sessions/account-one/org-one");
        let store_two = two.join("claude-code-sessions/account-two/org-two");
        for (profile, folder, account) in [(&one, &store_one, "account-one"), (&two, &store_two, "account-two")] {
            std::fs::create_dir_all(folder).unwrap();
            files::write_json(&profile.join("config.json"), &json!({"lastKnownAccountUuid": account})).unwrap();
            for id in ["local_a", "local_b"] {
                files::write_json(&folder.join(format!("{id}.json")),
                    &json!({"sessionId": id, "title": "Keep this title", "isArchived": false})).unwrap();
            }
        }
        let one = paths::normalize(&one);
        let two = paths::normalize(&two);
        let store_one = paths::normalize(&store_one);
        let store_two = paths::normalize(&store_two);
        write_mirror(&[format!("{}\0{}", paths::text(&store_one), paths::text(&store_two))]);

        let old = Snapshot::new(&["local_a", "local_b"], &["local_a", "local_b"], "recency");
        let mut none = Snapshot::new(&[], &[], "recency");
        none.scope = Some("account-one/org-one".into());
        let mut theirs = old.clone();
        theirs.scope = Some("account-two/org-two".into());
        let disk = Arc::new(Mutex::new(Disk::default()));
        {
            let mut d = disk.lock().unwrap();
            d.profiles.insert(paths::text(&one), none);
            d.profiles.insert(paths::text(&two), theirs);
        }
        let shared_disk = disk.clone();
        *hooks::STORAGE.lock().unwrap() = Some(Box::new(move |profiles, changes, _shared| {
            let mut d = shared_disk.lock().unwrap();
            match changes {
                Some(changes) => {
                    d.writes += 1;
                    if d.refuse {
                        return Err(Failure::new("test-write-failure"));
                    }
                    for profile in profiles {
                        let key = paths::text(profile);
                        let wanted = changes[&key].clone();
                        d.profiles.insert(key, wanted);
                    }
                }
                None => d.reads += 1,
            }
            Ok(d.profiles.clone())
        }));
        *hooks::RUNNING.lock().unwrap() = Some(Vec::new());
        Fixture { _sandbox: sandbox, one, two, store_one, disk }
    }

    fn sync(profile: &Path) {
        lock::with_launch_lock(|| synchronize_before_opening(profile));
    }

    fn counts(f: &Fixture) -> (usize, usize) {
        let d = f.disk.lock().unwrap();
        (d.reads, d.writes)
    }

    #[test]
    fn opening_a_closed_shared_profile_brings_both_sidebars_to_agree() {
        let f = fixture();
        let old = Snapshot::new(&["local_a", "local_b"], &["local_a", "local_b"], "recency");

        *hooks::RUNNING.lock().unwrap() = Some(vec![f.two.clone()]);
        sync(&f.one);
        assert_eq!(counts(&f), (0, 0), "an open linked profile prevents even opening its sidebar database");
        assert_eq!(status(&f.one), Some(SyncStatus::Waiting), "and the window says to quit the linked Claude");

        *hooks::RUNNING.lock().unwrap() = Some(Vec::new());
        sync(&f.one);
        assert_eq!(counts(&f).1, 1);
        assert!(f.disk.lock().unwrap().profiles[&paths::text(&f.one)].same_choices(&old),
            "opening a closed shared profile synchronizes both sidebars");
        assert_eq!(status(&f.one), Some(SyncStatus::Synced));
        let record = read_session(&f.store_one.join("local_a.json")).unwrap();
        assert!(record["isStarred"] == json!(true) && record["title"] == json!("Keep this title")
            && record["isArchived"] == json!(false),
            "pin metadata changes without altering a chat's title or archive state");

        sync(&f.two);
        assert_eq!(counts(&f).1, 1, "an unchanged sidebar does not rewrite either database");

        {
            let mut d = f.disk.lock().unwrap();
            let mine = d.profiles.get_mut(&paths::text(&f.one)).unwrap();
            mine.pins.clear();
            mine.order.clear();
            d.refuse = true;
        }
        let state_file = paths::graft_file(STATE_FILE);
        let baseline = std::fs::read(&state_file).unwrap();
        sync(&f.two);
        assert_eq!(std::fs::read(&state_file).unwrap(), baseline, "a failed storage write cannot advance the sidebar baseline");
        assert_eq!(status(&f.two), Some(SyncStatus::Retry), "and the window says it will retry");

        f.disk.lock().unwrap().refuse = false;
        sync(&f.two);
        assert!(f.disk.lock().unwrap().profiles[&paths::text(&f.two)].pins.is_empty(),
            "the next opening retries an unpin after a failed write");

        f.disk.lock().unwrap().profiles.get_mut(&paths::text(&f.one)).unwrap().scope =
            Some("different-account/other-org".into());
        let before = counts(&f).1;
        sync(&f.one);
        assert_eq!(counts(&f).1, before, "switching accounts cannot apply pins from the previous account's pair");

        write_mirror(&[]);
        forget_pairs(&BTreeSet::new());
        let dropped: Value = serde_json::from_slice(&std::fs::read(&state_file).unwrap()).unwrap();
        assert_eq!(dropped["pairs"], json!({}), "returning to independent histories forgets the old sidebar agreement");
        let reads = counts(&f).0;
        sync(&f.one);
        assert_eq!(counts(&f).0, reads, "independent profiles never open the sidebar helper");
    }

    #[test]
    fn a_pass_outside_the_launch_lock_opens_nothing() {
        let f = fixture();
        synchronize_before_opening(&f.one);
        assert_eq!(counts(&f), (0, 0));
        assert_eq!(status(&f.one), None);
    }

    #[test]
    fn a_session_record_that_cannot_be_read_stops_the_pass_before_any_write() {
        let f = fixture();
        std::fs::write(f.store_one.join("local_b.json"), "{ incomplete").unwrap();
        sync(&f.one);
        assert_eq!(counts(&f), (1, 0));
        assert_eq!(status(&f.one), Some(SyncStatus::Retry));
        assert_eq!(std::fs::read_to_string(f.store_one.join("local_b.json")).unwrap(), "{ incomplete");
    }

    #[test]
    fn a_chat_store_that_is_a_link_is_not_a_pair_to_sync() {
        let f = fixture();
        let elsewhere = f._sandbox.root.path().join("elsewhere");
        std::fs::rename(&f.store_one, &elsewhere).unwrap();
        std::os::unix::fs::symlink(&elsewhere, &f.store_one).unwrap();
        sync(&f.one);
        assert_eq!(counts(&f), (0, 0));
    }

    #[test]
    fn an_unreadable_baseline_is_not_an_empty_one() {
        let f = fixture();
        std::fs::create_dir_all(paths::graft_folder()).unwrap();
        std::fs::write(paths::graft_file(STATE_FILE), "{ incomplete").unwrap();
        sync(&f.one);
        assert_eq!(counts(&f).1, 0);
        assert_eq!(std::fs::read_to_string(paths::graft_file(STATE_FILE)).unwrap(), "{ incomplete",
            "the baseline is left for somebody to look at");
    }

    #[test]
    fn each_status_reads_as_a_sentence_for_the_window() {
        let _sandbox = Sandbox::new();
        let profile = paths::config_home().join("Somebody");
        assert_eq!(status_message(&profile), None, "a profile never synced says nothing");
        record_status(SyncStatus::Waiting, std::slice::from_ref(&profile));
        assert_eq!(status_message(&profile),
            Some("Quit the linked Claude apps, then open a shortcut to sync the sidebar."));
    }
}
