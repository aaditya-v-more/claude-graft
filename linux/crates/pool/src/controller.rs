//! Which Claudes draw on which accounts, and the window's side of keeping the
//! relay and every Claude's Code environment in step with that.
//!
//! The relay holds nothing of its own. Every pass reads each login afresh,
//! since Claude renews them and a lent token has to be the current one, and
//! hands the relay everything again, so a relay that was restarted, replaced
//! or killed is back in step within one pass.
//!
//! The work is split the way the window's threads are. `Controller` is what
//! the window draws from and edits, and nothing in it blocks.
//! `Controller::prepare_pass` gathers what a pass needs from it; `run_pass`
//! does everything that reads a login, a file or a socket, and belongs on a
//! worker thread with the one `PassMemory` the window keeps; and
//! `Controller::apply` takes the result back.

use crate::chats::{self, Chat, Filed, Focus, RecordWatch, Records};
use crate::code_environment::{self, Sealer, State};
use crate::environment;
use crate::limits::PoolLimits;
use crate::wire::{self, PoolCommand, PoolConfiguration, PoolMember, PoolSession, PoolStatus, SocketFailure};
use crate::{now, Time};
use graft_core::{diagnostics, files, paths, IconPreset, Shortcut};
use graft_credentials::{Failure, Prompting, Scheme};
use serde::{Deserialize, Serialize};
use serde_json::json;
use std::collections::{BTreeMap, BTreeSet, HashMap};
use std::path::{Path, PathBuf};
use std::time::Duration;
use uuid::Uuid;

pub const MAIN_KEY: &str = "main";
pub const ACCOUNT_PREFIX: &str = "account:";

/// The dropdown being open is somebody watching where conversations go,
/// which is worth a pass every few seconds; otherwise half a minute keeps
/// logins current, and that is all the relay needs from here.
pub const PASS_INTERVAL_WATCHED: Duration = Duration::from_secs(3);
pub const PASS_INTERVAL: Duration = Duration::from_secs(30);
/// How often the tray asks the relay what it carried and reads again the
/// records whose files changed, between full passes. A store watch wakes a
/// look sooner when a record lands.
pub const PEEK_INTERVAL: Duration = Duration::from_secs(2);

/// Which Claudes draw on which accounts. Every Claude has its own list, kept
/// by the Claude's key, `main` for Claude's own profile and a shortcut's id
/// for the rest, and a list can name other Claudes or accounts signed in
/// through Graft with no Claude of their own.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct UsageSharing {
    #[serde(default)]
    pub lenders: BTreeMap<String, Vec<String>>,
    /// Conversations moved to an account by hand, by session.
    #[serde(default)]
    pub assignments: BTreeMap<String, Assignment>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Assignment {
    pub to: String,
    pub at: Time,
}

/// An account signed in through Graft rather than through a Claude. Its token
/// is sealed with Claude's own safe-storage key, which Graft already holds for
/// reading Claude's logins, so keeping it costs no keyring item and no dialog
/// of its own.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PoolAccount {
    pub id: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub account_uuid: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub email: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub organization_name: Option<String>,
    pub scopes: Vec<String>,
    pub expires: Time,
    pub added: Time,
    pub sealed: String,
    /// Its colour in Graft, given when it was added. One that is also a Claude
    /// here shows that Claude's colour instead: see `preset_of`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub icon_preset: Option<IconPreset>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Lending {
    Ready,
    SignedOut,
    Expired,
    Unreadable,
}

#[derive(Debug, Clone, PartialEq)]
pub struct Login {
    pub state: Lending,
    pub identify: Vec<String>,
    pub token: Option<String>,
}

/// A login that could not be read this pass, with the key in hand, is a
/// `config.json` caught as Claude renames a new one over it. Sent as it
/// stands, it took the member's identity out of the relay for a pass, so the
/// last one read stands in for it, for ten minutes, after which a file still
/// unreadable is not a rename and the token in hand may be one Claude has
/// replaced.
pub fn steady(login: Login, kept: Option<&(Login, Time)>, now: Time) -> Login {
    match kept {
        Some((kept, at)) if login.state == Lending::Unreadable && now - at < 10.0 * 60.0 => kept.clone(),
        _ => login,
    }
}

/// Never prompts. The usage monitor is what asks for the keyring, once, and
/// the key it gets is the one every read in this process shares.
pub fn login_of(profile: &Path) -> Login {
    match graft_credentials::tokens(profile, Prompting::No) {
        Ok(None) | Err(Failure::NotSignedIn) => Login { state: Lending::SignedOut, identify: vec![], token: None },
        Err(_) => Login { state: Lending::Unreadable, identify: vec![], token: None },
        Ok(Some(all)) => {
            let current: Vec<&graft_credentials::Token> = all.iter().filter(|t| t.is_current()).collect();
            let best = |list: Vec<&&graft_credentials::Token>| list.into_iter().max_by_key(|t| t.expires).map(|t| t.value.clone());
            let lent = best(current.iter().filter(|t| t.scopes.iter().any(|s| s == crate::LENDING_SCOPE)).collect())
                .or_else(|| best(current.iter().filter(|t| t.covers(&[graft_credentials::INFERENCE_SCOPE])).collect()));
            Login {
                state: if lent.is_none() { Lending::Expired } else { Lending::Ready },
                identify: all.iter().map(|t| crate::digest(&t.value)).collect(),
                token: lent,
            }
        }
    }
}

/// What answered on the relay's socket.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum RelayReply {
    Running { binary: String },
    /// Something answered in a shape this build cannot read: a relay from a
    /// release whose status this one does not know.
    Unreadable,
    Nobody,
}

/// What becomes of the relay that is there before a pass's configuration
/// goes to it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RelayStep {
    Keep,
    Replace,
    Start,
}

/// A relay keeps the logins it was last given for as long as it runs, and a
/// new one knows none until it is told. So one from another build is replaced
/// only by a Graft that can tell its successor everything: while the keyring
/// is locked this Graft cannot read the logins, and replacing the old relay
/// then would swap lending that worked for a relay that knows nobody. One
/// whose reply cannot be read is a relay all the same; left running, another
/// would be started beside it every twenty seconds and exit on its lock.
pub fn relay_step(reply: &RelayReply, bundled: &str, can_configure: bool) -> RelayStep {
    match reply {
        RelayReply::Nobody => RelayStep::Start,
        RelayReply::Running { binary } if binary == bundled => RelayStep::Keep,
        RelayReply::Running { .. } | RelayReply::Unreadable => {
            if can_configure {
                RelayStep::Replace
            } else {
                RelayStep::Keep
            }
        }
    }
}

/// Blocking; not on the thread that draws.
pub fn ask(command: &PoolCommand, path: &Path) -> (Option<PoolStatus>, RelayReply) {
    match wire::send(command, path, Duration::from_secs(4)) {
        Ok(status) => {
            let binary = status.binary.clone();
            (Some(status), RelayReply::Running { binary })
        }
        Err(SocketFailure::Garbled) => (None, RelayReply::Unreadable),
        Err(_) => (None, RelayReply::Nobody),
    }
}

/// The relay this build carries, beside its own executable: `/app/bin` in the
/// Flatpak, the target folder in a development build. `GRAFT_RELAY` names
/// another, for a script.
pub fn relay_executable() -> Option<PathBuf> {
    if let Some(path) = std::env::var_os("GRAFT_RELAY").filter(|p| !p.is_empty()) {
        return Some(PathBuf::from(path));
    }
    let here = std::env::current_exe().ok()?;
    let candidate = here.parent()?.join("graft-relay");
    candidate.is_file().then_some(candidate)
}

/// What a relay started from this build reports about itself. A relay left
/// running by an earlier version answers with something else and is asked to
/// step aside.
pub fn digest_of(executable: &Path) -> Option<String> {
    std::fs::read(executable).ok().map(|data| files::digest(&data))
}

/// What a pass needs from the window, gathered where the window's state
/// lives.
#[derive(Debug, Clone)]
pub struct PassInput {
    sharing: UsageSharing,
    inputs: Vec<Input>,
    claude_profiles: Vec<(String, PathBuf)>,
}

#[derive(Debug, Clone)]
struct Input {
    key: String,
    name: String,
    profile: Option<PathBuf>,
    account: Option<PoolAccount>,
    borrows_from: Vec<String>,
    limits: Option<PoolLimits>,
}

/// What a pass found, for `Controller::apply`.
#[derive(Debug, Clone)]
pub struct PassOutput {
    lending: BTreeMap<String, Lending>,
    lending_paused: bool,
    status: Option<PoolStatus>,
    problem: Option<String>,
    environment: BTreeMap<String, State>,
    records: Records,
    signed_into: BTreeMap<String, String>,
}

/// What passes remember between them, on the thread that runs them.
pub struct PassMemory {
    last_logins: HashMap<String, (Login, Time)>,
    last_spawn: Time,
    records: RecordWatch,
    relay: Option<PathBuf>,
    bundled: Option<String>,
}

impl PassMemory {
    pub fn new() -> PassMemory {
        let relay = relay_executable();
        let bundled = relay.as_deref().and_then(digest_of);
        PassMemory::with_relay(relay, bundled)
    }

    /// A memory that starts the relay at `relay`, known by `bundled`, for a
    /// test or a script that keeps its relay elsewhere.
    pub fn with_relay(relay: Option<PathBuf>, bundled: Option<String>) -> PassMemory {
        PassMemory { last_logins: HashMap::new(), last_spawn: f64::NEG_INFINITY, records: RecordWatch::new(), relay, bundled }
    }
}

impl Default for PassMemory {
    fn default() -> Self {
        Self::new()
    }
}

/// Everything that reads a login, a file or a socket. Blocking, and so on a
/// worker thread, with the one memory the window keeps for its passes.
pub fn run_pass(input: &PassInput, memory: &mut PassMemory) -> PassOutput {
    let now = now();
    let mut lending = BTreeMap::new();
    let signed_into: BTreeMap<String, String> = input
        .claude_profiles
        .iter()
        .filter_map(|(key, profile)| files::account_of(profile).map(|a| (key.clone(), a)))
        .collect();
    // Shut is not missing: no keyring item at all is a desktop where Claude
    // seals with the fixed key, while a locked keyring has the key and this
    // Graft may not read it yet.
    let key = graft_credentials::safe_storage_key(Prompting::No);
    let key_shut = key.is_err();
    let opener = match key {
        Ok(Some(key)) => Sealer::new(Scheme::V11, key),
        _ => Sealer::new(Scheme::V10, graft_credentials::peanuts_key()),
    };
    let members: Vec<PoolMember> = input
        .inputs
        .iter()
        .map(|item| {
            if let Some(account) = &item.account {
                let token = opener.unseal(&account.sealed).and_then(|t| String::from_utf8(t).ok());
                let current = token.is_some() && account.expires > now + 120.0;
                lending.insert(
                    item.key.clone(),
                    if token.is_none() {
                        Lending::Unreadable
                    } else if current {
                        Lending::Ready
                    } else {
                        Lending::Expired
                    },
                );
                return PoolMember {
                    id: item.key.clone(),
                    name: item.name.clone(),
                    account: account.account_uuid.clone(),
                    identify: token.iter().map(|t| crate::digest(t)).collect(),
                    token: if current { token } else { None },
                    limits: None,
                    borrows_from: None,
                };
            }
            let profile = item.profile.as_deref().expect("a Claude's input carries its profile");
            let mut login = login_of(profile);
            if !key_shut {
                if login.state != Lending::Unreadable {
                    memory.last_logins.insert(item.key.clone(), (login.clone(), now));
                }
                login = steady(login, memory.last_logins.get(&item.key), now);
            }
            lending.insert(item.key.clone(), login.state);
            PoolMember {
                id: item.key.clone(),
                name: item.name.clone(),
                account: signed_into.get(&item.key).cloned(),
                identify: login.identify,
                token: login.token,
                limits: item.limits.clone(),
                borrows_from: Some(item.borrows_from.clone()),
            }
        })
        .collect();
    let assignments: BTreeMap<String, String> =
        input.sharing.assignments.iter().map(|(k, v)| (k.clone(), v.to.clone())).collect();
    let configuration = PoolConfiguration { enabled: true, members: members.clone(), assignments: Some(assignments) };
    let journal = environment::read_journal();
    let wanted = !members.is_empty() || !journal.profiles.is_empty();
    let paused = key_shut && !members.is_empty();
    let (mut status, mut problem) = deliver(&configuration, wanted, !key_shut, memory, now);

    let mut states = BTreeMap::new();
    if wanted {
        let mut wants: Vec<(PathBuf, bool)> = Vec::new();
        for (claude, profile) in &input.claude_profiles {
            let borrows = input.sharing.lenders.get(claude).is_some_and(|l| !l.is_empty());
            if borrows || journal.profiles.contains_key(&environment::key_for(profile)) {
                wants.push((profile.clone(), borrows));
            }
        }
        let relay = memory.relay.as_deref().map(paths::text);
        let by_path = code_environment::settle(
            &wants,
            |profile| Sealer::for_profile(profile, Prompting::No).ok(),
            relay.as_deref(),
            status.is_some(),
            graft_core::claude::is_running,
        );
        for (claude, profile) in &input.claude_profiles {
            if let Some(state) = by_path.get(&environment::key_for(profile)) {
                states.insert(claude.clone(), state.clone());
            }
        }
        // A relay that was not needed until the lines went in.
        if status.is_none() && by_path.values().any(State::holds_lines) {
            (status, problem) = deliver(&configuration, true, !key_shut, memory, now);
        }
        if key_shut {
            problem = problem.or_else(|| {
                Some(if paused {
                    "Lending is paused until the keyring is unlocked".to_string()
                } else {
                    "Graft needs the keyring unlocked to set up Claude Code for sharing.".to_string()
                })
            });
        }
    }

    let sessions: Vec<PoolSession> = status.as_ref().map(|s| s.sessions.clone()).unwrap_or_default();
    let records = memory.records.look_up(&sessions, &input.claude_profiles, now);
    PassOutput { lending, lending_paused: paused, status, problem, environment: states, records, signed_into }
}

/// `complete` is false while the keyring cannot be read, when every login in
/// `configuration` reads as missing: such a configuration is never sent,
/// since a relay told it forgets every login it was lending, and the relay
/// that is there is left with what it has. With no relay at all one is still
/// started, because a Claude pointed at a relay that is not there has no
/// Claude Code; it passes everything through as it came until a pass can tell
/// it more.
fn deliver(
    configuration: &PoolConfiguration,
    wanted: bool,
    complete: bool,
    memory: &mut PassMemory,
    now: Time,
) -> (Option<PoolStatus>, Option<String>) {
    let socket = crate::socket_file();
    if !wanted {
        // Nobody left to serve and no Claude still pointed at it. A relay
        // still running is asked to step aside; one that is not is left that
        // way.
        if files::exists(&socket) {
            let _ = wire::send(&PoolCommand::quit(), &socket, Duration::from_secs(4));
        }
        return (None, None);
    }
    let (Some(executable), Some(expected)) = (memory.relay.clone(), memory.bundled.clone()) else {
        return (None, Some("This copy of Claude Graft has no relay in it.".into()));
    };
    let command = if complete { PoolCommand::configure(configuration.clone()) } else { PoolCommand::status() };
    let (status, reply) = ask(&command, &socket);
    match relay_step(&reply, &expected, complete) {
        RelayStep::Keep => return (status, None),
        RelayStep::Replace => {
            let running = status.as_ref().map(|s| s.binary.chars().take(12).collect::<String>()).unwrap_or("unreadable".into());
            diagnostics::note(
                "relay replaced",
                &[("running", json!(running)), ("bundled", json!(expected.chars().take(12).collect::<String>()))],
            );
            // Bare, because every relay reads a quit whatever else has
            // changed. Answered once the old relay has let go of its port and
            // its lock, so the new one can start straight away.
            let _ = wire::send(&PoolCommand::quit(), &socket, Duration::from_secs(4));
        }
        RelayStep::Start => {}
    }
    // A relay that will not stay up must not be started again every three
    // seconds while somebody watches.
    if now - memory.last_spawn <= 20.0 {
        return (None, Some("The relay is not running. Diagnostics has the reason.".into()));
    }
    memory.last_spawn = now;
    if !environment::spawn_relay(&executable) {
        return (None, Some("The relay could not be started.".into()));
    }
    // The first start makes the certificate, which takes a moment.
    for _ in 0..60 {
        std::thread::sleep(Duration::from_millis(250));
        if let Ok(status) = wire::send(&command, &socket, Duration::from_secs(4)) {
            return (Some(status), None);
        }
    }
    (None, Some("The relay is not running. Diagnostics has the reason.".into()))
}

/// Between passes: what the relay carried since, and the records whose files
/// changed. A relay that stopped answering is a full pass's business, so this
/// answers `None` and changes nothing.
pub fn peek(profiles: &[(String, PathBuf)], memory: &mut PassMemory) -> Option<(PoolStatus, Records)> {
    let status = wire::ask(&PoolCommand::status()).ok()?;
    let records = memory.records.look_up(&status.sessions, profiles, now());
    Some((status, records))
}

/// The dropdown's button while lending is paused. Pressing it is a person
/// asking, which is the one thing that may put the keyring's dialog up; a pass
/// of its own only ever reads silently. A pass should follow, and the usage
/// figures after it, asking nobody.
pub fn open_keychain(interactive: bool) -> bool {
    graft_credentials::safe_storage_key(if interactive { Prompting::Yes } else { Prompting::No }).is_ok()
}

/// Asks the relay to step aside so the next pass starts a fresh one at once.
pub fn restart_relay(memory: &mut PassMemory) {
    let _ = wire::ask(&PoolCommand::quit());
    memory.last_spawn = f64::NEG_INFINITY;
}

/// What the window draws from and edits. Nothing here blocks; a change is
/// saved as it is made, and the window then runs a pass.
#[derive(Debug, Clone, Default)]
pub struct Controller {
    pub sharing: UsageSharing,
    pub accounts: Vec<PoolAccount>,
    pub status: Option<PoolStatus>,
    pub problem: Option<String>,
    /// Something is lent and this Graft cannot read the key the logins are
    /// sealed with, so the relay is left with what it was last told.
    pub lending_paused: bool,
    pub lending: BTreeMap<String, Lending>,
    /// By Claude key.
    pub environment: BTreeMap<String, State>,
    /// By session.
    pub filed: BTreeMap<String, Filed>,
    /// By Claude key.
    pub focused: BTreeMap<String, Focus>,
    /// Which account each Claude is signed into, by key. It comes out of each
    /// profile's `config.json`, so a pass reads it and a view only ever reads
    /// this.
    pub claude_accounts: BTreeMap<String, String>,
    shortcuts: Option<Vec<Shortcut>>,
}

impl Controller {
    /// What is saved, and nothing that runs: no relay, no timer, no file
    /// written.
    pub fn load() -> Controller {
        let sharing = match files::read_json(&crate::settings_file()) {
            files::JsonRead::Value(v) => serde_json::from_value(v).unwrap_or_default(),
            _ => UsageSharing::default(),
        };
        let accounts = match files::read_json(&crate::accounts_file()) {
            files::JsonRead::Value(v) => serde_json::from_value(v).unwrap_or_default(),
            _ => Vec::new(),
        };
        Controller { sharing, accounts, ..Controller::default() }
    }

    /// The shortcut list as the window has it, which names every Claude here.
    pub fn set_shortcuts(&mut self, shortcuts: Vec<Shortcut>) {
        self.shortcuts = Some(shortcuts);
    }

    fn shortcuts(&self) -> &[Shortcut] {
        self.shortcuts.as_deref().unwrap_or(&[])
    }

    fn shortcut(&self, key: &str) -> Option<&Shortcut> {
        let id = Uuid::parse_str(key).ok()?;
        self.shortcuts().iter().find(|s| s.id == id)
    }

    pub fn key_for(shortcut: &Shortcut) -> String {
        shortcut.id.to_string().to_uppercase()
    }

    // Who lends to whom.

    pub fn lenders_of(&self, claude: &str) -> Vec<String> {
        self.sharing.lenders.get(claude).cloned().unwrap_or_default()
    }

    pub fn is_anyone_borrowing(&self) -> bool {
        self.sharing.lenders.values().any(|l| !l.is_empty())
    }

    pub fn add_lender(&mut self, lender: &str, claude: &str) {
        if lender == claude || self.lenders_of(claude).iter().any(|l| l == lender) {
            return;
        }
        let mut next = self.sharing.clone();
        next.lenders.entry(claude.to_string()).or_default().push(lender.to_string());
        self.save(next);
    }

    /// An account signed in through Graft goes when the last Claude using it
    /// lets it go: nothing else would ever lend it, and its token is a year of
    /// access nobody is looking at.
    pub fn remove_lender(&mut self, lender: &str, claude: &str) {
        let mut next = self.sharing.clone();
        if let Some(list) = next.lenders.get_mut(claude) {
            list.retain(|l| l != lender);
            if list.is_empty() {
                next.lenders.remove(claude);
            }
        }
        self.save(next);
        self.forget_unused_accounts();
    }

    /// A choice from the dropdown, or `None` for Automatic. It goes in front
    /// of the ranking and the cache for as long as that account has room, and
    /// is dropped once the conversation has been quiet for a day.
    pub fn assign(&mut self, session: &str, member: Option<&str>) {
        let mut next = self.sharing.clone();
        match member {
            Some(member) => {
                next.assignments.insert(session.to_string(), Assignment { to: member.to_string(), at: now() });
            }
            None => {
                next.assignments.remove(session);
            }
        }
        self.persist(next);
    }

    /// One account signed in twice is one entry, holding the newer login.
    pub fn add_account(&mut self, account: PoolAccount, claude: &str) {
        let mut stored = account.clone();
        let existing = account.account_uuid.as_ref().and_then(|uuid| {
            self.accounts.iter().position(|a| a.account_uuid.as_ref() == Some(uuid))
        });
        if let Some(index) = existing {
            stored.id = self.accounts[index].id.clone();
            stored.icon_preset = self.accounts[index].icon_preset;
            self.accounts[index] = stored.clone();
        } else {
            // One that is a Claude here takes that Claude's colour, and keeps
            // it should that Claude go.
            let same = account
                .account_uuid
                .as_ref()
                .and_then(|uuid| self.claudes().into_iter().find(|c| self.claude_accounts.get(c) == Some(uuid)));
            stored.icon_preset = Some(match same {
                Some(claude) => self.preset_of(&claude),
                None => IconPreset::next(&self.colours_in_use()),
            });
            self.accounts.push(stored.clone());
        }
        self.save_accounts();
        self.add_lender(&stored.id, claude);
    }

    fn forget_unused_accounts(&mut self) {
        let used: BTreeSet<&String> = self.sharing.lenders.values().flatten().collect();
        let kept: Vec<PoolAccount> = self.accounts.iter().filter(|a| used.contains(&a.id)).cloned().collect();
        if kept.len() != self.accounts.len() {
            self.accounts = kept;
            self.save_accounts();
        }
    }

    fn save(&mut self, next: UsageSharing) {
        let borrowers = next.lenders.len();
        let lenders: usize = next.lenders.values().map(Vec::len).sum();
        self.persist(next);
        diagnostics::note("usage sharing changed", &[("borrowers", json!(borrowers)), ("lenders", json!(lenders))]);
    }

    fn persist(&mut self, next: UsageSharing) {
        self.sharing = next;
        if let Ok(data) = serde_json::to_vec(&self.sharing) {
            let _ = files::atomic_write(&crate::settings_file(), &data, 0o600);
        }
    }

    fn save_accounts(&self) {
        let dir = crate::directory();
        if std::fs::create_dir_all(&dir).is_ok() {
            use std::os::unix::fs::PermissionsExt;
            let _ = std::fs::set_permissions(&dir, std::fs::Permissions::from_mode(0o700));
        }
        if let Ok(data) = serde_json::to_vec(&self.accounts) {
            let _ = files::atomic_write(&crate::accounts_file(), &data, 0o600);
        }
    }

    /// A shortcut deleted, or a draft discarded, leaves its id behind here with
    /// nothing to stand for, as a borrower and as a lender alike.
    pub fn prune(&mut self, now: Time) {
        if self.shortcuts.is_none() {
            return;
        }
        let mut known: BTreeSet<String> = BTreeSet::from([MAIN_KEY.to_string()]);
        known.extend(self.shortcuts().iter().map(Self::key_for));
        known.extend(self.accounts.iter().map(|a| a.id.clone()));
        let known_key = |key: &str| known.contains(key) || known.contains(&key.to_uppercase());
        let mut next = self.sharing.clone();
        let claudes: Vec<String> = next.lenders.keys().cloned().collect();
        for claude in claudes {
            let list = next.lenders.remove(&claude).unwrap_or_default();
            let kept: Vec<String> = if known_key(&claude) { list.into_iter().filter(|l| known_key(l)).collect() } else { vec![] };
            if !kept.is_empty() {
                next.lenders.insert(claude, kept);
            }
        }
        // A choice made by hand outlives the cache it was protecting, but not
        // the day: by then the conversation is over or somebody has moved on.
        let active: BTreeSet<String> = self
            .status
            .as_ref()
            .map(|s| s.sessions.iter().filter(|x| now - x.last_at < 86_400.0).map(|x| x.id.clone()).collect())
            .unwrap_or_default();
        next.assignments.retain(|session, a| active.contains(session) || now - a.at < 86_400.0);
        if next != self.sharing {
            self.persist(next);
            self.forget_unused_accounts();
        }
    }

    // Names.

    pub fn profile_of(&self, key: &str) -> Option<PathBuf> {
        if key == MAIN_KEY {
            return Some(paths::main_profile());
        }
        self.shortcut(key).map(Shortcut::profile_dir)
    }

    pub fn name_of(&self, key: &str) -> String {
        if key == MAIN_KEY {
            return "Claude".into();
        }
        if let Some(account) = self.accounts.iter().find(|a| a.id == key) {
            return account.email.clone().unwrap_or_else(|| "Signed-in account".into());
        }
        self.shortcut(key).map(|s| s.name.clone()).unwrap_or_else(|| "Removed shortcut".into())
    }

    /// The colour a Claude or an account is drawn in. An account that is also
    /// a Claude on this machine is that Claude, so it wears that Claude's
    /// colour and follows it when the colour changes.
    pub fn preset_of(&self, key: &str) -> IconPreset {
        if key == MAIN_KEY {
            return IconPreset::Original;
        }
        if Uuid::parse_str(key).is_ok() {
            return self.shortcut(key).map(|s| s.icon_preset).unwrap_or(IconPreset::Original);
        }
        if let Some(claude) = self.claude_same_account_as(key) {
            return self.preset_of(&claude);
        }
        self.accounts.iter().find(|a| a.id == key).and_then(|a| a.icon_preset).unwrap_or(IconPreset::Graphite)
    }

    fn claude_same_account_as(&self, key: &str) -> Option<String> {
        let uuid = self.account_uuid_of(key)?;
        self.claudes().into_iter().find(|c| self.claude_accounts.get(c) == Some(&uuid))
    }

    /// Every colour something here is already drawn in, Claude's own first.
    pub fn colours_in_use(&self) -> Vec<IconPreset> {
        let mut out = vec![IconPreset::Original];
        out.extend(self.shortcuts().iter().map(|s| s.icon_preset));
        out.extend(
            self.accounts.iter().filter(|a| self.claude_same_account_as(&a.id).is_none()).filter_map(|a| a.icon_preset),
        );
        out
    }

    /// Accounts added before accounts had colours are given one, once, in the
    /// order they were added.
    fn colour_accounts(&mut self) {
        if self.shortcuts.is_none() || !self.accounts.iter().any(|a| a.icon_preset.is_none()) {
            return;
        }
        let mut order: Vec<usize> = (0..self.accounts.len()).collect();
        order.sort_by(|a, b| self.accounts[*a].added.partial_cmp(&self.accounts[*b].added).unwrap_or(std::cmp::Ordering::Equal));
        for index in order {
            if self.accounts[index].icon_preset.is_some() {
                continue;
            }
            let id = self.accounts[index].id.clone();
            let preset = match self.claude_same_account_as(&id) {
                Some(claude) => self.preset_of(&claude),
                None => IconPreset::next(&self.colours_in_use()),
            };
            self.accounts[index].icon_preset = Some(preset);
        }
        self.save_accounts();
    }

    /// The Claude a profile folder belongs to, by its key here.
    pub fn claude_key_for(&self, profile: &Path) -> Option<String> {
        let wanted = environment::key_for(profile);
        self.claudes().into_iter().find(|c| self.profile_of(c).map(|p| environment::key_for(&p)) == Some(wanted.clone()))
    }

    pub fn account_uuid_of(&self, key: &str) -> Option<String> {
        if let Some(account) = self.accounts.iter().find(|a| a.id == key) {
            return account.account_uuid.clone();
        }
        self.claude_accounts.get(key).cloned()
    }

    /// Off the drawing thread: every one of these is a file read.
    pub fn read_accounts(claudes: &[(String, PathBuf)]) -> BTreeMap<String, String> {
        claudes.iter().filter_map(|(key, profile)| files::account_of(profile).map(|a| (key.clone(), a))).collect()
    }

    pub fn learn_accounts(&mut self, found: BTreeMap<String, String>) {
        self.claude_accounts = found;
        // After the Claudes' accounts are known, so an account that is one of
        // them takes its colour rather than using up a free one.
        self.colour_accounts();
    }

    /// Every Claude this machine has, by key, Claude's own first.
    pub fn claudes(&self) -> Vec<String> {
        let mut out = vec![MAIN_KEY.to_string()];
        out.extend(self.shortcuts().iter().map(Self::key_for));
        out
    }

    /// The other Claude on the same account, which is worth saying beside an
    /// account somebody has just signed in: it is the same allowance twice.
    pub fn same_account(&self, key: &str, besides: &str) -> Option<String> {
        let uuid = self.account_uuid_of(key)?;
        self.claudes().into_iter().find(|c| c != key && c != besides && self.account_uuid_of(c).as_ref() == Some(&uuid))
    }

    /// Where each Claude keeps its records, for the window's file watch, and
    /// none while nothing is shared. A folder that does not exist yet is left
    /// out until it does, which the next call finds. The watch, not a look,
    /// decides which stores are watched, because the watch itself wakes a look
    /// and must not be torn down from inside its own call.
    pub fn watched_stores(&self) -> Vec<PathBuf> {
        if !self.is_anyone_borrowing() {
            return Vec::new();
        }
        let mut seen = BTreeSet::new();
        self.claudes()
            .iter()
            .filter_map(|c| self.profile_of(c))
            .map(|p| chats::record_store(&p))
            .filter(|s| s.is_dir() && seen.insert(s.clone()))
            .collect()
    }

    /// Whether a look between passes is worth making.
    pub fn should_peek(&self) -> bool {
        self.status.is_some() && self.is_anyone_borrowing()
    }

    pub fn claude_profiles(&self) -> Vec<(String, PathBuf)> {
        self.claudes().into_iter().filter_map(|c| self.profile_of(&c).map(|p| (c, p))).collect()
    }

    // Passes.

    /// What a pass needs from here. `usage` says what the usage endpoint last
    /// read for a profile, for the one detail view of each account the window
    /// keeps; an account the relay has not sent anything to yet is then not a
    /// blank.
    pub fn prepare_pass(&mut self, usage: impl Fn(&Path) -> Option<PoolLimits>) -> PassInput {
        self.prune(now());
        let mut involved: Vec<String> = Vec::new();
        for (claude, list) in &self.sharing.lenders {
            if list.is_empty() {
                continue;
            }
            for key in std::iter::once(claude).chain(list) {
                if !involved.contains(key) {
                    involved.push(key.clone());
                }
            }
        }
        let mut seen = BTreeSet::new();
        // Two shortcuts can stand for one profile; lending it twice would count
        // one account's room twice.
        let inputs = involved
            .into_iter()
            .filter_map(|key| {
                if let Some(account) = self.accounts.iter().find(|a| a.id == key) {
                    return Some(Input {
                        name: account.email.clone().unwrap_or_else(|| key.clone()),
                        key,
                        profile: None,
                        account: Some(account.clone()),
                        borrows_from: vec![],
                        limits: None,
                    });
                }
                let profile = self.profile_of(&key)?;
                if !seen.insert(environment::key_for(&profile)) {
                    return None;
                }
                Some(Input {
                    name: self.name_of(&key),
                    borrows_from: self.lenders_of(&key),
                    limits: usage(&profile),
                    profile: Some(profile),
                    account: None,
                    key,
                })
            })
            .collect();
        PassInput {
            sharing: self.sharing.clone(),
            inputs,
            // Every Claude Graft knows of, so one that stopped borrowing has
            // its lines taken out as well as one that started has them put in.
            claude_profiles: self.claude_profiles(),
        }
    }

    /// Takes a pass's findings back. True when anything a view shows changed,
    /// since most passes change nothing and a redraw for nothing is a cost.
    pub fn apply(&mut self, output: PassOutput) -> bool {
        let before = (
            self.lending.clone(),
            self.lending_paused,
            self.status.clone(),
            self.problem.clone(),
            self.environment.clone(),
            self.filed.clone(),
            self.focused.clone(),
            self.claude_accounts.clone(),
        );
        self.learn_accounts(output.signed_into);
        self.lending = output.lending;
        self.lending_paused = output.lending_paused;
        self.status = output.status;
        self.problem = output.problem;
        self.environment = output.environment;
        self.filed = output.records.filed;
        self.focused = output.records.focused;
        before
            != (
                self.lending.clone(),
                self.lending_paused,
                self.status.clone(),
                self.problem.clone(),
                self.environment.clone(),
                self.filed.clone(),
                self.focused.clone(),
                self.claude_accounts.clone(),
            )
    }

    /// Takes a look's findings back, the same way.
    pub fn apply_peek(&mut self, status: PoolStatus, records: Records) -> bool {
        let changed = self.status.as_ref() != Some(&status) || self.filed != records.filed || self.focused != records.focused;
        self.status = Some(status);
        self.filed = records.filed;
        self.focused = records.focused;
        changed
    }

    // Conversations.

    /// The ones worth a line in the dropdown: from a Claude that borrows,
    /// active in the last half hour, the Claude in front first and then the
    /// newest, five at most.
    pub fn chats(&self, front: Option<&Path>, now: Time) -> Vec<Chat> {
        let Some(status) = &self.status else { return vec![] };
        let front = front.map(environment::key_for);
        let in_front = |session: &PoolSession| {
            front.is_some() && self.profile_of(&session.from).map(|p| environment::key_for(&p)) == front
        };
        let mut listed: Vec<&PoolSession> =
            status.sessions.iter().filter(|s| now - s.last_at < 30.0 * 60.0 && self.is_shown(s)).collect();
        listed.sort_by(|a, b| {
            let (fa, fb) = (in_front(a), in_front(b));
            if fa != fb {
                fb.cmp(&fa)
            } else {
                b.last_at.partial_cmp(&a.last_at).unwrap_or(std::cmp::Ordering::Equal)
            }
        });
        listed.into_iter().take(5).map(|s| self.chat_for(s)).collect()
    }

    /// The conversation on screen in the Claude in front, for the tray.
    pub fn current(&self, front: Option<&Path>, now: Time) -> Option<Chat> {
        let front = front?;
        let status = self.status.as_ref()?;
        let key = environment::key_for(front);
        let mine: Vec<&PoolSession> = status
            .sessions
            .iter()
            .filter(|s| self.is_shown(s) && self.profile_of(&s.from).map(|p| environment::key_for(&p)) == Some(key.clone()))
            .collect();
        let claude = self.claudes().into_iter().find(|c| self.profile_of(c).map(|p| environment::key_for(&p)) == Some(key.clone()));
        let focus = claude.as_ref().and_then(|c| self.focused.get(c));
        let mut list: Vec<Chat> = mine.into_iter().map(|s| self.chat_for(s)).collect();
        if let (Some(claude), Some(focus)) = (&claude, focus) {
            let lenders = self.lenders_of(claude);
            if !lenders.is_empty() {
                let choices = self.one_each(std::iter::once(claude.clone()).chain(lenders).collect());
                let assigned = self.sharing.assignments.get(&focus.session).map(|a| a.to.clone());
                if let Some(alone) = chats::uncarried(focus, claude, &status.sessions, choices, assigned) {
                    list.push(alone);
                }
            }
        }
        chats::current(&list, focus, now)
    }

    /// "Automatic" alone did not say whether a conversation answered by
    /// Claude 2 was there because it was chosen or because the ranking put it
    /// there, so the choice names the account it has settled on.
    pub fn automatic_title(&self, chat: &Chat) -> String {
        match (&chat.assigned, &chat.served_by) {
            (None, Some(served)) => format!("Automatic \u{b7} now {}", self.name_of(served)),
            _ => "Automatic".into(),
        }
    }

    /// A conversation archived or deleted in its sidebar has been put away,
    /// and the tray stops naming it; so has one in a Claude that no longer
    /// draws on anybody.
    fn is_shown(&self, session: &PoolSession) -> bool {
        !self.lenders_of(&session.from).is_empty()
            && self.filed.get(&session.id).map(|f| !f.archived && !f.gone) != Some(false)
    }

    fn chat_for(&self, session: &PoolSession) -> Chat {
        let assigned = self.sharing.assignments.get(&session.id).map(|a| a.to.clone());
        let filed = self.filed.get(&session.id);
        Chat {
            id: session.id.clone(),
            title: filed.and_then(|f| f.title.clone()),
            claude: session.from.clone(),
            served_by: session.served_by.clone(),
            choices: self.one_each(std::iter::once(session.from.clone()).chain(self.lenders_of(&session.from)).collect()),
            last_at: session.last_at,
            passed_over: assigned.is_some() && session.served_by != assigned && !session.passed_over.is_empty(),
            assigned,
            started_at: session.started_at,
            focused_at: filed.and_then(|f| f.focused_at),
            prompted_at: session.prompted_at,
            carried: true,
        }
    }

    /// Everyone a Claude draws on, itself first, one name to an account.
    pub fn members_of(&self, claude: &str) -> Vec<String> {
        let lenders = self.lenders_of(claude);
        if lenders.is_empty() {
            return vec![];
        }
        self.one_each(std::iter::once(claude.to_string()).chain(lenders).collect())
    }

    /// One name for each account, the one the relay counts: the first that
    /// can lend. Two names for one account would be a choice that changes
    /// nothing.
    fn one_each(&self, keys: Vec<String>) -> Vec<String> {
        let can_lend = |key: &str| {
            self.lending.get(key) == Some(&Lending::Ready)
                && self.status.as_ref().and_then(|s| s.members.iter().find(|m| m.id == key)).map(|m| m.token_refused)
                    != Some(true)
        };
        let mut out: Vec<String> = Vec::new();
        let mut place: HashMap<String, usize> = HashMap::new();
        for key in keys {
            let account = self.account_uuid_of(&key).unwrap_or_else(|| key.clone());
            if let Some(&index) = place.get(&account) {
                if index > 0 && !can_lend(&out[index]) && can_lend(&key) {
                    out[index] = key;
                }
            } else {
                place.insert(account, out.len());
                out.push(key);
            }
        }
        out
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use graft_core::testing::Sandbox;
    use std::io::Write;
    use std::os::unix::net::UnixListener;

    const NOW: Time = 1_800_000_000.0;
    const DAY: f64 = 86_400.0;

    #[test]
    fn a_relay_whose_status_cannot_be_read_is_asked_to_step_aside() {
        assert_eq!(relay_step(&RelayReply::Unreadable, "today", true), RelayStep::Replace);
        let older = RelayReply::Running { binary: "older".into() };
        assert_eq!(relay_step(&older, "today", true), RelayStep::Replace, "and so is one built from another binary");
        assert!(
            relay_step(&older, "today", false) == RelayStep::Keep && relay_step(&RelayReply::Unreadable, "today", false) == RelayStep::Keep,
            "but a Graft shut out of the keyring leaves either with the logins it was lending"
        );
        let today = RelayReply::Running { binary: "today".into() };
        assert!(
            relay_step(&today, "today", true) == RelayStep::Keep && relay_step(&today, "today", false) == RelayStep::Keep,
            "a relay built from this binary is kept whatever this Graft can read"
        );
        assert!(
            relay_step(&RelayReply::Nobody, "today", false) == RelayStep::Start
                && relay_step(&RelayReply::Nobody, "today", true) == RelayStep::Start,
            "and with none at all one is started either way, since a Claude pointed at no relay has no Claude Code"
        );
    }

    fn relay_answering(line: String) -> (graft_core::testing::tempfile_lite::TempDir, PathBuf) {
        let dir = graft_core::testing::tempfile_lite::TempDir::new();
        let path = dir.path().join("r.sock");
        let listener = UnixListener::bind(&path).unwrap();
        std::thread::spawn(move || {
            if let Ok((mut client, _)) = listener.accept() {
                let _ = wire::read_line(&mut client, 1 << 20);
                let _ = client.write_all(format!("{line}\n").as_bytes());
            }
        });
        (dir, path)
    }

    #[test]
    fn a_relay_that_answers_in_this_releases_shape_is_known_by_its_binary() {
        let today = PoolStatus {
            binary: "today".into(),
            pid: 1,
            started: 0.0,
            enabled: true,
            configured: true,
            members: vec![],
            recent: vec![],
            sessions: vec![],
            open_exchanges: 0,
        };
        let (_dir, path) = relay_answering(serde_json::to_string(&today).unwrap());
        assert_eq!(ask(&PoolCommand::status(), &path).1, RelayReply::Running { binary: "today".into() });
        let older = r#"{"binary":"older","pid":1,"started":0,"enabled":true,"configured":true,"members":[],"recent":[]}"#;
        let (_dir, path) = relay_answering(older.to_string());
        assert_eq!(ask(&PoolCommand::status(), &path).1, RelayReply::Unreadable, "one in an older shape is still a relay");
        let nobody = graft_core::testing::tempfile_lite::TempDir::new();
        assert_eq!(ask(&PoolCommand::status(), &nobody.path().join("x.sock")).1, RelayReply::Nobody);
    }

    #[test]
    fn a_login_caught_mid_write_is_lent_as_it_was_last_read_whole() {
        let whole = Login { state: Lending::Ready, identify: vec!["digest".into()], token: Some("token".into()) };
        let caught = Login { state: Lending::Unreadable, identify: vec![], token: None };
        assert_eq!(steady(caught.clone(), Some(&(whole.clone(), NOW - 30.0)), NOW), whole);
        assert_eq!(
            steady(caught, Some(&(whole.clone(), NOW - 3600.0)), NOW).state,
            Lending::Unreadable,
            "but not for long: a file still unreadable after ten minutes is not a rename"
        );
        let out = Login { state: Lending::SignedOut, identify: vec![], token: None };
        assert_eq!(steady(out, Some(&(whole, NOW - 30.0)), NOW).state, Lending::SignedOut, "a login read whole says what it says");
    }

    fn account(id: &str, uuid: Option<&str>, sealed: &str) -> PoolAccount {
        PoolAccount {
            id: id.into(),
            account_uuid: uuid.map(str::to_string),
            email: Some(format!("{}@example.com", uuid.unwrap_or("x"))),
            organization_name: None,
            scopes: vec![crate::sign_in::SCOPE.into()],
            expires: NOW + 365.0 * DAY,
            added: NOW,
            sealed: sealed.into(),
            icon_preset: None,
        }
    }

    #[test]
    fn a_machine_that_never_shared_usage_has_nothing_set_up() {
        let _sandbox = Sandbox::new();
        let pool = Controller::load();
        assert!(pool.sharing == UsageSharing::default() && pool.accounts.is_empty());
    }

    #[test]
    fn a_claude_is_given_an_account_to_draw_on_once_and_never_itself() {
        let _sandbox = Sandbox::new();
        let mut pool = Controller::load();
        pool.add_lender("second", MAIN_KEY);
        assert_eq!(pool.lenders_of(MAIN_KEY), ["second"]);
        pool.add_lender("second", MAIN_KEY);
        pool.add_lender(MAIN_KEY, MAIN_KEY);
        assert_eq!(pool.lenders_of(MAIN_KEY), ["second"]);
        assert_eq!(Controller::load().lenders_of(MAIN_KEY), ["second"], "what each Claude draws on is kept between launches");
        assert!(pool.lenders_of("second").is_empty(), "lending to a Claude does not make it a borrower");
        pool.remove_lender("second", MAIN_KEY);
        assert!(pool.sharing.lenders.is_empty(), "a Claude given nothing has no entry left");
    }

    #[test]
    fn one_account_signed_in_twice_is_one_entry_holding_the_newer_login() {
        let _sandbox = Sandbox::new();
        let mut pool = Controller::load();
        pool.add_account(account("account:a", Some("a"), "first"), MAIN_KEY);
        pool.add_account(account("account:other-id", Some("a"), "second"), MAIN_KEY);
        assert!(pool.accounts.len() == 1 && pool.accounts[0].id == "account:a" && pool.accounts[0].sealed == "second");
        assert_eq!(pool.lenders_of(MAIN_KEY), ["account:a"], "and is lent once");
        assert!(
            pool.name_of("account:a") == "a@example.com" && pool.name_of(MAIN_KEY) == "Claude",
            "a signed-in account goes by its address, Claude's own profile by its name"
        );
        pool.add_account(account("account:a", Some("a"), "third"), "elsewhere");
        pool.remove_lender("account:a", MAIN_KEY);
        assert_eq!(pool.accounts.len(), 1, "an account another Claude still draws on is kept");
        pool.remove_lender("account:a", "elsewhere");
        assert!(
            pool.accounts.is_empty() && Controller::load().accounts.is_empty(),
            "and goes, login and all, when nothing draws on it any more"
        );
    }

    #[test]
    fn an_account_signed_in_here_that_a_claude_already_holds_is_named_as_the_same_account() {
        let _sandbox = Sandbox::new();
        let main = paths::main_profile();
        std::fs::create_dir_all(&main).unwrap();
        std::fs::write(main.join("config.json"), br#"{"lastKnownAccountUuid":"a"}"#).unwrap();
        let mut pool = Controller::load();
        pool.set_shortcuts(vec![]);
        assert!(pool.same_account(MAIN_KEY, "elsewhere").is_none(), "nothing is known of a Claude's account until a pass reads it");
        pool.learn_accounts(Controller::read_accounts(&[(MAIN_KEY.to_string(), main)]));
        pool.add_account(account("account:a", Some("a"), "s"), "elsewhere");
        assert_eq!(pool.same_account("account:a", "elsewhere").as_deref(), Some(MAIN_KEY));
        assert_eq!(pool.same_account("account:a", MAIN_KEY), None, "but not to the Claude that holds it");
        assert_eq!(pool.preset_of("account:a"), IconPreset::Original, "and wears that Claude's colour");
    }

    #[test]
    fn a_new_account_takes_the_first_colour_nothing_has() {
        let _sandbox = Sandbox::new();
        let mut pool = Controller::load();
        let mut shortcut = Shortcut::new("Claude 2", None, graft_core::Source::Main, IconPreset::Blue);
        shortcut.id = Uuid::new_v4();
        pool.set_shortcuts(vec![shortcut]);
        pool.add_account(account("account:b", Some("b"), "s"), MAIN_KEY);
        assert_eq!(pool.preset_of("account:b"), IconPreset::Violet);
        pool.add_account(account("account:b2", Some("b"), "renewed"), MAIN_KEY);
        assert_eq!(pool.preset_of("account:b"), IconPreset::Violet, "and a login renewed keeps its colour");
    }

    #[test]
    fn a_choice_made_by_hand_lasts_the_day_and_a_removed_shortcut_leaves_nothing_behind() {
        let _sandbox = Sandbox::new();
        let mut pool = Controller::load();
        let gone = Uuid::new_v4().to_string().to_uppercase();
        pool.add_lender(&gone, MAIN_KEY);
        pool.add_lender("account:missing", MAIN_KEY);
        pool.assign("fresh", Some(MAIN_KEY));
        pool.sharing.assignments.insert("stale".into(), Assignment { to: MAIN_KEY.into(), at: now() - 2.0 * DAY });
        pool.set_shortcuts(vec![]);
        pool.prune(now());
        assert!(pool.sharing.lenders.is_empty(), "a shortcut deleted, or an account forgotten, leaves its id behind nowhere");
        assert!(pool.sharing.assignments.contains_key("fresh") && !pool.sharing.assignments.contains_key("stale"));
    }

    fn session(id: &str, from: &str, served: &str, last_at: Time) -> PoolSession {
        PoolSession {
            id: id.into(),
            from: from.into(),
            served_by: Some(served.into()),
            last_at,
            requests: 1,
            model: None,
            cached_until: None,
            passed_over: vec![],
            started_at: None,
            prompted_at: None,
        }
    }

    fn status_with(sessions: Vec<PoolSession>) -> PoolStatus {
        PoolStatus {
            binary: "b".into(),
            pid: 1,
            started: 0.0,
            enabled: true,
            configured: true,
            members: vec![],
            recent: vec![],
            sessions,
            open_exchanges: 0,
        }
    }

    #[test]
    fn automatic_says_which_account_it_has_settled_on() {
        let pool = Controller::default();
        let t = now();
        let mut chat = pool.chat_for(&session("a", MAIN_KEY, MAIN_KEY, t));
        assert_eq!(pool.automatic_title(&chat), "Automatic \u{b7} now Claude");
        chat.assigned = Some("c2".into());
        assert_eq!(pool.automatic_title(&chat), "Automatic", "and only that, once an account is chosen");
        chat.assigned = None;
        chat.served_by = None;
        assert_eq!(pool.automatic_title(&chat), "Automatic", "or before anyone has answered");
    }

    #[test]
    fn a_conversation_put_away_leaves_the_dropdown_and_the_tray_alike() {
        let _sandbox = Sandbox::new();
        let t = now();
        let mut pool = Controller::load();
        pool.set_shortcuts(vec![]);
        pool.add_lender("account:x", MAIN_KEY);
        pool.status = Some(status_with(vec![session("kept", MAIN_KEY, MAIN_KEY, t), session("archived", MAIN_KEY, MAIN_KEY, t - 1.0)]));
        pool.filed.insert("archived".into(), Filed { archived: true, ..Filed::default() });
        let front = paths::main_profile();
        let listed: Vec<String> = pool.chats(Some(&front), t).into_iter().map(|c| c.id).collect();
        assert_eq!(listed, ["kept"]);
        pool.focused.insert(MAIN_KEY.into(), Focus::new("archived", t));
        assert!(pool.current(Some(&front), t).is_none(), "and the tray names nothing rather than the one before it");
    }

    #[test]
    fn one_name_stands_for_one_account_the_one_that_can_lend() {
        let _sandbox = Sandbox::new();
        let mut pool = Controller::load();
        let mut shortcut = Shortcut::new("Claude 2", None, graft_core::Source::Main, IconPreset::Blue);
        shortcut.id = Uuid::new_v4();
        let c2 = Controller::key_for(&shortcut);
        pool.set_shortcuts(vec![shortcut]);
        pool.claude_accounts.insert(c2.clone(), "acct-2".into());
        pool.accounts.push(account("account:acct-2", Some("acct-2"), "s"));
        pool.lending.insert(c2.clone(), Lending::Expired);
        pool.lending.insert("account:acct-2".into(), Lending::Ready);
        pool.sharing.lenders.insert(MAIN_KEY.into(), vec![c2.clone(), "account:acct-2".into()]);
        assert_eq!(pool.members_of(MAIN_KEY), [MAIN_KEY.to_string(), "account:acct-2".to_string()]);
        pool.lending.insert(c2.clone(), Lending::Ready);
        assert_eq!(pool.members_of(MAIN_KEY), [MAIN_KEY.to_string(), c2], "and the first named while both can");
    }
}
