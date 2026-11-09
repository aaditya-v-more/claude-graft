//! Long-lived objects, one of each, owned by the main loop.
//!
//! Views never own state of their own that another view shows: the list, the
//! figures, the Claude in front and the pool are kept here, and a view that
//! shows one of them listens for its topic and redraws. Nothing here waits:
//! whatever needs a process listing, a file walk or the keyring is handed to
//! `ui::off_main`, and its answer is put back here.

use crate::front::FrontmostClaude;
use crate::settings::Settings;
use adw::prelude::*;
use graft_chats::SessionRecords;
use graft_pool::controller::{Controller, PassMemory};
use graft_profiles::ShortcutStore;
use graft_usage::{Monitor, Snapshot};
use graft_web::{ArtifactCenter, ProjectCenter};
use std::cell::{Cell, OnceCell, RefCell};
use std::path::PathBuf;
use std::rc::Rc;
use std::sync::{Arc, Mutex};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Topic {
    /// The shortcut list.
    Store,
    /// The usage monitor's figures.
    Usage,
    Settings,
    /// The Claude in front.
    Front,
    /// Shared usage: lending, the relay's sessions, the records it names.
    Pool,
    /// What the last sweep recovered.
    Records,
    /// Whether Claude is installed, and its version.
    Claude,
    /// A pane sign-in under way or failed.
    SignIn,
    /// Whether the tray item can be reached.
    Reach,
}

/// A pane sign-in, by the Claude it is for.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub enum SignInPhase {
    #[default]
    Idle,
    Waiting(String),
    Failed(String, String),
}

/// What is known about the Claude installed on this machine, read once off
/// the main loop as Graft starts.
#[derive(Debug, Clone, Default)]
pub struct ClaudeInstall {
    /// `None` until the look has come back.
    pub found: Option<bool>,
    pub version: Option<String>,
}

type Listener = (u64, Vec<Topic>, Rc<dyn Fn()>);

pub struct Shared {
    pub app: adw::Application,
    pub store: RefCell<ShortcutStore>,
    pub settings: RefCell<Settings>,
    pub monitor: Arc<Monitor>,
    pub snapshot: RefCell<Snapshot>,
    pub records: Arc<SessionRecords>,
    /// Titles the last sweep filed, for the one line the tray's window says.
    pub recovered: RefCell<Vec<String>>,
    pub front: RefCell<FrontmostClaude>,
    pub pool: RefCell<Controller>,
    /// Only ever on a worker, one pass or look at a time.
    pub memory: Arc<Mutex<PassMemory>>,
    pub sign_in: RefCell<SignInPhase>,
    pub sign_in_cancel: RefCell<Option<Arc<std::sync::atomic::AtomicBool>>>,
    pub claude: RefCell<ClaudeInstall>,
    pub reach: Arc<crate::reach::Reach>,
    pub artifacts: Rc<ArtifactCenter>,
    pub projects: Rc<ProjectCenter>,
    /// Whether the window has been shown since Graft started, which is what
    /// makes closing it putting it away rather than a start that never showed.
    pub has_shown_window: Cell<bool>,
    /// Set once a quit has been let through, so windows closing on the way
    /// out are not recorded as put away, which would bring the next start up
    /// hidden.
    pub terminating: Cell<bool>,
    /// How many of the tray's windows are open, which puts the pool on its
    /// fast timer.
    pub watchers: Cell<u32>,
    pub x11_front: Cell<bool>,
    /// Profiles seen running on the last pass of the monitor, so one gone is
    /// noticed: nothing on Linux says a Claude quit.
    pub running_before: RefCell<Vec<PathBuf>>,
    listeners: RefCell<Vec<Listener>>,
    next_listener: Cell<u64>,
}

thread_local! {
    static SHARED: OnceCell<Rc<Shared>> = const { OnceCell::new() };
}

/// The one set of objects. Only the main thread has it.
pub fn shared() -> Rc<Shared> {
    SHARED.with(|s| s.get().cloned()).expect("Graft's objects are made when it starts")
}

pub fn is_ready() -> bool {
    SHARED.with(|s| s.get().is_some())
}

impl Shared {
    /// Made once GTK is up, since the web pages' centres need it.
    pub fn install(app: &adw::Application) -> Rc<Shared> {
        let artifacts = ArtifactCenter::new();
        let projects = ProjectCenter::new(artifacts.clone(), Arc::new(crate::transcripts::ChatTranscripts::default()));
        let store = ShortcutStore::load();
        let mut pool = Controller::load();
        pool.set_shortcuts(store.shortcuts().to_vec());
        let shared = Rc::new(Shared {
            app: app.clone(),
            store: RefCell::new(store),
            settings: RefCell::new(Settings::load()),
            monitor: Arc::new(Monitor::system()),
            snapshot: RefCell::new(Snapshot::default()),
            records: Arc::new(SessionRecords::new()),
            recovered: RefCell::new(Vec::new()),
            front: RefCell::new(FrontmostClaude::default()),
            pool: RefCell::new(pool),
            memory: Arc::new(Mutex::new(PassMemory::new())),
            sign_in: RefCell::new(SignInPhase::Idle),
            sign_in_cancel: RefCell::new(None),
            claude: RefCell::new(ClaudeInstall::default()),
            reach: Arc::new(crate::reach::Reach::default()),
            artifacts,
            projects,
            has_shown_window: Cell::new(false),
            terminating: Cell::new(false),
            watchers: Cell::new(0),
            x11_front: Cell::new(false),
            running_before: RefCell::new(Vec::new()),
            listeners: RefCell::new(Vec::new()),
            next_listener: Cell::new(1),
        });
        SHARED.with(|s| {
            let _ = s.set(shared.clone());
        });
        shared
    }
}

/// Calls `f` whenever one of `topics` changes, for as long as `widget` lives.
pub fn listen(widget: &impl IsA<gtk::Widget>, topics: &[Topic], f: impl Fn() + 'static) {
    let id = listen_forever(topics, f);
    widget.connect_destroy(move |_| unlisten(id));
}

pub fn listen_forever(topics: &[Topic], f: impl Fn() + 'static) -> u64 {
    let shared = shared();
    let id = shared.next_listener.get();
    shared.next_listener.set(id + 1);
    shared.listeners.borrow_mut().push((id, topics.to_vec(), Rc::new(f)));
    id
}

pub fn unlisten(id: u64) {
    if is_ready() {
        shared().listeners.borrow_mut().retain(|(i, _, _)| *i != id);
    }
}

/// Never called while a borrow of what changed is held: every listener reads
/// it again.
pub fn emit(topic: Topic) {
    let calls: Vec<Rc<dyn Fn()>> = shared()
        .listeners
        .borrow()
        .iter()
        .filter(|(_, topics, _)| topics.contains(&topic))
        .map(|(_, _, f)| f.clone())
        .collect();
    for call in calls {
        call();
    }
}

/// Change the shortcut list and say so, keeping the pool's copy of it in step.
pub fn change_store<R>(change: impl FnOnce(&mut ShortcutStore) -> R) -> R {
    let shared = shared();
    let result = change(&mut shared.store.borrow_mut());
    let list = shared.store.borrow().shortcuts().to_vec();
    shared.pool.borrow_mut().set_shortcuts(list);
    emit(Topic::Store);
    result
}

pub fn change_settings(change: impl FnOnce(&mut Settings)) {
    let shared = shared();
    {
        let mut settings = shared.settings.borrow_mut();
        change(&mut settings);
        settings.save();
    }
    emit(Topic::Settings);
}

/// Files the records a grafted profile never wrote. No timer: at start and
/// whenever the tray's window opens, the two times somebody is looking at
/// Graft itself; the pass that matters runs in a shortcut's launcher.
pub fn sweep() {
    let shared = shared();
    let records = shared.records.clone();
    let shortcuts = shared.store.borrow().shortcuts().to_vec();
    crate::ui::off_main(
        move || records.sweep(&shortcuts),
        |filed| {
            if let Some(filed) = filed {
                *crate::shared::shared().recovered.borrow_mut() = filed;
                emit(Topic::Records);
            }
        },
    );
}

/// Every profile that can be opened: Claude's own and each shortcut created.
pub fn openable_profiles() -> Vec<PathBuf> {
    let shared = shared();
    let store = shared.store.borrow();
    std::iter::once(graft_core::paths::main_profile())
        .chain(store.shortcuts().iter().filter(|s| s.installed_name.is_some()).map(|s| s.profile_dir()))
        .collect()
}
