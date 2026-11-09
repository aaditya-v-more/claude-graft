//! Session records: what a sidebar lists, and the sweep that files the ones a
//! grafted profile never wrote.

use crate::cache::{self, RecordSessions};
use crate::fsx::{self, exists, is_directory, names, names_or_empty, resolve, resolved_text};
use crate::transcript::{ConversationTrace, SessionFacts};
use crate::{running, NON_ACCOUNT_STORE_ITEMS, STASH_SUFFIX};
use graft_core::{config, diagnostics, files, paths};
use serde::{Deserialize, Serialize};
use serde_json::{json, Map, Value};
use std::collections::{BTreeMap, BTreeSet, HashMap, HashSet};
use std::path::{Path, PathBuf};
use std::time::{Duration, SystemTime};

/// Where the command line files one transcript per session, under one
/// directory per working directory.
pub fn claude_projects() -> PathBuf {
    paths::home().join(".claude/projects")
}

/// A record's times are milliseconds, written as Claude writes them: whole
/// numbers, not `1.7e12` and not `...893.0`.
fn number(value: f64) -> Value {
    if value.fract() == 0.0 && value.abs() < 9.0e15 {
        json!(value as i64)
    } else {
        json!(value)
    }
}

/// The record that puts a session back in a sidebar, shaped like the ones
/// Claude Desktop writes for the sessions it owns.
///
/// The bridge id is the one thing renamed on the way: the transcript calls it
/// `cse_...`, the record calls the same id `session_...`, measured against a
/// session that had both written down. The name is fixed by the session too,
/// `local_<cliSessionId>`, so a later pass can only ever find the record where
/// this one put it, and the marker a delete leaves behind names the session by
/// the same id.
pub fn session_record(facts: &SessionFacts) -> Map<String, Value> {
    let bridge: Vec<String> = facts
        .bridge_ids
        .iter()
        .map(|id| match id.strip_prefix("cse_") {
            Some(rest) => format!("session_{rest}"),
            None => id.clone(),
        })
        .collect();
    let mut record = Map::new();
    record.insert("sessionId".into(), json!(format!("local_{}", facts.cli_session_id)));
    record.insert("cliSessionId".into(), json!(facts.cli_session_id));
    record.insert("cwd".into(), json!(facts.cwd));
    record.insert("originCwd".into(), json!(facts.cwd));
    record.insert("lastFocusedAt".into(), number(facts.last_activity_at));
    record.insert("createdAt".into(), number(facts.created_at));
    record.insert("lastActivityAt".into(), number(facts.last_activity_at));
    record.insert("model".into(), json!(facts.model));
    record.insert("effort".into(), json!(facts.effort));
    record.insert("isArchived".into(), json!(false));
    record.insert("title".into(), json!(facts.title));
    record.insert("titleSource".into(), json!("auto"));
    record.insert("permissionMode".into(), json!(facts.permission_mode));
    // A record written by the desktop carries its MCP server config here.
    // Nothing the transcript holds can recover that, and an empty list is what
    // a session sees when none is configured.
    record.insert("remoteMcpServersConfig".into(), json!([]));
    // No `chromePermissionMode`. It is a choice made per session and nothing
    // in a transcript says what it was; a record without one takes the
    // person's own setting, which is how Claude itself rewrites most of them.
    record.insert("completedTurns".into(), json!(facts.prompts));
    record.insert("lastSpawnRootDetected".into(), json!(false));
    record.insert("bridgeSessionIds".into(), json!(bridge));
    record.insert("remoteControlAutoEligible".into(), json!(true));
    record.insert("alwaysAllowedReasons".into(), json!([]));
    record.insert("sessionPermissionUpdates".into(), json!([]));
    record.insert("classifierSummaryEnabled".into(), json!(true));
    record.insert("reportFindingsCard".into(), json!(true));
    record.insert("spawnSeed".into(), json!({}));
    if !facts.branches.is_empty() {
        record.insert("writtenBranches".into(), json!(facts.branches));
    }
    record
}

/// The bytes a set of facts files as a record. Written with sorted keys and no
/// formatting, so the same facts always produce the same bytes, which is how a
/// later pass tells a record still its own from one that something else has
/// rewritten.
pub fn session_record_data(facts: &SessionFacts) -> Vec<u8> {
    files::sorted_json(&Value::Object(session_record(facts)))
}

/// The bytes the same facts would have been filed as in the shape that said to
/// skip every browser permission check. No Linux build ever wrote it, but a
/// profile carried over from a Mac may hold records in it, and those are this
/// app's to rewrite without it.
pub fn older_session_record_data(facts: &SessionFacts) -> Vec<u8> {
    let mut record = session_record(facts);
    record.insert("chromePermissionMode".into(), json!("skip_all_permission_checks"));
    files::sorted_json(&Value::Object(record))
}

/// Where a session's record lives, resolved through any graft link on the way,
/// because where a link points is where both windows read.
pub fn record_file(facts: &SessionFacts, profile: &Path) -> PathBuf {
    resolve(
        &profile
            .join("claude-code-sessions")
            .join(&facts.owner_account)
            .join(&facts.owner_organization),
    )
    .join(format!("local_{}.json", facts.cli_session_id))
}

/// Whether the record on disk is still the one this app wrote, in today's
/// shape or the older one. Claude writes its own shape over a record it takes
/// an interest in, so this is the line between keeping a record current and
/// flattening one.
fn record_on_disk_matches(facts: &SessionFacts, file: &Path) -> (bool, bool) {
    let Ok(on_disk) = std::fs::read(file) else { return (false, false) };
    (on_disk == session_record_data(facts), on_disk == older_session_record_data(facts))
}

/// Puts one record where the session's owner will list it.
pub fn write_session_record(facts: &SessionFacts, profile: &Path) -> bool {
    let file = record_file(facts, profile);
    if exists(&file) {
        return false;
    }
    if std::fs::create_dir_all(fsx::parent(&file)).is_err() {
        return false;
    }
    files::atomic_write(&file, &session_record_data(facts), 0o644).is_ok()
}

/// Brings a record this app owns up to date with its transcript. The caller
/// has already established that the record on disk is the one this app wrote;
/// nothing else may be overwritten with a parsed version.
pub fn rewrite_session_record(facts: &SessionFacts, profile: &Path) -> bool {
    let file = record_file(facts, profile);
    files::atomic_write(&file, &session_record_data(facts), 0o644).is_ok()
}

/// What one walk of the chat stores found.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct StoreContents {
    /// The session a record describes, against the organization directory the
    /// record sits in. The directory rather than the file name, because knowing
    /// where a record was is what makes its absence later mean something.
    pub records: BTreeMap<String, String>,
    /// The moment of every deletion marker, against the name it carries. Named
    /// rather than counted because a marker naming a session this machine has a
    /// transcript for is read outright, and only the rest are left to be
    /// guessed at by when they were written.
    pub deletions: BTreeMap<String, f64>,
    /// What every deletion marker is named after. A record this app filed is
    /// named for its session, so the marker Claude leaves when one is deleted
    /// names the session too, which is the one identity a deletion can be read
    /// by rather than inferred.
    pub deleted: BTreeSet<String>,
    /// Every organization directory this walk actually managed to read. A
    /// directory missing from here was not looked in, which is a different
    /// thing from being looked in and found empty.
    pub stores: BTreeSet<String>,
    /// Every session a record says it has carried on from. Claude gives a
    /// conversation a new command line session as it goes, and the transcript
    /// of the old one stays on disk, whole, with no record naming it, which is
    /// exactly what a session that closed without a record looks like from
    /// here.
    pub superseded: BTreeSet<String>,
    /// The session every record holds, against the record's own name, which is
    /// the only name a fork's record gives its source.
    pub sessions_by_record: BTreeMap<String, String>,
    /// Every session a record says was forked, against the name of the record
    /// it was forked from.
    pub forked_from: BTreeMap<String, String>,
}

impl StoreContents {
    /// The forks whose source record this walk found, both halves as sessions.
    pub fn forks(&self) -> BTreeMap<String, String> {
        self.forked_from
            .iter()
            .filter_map(|(fork, record)| self.sessions_by_record.get(record).map(|s| (fork.clone(), s.clone())))
            .collect()
    }
}

/// Everything the chat stores on the machine hold, in one walk. Organization
/// directories grafted by hand resolve through to where they really sit, so a
/// store two profiles share is only read once and a record filed through a
/// link counts the same as one filed beside it.
pub fn session_store_contents() -> StoreContents {
    let mut contents = StoreContents::default();
    let mut walked: HashSet<String> = HashSet::new();

    for profile in config::session_store_profiles() {
        let store = profile.join("claude-code-sessions");
        let Some(accounts) = names(&store) else { continue };
        for account in accounts {
            if account.starts_with('.')
                || account.ends_with(STASH_SUFFIX)
                || NON_ACCOUNT_STORE_ITEMS.contains(&account.as_str())
            {
                continue;
            }
            let account_dir = store.join(&account);
            let Some(orgs) = names(&account_dir) else { continue };
            for org in orgs {
                if org.starts_with('.') || org.ends_with(STASH_SUFFIX) {
                    continue;
                }
                let org_dir = account_dir.join(&org);
                let Some(entries) = names(&org_dir) else { continue };
                let resolved = resolved_text(&org_dir);
                contents.stores.insert(resolved.clone());
                for name in entries {
                    let file = resolve(&org_dir.join(&name));
                    if !walked.insert(paths::text(&file)) {
                        continue;
                    }
                    if name.starts_with("local_") && name.ends_with(".json") {
                        let sessions: RecordSessions = cache::sessions_of_record(&file);
                        // The first store to hold it keeps it. A mirrored
                        // record sits in two, and letting the last one win
                        // left the place a session was filed changing from
                        // pass to pass in the one file written to be read
                        // afterwards.
                        if let Some(session) = &sessions.cli_session_id {
                            contents.records.entry(session.clone()).or_insert_with(|| resolved.clone());
                        }
                        contents.superseded.extend(sessions.prior.iter().cloned());
                        if let Some(session) = sessions.cli_session_id {
                            let record_name = name.trim_end_matches(".json").to_string();
                            contents.sessions_by_record.insert(record_name, session.clone());
                            if let Some(source) = sessions.forked_from {
                                contents.forked_from.insert(session, source);
                            }
                        }
                    } else if let Some(marker) = name.strip_prefix("deleted_") {
                        contents.deleted.insert(marker.to_string());
                        if let Ok(text) = std::fs::read_to_string(&file) {
                            if let Ok(when) = text.trim().parse::<f64>() {
                                contents.deletions.insert(marker.to_string(), when);
                            }
                        }
                    }
                }
            }
        }
    }
    cache::forget_records_outside_of(&walked);
    contents
}

/// What a sweep remembers between passes: where every record it has seen was
/// sitting, so one that is gone from a store this pass did read is known to
/// have been deleted by hand rather than merely out of sight; every session
/// withdrawn that way, so none of them is ever brought back; and the facts
/// behind every record it filed itself, so a record still holding those exact
/// bytes is known to be one of ours to keep current.
///
/// Every key is optional on the way in: a state file from a version before one
/// of them existed loads with the rest, claiming nothing it does not say.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
pub struct SessionRecordState {
    #[serde(default)]
    pub records: BTreeMap<String, String>,
    #[serde(default)]
    pub withdrawn: Vec<String>,
    #[serde(default)]
    pub authored: BTreeMap<String, SessionFacts>,
    /// Records missing on the last pass and not yet missing on a second.
    #[serde(default)]
    pub vanished: Vec<String>,
    /// Every fork seen while both its records stood, against the session it
    /// came from. Remembered because the pass that needs it is the one where
    /// neither record stands any more.
    #[serde(default)]
    pub forks: BTreeMap<String, String>,
}

pub fn session_record_state_file() -> PathBuf {
    paths::graft_file("session-records.json")
}

pub fn load_session_record_state() -> SessionRecordState {
    std::fs::read(session_record_state_file())
        .ok()
        .and_then(|b| serde_json::from_slice(&b).ok())
        .unwrap_or_default()
}

pub fn save_session_record_state(state: &SessionRecordState) {
    if let Ok(bytes) = serde_json::to_vec(state) {
        let _ = files::atomic_write(&session_record_state_file(), &bytes, 0o644);
    }
}

/// What a pass makes of one transcript.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SessionFiling {
    File,
    /// Claude wrote its own record, or an earlier pass wrote this one.
    AlreadyRecorded,
    /// Deleted in a sidebar once, and not to be brought back.
    Withdrawn,
    /// Written too recently to be sure Claude will not write the record
    /// itself; the owner's own window always does, within moments.
    TooRecent,
    /// No profile on this machine holds the owner's account, so there is
    /// nowhere the record would be read. Asked again next pass, because
    /// accounts move between profiles.
    NoOwnerProfile,
    /// Run from a terminal, or by a Claude without a bridge: never in a
    /// sidebar, so nothing is missing.
    NotADesktopSession,
    /// Worked in on another machine, whose transcripts were carried here.
    /// Asked again next pass, because a drive that is not mounted yet will be.
    FolderElsewhere,
}

/// How many leading components name the place a session's folder sits in: a
/// home folder or a drive, never the folder itself.
///
/// On a Mac that was `/Users/<name>` and `/Volumes/<drive>`. A Linux desktop
/// keeps homes in `/home/<name>`, or `/var/home/<name>` on Fedora's atomic
/// editions, where `/home` is a link to it, and mounts removable and second
/// drives at `/run/media/<name>/<drive>` (udisks on most desktops),
/// `/media/<name>/<drive>` (Debian and Ubuntu) or `/mnt/<drive>`. A transcript
/// carried over from a Mac still names `/Users/<name>`, which this machine
/// never has, and that is the case this exists for. Anywhere else is read by
/// its first component, which is what the Mac did for everything outside a
/// home or a volume.
fn place_depth(parts: &[String]) -> usize {
    let at = |i: usize| parts.get(i).map(String::as_str).unwrap_or("");
    match at(1) {
        "Users" | "Volumes" | "home" | "mnt" => 3,
        "media" => 4,
        "var" if at(2) == "home" => 4,
        "run" if at(2) == "media" => 5,
        _ => 2,
    }
}

/// Whether the folder a session worked in could be on this machine at all.
///
/// Transcripts carried over from another machine name its home folder, and a
/// record filed for one is a chat that cannot be opened: Claude tries to start
/// it, finds no folder and leaves it in the sidebar unusable, beside the real
/// copy of the same project under this machine's own path. Fourteen came in on
/// a Mac that way from a home that never existed there.
///
/// The question is asked of the home folder or the drive, never of the folder
/// itself. A worktree cleaned up after its chat closed is gone while its
/// project is not, and that chat is this machine's history, still read in the
/// sidebar; only a home or a drive that is not here at all says the session
/// happened somewhere else.
///
/// Inside the Flatpak this is asked of the sandbox's view of the disk, which
/// `--filesystem=host` makes the host's for every home and mount point named
/// above; `/usr` and `/etc` are the runtime's own, but nobody's session runs
/// there and both exist either way.
pub fn session_folder_is_here(cwd: &str) -> bool {
    if !cwd.starts_with('/') {
        return true;
    }
    let standard = fsx::standardized(Path::new(cwd));
    let parts: Vec<String> = standard
        .components()
        .map(|c| c.as_os_str().to_string_lossy().into_owned())
        .collect();
    if parts.len() <= 1 {
        return true;
    }
    let depth = place_depth(&parts).min(parts.len());
    let mut place = PathBuf::new();
    for part in &parts[..depth] {
        place.push(part);
    }
    is_directory(&place)
}

/// Whether a transcript still needs a record written for it.
#[allow(clippy::too_many_arguments)]
pub fn session_filing(
    facts: Option<&SessionFacts>,
    recorded: &HashSet<String>,
    withdrawn: &HashSet<String>,
    deletions: &[f64],
    last_write: SystemTime,
    owner_profile: Option<&Path>,
    owner_is_running: bool,
    folder_is_here: bool,
    now: SystemTime,
    quiet_window: Duration,
) -> SessionFiling {
    let Some(facts) = facts else { return SessionFiling::NotADesktopSession };
    if !folder_is_here {
        return SessionFiling::FolderElsewhere;
    }
    if recorded.contains(&facts.cli_session_id) {
        return SessionFiling::AlreadyRecorded;
    }
    if withdrawn.contains(&facts.cli_session_id) {
        return SessionFiling::Withdrawn;
    }

    // A marker naming a record Claude wrote names it by an id no transcript
    // carries, so the only trace of which session went is timing: the one that
    // had just gone quiet is the one that was on screen when the delete was
    // pressed. A session still being written grows past the window and files
    // later, so this can only hold back one whose final line fell inside it.
    //
    // Only those markers reach here. `deletions` used to carry every marker on
    // the machine, including the ones this app can read outright by name, and
    // they accumulate and are never pruned, so one deletion went on suppressing
    // whatever else had happened to go quiet in the minute before it, for as
    // long as the marker existed.
    if deletions
        .iter()
        .any(|d| facts.last_activity_at > d - 60_000.0 && facts.last_activity_at <= *d)
    {
        return SessionFiling::Withdrawn;
    }

    // The wait is for one thing only: the Claude signed into the owner's
    // account writing the record itself, which it does within a second of a
    // session opening. No such Claude running is nobody about to write it, and
    // waiting then is waiting for something that is not coming, which is what
    // made closing a chat and reaching straight for the other profile the one
    // move guaranteed to miss it, since a sidebar is built once at launch and
    // that launch is the press.
    if owner_is_running {
        let quiet = now.duration_since(last_write).unwrap_or(Duration::ZERO);
        if quiet < quiet_window {
            return SessionFiling::TooRecent;
        }
    }
    if owner_profile.is_none() {
        return SessionFiling::NoOwnerProfile;
    }
    SessionFiling::File
}

/// What a pass does with a record that already exists on disk.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SessionUpdate {
    /// Nothing to do: the record is not one this app wrote, or it already says
    /// what the transcript says.
    Leave,
    /// The transcript has moved past the record, which still holds what this
    /// app last wrote, so the record is brought up to date.
    Refresh,
    /// Something else has rewritten the record, Claude's own hand, always
    /// richer than a parsed transcript, and this app never touches it again.
    TakenOver,
}

/// Whether a record this app filed gets rewritten as its transcript moves on.
/// A record only stays this app's while it holds byte for byte what the last
/// pass wrote there: Claude Desktop rewrites the records of the sessions it
/// takes over, measured coming down over a filed record within seconds of the
/// session being opened in the owner's window, and flattening one of those
/// back into a parsed version would throw away everything a transcript cannot
/// recover.
///
/// A record still in the shape an earlier version filed is this app's as
/// surely as one in today's, and is rewritten whether or not its transcript
/// moved: that shape skipped every browser permission check, and leaving it
/// until the session happened to move again would leave it there for good.
pub fn session_update_decision(
    authored: Option<&SessionFacts>,
    facts: &SessionFacts,
    disk_matches_authored: bool,
    disk_in_older_shape: bool,
) -> SessionUpdate {
    let Some(authored) = authored else { return SessionUpdate::Leave };
    if disk_in_older_shape {
        return SessionUpdate::Refresh;
    }
    if authored == facts {
        return SessionUpdate::Leave;
    }
    if disk_matches_authored {
        SessionUpdate::Refresh
    } else {
        SessionUpdate::TakenOver
    }
}

/// Whether a record may be written into an organization folder.
///
/// A folder this pass could not read is not a folder with nothing in it. It is
/// stashed behind a graft, or waiting on a permission, or gone for the moment
/// while Claude renames something over it, and from outside every session it
/// holds looks like a session nobody ever filed. Writing those records again
/// gives the profile a second copy of a history it already has, and a record
/// written fresh says `isArchived: false`, so a chat put out of sight by hand
/// comes back into the sidebar.
///
/// A folder that is simply not there yet is a different thing and is filed
/// into, since that is how a profile gets its first record, unless this app
/// stashed it itself, which is the one absence it can recognise. The absence
/// has to be asked about all the way up: a cross-account graft stashes the
/// organization folder and leaves a sibling beside it, while two profiles on
/// one account stash the whole store and leave no folder for a sibling to
/// stand next to. `is_stashed_away` is both shapes.
pub fn may_file_records(dir: &Path, stores_read: &BTreeSet<String>) -> bool {
    if !exists(dir) {
        return !fsx::is_stashed_away(dir);
    }
    stores_read.contains(&resolved_text(dir))
}

/// The account a profile is signed into, read as Claude wrote it.
fn account_of(profile: &Path) -> Option<String> {
    files::config_json(profile).get("lastKnownAccountUuid").and_then(Value::as_str).map(str::to_string)
}

/// The profile a session's owner account lives on now. Accounts move between
/// profiles, a migration or a sign-in, so it is asked for rather than assumed;
/// the profile holding the account is where the record belongs, and where
/// every profile grafted onto it reads.
pub fn owner_profile(facts: &SessionFacts, among: &[PathBuf]) -> Option<PathBuf> {
    let mut seen: HashSet<PathBuf> = HashSet::new();
    for profile in among {
        if !seen.insert(resolve(profile)) {
            continue;
        }
        if account_of(profile).as_deref() == Some(facts.owner_account.as_str()) {
            return Some(profile.clone());
        }
    }
    None
}

/// The sessions that are only an earlier copy of a conversation another
/// session carries on, and are therefore never filed.
///
/// Editing a prompt or resuming a conversation in the desktop starts a new
/// command line session. Sometimes that session copies the history before it
/// and asks the edited prompt again; sometimes it is a fresh session where the
/// same opening prompt was typed again and then left. Either way the earlier
/// one stays on disk with no record, and the record that would have named it
/// in `priorCliSessionIds` was never written if the profile was grafted when
/// it happened, so the sweep filed it, and the same conversation turned up in
/// a sidebar five and six times under one title.
///
/// A session is a copy of another of the same account, organization and
/// folder when three things hold.
///
/// They open with the same prompt, and it says something. A retry and a
/// resume both start where the original did; a chat that opened with "hi"
/// shares that with a hundred others, and the first version of this rule put
/// three unrelated greetings out of sight on the strength of it.
///
/// Every prompt typed in it was typed in the other, which holds more, or holds
/// the same and was active later. Prompts rather than titles, which collide on
/// their own.
///
/// And it did nothing the other did not. Asking the same questions is not the
/// same conversation when the answers differ: one copy turned out to hold the
/// only finished draft of a PR comment, its counterpart having been overloaded
/// at that turn, and another held the original implementation that its
/// counterpart only found and built on. Answers are compared by line id, which
/// a resumed copy keeps for every line it copied, so the work a session did of
/// its own is precisely what the other has no id for. A file edited there is
/// never a copy; a few sentences of its own, what a stopped attempt leaves
/// behind, are allowed for.
///
/// A fork is the one exception, and only its record can say so. Forking a
/// chat copies every line, still stamped with the original's id, and then asks
/// something more, so from transcripts alone the original reads exactly as a
/// resumed copy of its fork. But a fork is a second conversation somebody
/// asked for, and Claude gives it a record of its own naming the source in
/// `forkedFromSessionId`, which a resume never does. Neither half of a pair in
/// `forks` is ever a copy of the other.
///
/// Measured against 302 sessions read through one by one: the 28 found to be
/// copies did at most 173 bytes of answering of their own and edited nothing;
/// the two that were not did 2,573 bytes and four edits, and 9,097 bytes. The
/// allowance sits between the two.
///
/// Only filing asks this. A record already on disk, whoever wrote it, is left
/// where it is.
pub const OPENING_AT_LEAST: i64 = 20;
pub const OWN_WORK_ALLOWANCE: i64 = 500;

pub fn resumed_copies(
    sessions: &[(SessionFacts, ConversationTrace)],
    forks: &BTreeMap<String, String>,
) -> BTreeSet<String> {
    struct Entry<'a> {
        facts: &'a SessionFacts,
        trace: &'a ConversationTrace,
        asked: HashSet<&'a str>,
    }
    let mut groups: HashMap<String, Vec<Entry>> = HashMap::new();
    for (facts, trace) in sessions {
        if trace.opening_size < OPENING_AT_LEAST || trace.prompts.is_empty() {
            continue;
        }
        let key = [
            facts.owner_account.as_str(),
            facts.owner_organization.as_str(),
            facts.cwd.as_str(),
            trace.prompts[0].as_str(),
        ]
        .join("\0");
        groups.entry(key).or_default().push(Entry {
            facts,
            trace,
            asked: trace.prompts.iter().map(String::as_str).collect(),
        });
    }
    let mut copies = BTreeSet::new();
    for group in groups.values().filter(|g| g.len() > 1) {
        for copy in group {
            let carried_on = group.iter().any(|other| {
                let (c, o) = (&copy.facts.cli_session_id, &other.facts.cli_session_id);
                if o == c
                    || forks.get(c) == Some(o)
                    || forks.get(o) == Some(c)
                    || !copy.asked.is_subset(&other.asked)
                {
                    return false;
                }
                let own_work: i64 = copy
                    .trace
                    .work
                    .iter()
                    .filter(|(id, _)| !other.trace.work.contains_key(*id))
                    .map(|(_, size)| *size)
                    .sum();
                if own_work > OWN_WORK_ALLOWANCE {
                    return false;
                }
                if other.asked.len() > copy.asked.len() {
                    return true;
                }
                let later = other
                    .facts
                    .last_activity_at
                    .partial_cmp(&copy.facts.last_activity_at)
                    .unwrap_or(std::cmp::Ordering::Equal)
                    .then_with(|| o.cmp(c));
                later == std::cmp::Ordering::Greater
            });
            if carried_on {
                copies.insert(copy.facts.cli_session_id.clone());
            }
        }
    }
    copies
}

/// Writes a record for every session whose transcript survived without one,
/// keeps the ones it wrote current while their transcripts move, and hands
/// back what it filed so the caller can say so.
///
/// A profile will not write a record into a folder that resolves outside
/// itself, and a grafted profile's chat folder is exactly that, so every
/// session started from one has been closing without a record and vanishing
/// from every sidebar while its transcript sat whole on disk. Filing the
/// record where the owner's account lives puts it back in front of the session
/// and everything grafted onto it; deleting it from a sidebar is remembered,
/// so a recovery is a one-way thing nobody has to hold down.
///
/// A session is held back until its transcript has been quiet for a minute,
/// but only while a Claude on the owner's account is running. `filing_into` is
/// what the caller knows about; the profiles this app made and Claude's own
/// are added to it, because a launcher knows its own shortcut and its source
/// and nothing else.
pub fn file_missing_session_records(filing_into: &[PathBuf]) -> Vec<SessionFacts> {
    file_missing_session_records_at(filing_into, SystemTime::now(), Duration::from_secs(60))
}

pub fn file_missing_session_records_at(
    filing_into: &[PathBuf],
    now: SystemTime,
    quiet_window: Duration,
) -> Vec<SessionFacts> {
    graft_core::lock::with_state_lock(|| sweep(filing_into, now, quiet_window))
}

fn sweep(filing_into: &[PathBuf], now: SystemTime, quiet_window: Duration) -> Vec<SessionFacts> {
    let mut state = load_session_record_state();
    let contents = session_store_contents();
    let candidates = config::record_filing_profiles(filing_into);
    diagnostics::note(
        "sweep.begin",
        &[
            ("storesRead", json!(contents.stores.len())),
            ("recordsSeen", json!(contents.records.len())),
            ("deletionMarkers", json!(contents.deleted.len())),
            ("filingInto", json!(candidates.iter().map(|p| fsx::file_name(p)).collect::<Vec<_>>())),
            ("remembered", json!(state.records.len())),
            ("vanishedLastPass", json!(state.vanished.len())),
        ],
    );

    let recorded: HashSet<String> = contents.records.keys().cloned().collect();
    let mut withdrawn: BTreeSet<String> = state.withdrawn.iter().cloned().collect();
    let withdrawn_before = withdrawn.len();
    let vanished: HashSet<String> = state.vanished.iter().cloned().collect();
    let mut missing: BTreeSet<String> = BTreeSet::new();
    // Sessions whose record is remembered in a store this pass never got into.
    // They are not gone and they are not missing; they are out of sight, and
    // the record still sitting there is the one the profile reads.
    let mut out_of_sight: HashSet<String> = HashSet::new();
    // Named rather than counted, because which store went quiet is the whole
    // diagnosis and a number says nothing.
    let mut stashed_stores: BTreeSet<String> = BTreeSet::new();
    let mut unread_stores: BTreeSet<String> = BTreeSet::new();
    // Sessions remembered at a folder that has stopped existing, which is a
    // pair to drop rather than a fact to act on.
    let mut forgotten: HashSet<String> = HashSet::new();
    let mut gone_stores: BTreeSet<String> = BTreeSet::new();

    // A record gone from a store this pass did read was deleted by hand, and
    // two things have to hold before a pass will say so: the store has to have
    // been read, and the record has to be missing twice over. Claude re-files a
    // session under a new record name as it goes, so there is a moment when the
    // old name is gone and the new one is not yet there. Withdrawing is
    // forever; being sure of it costs one more pass.
    let mut store_answers: HashMap<String, (bool, bool)> = HashMap::new();
    let mut answers = |store: &str| -> (bool, bool) {
        if let Some(known) = store_answers.get(store) {
            return *known;
        }
        let path = Path::new(store);
        let answer = (fsx::is_stashed_away(path), exists(path));
        store_answers.insert(store.to_string(), answer);
        answer
    };

    let remembered_before = state.records.clone();
    for (session, store) in remembered_before.iter().filter(|(_, s)| s.starts_with('/')) {
        let (stashed, present) = answers(store);
        // A store with a stash beside it is one this app emptied itself. A
        // mirror's first pass moves the profile's own chats to the hidden
        // sibling and leaves a real, readable, empty folder behind, which reads
        // exactly like a sidebar someone cleared by hand. Withdrawing is
        // forever, and this is how 150 sessions across two profiles were
        // withdrawn in a single pass.
        //
        // A record this pass can see is not one of those, whatever sits beside
        // its folder: every mirrored folder has a stash beside it.
        if stashed && !recorded.contains(session) {
            out_of_sight.insert(session.clone());
            stashed_stores.insert(store.clone());
            continue;
        }
        // A folder that is not there, with nothing stashed anywhere above it to
        // say this app put it away, is a folder nobody writes to any more: the
        // profile was deleted, or signing in again moved <account>/<org> out
        // from under the pair. Forgetting is what lets the session look new and
        // land where the account lives now.
        if !present {
            forgotten.insert(session.clone());
            gone_stores.insert(store.clone());
            diagnostics::note(
                "sweep.forget",
                &[
                    ("session", json!(session)),
                    ("store", json!(store)),
                    ("because", json!("the folder is gone and nothing here stashed it")),
                ],
            );
            continue;
        }
        if !contents.stores.contains(store) {
            out_of_sight.insert(session.clone());
            unread_stores.insert(store.clone());
            continue;
        }
        if recorded.contains(session) {
            continue;
        }
        if vanished.contains(session) {
            withdrawn.insert(session.clone());
            state.authored.remove(session);
            diagnostics::note(
                "sweep.withdraw",
                &[
                    ("session", json!(session)),
                    ("store", json!(store)),
                    ("because", json!("missing from a store read twice")),
                ],
            );
        } else {
            missing.insert(session.clone());
        }
    }

    // Where a record was is remembered until it is seen again or given up on,
    // which is what leaves the next pass something to agree with. Entries that
    // name a file rather than a folder go on sight, since every record still
    // on disk is learned again right here.
    let mut remembered: BTreeMap<String, String> = state
        .records
        .iter()
        .filter(|(k, v)| v.starts_with('/') && !forgotten.contains(*k))
        .map(|(k, v)| (k.clone(), v.clone()))
        .collect();
    for (session, store) in &contents.records {
        remembered.insert(session.clone(), store.clone());
    }
    for session in &withdrawn {
        remembered.remove(session);
    }
    state.records = remembered;
    state.vanished = missing.iter().cloned().collect();

    let mut filed: Vec<SessionFacts> = Vec::new();
    let mut visited: HashSet<String> = HashSet::new();
    // One look at the process list per pass, not one per transcript.
    let listing = running::processes();
    let mut running_profiles: HashMap<PathBuf, bool> = HashMap::new();
    let mut running = |profile: &Path| -> bool {
        *running_profiles
            .entry(profile.to_path_buf())
            .or_insert_with(|| graft_core::claude::is_running_in(&listing, profile))
    };

    let projects_dir = claude_projects();
    let Some(projects) = names(&projects_dir) else {
        state.withdrawn = withdrawn.iter().cloned().collect();
        save_session_record_state(&state);
        diagnostics::note("sweep.end", &[("filed", json!(0)), ("because", json!("no transcripts to read"))]);
        return filed;
    };

    let transcripts: Vec<(PathBuf, Vec<String>)> = projects
        .into_iter()
        .map(|project| {
            let dir = projects_dir.join(project);
            let names: Vec<String> = names_or_empty(&dir).into_iter().filter(|n| n.ends_with(".jsonl")).collect();
            (dir, names)
        })
        .collect();

    // Markers this app cannot read by name, which are the only ones worth
    // guessing about. A marker naming a session with a transcript is matched
    // outright further down.
    let named: HashSet<String> = transcripts
        .iter()
        .flat_map(|(_, names)| names.iter().map(|n| n.trim_end_matches(".jsonl").to_string()))
        .collect();
    let guessable_deletions: Vec<f64> =
        contents.deletions.iter().filter(|(k, _)| !named.contains(*k)).map(|(_, v)| *v).collect();

    // Every session on this machine is stamped with one of a handful of
    // accounts, and answering this reads a config.json per candidate.
    let mut owners: HashMap<String, Option<PathBuf>> = HashMap::new();
    let mut owner_of = |facts: &SessionFacts| -> Option<PathBuf> {
        owners
            .entry(facts.owner_account.clone())
            .or_insert_with(|| owner_profile(facts, &candidates))
            .clone()
    };

    // Read in full before anything is filed, because whether a session is a
    // copy of another depends on every other session there is.
    let mut readings: Vec<(SessionFacts, ConversationTrace, SystemTime)> = Vec::new();
    for (dir, names) in &transcripts {
        for name in names {
            let transcript = dir.join(name);
            visited.insert(paths::text(&transcript));
            let last_write = fsx::modified(&transcript);
            let id = name.trim_end_matches(".jsonl");
            let (facts, trace) = cache::transcript_reading(&transcript, id);
            if let Some(facts) = facts {
                readings.push((facts, trace, last_write));
            }
        }
    }
    // Learned while both records of a fork stand, because the pass this
    // matters to is one where neither does: a profile deleted, or a sign-in
    // moving its folder, and every session in it filed again from its
    // transcript alone. Judged that way the original is a copy its fork carried
    // on, and only the fork would have come back.
    state.forks.extend(contents.forks());
    let present: HashSet<&str> = readings.iter().map(|r| r.0.cli_session_id.as_str()).collect();
    state.forks.retain(|k, v| present.contains(k.as_str()) || present.contains(v.as_str()));
    let pairs: Vec<(SessionFacts, ConversationTrace)> =
        readings.iter().map(|(f, t, _)| (f.clone(), t.clone())).collect();
    let copies = resumed_copies(&pairs, &state.forks);
    let mut copies_held_back = 0;
    let withdrawn_set = |w: &BTreeSet<String>| -> HashSet<String> { w.iter().cloned().collect() };

    for (facts, _, last_write) in &readings {
        // A session opened and closed with nothing said in it never had a
        // conversation to lose; listing it would put an empty chat in front of
        // every sidebar for the asking.
        if facts.prompts <= 0 {
            continue;
        }
        let id = &facts.cli_session_id;
        // The records this app files are named for their session, so a marker
        // naming one names the session outright. Markers are never pruned, so
        // the one that withdrew a session is still there on every pass after;
        // reading it as news put a withdrawal in the log for one session on
        // each of 54 passes when nothing had been done to it in weeks.
        if contents.deleted.contains(id) && !withdrawn.contains(id) {
            withdrawn.insert(id.clone());
            state.authored.remove(id);
            diagnostics::note(
                "sweep.withdraw",
                &[("session", json!(id)), ("because", json!("a deletion marker names it"))],
            );
        }
        if withdrawn.contains(id) {
            continue;
        }
        // A record that went missing this very pass is a question the next
        // pass answers. Filing one now would answer it wrong: the record comes
        // back under this app's name, the pass after finds it there, and a chat
        // taken out of a sidebar is quietly put back by the thing that was meant
        // to notice it going.
        if missing.contains(id) {
            continue;
        }
        // A record in a store this pass could not open is a record, not an
        // absence. The session is listed already; the pass just cannot see it.
        if out_of_sight.contains(id) {
            continue;
        }
        // A conversation that carried on under a new command line session is
        // one the sidebar lists under the id it carried on as, and filing its
        // earlier transcript puts the same conversation there a second time.
        if contents.superseded.contains(id) {
            continue;
        }
        let owner = owner_of(facts);

        if recorded.contains(id) {
            // A record on disk that this app did not write belongs to whoever
            // did. One still holding byte for byte what a pass filed here is
            // this app's to keep current, and the transcript moving on past it
            // is the moment to do so.
            if let (Some(owner), Some(authored)) = (owner.as_ref(), state.authored.get(id).cloned()) {
                let (current, older) = record_on_disk_matches(&authored, &record_file(facts, owner));
                match session_update_decision(Some(&authored), facts, current, older) {
                    SessionUpdate::Leave => {}
                    SessionUpdate::TakenOver => {
                        state.authored.remove(id);
                    }
                    SessionUpdate::Refresh => {
                        if rewrite_session_record(facts, owner) {
                            state.authored.insert(id.clone(), facts.clone());
                        }
                    }
                }
            }
            continue;
        }

        // A conversation edited or resumed in the desktop goes on in a new
        // command line session, and the one it left behind has nothing a
        // record names it by. See `resumed_copies`.
        if copies.contains(id) {
            copies_held_back += 1;
            continue;
        }

        let Some(owner) = owner else { continue };
        let decision = session_filing(
            Some(facts),
            &recorded,
            &withdrawn_set(&withdrawn),
            &guessable_deletions,
            *last_write,
            Some(&owner),
            running(&owner),
            session_folder_is_here(&facts.cwd),
            now,
            quiet_window,
        );
        if decision != SessionFiling::File {
            continue;
        }

        // Nothing above knows what the store the record is headed for looked
        // like, only what this session's own history says. A profile with its
        // store stashed away has no sessions on record at all as far as a walk
        // can tell, and a first pass has nothing remembered to contradict it.
        let destination = fsx::parent(&record_file(facts, &owner));
        if !may_file_records(&destination, &contents.stores) {
            let because = if exists(&destination) {
                "the folder is there but this pass never read it"
            } else {
                "this app stashed the folder itself"
            };
            diagnostics::note(
                "sweep.refused",
                &[
                    ("session", json!(id)),
                    ("destination", json!(paths::text(&destination))),
                    ("because", json!(because)),
                ],
            );
            continue;
        }

        if write_session_record(facts, &owner) {
            diagnostics::note(
                "sweep.file",
                &[
                    ("session", json!(id)),
                    ("owner", json!(facts.owner_account)),
                    ("into", json!(fsx::file_name(&owner))),
                    // No title. The session id names it already, and the log is
                    // what a bug report asks to have pasted in.
                    ("cwd", json!(facts.cwd)),
                ],
            );
            filed.push(facts.clone());
            state.records.insert(id.clone(), paths::text(&destination));
            state.authored.insert(id.clone(), facts.clone());
        }
    }

    state.withdrawn = withdrawn.iter().cloned().collect();
    // The loop above withdrew every session a marker names, which happens after
    // the record memory was squared up, so without this those keep an entry
    // naming a folder their record has already gone from, and the two passes it
    // then takes to rediscover them as missing inflate the very counts a
    // diagnosis is read off.
    for session in &withdrawn {
        state.records.remove(session);
    }
    save_session_record_state(&state);
    diagnostics::note(
        "sweep.end",
        &[
            ("filed", json!(filed.len())),
            // Every other count here is this pass's, and a running total under
            // the same kind of name read as five withdrawals a pass.
            ("withdrawn", json!(withdrawn.len() - withdrawn_before)),
            ("withdrawnInAll", json!(withdrawn.len())),
            ("missingThisPass", json!(missing.len())),
            ("outOfSight", json!(out_of_sight.len())),
            ("forgotten", json!(forgotten.len())),
            ("storesStashed", json!(stashed_stores)),
            ("storesUnread", json!(unread_stores)),
            ("storesGone", json!(gone_stores)),
            ("resumedCopies", json!(copies_held_back)),
            ("forksKnown", json!(state.forks.len())),
        ],
    );
    cache::save_transcript_cache(&visited);
    filed
}
