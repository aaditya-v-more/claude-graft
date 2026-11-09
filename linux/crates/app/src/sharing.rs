//! Driving shared usage from the window: the passes that keep the relay and
//! every Claude's Code environment in step, the looks between them that say
//! which conversation is on screen, the watch on the record stores that wakes
//! a look as soon as Claude writes, and the pane sign-in.
//!
//! The controller lives on the main loop and never waits; a pass and a look
//! run on a worker with the one `PassMemory`, and their findings come back to
//! the controller here.

use crate::shared::{emit, shared, SignInPhase, Topic};
use crate::ui::off_main;
use graft_core::{diagnostics, files};
use graft_credentials::Prompting;
use graft_pool::controller::{self, PASS_INTERVAL, PASS_INTERVAL_WATCHED, PEEK_INTERVAL};
use graft_pool::sign_in::{Failure, SignIn};
use graft_pool::PoolLimits;
use graft_usage::Snapshot;
use gtk::gio;
use gtk::glib;
use adw::prelude::*;
use std::cell::{Cell, RefCell};
use std::path::{Path, PathBuf};
use std::time::{Duration, UNIX_EPOCH};

thread_local! {
    static PASSING: Cell<bool> = const { Cell::new(false) };
    static PASS_AGAIN: Cell<bool> = const { Cell::new(false) };
    static PEEKING: Cell<bool> = const { Cell::new(false) };
    static PEEK_AGAIN: Cell<bool> = const { Cell::new(false) };
    static PASS_TIMER: RefCell<Option<glib::SourceId>> = const { RefCell::new(None) };
    static WATCH: RefCell<StoreWatch> = RefCell::new(StoreWatch::default());
}

fn seconds(time: std::time::SystemTime) -> f64 {
    time.duration_since(UNIX_EPOCH).map(|d| d.as_secs_f64()).unwrap_or(0.0)
}

/// What the usage endpoint last read for a profile, for the one detail view
/// of each account: an account the relay has sent nothing to yet is then not
/// a blank.
pub fn limits_for(snapshot: &Snapshot, profile: &Path) -> Option<PoolLimits> {
    let entry = snapshot.entry_for(profile)?;
    let usage = entry.current_usage()?;
    Some(PoolLimits::from_percent(
        usage.five_hour,
        usage.five_hour_reset.map(seconds),
        usage.week,
        usage.week_reset.map(seconds),
        seconds(usage.sampled),
    ))
}

/// Starts the passes and the looks between them. Before anything that might
/// wait on the network: a Claude connected to the pool after a restart has
/// no Claude Code until the relay is back.
pub fn start() {
    schedule();
    pass();
    glib::timeout_add_local(PEEK_INTERVAL, || {
        follow_stores();
        peek();
        glib::ControlFlow::Continue
    });
}

/// Half a minute keeps logins current, which is all the relay needs; with
/// the tray's window open somebody is watching where conversations go, which
/// is worth a pass every few seconds.
pub fn schedule() {
    let every = if shared().watchers.get() > 0 { PASS_INTERVAL_WATCHED } else { PASS_INTERVAL };
    PASS_TIMER.with(|t| {
        if let Some(old) = t.borrow_mut().take() {
            old.remove();
        }
        let id = glib::timeout_add_local(every, || {
            pass();
            glib::ControlFlow::Continue
        });
        *t.borrow_mut() = Some(id);
    });
}

pub fn watch() {
    let shared = shared();
    shared.watchers.set(shared.watchers.get() + 1);
    schedule();
    pass();
}

/// Paired with `watch` where the tray's window closes, never in a view that
/// might go without saying so: a watch never let go keeps the pool on its
/// fast timer for good.
pub fn unwatch() {
    let shared = shared();
    shared.watchers.set(shared.watchers.get().saturating_sub(1));
    schedule();
}

/// One pass. A pass asked for while one runs is run once that one is done,
/// since what it was asked for may be what the running one missed.
pub fn pass() {
    if PASSING.with(Cell::get) {
        PASS_AGAIN.with(|a| a.set(true));
        return;
    }
    PASSING.with(|p| p.set(true));
    let shared = shared();
    let snapshot = shared.snapshot.borrow().clone();
    let input = shared.pool.borrow_mut().prepare_pass(|profile| limits_for(&snapshot, profile));
    let memory = shared.memory.clone();
    off_main(
        move || {
            let mut memory = memory.lock().unwrap_or_else(|e| e.into_inner());
            controller::run_pass(&input, &mut memory)
        },
        |output| {
            let changed = crate::shared::shared().pool.borrow_mut().apply(output);
            PASSING.with(|p| p.set(false));
            if changed {
                emit(Topic::Pool);
            }
            follow_stores();
            if PASS_AGAIN.with(|a| a.replace(false)) {
                pass();
            }
        },
    );
}

/// What the relay carried since the last pass and the records whose files
/// changed. Only while something is lent and the relay has answered once.
pub fn peek() {
    let shared = shared();
    if !shared.pool.borrow().should_peek() {
        return;
    }
    if PASSING.with(Cell::get) || PEEKING.with(Cell::get) {
        PEEK_AGAIN.with(|a| a.set(true));
        return;
    }
    PEEKING.with(|p| p.set(true));
    let profiles = shared.pool.borrow().claude_profiles();
    let memory = shared.memory.clone();
    off_main(
        move || {
            let mut memory = memory.lock().unwrap_or_else(|e| e.into_inner());
            controller::peek(&profiles, &mut memory)
        },
        |found| {
            PEEKING.with(|p| p.set(false));
            if let Some((status, records)) = found {
                if !PASSING.with(Cell::get) && crate::shared::shared().pool.borrow_mut().apply_peek(status, records) {
                    emit(Topic::Pool);
                }
            }
            if PEEK_AGAIN.with(|a| a.replace(false)) {
                peek();
            }
        },
    );
}

/// A change to the lending, saved, then sent to the relay at once.
pub fn change(edit: impl FnOnce(&mut graft_pool::controller::Controller)) {
    edit(&mut shared().pool.borrow_mut());
    emit(Topic::Pool);
    pass();
}

/// The figures moved. A Claude seen running before and not now has quit,
/// which is the moment its lines can be settled, Claude's own above all,
/// since it has no launcher to do it on the way back in.
pub fn usage_changed() {
    let shared = shared();
    let running: Vec<PathBuf> =
        shared.snapshot.borrow().entries.iter().filter(|e| e.is_running).map(|e| e.profile.clone()).collect();
    let quit = {
        let before = shared.running_before.borrow();
        before.iter().any(|p| !running.contains(p))
    };
    *shared.running_before.borrow_mut() = running;
    if quit {
        pass();
    }
}

/// The dropdown's button while lending is paused. Pressing it is a person
/// asking, the one thing that may put the keyring's dialog up; a pass of its
/// own only ever reads silently.
pub fn open_keyring() {
    off_main(
        || controller::open_keychain(true),
        |_| {
            pass();
            shared().monitor.refresh(graft_usage::Refresh {
                prompting: Some(Prompting::No),
                freshness: Some(graft_usage::Freshness::Recent),
                ..Default::default()
            });
        },
    );
}

/// Who may raise the keyring's dialog while a sign-in seals its login: the
/// person who pressed Sign In, and nobody else.
pub fn prompting_for(pressed: bool) -> Prompting {
    if pressed {
        Prompting::Yes
    } else {
        Prompting::No
    }
}

/// A sign-in for `claude` in the browser. The listener is up before the
/// browser is sent anywhere; the wait for its answer is a worker's.
pub fn sign_in(claude: &str, parent: Option<&gtk::Widget>) {
    let shared = shared();
    if matches!(*shared.sign_in.borrow(), SignInPhase::Waiting(_)) {
        return;
    }
    let session = match SignIn::begin() {
        Ok(session) => session,
        Err(failure) => {
            *shared.sign_in.borrow_mut() = SignInPhase::Failed(claude.to_string(), failure.message());
            emit(Topic::SignIn);
            return;
        }
    };
    *shared.sign_in_cancel.borrow_mut() = Some(session.canceller());
    *shared.sign_in.borrow_mut() = SignInPhase::Waiting(claude.to_string());
    emit(Topic::SignIn);
    crate::ui::open_url(&session.url, parent);
    let own = shared.pool.borrow().account_uuid_of(claude);
    let prompting = prompting_for(true);
    let for_claude = claude.to_string();
    off_main(
        move || session.wait(own.as_deref(), prompting),
        move |result| {
            let shared = crate::shared::shared();
            if *shared.sign_in.borrow() != SignInPhase::Waiting(for_claude.clone()) {
                return;
            }
            let phase = match result {
                Ok(account) => {
                    change(|pool| pool.add_account(account, &for_claude));
                    SignInPhase::Idle
                }
                Err(Failure::Cancelled) => SignInPhase::Idle,
                Err(failure) => SignInPhase::Failed(for_claude.clone(), failure.message()),
            };
            *shared.sign_in.borrow_mut() = phase;
            *shared.sign_in_cancel.borrow_mut() = None;
            emit(Topic::SignIn);
        },
    );
}

pub fn cancel_sign_in() {
    let shared = shared();
    if let Some(cancel) = shared.sign_in_cancel.borrow_mut().take() {
        cancel.store(true, std::sync::atomic::Ordering::SeqCst);
    }
    *shared.sign_in.borrow_mut() = SignInPhase::Idle;
    emit(Topic::SignIn);
}

pub fn dismiss_sign_in_failure() {
    let shared = shared();
    if matches!(*shared.sign_in.borrow(), SignInPhase::Failed(..)) {
        *shared.sign_in.borrow_mut() = SignInPhase::Idle;
        emit(Topic::SignIn);
    }
}

/// Says when a record store changes, a tenth of a second after it does, so
/// a write is read when it lands rather than on the next look. inotify is not
/// recursive, so every organization folder is watched, and the store and each
/// account folder too, so a new organization is found.
#[derive(Default)]
struct StoreWatch {
    watched: Vec<PathBuf>,
    monitors: Vec<gio::FileMonitor>,
    pending: Option<glib::SourceId>,
}

/// The folders to watch under each store.
pub fn folders_under(stores: &[PathBuf]) -> Vec<PathBuf> {
    let mut out = Vec::new();
    for store in stores {
        out.push(store.clone());
        for account in files::listing(store).unwrap_or_default() {
            if account.ends_with(graft_chats::STASH_SUFFIX) || graft_chats::NON_ACCOUNT_STORE_ITEMS.contains(&account.as_str()) {
                continue;
            }
            let account_dir = store.join(&account);
            if !account_dir.is_dir() {
                continue;
            }
            out.push(account_dir.clone());
            for org in files::listing(&account_dir).unwrap_or_default() {
                let org_dir = account_dir.join(&org);
                if !org.ends_with(graft_chats::STASH_SUFFIX) && org_dir.is_dir() {
                    out.push(org_dir);
                }
            }
        }
    }
    out.sort();
    out.dedup();
    out
}

/// Asked again on every look's tick and after every pass, so a Claude added
/// or a store made since is watched from the next, and here rather than in
/// the look the watch itself wakes, which must not tear the watch down.
pub fn follow_stores() {
    let stores = shared().pool.borrow().watched_stores();
    let wanted = folders_under(&stores);
    WATCH.with(|w| {
        let mut watch = w.borrow_mut();
        if watch.watched == wanted {
            return;
        }
        watch.monitors.clear();
        for folder in &wanted {
            let file = gio::File::for_path(folder);
            match file.monitor_directory(gio::FileMonitorFlags::NONE, None::<&gio::Cancellable>) {
                Ok(monitor) => {
                    monitor.connect_changed(|_, _, _, _| woken());
                    watch.monitors.push(monitor);
                }
                Err(e) => diagnostics::note("store-watch.failed", &[("reason", e.to_string().into())]),
            }
        }
        watch.watched = wanted;
    });
}

fn woken() {
    WATCH.with(|w| {
        let mut watch = w.borrow_mut();
        if watch.pending.is_some() {
            return;
        }
        watch.pending = Some(glib::timeout_add_local_once(Duration::from_millis(100), || {
            WATCH.with(|w| w.borrow_mut().pending = None);
            peek();
        }));
    });
}

#[cfg(test)]
mod tests {
    use super::*;
    use graft_core::testing::Sandbox;

    #[test]
    fn every_organization_folder_of_a_store_is_watched_and_no_stash() {
        let sandbox = Sandbox::new();
        let store = sandbox.config().join("Claude/claude-code-sessions");
        std::fs::create_dir_all(store.join("acct/org")).unwrap();
        std::fs::create_dir_all(store.join("acct/old.graft-own")).unwrap();
        std::fs::create_dir_all(store.join("skills-plugin/org")).unwrap();
        let folders = folders_under(&[store.clone()]);
        assert!(folders.contains(&store.join("acct/org")), "an organization folder is watched, since inotify is not recursive");
        assert!(folders.contains(&store) && folders.contains(&store.join("acct")), "and the folders above it, so a new one is found");
        assert!(!folders.iter().any(|f| f.to_string_lossy().contains("graft-own")), "a stash is never read as a store");
        assert!(!folders.iter().any(|f| f.to_string_lossy().contains("skills-plugin")), "and skills-plugin is not an account");
    }

    #[test]
    fn only_a_press_may_raise_the_keyring_for_a_sign_in() {
        assert_eq!(prompting_for(true), Prompting::Yes);
        assert_eq!(prompting_for(false), Prompting::No);
    }
}
