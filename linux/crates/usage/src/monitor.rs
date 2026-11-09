//! Polls each profile's plan usage.
//!
//! Two sources, in order of preference. The live one is Anthropic's usage
//! endpoint, reached with the token that profile already holds: exact reset
//! times, and current figures even when that Claude is not running. The
//! fallback is `plan-usage-history.json`, which a profile writes while it runs,
//! good enough to show something before the keyring's unlock dialog is
//! answered, or if it is declined.
//!
//! The fallback is never taken in silence. When the keyring turns out to be
//! locked, one pass raises the dialog and the tray says so until it is
//! answered; `set_may_prompt_unasked` is the one case that waits.
//!
//! Every pass runs on the monitor's own thread, one at a time, and that same
//! thread is the only one that touches the live cache and the back-off, which
//! is what orders `invalidate` against the pass that follows it.

use crate::api::{ApiFailure, Reading, Session};
use crate::history::{self, countdown, Headline, Usage};
use graft_core::claude::{self, Process};
use graft_core::{files, paths};
use graft_credentials::{Failure, Prompting, USAGE_SCOPE};
use serde_json::{json, Map, Value};
use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::mpsc::{self, Receiver, RecvTimeoutError, Sender};
use std::sync::{Arc, Condvar, Mutex};
use std::time::{Duration, SystemTime};
use uuid::Uuid;

// MARK: the rules, kept pure so the suite can drive them

/// A tick nobody asked for reuses a figure up to five minutes old.
pub const LIVE_INTERVAL: Duration = Duration::from_secs(5 * 60);
/// A Claude that is open is a Claude being used, and its figures move while
/// nobody looks. Five minutes behind an account in use is what made the bar
/// read like it had stopped; one closed moves only when something else spends
/// it, and keeps the longer interval.
pub const ACTIVE_INTERVAL: Duration = Duration::from_secs(2 * 60);
/// The five-minute cache exists to keep a thirty-second timer off the
/// endpoint, not to make a button do nothing. Someone looking gets a shorter
/// cache; the floor is still there, because a button can be pressed faster
/// than any service wants to answer.
pub const RECENT_INTERVAL: Duration = Duration::from_secs(60);
/// Opening the tray's window is someone looking; pressing Refresh Usage is
/// someone asking. With one interval for both, the minute that made the
/// first cheap made the second do nothing at all. The floor left here is only
/// wide enough to swallow a double-click.
pub const NOW_INTERVAL: Duration = Duration::from_secs(2);
/// A failed call must not simply be retried on the next thirty-second tick:
/// that turns one refused request into a hundred and twenty an hour, which is
/// exactly how a client earns a rate limit.
pub const BACKOFF_STEPS: [u64; 5] = [60, 120, 300, 900, 1800];
pub const TICK: Duration = Duration::from_secs(30);

/// How current the figures need to be for whoever is asking.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Freshness {
    /// A tick nobody asked for. Five minutes old will do.
    Cached,
    /// Someone is looking at the numbers right now.
    Recent,
    /// Someone pressed something and is waiting for the answer.
    Now,
}

pub fn may_use_cache(age: Duration, freshness: Freshness, running: bool) -> bool {
    match freshness {
        Freshness::Cached => age < if running { ACTIVE_INTERVAL } else { LIVE_INTERVAL },
        Freshness::Recent => age < RECENT_INTERVAL,
        Freshness::Now => age < NOW_INTERVAL,
    }
}

/// Two requests waiting on one pass are one request for the stronger of them.
pub fn stronger(a: Freshness, b: Freshness) -> Freshness {
    let rank = |f: Freshness| match f {
        Freshness::Now => 2,
        Freshness::Recent => 1,
        Freshness::Cached => 0,
    };
    if rank(a) >= rank(b) {
        a
    } else {
        b
    }
}

/// A profile writes its own record while it runs. When that is newer than the
/// last answer from the service, it is the better figure: a reading held in
/// the cache used to win regardless, so the bar stayed behind an account the
/// profile itself had just reported on.
pub fn prefers_local(live_fetched: SystemTime, local_sampled: Option<SystemTime>) -> bool {
    local_sampled.map(|s| s > live_fetched).unwrap_or(false)
}

/// Which profile a Claude process belongs to, from its command line. The main
/// profile is the one launched with no `--user-data-dir` at all.
pub fn profile_launched_by(command: &str, among: &[PathBuf]) -> Option<PathBuf> {
    if !claude::is_claude_process(command) {
        return None;
    }
    if claude::is_default_instance(command) {
        return among.iter().find(|p| paths::is_main_profile(p)).cloned();
    }
    among.iter().find(|p| claude::carries_data_dir(command, p)).cloned()
}

/// What becomes of a request that arrives while a pass is already running.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Arrival {
    Start,
    Queue,
    Drop,
}

/// A tick is worth dropping: the pass in flight is about to answer the same
/// question. Anything stronger is not. Opening the tray's window starts a
/// pass of its own, so pressing Refresh Usage a moment later, the obvious
/// thing to do when the figure looks stale, used to land inside it and be
/// discarded without a trace; and opening the window mid-pass, dropped like a
/// tick, showed whatever the timer's pass happened to reuse.
pub fn arrival(pass_in_flight: bool, interactive: bool, freshness: Freshness) -> Arrival {
    if !pass_in_flight {
        Arrival::Start
    } else if interactive || freshness != Freshness::Cached {
        Arrival::Queue
    } else {
        Arrival::Drop
    }
}

/// How long to wait after `failures` consecutive failures. A `Retry-After`
/// from the service wins over Graft's own schedule, floored at a minute.
pub fn retry_delay(failures: u32, retry_after: Option<f64>) -> Duration {
    if let Some(asked) = retry_after.filter(|a| *a > 0.0) {
        return Duration::from_secs_f64(asked.max(BACKOFF_STEPS[0] as f64));
    }
    let index = (failures.max(1) as usize - 1).min(BACKOFF_STEPS.len() - 1);
    Duration::from_secs(BACKOFF_STEPS[index])
}

/// Who may raise the keyring dialog for one profile in a pass. Once the
/// keyring has said no in this pass, nobody: the password is one item for
/// every profile, so each profile after a refusal was another dialog asking
/// the same question.
pub fn prompting(asking: Prompting, elsewhere: Prompting, focused: bool, locked: bool) -> Prompting {
    if locked {
        Prompting::No
    } else if focused {
        asking
    } else {
        elsewhere
    }
}

/// `Retry-After` is the one wait a person's own refresh cannot shorten.
pub fn may_attempt(now: SystemTime, until: Option<SystemTime>, interactive: bool, server_asked: bool) -> bool {
    match until {
        None => true,
        Some(until) if now >= until => true,
        Some(_) => interactive && !server_asked,
    }
}

/// Why asking for one profile's live figure failed: before anything was sent,
/// or after.
#[derive(Debug, Clone, PartialEq)]
pub enum LiveError {
    Credentials(Failure),
    Api(ApiFailure),
}

impl LiveError {
    pub fn message(&self) -> String {
        match self {
            LiveError::Credentials(f) => f.message().to_string(),
            LiveError::Api(a) => a.message(),
        }
    }
}

/// The `Retry-After` a failure carried, if the service sent one.
pub fn wait_asked(error: &LiveError) -> Option<f64> {
    match error {
        LiveError::Api(ApiFailure::Http { retry_after, .. }) => *retry_after,
        _ => None,
    }
}

pub fn is_keyring_failure(error: &LiveError) -> bool {
    matches!(error, LiveError::Credentials(Failure::NoKeyringAccess | Failure::KeyringDeclined))
}

/// Only an answer, or no answer, from the service is a reason to back off.
/// Everything the credentials can refuse happens before anything is sent, is
/// put right on this computer, and is tried again on the next pass.
pub fn came_from_service(error: &LiveError) -> bool {
    matches!(error, LiveError::Api(_))
}

/// The row's word for why its figure is not live. Every failure used to read
/// "Anthropic didn't answer", which sent someone with an expired login to look
/// at their network.
pub fn failure_line(error: &LiveError) -> &'static str {
    match error {
        LiveError::Credentials(Failure::NoKeyringAccess | Failure::KeyringDeclined) => {
            "Live usage needs the keyring unlocked"
        }
        LiveError::Credentials(Failure::Expired) => "Login expired; open that Claude to renew it",
        LiveError::Credentials(Failure::NotSignedIn) => "Its login could not be read on this computer",
        LiveError::Credentials(Failure::ConfigUnreadable) => "Its settings were being saved; trying again",
        LiveError::Api(_) => "Anthropic didn't answer",
    }
}

/// The time alone for today, with the day for anything older, in local time.
pub fn clock(date: SystemTime, now: SystemTime) -> String {
    let (Some(when), Some(today)) = (local(date), local(now)) else { return String::new() };
    let same_day = when.tm_year == today.tm_year && when.tm_yday == today.tm_yday;
    strftime(&when, if same_day { "%H:%M" } else { "%e %b %Y, %H:%M" }).trim().to_string()
}

fn local(time: SystemTime) -> Option<libc::tm> {
    let secs = time.duration_since(SystemTime::UNIX_EPOCH).ok()?.as_secs() as libc::time_t;
    let mut out: libc::tm = unsafe { std::mem::zeroed() };
    // localtime_r rather than localtime: the result is ours, not a buffer
    // another thread may be writing into.
    let ok = unsafe { !libc::localtime_r(&secs, &mut out).is_null() };
    ok.then_some(out)
}

fn strftime(tm: &libc::tm, format: &str) -> String {
    let format = std::ffi::CString::new(format).unwrap_or_default();
    let mut buffer = [0u8; 64];
    let written = unsafe { libc::strftime(buffer.as_mut_ptr().cast(), buffer.len(), format.as_ptr(), tm) };
    String::from_utf8_lossy(&buffer[..written]).into_owned()
}

/// "just now", "5 min ago", "3 hr ago", "2 days ago".
fn relative(then: SystemTime, now: SystemTime) -> String {
    let age = now.duration_since(then).unwrap_or_default().as_secs();
    if age < 45 {
        "just now".into()
    } else if age < 3_600 {
        format!("{} min ago", (age / 60).max(1))
    } else if age < 86_400 {
        format!("{} hr ago", age / 3_600)
    } else {
        let days = age / 86_400;
        format!("{days} {} ago", if days == 1 { "day" } else { "days" })
    }
}

// MARK: what a pass found

#[derive(Debug, Clone, PartialEq)]
pub struct Entry {
    pub name: String,
    pub profile: PathBuf,
    pub usage: Option<Usage>,
    pub is_running: bool,
    /// `None` for the main profile, which has no shortcut behind it.
    pub shortcut: Option<Uuid>,
    /// True when the figures came from the API rather than from disk.
    pub is_live: bool,
    pub plan: Option<String>,
    /// When the figures shown were actually taken: the moment the service
    /// answered, or the moment the profile wrote its own record. Never the
    /// moment a pass happened to run, which is what every reused reading used
    /// to be stamped with: a figure five minutes old presented as one taken
    /// just now, and an hour-old one after a failure presented the same way.
    pub refreshed: Option<SystemTime>,
    /// Why these are not a live figure from this pass, in a sentence, or
    /// `None` when they are. The fallback is never taken in silence.
    pub note: Option<String>,
}

impl Entry {
    pub fn new(name: &str, profile: PathBuf, usage: Option<Usage>, is_running: bool) -> Entry {
        Entry {
            name: name.to_string(),
            profile,
            usage,
            is_running,
            shortcut: None,
            is_live: false,
            plan: None,
            refreshed: None,
            note: None,
        }
    }

    pub fn id(&self) -> String {
        paths::text(&self.profile)
    }

    /// When the figure was taken and where from, for the line under it.
    pub fn freshness(&self, now: SystemTime) -> String {
        let Some(refreshed) = self.refreshed else { return "Not refreshed yet".into() };
        let when = relative(refreshed, now);
        if self.is_live {
            format!("Updated {when} from Anthropic")
        } else {
            format!("Updated {when} from this profile's own record")
        }
    }

    /// A live figure was taken just now, so only one read off disk can be old
    /// enough to be worth warning about.
    pub fn is_dimmed(&self) -> bool {
        self.usage.as_ref().map(|u| !self.is_live && u.is_stale()).unwrap_or(false)
    }

    /// The figures, when they still say something about the window now. A
    /// figure read off disk hours ago does not, and the bar leaves it out.
    pub fn current_usage(&self) -> Option<&Usage> {
        self.usage.as_ref().filter(|u| self.is_live || !u.is_stale())
    }

    /// Start Session opens a five-hour window, and an account without one has
    /// nothing for it to open. Before any figure has come in, whether it has
    /// one is not known yet, and the button stays.
    pub fn may_start_session(&self) -> bool {
        self.usage.as_ref().map(|u| u.five_hour.is_some()).unwrap_or(true)
    }
}

/// The account the number in the tray is about.
///
/// Whatever is open, since that is the window being spent. An account left
/// sitting at its limit should not shout over the one actually in use. With
/// nothing open it falls back to whichever is closest to its limit, each
/// weighed by the figure the bar would show for it: weighed by the five-hour
/// window alone, an account without one counted as zero and lost to every
/// other account, however little of its week was left.
pub fn headline_entry(entries: &[Entry]) -> Option<&Entry> {
    let usable: Vec<&Entry> = entries.iter().filter(|e| e.current_usage().is_some()).collect();
    let running: Vec<&Entry> = usable.iter().copied().filter(|e| e.is_running).collect();
    let pool = if running.is_empty() { usable } else { running };
    let weight = |e: &&Entry| e.usage.as_ref().map(|u| u.headline().percent).unwrap_or(0);
    // The first of equals, so a tie goes to the main profile, as it does on
    // a Mac.
    pool.into_iter().fold(None, |best: Option<&Entry>, e| match best {
        Some(b) if weight(&b) >= weight(&e) => Some(b),
        _ => Some(e),
    })
}

/// One account's entry, by path.
///
/// The window used to read `plan-usage-history.json` for itself while the bar
/// showed what the endpoint had just said, so one account was reported from
/// two sources that do not agree. The file is only written while that Claude
/// runs, so a profile closed since Tuesday had Tuesday's figures in the window
/// and today's in the bar, from before a weekly reset. Every view showing one
/// account asks this.
pub fn entry_for<'a>(entries: &'a [Entry], profile: &Path) -> Option<&'a Entry> {
    entries.iter().find(|e| paths::same_path(&e.profile, profile))
}

/// The account the tray is about, and whether it is named there.
///
/// Named, it is the Claude in front whatever its figures say. Unnamed, the
/// tightest window open is the right figure to show; with a name beside it,
/// another account's figure would be read as that account's own.
pub fn status_entry(entries: &[Entry], front: Option<&Path>) -> Option<(Entry, bool)> {
    if let Some(named) = front.and_then(|f| entry_for(entries, f)) {
        return Some((named.clone(), true));
    }
    headline_entry(entries).map(|e| (e.clone(), false))
}

/// Everything a view needs from the monitor at one moment.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct Snapshot {
    pub entries: Vec<Entry>,
    /// Set when live usage is unavailable for a reason worth showing once.
    pub live_problem: Option<String>,
    /// True once a pass has found the keyring locked to it, so the tray can
    /// point at Refresh Usage rather than silently showing the weaker figures
    /// from disk.
    pub needs_keyring_access: bool,
    /// True while a pass someone asked for is running, so a button can say
    /// so. Ticks leave it alone: a spinner every thirty seconds is noise.
    pub is_refreshing: bool,
    /// The one profile that pass is about, or `None` when it is about all of
    /// them, so only that row's button spins.
    pub refreshing_profile: Option<PathBuf>,
}

impl Snapshot {
    pub fn entry_for(&self, profile: &Path) -> Option<&Entry> {
        entry_for(&self.entries, profile)
    }

    pub fn headline_entry(&self) -> Option<&Entry> {
        headline_entry(&self.entries)
    }

    pub fn headline(&self) -> Option<Headline> {
        self.headline_entry().and_then(|e| e.usage.as_ref()).map(Usage::headline)
    }

    pub fn status_entry(&self, front: Option<&Path>) -> Option<(Entry, bool)> {
        status_entry(&self.entries, front)
    }
}

// MARK: asking for a pass

/// One request for figures. The constructors are the ways the app asks.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct Refresh {
    /// A person pressed something, which is what allows a pass to skip its
    /// own back-off. Whether the keyring dialog may be raised is a separate
    /// question, because opening the tray's window should be allowed to ask
    /// for access without also being allowed to hammer the endpoint.
    pub interactive: bool,
    pub prompting: Option<Prompting>,
    pub freshness: Option<Freshness>,
    /// Narrows the request to one profile: it gets the freshness asked for,
    /// and every other profile is read as a tick would read it.
    pub focus: Option<PathBuf>,
}

impl Refresh {
    /// The timer's pass.
    pub fn tick() -> Refresh {
        Refresh::default()
    }

    /// Refresh Usage pressed for every account.
    pub fn pressed() -> Refresh {
        Refresh { interactive: true, ..Refresh::default() }
    }

    /// A row's own refresh button.
    pub fn pressed_for(profile: &Path) -> Refresh {
        Refresh { interactive: true, focus: Some(profile.to_path_buf()), ..Refresh::default() }
    }

    /// The tray's window or the main window opening: someone looking. It may
    /// raise the one dialog, and wants a figure inside the minute, but keeps
    /// to the back-off, since a failing endpoint would otherwise be asked
    /// again on every open.
    pub fn looking() -> Refresh {
        Refresh { prompting: Some(Prompting::OnceIfShut), freshness: Some(Freshness::Recent), ..Refresh::default() }
    }

    /// Bringing a Claude to the front is switching to that account, which is
    /// the moment its figure matters. Only that profile is asked, and only as
    /// often as someone looking may ask, so flicking between windows costs at
    /// most one request a minute.
    pub fn came_forward(profile: &Path) -> Refresh {
        Refresh { freshness: Some(Freshness::Recent), focus: Some(profile.to_path_buf()), ..Refresh::default() }
    }

    /// Sharing usage wants figures inside the minute to choose a lender by,
    /// and never a dialog.
    pub fn for_lending() -> Refresh {
        Refresh { prompting: Some(Prompting::No), freshness: Some(Freshness::Recent), ..Refresh::default() }
    }

    /// The refresh after a session was started. Starting it already asked
    /// the keyring for this press, so this asks nobody, and only about the
    /// account the session was started on: unfocused and allowed to ask, a
    /// refusal was answered with a dialog for every signed-in Claude in turn.
    pub fn after_session_started(profile: &Path) -> Refresh {
        Refresh {
            interactive: true,
            prompting: Some(Prompting::No),
            freshness: None,
            focus: Some(profile.to_path_buf()),
        }
    }
}

/// A request that arrived mid-pass, run as soon as that pass is out of the
/// way. Only ever one: two presses waiting on the same answer are one press.
#[derive(Debug, Clone, PartialEq)]
pub struct Waiting {
    pub interactive: bool,
    /// Kept rather than assumed. A queued request used to be replayed as a
    /// press whatever it had been, so merely opening the tray mid-pass
    /// skipped the back-off and put a spinner up.
    pub prompting: Prompting,
    pub freshness: Freshness,
    pub focus: Option<PathBuf>,
}

/// Folds a second request into one already waiting. A focus survives only
/// when both were about the same profile; two different ones are a pass
/// about everybody.
pub fn merge_waiting(earlier: Option<Waiting>, interactive: bool, asking: Prompting, wanted: Freshness, focus: Option<PathBuf>) -> Waiting {
    match earlier {
        None => Waiting { interactive, prompting: asking, freshness: wanted, focus },
        Some(earlier) => Waiting {
            interactive: earlier.interactive || interactive,
            prompting: if interactive { asking } else { earlier.prompting },
            freshness: stronger(earlier.freshness, wanted),
            focus: match (earlier.focus, focus) {
                (Some(a), Some(b)) if paths::same_path(&a, &b) => Some(a),
                _ => None,
            },
        },
    }
}

/// One profile a pass reads.
#[derive(Debug, Clone, PartialEq)]
pub struct Target {
    pub name: String,
    pub profile: PathBuf,
    pub shortcut: Option<Uuid>,
}

/// Claude's own profile first, then every shortcut that has been created. A
/// draft in the list has no Claude behind it yet.
pub fn targets_from_shortcuts() -> Vec<Target> {
    let mut out = vec![Target { name: "Claude".into(), profile: paths::main_profile(), shortcut: None }];
    for shortcut in graft_core::config::load_shortcuts() {
        if shortcut.installed_name.is_some() {
            out.push(Target { name: shortcut.name.clone(), profile: shortcut.profile_dir(), shortcut: Some(shortcut.id) });
        }
    }
    out
}

type TargetsFn = dyn Fn() -> Vec<Target> + Send + Sync;
type ProcessesFn = dyn Fn() -> Vec<Process> + Send + Sync;

/// Where a monitor finds the profiles it reads and the processes it checks
/// them against. The suite hands in its own; the app takes `Sources::system`.
#[derive(Clone)]
pub struct Sources {
    pub targets: Arc<TargetsFn>,
    pub processes: Arc<ProcessesFn>,
}

impl Sources {
    pub fn system() -> Sources {
        Sources { targets: Arc::new(targets_from_shortcuts), processes: Arc::new(claude::processes) }
    }
}

/// A plain record of what the last pass found, so the state of live usage can
/// be checked without reading it off the screen. Figures only: no token, and
/// nothing that is not already on disk elsewhere.
pub fn status_file() -> PathBuf {
    paths::graft_file("usage-status.json")
}

// MARK: the monitor

pub type SubscriptionId = u64;
type Subscriber = Arc<dyn Fn(&Snapshot) + Send + Sync>;

enum Message {
    Pass(Job),
    Invalidate(PathBuf),
}

struct Job {
    targets: Vec<Target>,
    interactive: bool,
    asking: Prompting,
    elsewhere: Prompting,
    wanted: Freshness,
    focus: Option<PathBuf>,
}

#[derive(Default)]
struct Control {
    in_flight: bool,
    waiting: Option<Waiting>,
}

struct Shared {
    sources: Sources,
    sender: Mutex<Sender<Message>>,
    published: Mutex<Snapshot>,
    control: Mutex<Control>,
    idle: Condvar,
    may_prompt_unasked: AtomicBool,
    subscribers: Mutex<Vec<(SubscriptionId, Subscriber)>>,
    next_id: AtomicU64,
}

/// The usage monitor. One per process, owned by the app; dropping it ends
/// its thread and its timer.
pub struct Monitor {
    shared: Arc<Shared>,
    ticker: Mutex<Option<Sender<()>>>,
}

impl Monitor {
    pub fn new(sources: Sources) -> Monitor {
        let (sender, receiver) = mpsc::channel();
        let shared = Arc::new(Shared {
            sources,
            sender: Mutex::new(sender),
            published: Mutex::new(Snapshot::default()),
            control: Mutex::new(Control::default()),
            idle: Condvar::new(),
            may_prompt_unasked: AtomicBool::new(true),
            subscribers: Mutex::new(Vec::new()),
            next_id: AtomicU64::new(1),
        });
        let weak = Arc::downgrade(&shared);
        std::thread::Builder::new()
            .name("graft-usage".into())
            .spawn(move || worker(weak, receiver))
            .expect("the usage thread starts");
        Monitor { shared, ticker: Mutex::new(None) }
    }

    pub fn system() -> Monitor {
        Monitor::new(Sources::system())
    }

    /// Called with the new state whenever it changes, on whichever thread
    /// changed it (the monitor's own, or the one that asked for a pass). A
    /// GTK app hops to its main loop inside the callback.
    pub fn subscribe(&self, callback: impl Fn(&Snapshot) + Send + Sync + 'static) -> SubscriptionId {
        let id = self.shared.next_id.fetch_add(1, Ordering::SeqCst);
        self.shared.subscribers.lock().unwrap_or_else(|e| e.into_inner()).push((id, Arc::new(callback)));
        id
    }

    pub fn unsubscribe(&self, id: SubscriptionId) {
        self.shared.subscribers.lock().unwrap_or_else(|e| e.into_inner()).retain(|(i, _)| *i != id);
    }

    pub fn snapshot(&self) -> Snapshot {
        self.shared.published.lock().unwrap_or_else(|e| e.into_inner()).clone()
    }

    pub fn entries(&self) -> Vec<Entry> {
        self.snapshot().entries
    }

    pub fn entry_for(&self, profile: &Path) -> Option<Entry> {
        self.snapshot().entry_for(profile).cloned()
    }

    pub fn headline_entry(&self) -> Option<Entry> {
        self.snapshot().headline_entry().cloned()
    }

    pub fn headline(&self) -> Option<Headline> {
        self.snapshot().headline()
    }

    pub fn status_entry(&self, front: Option<&Path>) -> Option<(Entry, bool)> {
        self.snapshot().status_entry(front)
    }

    /// Whether a pass nobody asked for may raise the one keyring dialog.
    ///
    /// False only while the app is coming up into the tray with no window,
    /// which is what an autostart does: a dialog thrown over whatever someone
    /// is doing at login is an ambush, and the answer to it is worthless. The
    /// app turns it on the moment the window or the tray's window is opened,
    /// which is when the figures are being looked at anyway.
    pub fn set_may_prompt_unasked(&self, may: bool) {
        self.shared.may_prompt_unasked.store(may, Ordering::SeqCst);
    }

    pub fn may_prompt_unasked(&self) -> bool {
        self.shared.may_prompt_unasked.load(Ordering::SeqCst)
    }

    /// Starts the timer. Idempotent: the window and the tray both ask for it,
    /// and only one of the two may ever appear; a second call is a tick.
    pub fn start(&self, every: Duration) {
        let mut ticker = self.ticker.lock().unwrap_or_else(|e| e.into_inner());
        self.refresh(Refresh::tick());
        if ticker.is_some() {
            return;
        }
        let (stop, wait) = mpsc::channel::<()>();
        let weak = Arc::downgrade(&self.shared);
        std::thread::Builder::new()
            .name("graft-usage-timer".into())
            .spawn(move || {
                // Anything but a timeout is the monitor letting go of the
                // other end, which is the timer's cue to stop.
                while let Err(RecvTimeoutError::Timeout) = wait.recv_timeout(every) {
                    match weak.upgrade() {
                        Some(shared) => shared.refresh(Refresh::tick()),
                        None => break,
                    }
                }
            })
            .expect("the usage timer starts");
        *ticker = Some(stop);
    }

    /// See `Refresh::came_forward`. The app's front-window watch calls this.
    pub fn claude_came_forward(&self, profile: &Path) {
        self.refresh(Refresh::came_forward(profile));
    }

    pub fn refresh(&self, request: Refresh) {
        self.shared.refresh(request);
    }

    /// Drops the stored reading for one profile, for the caller that has just
    /// changed the number itself. Starting a session opens a five-hour window,
    /// so the figure taken before it is known to be wrong, and it was still
    /// inside every cache window there is, which is why pressing Start
    /// Session moved nothing on screen. Ordered ahead of the refresh that
    /// follows by the thread both go through.
    pub fn invalidate(&self, profile: &Path) {
        let _ = self.shared.sender.lock().unwrap_or_else(|e| e.into_inner()).send(Message::Invalidate(paths::normalize(profile)));
    }

    /// Blocks until no pass is running or waiting, or the time runs out. For
    /// the suite and for a command that wants one answer and then to leave.
    pub fn wait_until_idle(&self, timeout: Duration) -> bool {
        let control = self.shared.control.lock().unwrap_or_else(|e| e.into_inner());
        let (control, result) = self
            .shared
            .idle
            .wait_timeout_while(control, timeout, |c| c.in_flight || c.waiting.is_some())
            .unwrap_or_else(|e| e.into_inner());
        drop(control);
        !result.timed_out()
    }

    /// For the suite of whatever draws the tray: drive the figure without a
    /// filesystem.
    #[doc(hidden)]
    pub fn set_entries_for_testing(&self, entries: Vec<Entry>) {
        self.shared.publish(|s| s.entries = entries);
    }
}

impl Drop for Monitor {
    fn drop(&mut self) {
        self.ticker.lock().unwrap_or_else(|e| e.into_inner()).take();
    }
}

impl Shared {
    fn publish(&self, change: impl FnOnce(&mut Snapshot)) {
        let snapshot = {
            let mut published = self.published.lock().unwrap_or_else(|e| e.into_inner());
            let before = published.clone();
            change(&mut published);
            if *published == before {
                return;
            }
            published.clone()
        };
        let subscribers: Vec<Subscriber> =
            self.subscribers.lock().unwrap_or_else(|e| e.into_inner()).iter().map(|(_, s)| s.clone()).collect();
        for subscriber in subscribers {
            subscriber(&snapshot);
        }
    }

    /// The request with every default filled in, read at the moment it is
    /// made, and the profiles it is about.
    fn job(&self, request: Refresh) -> Job {
        let may_prompt = self.may_prompt_unasked.load(Ordering::SeqCst);
        let asking = request.prompting.unwrap_or(if request.interactive {
            Prompting::Yes
        } else if may_prompt {
            Prompting::OnceIfShut
        } else {
            Prompting::No
        });
        // Opening the tray wants a current figure but must not skip the
        // back-off: a failing endpoint would then be asked again on every open.
        let wanted = request.freshness.unwrap_or(if request.interactive { Freshness::Now } else { Freshness::Cached });
        // A press about one account may ask the keyring for that account; the
        // others are read the way a tick would read them.
        let elsewhere = if may_prompt { Prompting::OnceIfShut } else { Prompting::No };
        Job {
            targets: (self.sources.targets)(),
            interactive: request.interactive,
            asking,
            elsewhere,
            wanted,
            focus: request.focus,
        }
    }

    fn refresh(&self, request: Refresh) {
        let job = self.job(request);
        if job.interactive {
            let focus = job.focus.clone();
            self.publish(|s| {
                s.is_refreshing = true;
                s.refreshing_profile = focus;
            });
        }

        let mut control = self.control.lock().unwrap_or_else(|e| e.into_inner());
        match arrival(control.in_flight, job.interactive, job.wanted) {
            Arrival::Drop => return,
            Arrival::Queue => {
                control.waiting =
                    Some(merge_waiting(control.waiting.take(), job.interactive, job.asking, job.wanted, job.focus));
                return;
            }
            Arrival::Start => {}
        }
        control.in_flight = true;
        drop(control);
        self.send(job);
    }

    fn send(&self, job: Job) {
        if self.sender.lock().unwrap_or_else(|e| e.into_inner()).send(Message::Pass(job)).is_err() {
            self.control.lock().unwrap_or_else(|e| e.into_inner()).in_flight = false;
            self.idle.notify_all();
        }
    }

    /// A pass is over: show what it found, then run whatever waited for it.
    /// The waiting request goes to the back of the thread's queue, behind any
    /// `invalidate` that arrived while the pass ran, and the monitor stays
    /// busy in between, so nothing slips in ahead of it.
    fn finished(&self, fresh: Vec<Entry>, problem: Option<String>, locked: bool) {
        let waiting = {
            let mut control = self.control.lock().unwrap_or_else(|e| e.into_inner());
            let waiting = control.waiting.take();
            control.in_flight = waiting.is_some();
            waiting
        };
        match waiting {
            Some(queued) => {
                let job = self.job(Refresh {
                    interactive: queued.interactive,
                    prompting: Some(queued.prompting),
                    freshness: Some(queued.freshness),
                    focus: queued.focus,
                });
                let interactive = job.interactive;
                let focus = job.focus.clone();
                self.publish(|s| {
                    s.entries = fresh;
                    s.live_problem = problem;
                    s.needs_keyring_access = locked;
                    if interactive {
                        s.is_refreshing = true;
                        s.refreshing_profile = focus;
                    }
                });
                self.send(job);
            }
            None => {
                self.publish(|s| {
                    s.entries = fresh;
                    s.live_problem = problem;
                    s.needs_keyring_access = locked;
                    s.is_refreshing = false;
                    s.refreshing_profile = None;
                });
                self.idle.notify_all();
            }
        }
    }
}

/// What the monitor's thread alone holds.
struct Live {
    session: Session,
    /// The API is polled far less often than the disk, and only per profile.
    cache: HashMap<PathBuf, (SystemTime, Reading)>,
    backoff: HashMap<PathBuf, (SystemTime, u32)>,
    /// What went wrong the last time a profile's figures were asked for, kept
    /// until an answer comes back so every pass in between can say so.
    last_failure: HashMap<PathBuf, String>,
    /// Profiles the service itself asked to be left alone for a while.
    server_asked: HashSet<PathBuf>,
}

/// What asking for one profile's live figures came to: the reading to show,
/// if there is one, when it was really taken, and, when this pass did not get
/// a current answer, why not.
#[derive(Default)]
struct Outcome {
    reading: Option<Reading>,
    fetched: Option<SystemTime>,
    failure: Option<String>,
}

fn worker(shared: std::sync::Weak<Shared>, receiver: Receiver<Message>) {
    let mut live = Live {
        session: Session::new(Duration::from_secs(10)),
        cache: HashMap::new(),
        backoff: HashMap::new(),
        last_failure: HashMap::new(),
        server_asked: HashSet::new(),
    };
    // The channel's sender lives in `Shared`, so it closes only when the
    // monitor is gone; a pass still queued then has nobody to report to.
    while let Ok(message) = receiver.recv() {
        let Some(shared) = shared.upgrade() else { break };
        match message {
            Message::Invalidate(profile) => {
                live.cache.remove(&profile);
            }
            Message::Pass(job) => {
                let listing = (shared.sources.processes)();
                let (fresh, problem, locked) = live.pass(&job, &listing);
                record(&fresh, locked, problem.as_deref());
                shared.finished(fresh, problem, locked);
            }
        }
    }
}

impl Live {
    fn pass(&mut self, job: &Job, listing: &[Process]) -> (Vec<Entry>, Option<String>, bool) {
        let mut problem = None;
        let mut locked = false;
        let mut fresh = Vec::with_capacity(job.targets.len());
        for target in &job.targets {
            let on_disk = history::usage_of(&target.profile);
            let running = claude::is_running_in(listing, &target.profile);
            let focused = job.focus.as_ref().map(|f| paths::same_path(f, &target.profile)).unwrap_or(true);
            let mut entry = Entry::new(&target.name, target.profile.clone(), on_disk.clone(), running);
            entry.shortcut = target.shortcut;
            entry.refreshed = on_disk.as_ref().map(|u| u.sampled);

            let outcome = self.live(
                &target.profile,
                job.interactive && focused,
                if focused { job.wanted } else { Freshness::Cached },
                running,
                prompting(job.asking, job.elsewhere, focused, locked),
                &mut problem,
                &mut locked,
            );
            let now = SystemTime::now();
            match (&outcome.reading, outcome.fetched) {
                (Some(reading), Some(fetched)) if !prefers_local(fetched, on_disk.as_ref().map(|u| u.sampled)) => {
                    entry.usage = Some(Usage {
                        five_hour: reading.five_hour,
                        week: reading.week,
                        organization: on_disk.as_ref().and_then(|u| u.organization.clone()),
                        sampled: fetched,
                        five_hour_reset: reading.five_hour_reset,
                        week_reset: reading.week_reset,
                        fable: reading.fable,
                        fable_reset: reading.fable_reset,
                    });
                    entry.is_live = true;
                    entry.plan = reading.plan.clone();
                    entry.refreshed = Some(fetched);
                    // One line, and the time the figure is from: a fallback
                    // read as a figure taken now is what this note is for.
                    entry.note = outcome.failure.map(|why| format!("{why}; figure from {}", clock(fetched, now)));
                }
                _ => {
                    entry.note = match &on_disk {
                        Some(disk) => outcome
                            .failure
                            .map(|why| format!("{why}; own record from {}", clock(disk.sampled, now))),
                        None => outcome.failure,
                    };
                }
            }
            fresh.push(entry);
        }
        (fresh, problem, locked)
    }

    /// Cached hard: one call per profile per interval, and never a dialog on a
    /// pass that may not raise one.
    #[allow(clippy::too_many_arguments)]
    fn live(
        &mut self,
        profile: &Path,
        interactive: bool,
        freshness: Freshness,
        running: bool,
        prompting: Prompting,
        problem: &mut Option<String>,
        locked: &mut bool,
    ) -> Outcome {
        let key = paths::normalize(profile);
        let now = SystemTime::now();
        let cached = self.cache.get(&key).cloned();
        if let Some((fetched, reading)) = &cached {
            let age = now.duration_since(*fetched).unwrap_or_default();
            if may_use_cache(age, freshness, running) {
                return Outcome { reading: Some(reading.clone()), fetched: Some(*fetched), failure: None };
            }
        }
        let held = || Outcome {
            reading: cached.as_ref().map(|c| c.1.clone()),
            fetched: cached.as_ref().map(|c| c.0),
            failure: None,
        };

        let until = self.backoff.get(&key).map(|b| b.0);
        if !may_attempt(now, until, interactive, self.server_asked.contains(&key)) {
            if interactive {
                *problem = Some(waiting_message(until, false));
            }
            let why: Vec<String> =
                [self.last_failure.get(&key).cloned(), Some(waiting_message(until, true))].into_iter().flatten().collect();
            return Outcome { failure: Some(why.join("; ")), ..held() };
        }

        let result = match graft_credentials::token(profile, prompting, &[USAGE_SCOPE]) {
            Ok(None) => return Outcome { failure: Some("No saved login to ask Anthropic with".into()), ..Outcome::default() },
            Ok(Some(token)) => {
                // The key is the process's from here on, so nothing after this
                // in the pass is shut out.
                *locked = false;
                self.session.fetch(&token.value).map_err(LiveError::Api)
            }
            Err(failure) => Err(LiveError::Credentials(failure)),
        };

        match result {
            Ok(reading) => {
                let fetched = SystemTime::now();
                self.cache.insert(key.clone(), (fetched, reading.clone()));
                self.backoff.remove(&key);
                self.last_failure.remove(&key);
                self.server_asked.remove(&key);
                Outcome { reading: Some(reading), fetched: Some(fetched), failure: None }
            }
            Err(error) => {
                // Falling back to the on-disk figures is the normal outcome
                // here, so only an explicit request raises it to the tray's
                // problem line. The row itself always says, quietly. The
                // keyring being locked is worth surfacing either way, since it
                // is one unlock to fix and everything live depends on it.
                let why = failure_line(&error).to_string();
                self.last_failure.insert(key.clone(), why.clone());
                let keyring = is_keyring_failure(&error);
                // After a refusal the profiles after it are read silently and
                // fail the same way; repeating the unlock advice would bury
                // the answer the person just gave.
                if interactive && !(keyring && *locked) {
                    *problem = Some(error.message());
                }
                if keyring {
                    *locked = true;
                }

                // A login that cannot be read here is put right on this
                // computer, and the next pass is the first to see it put
                // right. Waiting longer between tries only kept the account
                // stale for half an hour after.
                if !came_from_service(&error) {
                    return Outcome { failure: Some(why), ..held() };
                }

                let retry_after = wait_asked(&error);
                if retry_after.is_some() {
                    self.server_asked.insert(key.clone());
                } else {
                    // The wait that follows is Graft's own, which a press may
                    // skip. Kept from an earlier Retry-After, it held a press
                    // off until an ordinary back-off ran out.
                    self.server_asked.remove(&key);
                }
                let failures = self.backoff.get(&key).map(|b| b.1).unwrap_or(0) + 1;
                let delay = retry_delay(failures, retry_after);
                self.backoff.insert(key, (SystemTime::now() + delay, failures));
                Outcome { failure: Some(why), ..held() }
            }
        }
    }
}

fn waiting_message(until: Option<SystemTime>, in_line: bool) -> String {
    match until.and_then(|u| countdown(u, SystemTime::now())) {
        Some(remaining) if in_line => format!("next try in {remaining}"),
        Some(remaining) => format!("Next try in {remaining}"),
        None if in_line => "next try soon".into(),
        None => "Next try soon".into(),
    }
}

fn stamp(time: SystemTime) -> String {
    let secs = time.duration_since(SystemTime::UNIX_EPOCH).map(|d| d.as_secs() as i64).unwrap_or(0);
    let format = time::macros::format_description!("[year]-[month]-[day]T[hour]:[minute]:[second]Z");
    time::OffsetDateTime::from_unix_timestamp(secs).ok().and_then(|t| t.format(&format).ok()).unwrap_or_default()
}

fn record(entries: &[Entry], locked: bool, problem: Option<&str>) {
    let mut report = Map::new();
    report.insert("checkedAt".into(), json!(stamp(SystemTime::now())));
    report.insert("keyringLocked".into(), json!(locked));
    if let Some(problem) = problem {
        report.insert("problem".into(), json!(problem));
    }
    let rows: Vec<Value> = entries
        .iter()
        .map(|entry| {
            let mut row = Map::new();
            row.insert("name".into(), json!(entry.name));
            row.insert("live".into(), json!(entry.is_live));
            if let Some(refreshed) = entry.refreshed {
                row.insert("refreshedAt".into(), json!(stamp(refreshed)));
            }
            if let Some(note) = &entry.note {
                row.insert("note".into(), json!(note));
            }
            if let Some(usage) = &entry.usage {
                match usage.five_hour {
                    Some(five_hour) => row.insert("fiveHour".into(), json!(five_hour)),
                    None => row.insert("noFiveHourLimit".into(), json!(true)),
                };
                row.insert("week".into(), json!(usage.week));
                if let Some(reset) = usage.five_hour_reset {
                    row.insert("fiveHourReset".into(), json!(stamp(reset)));
                }
                if let Some(reset) = usage.week_reset {
                    row.insert("weekReset".into(), json!(stamp(reset)));
                }
                if let Some(fable) = usage.fable {
                    row.insert("fable".into(), json!(fable));
                }
                if let Some(reset) = usage.fable_reset {
                    row.insert("fableReset".into(), json!(stamp(reset)));
                }
            }
            if let Some(plan) = &entry.plan {
                row.insert("plan".into(), json!(plan));
            }
            Value::Object(row)
        })
        .collect();
    report.insert("profiles".into(), Value::Array(rows));
    let _ = files::write_json(&status_file(), &Value::Object(report));
}

#[cfg(test)]
mod tests;
