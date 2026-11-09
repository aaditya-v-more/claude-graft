//! Starting, showing, hiding and quitting, and keeping the tray item saying
//! what the window knows.
//!
//! Closing the window is not quitting: the tray item carries on. Ctrl+Q puts
//! the window away as closing it does, and only Quit in the tray's window,
//! or the session ending, ends Graft, unless nothing in the tray can be
//! reached, when a quit is a quit.

use crate::front::{self, Meaning};
use crate::policy::{QuitPolicy, WindowPolicy};
use crate::settings::APP_ID;
use crate::shared::{self, emit, listen_forever, shared, Shared, Topic};
use crate::sharing;
use crate::title::{self, LookInput, TrayChat};
use crate::tray::Tray;
use crate::ui::{self, off_main, on_main};
use graft_core::{claude, diagnostics, paths, IconPreset};
use graft_pool::chats;
use graft_pool::controller::PEEK_INTERVAL;
use graft_profiles::installer;
use graft_usage::TICK;
use gtk::gio;
use gtk::glib;
use adw::prelude::*;
use std::cell::{Cell, RefCell};
use std::sync::{Arc, Mutex};
use std::time::Duration;

thread_local! {
    static HOLD: RefCell<Option<gio::ApplicationHoldGuard>> = const { RefCell::new(None) };
    static TRAY: RefCell<Option<Tray>> = const { RefCell::new(None) };
    /// Set when the window was put away last time Graft stopped, so this
    /// start leaves it closed; everything after starts behaves normally.
    static START_HIDDEN: Cell<bool> = const { Cell::new(false) };
    static LAST_CHAT: RefCell<Option<String>> = const { RefCell::new(None) };
    static RECORDS: Arc<Mutex<chats::RecordWatch>> = Arc::new(Mutex::new(chats::RecordWatch::new()));
}

pub fn run() -> glib::ExitCode {
    let app = adw::Application::builder().application_id(APP_ID).build();
    app.connect_startup(startup);
    app.connect_activate(|_| {
        if START_HIDDEN.with(|h| h.replace(false)) {
            return;
        }
        show_main_window();
    });
    // The arguments are Graft's own and were read before this; GApplication
    // would take them for files to open.
    app.run_with_args(&["graft"])
}

fn startup(app: &adw::Application) {
    diagnostics::set_who("app");
    // Connects the areas that cannot depend on each other, so a pair the
    // mirror drops is dropped from the sidebar sync in the same pass.
    graft_launch::wire();
    ui::ensure_style();
    let shared = Shared::install(app);
    HOLD.with(|h| *h.borrow_mut() = Some(app.hold()));

    let settings = shared.settings.borrow().clone();
    let hidden = !settings.main_window_open && settings.show_in_tray;
    START_HIDDEN.with(|h| h.set(hidden));
    // A login start puts nothing on screen, so there is nobody to read a
    // keyring dialog; the first pass stays quiet and the prompt waits for the
    // window or the tray's window.
    shared.monitor.set_may_prompt_unasked(!hidden);
    shared.monitor.subscribe(|snapshot| {
        let snapshot = snapshot.clone();
        on_main(move || {
            *shared::shared().snapshot.borrow_mut() = snapshot;
            emit(Topic::Usage);
            sharing::usage_changed();
        });
    });
    shared.monitor.start(TICK);
    start_front_watch();
    // Before anything that might wait on the network: a Claude connected to
    // the pool after a restart has no Claude Code until the relay is back.
    sharing::start();
    // Filing records is a thing the tray does on its own whether anyone is
    // looking or not. Once, here; the pass that matters runs in a shortcut's
    // launcher, on its way to opening the Claude that will read what it files.
    shared::sweep();
    // A shortcut goes on behaving like the Graft that built it until its
    // entry is rewritten, and the update that replaced this app touched none
    // of them. Off the main loop, with the sources resolved here, where the
    // list lives.
    let with_sources = shared.store.borrow().with_sources();
    off_main(move || installer::refresh_launchers(&with_sources), |_| {});
    off_main(
        || (claude::launcher().is_some(), claude::version()),
        |(found, version)| {
            *shared::shared().claude.borrow_mut() = shared::ClaudeInstall { found: Some(found), version };
            emit(Topic::Claude);
        },
    );
    crate::icons::load_previews();
    crate::reach::watch(shared.reach.clone(), || on_main(reach_changed));
    apply_tray_setting();
    listen_forever(&[Topic::Settings], apply_tray_setting);
    listen_forever(&[Topic::Usage, Topic::Front, Topic::Pool, Topic::Settings, Topic::Store, Topic::Reach], refresh_tray);

    let quit_shortcut = gio::SimpleAction::new("quit-shortcut", None);
    quit_shortcut.connect_activate(|_, _| ctrl_q());
    app.add_action(&quit_shortcut);
    app.set_accels_for_action("app.quit-shortcut", &["<Control>q"]);

    // The session ending needs nothing here: it ends Graft with a signal
    // whose default is to stop, and nothing in Graft refuses it, so the
    // clause the Mac needs for a logout has nothing to let through.

    if hidden {
        // The item has to be registered and taken by a host before anybody
        // can reach it, so a hidden start looks again once it has had time.
        glib::timeout_add_local_once(WindowPolicy::PLACEMENT_GRACE, || {
            let shared = shared::shared();
            if WindowPolicy::shows_window_after_hidden_launch(shared.reach.is_showing(), has_visible_window()) {
                diagnostics::note("window.shown", &[("reason", "tray item out of reach after a hidden start".into())]);
                show_main_window();
            }
        });
    }
}

fn has_visible_window() -> bool {
    crate::window::existing().map(|w| w.is_visible()).unwrap_or(false)
}

pub fn show_main_window() {
    let shared = shared();
    shared.monitor.set_may_prompt_unasked(true);
    let window = crate::window::build(&shared.app);
    shared.has_shown_window.set(true);
    window.present();
    if !shared.settings.borrow().main_window_open {
        shared::change_settings(|s| s.main_window_open = true);
    }
}

/// Only when the window is actually closed by somebody. Out of reach, the
/// window is left recorded as open, so the next start brings one up rather
/// than starting hidden behind nothing.
pub fn close_requested(window: &adw::ApplicationWindow) -> glib::Propagation {
    let shared = shared();
    if shared.terminating.get() {
        return glib::Propagation::Proceed;
    }
    let show_in_tray = shared.settings.borrow().show_in_tray;
    if WindowPolicy::hides_window(show_in_tray, shared.has_shown_window.get(), shared.reach.is_showing()) {
        window.set_visible(false);
        shared::change_settings(|s| s.main_window_open = false);
        return glib::Propagation::Stop;
    }
    quit_now();
    glib::Propagation::Proceed
}

/// What Ctrl+Q does instead of quitting while the item can be reached: the
/// same state closing the window by hand leaves behind.
fn ctrl_q() {
    let shared = shared();
    if QuitPolicy::ends_the_app(false, false, shared.reach.is_showing()) {
        quit_now();
        return;
    }
    if let Some(window) = crate::window::existing() {
        window.set_visible(false);
    }
    shared::change_settings(|s| s.main_window_open = false);
}

/// Quit in the tray's window and its menu, and nothing else.
pub fn quit() {
    if QuitPolicy::ends_the_app(true, false, shared().reach.is_showing()) {
        quit_now();
    }
}

fn quit_now() {
    let shared = shared();
    if shared.terminating.replace(true) {
        return;
    }
    if let Some(tray) = TRAY.with(|t| t.borrow_mut().take()) {
        tray.stop();
    }
    HOLD.with(|h| h.borrow_mut().take());
    shared.app.quit();
}

pub fn reach_changed() {
    if shared::is_ready() {
        emit(Topic::Reach);
    }
}

/// Without the item and without a window there would be nothing left to
/// click, not even a way to quit, so turning the item off brings the window.
fn apply_tray_setting() {
    let shared = shared();
    let wanted = shared.settings.borrow().show_in_tray;
    let running = TRAY.with(|t| t.borrow().is_some());
    if wanted && !running {
        TRAY.with(|t| *t.borrow_mut() = Some(Tray::start(shared.reach.clone())));
        refresh_tray();
    } else if !wanted && running {
        if let Some(tray) = TRAY.with(|t| t.borrow_mut().take()) {
            tray.stop();
        }
        if !has_visible_window() {
            show_main_window();
        }
    }
}

/// The tray's look, worked out again from what the window knows.
fn refresh_tray() {
    if !TRAY.with(|t| t.borrow().is_some()) {
        return;
    }
    let look = current_look();
    TRAY.with(|t| {
        if let Some(tray) = t.borrow_mut().as_mut() {
            tray.show(look);
        }
    });
}

fn current_look() -> title::TrayLook {
    let shared = shared();
    let settings = shared.settings.borrow().clone();
    let snapshot = shared.snapshot.borrow().clone();
    let front = shared.front.borrow().profile.clone();
    let wanted = settings.show_front_icon || settings.show_front_name;
    let named = snapshot.status_entry(if wanted { front.as_deref() } else { None }).and_then(|(e, named)| named.then_some(e));
    let tightest = snapshot.status_entry(None).map(|(e, _)| e);
    let store = shared.store.borrow();
    let front_preset = named
        .as_ref()
        .and_then(|e| e.shortcut)
        .and_then(|id| store.shortcut(id))
        .map(|s| s.icon_preset)
        .unwrap_or(IconPreset::Original);
    let pool = shared.pool.borrow();
    let now = graft_pool::now();
    let chat = front.as_deref().and_then(|f| pool.current(Some(f), now));
    let answering = |chat: &chats::Chat| -> Option<String> {
        chats::answering_key(chat).map(|k| pool.name_of(&k)).or_else(|| chat.carried.then(|| "Automatic".to_string()))
    };
    let ticker = chat.as_ref().map(|c| {
        let account = answering(c);
        (title::ticker_text(c.title.as_deref(), account.as_deref()), title::ticker_spoken(c.title.as_deref(), account.as_deref()))
    });
    // Which conversation the tray names, by the start of its id and never
    // its title, so a run can be checked against what was on screen.
    let chat_id = chat.as_ref().map(|c| c.id.clone());
    if LAST_CHAT.with(|l| *l.borrow() != chat_id) {
        LAST_CHAT.with(|l| *l.borrow_mut() = chat_id.clone());
        diagnostics::note(
            "menu.chat",
            &[
                ("session", chat_id.as_deref().map(|s| s.chars().take(8).collect::<String>()).unwrap_or_else(|| "none".into()).into()),
                ("chosen", chat.as_ref().map(|c| c.assigned.is_some()).unwrap_or(false).into()),
                ("carried", chat.as_ref().map(|c| c.carried).unwrap_or(true).into()),
            ],
        );
    }
    // The accounts the Claude in front draws on, and the one answering the
    // conversation on screen, matched by account, since the relay may name an
    // account by another of its names than the list does.
    let drawing = named.as_ref().and_then(|_| {
        let key = front.as_deref().and_then(|f| pool.claude_key_for(f))?;
        let members = pool.members_of(&key);
        if members.len() < 2 {
            return None;
        }
        let answering_key = chat.as_ref().filter(|c| c.claude == key).and_then(chats::answering_key);
        let lit = answering_key.and_then(|k| {
            members.iter().position(|m| *m == k || (pool.account_uuid_of(m).is_some() && pool.account_uuid_of(m) == pool.account_uuid_of(&k)))
        });
        let words = match lit {
            Some(i) => format!("Answering now: {}", pool.name_of(&members[i])),
            None => format!("Drawing on {}", members.iter().map(|m| pool.name_of(m)).collect::<Vec<_>>().join(", ")),
        };
        Some((words, lit.filter(|i| *i > 0).map(|i| pool.preset_of(&members[i]))))
    });
    let tray_chat = chat.as_ref().map(|c| {
        let mut choices = vec![(None, pool.automatic_title(c))];
        choices.extend(c.choices.iter().map(|k| (Some(k.clone()), pool.name_of(k))));
        let selected = c.assigned.as_ref().and_then(|a| c.choices.iter().position(|k| k == a)).map(|i| i + 1).unwrap_or(0);
        TrayChat { id: c.id.clone(), heading: c.title.clone().unwrap_or_else(|| "New chat".into()), choices, selected }
    });
    title::look(LookInput {
        show_front_icon: settings.show_front_icon,
        show_front_name: settings.show_front_name,
        named,
        tightest,
        entries: &snapshot.entries,
        front_preset,
        ticker,
        drawing,
        chat: tray_chat,
    })
}

/// Follows the window in front through the X server where there is one, and
/// through the records where there is not.
fn start_front_watch() {
    let x11 = front::watch_x11(|window, switched| on_main(move || window_arrived(window, switched)));
    shared().x11_front.set(x11);
    if !x11 {
        glib::timeout_add_local(PEEK_INTERVAL, || {
            follow_records_front();
            glib::ControlFlow::Continue
        });
    }
}

/// A moment's wait first: focus moving to Graft's own window is reported by
/// the X server before GTK has marked that window active.
fn window_arrived(window: Option<front::ActiveWindow>, switched: bool) {
    glib::timeout_add_local_once(Duration::from_millis(150), move || {
        let shared = shared();
        let graft_in_front = shared.app.windows().iter().any(|w| w.is_active());
        let meaning = front::meaning(window.as_ref(), graft_in_front, APP_ID);
        let (ticket, cleared) = shared.front.borrow_mut().activated(meaning);
        if cleared {
            emit(Topic::Front);
        }
        let (Some(ticket), Meaning::Claude(pid)) = (ticket, meaning) else { return };
        let profiles = shared::openable_profiles();
        off_main(
            move || {
                claude::processes()
                    .into_iter()
                    .find(|p| p.pid as u32 == pid)
                    .and_then(|p| graft_usage::monitor::profile_launched_by(&p.command, &profiles))
            },
            move |found| {
                let shared = shared::shared();
                let landing = shared.front.borrow_mut().resolved(ticket, found, switched);
                if let Some(profile) = landing.came_forward {
                    shared.monitor.claude_came_forward(&profile);
                }
                if landing.changed {
                    emit(Topic::Front);
                }
            },
        );
    });
}

/// Where no window manager can say which window is in front: the Claude
/// whose own records were focused last, among those running. Only while the
/// tray shows the Claude in front, since this is a walk over every record.
fn follow_records_front() {
    let shared = shared();
    let settings = shared.settings.borrow().clone();
    if !settings.show_in_tray || !(settings.show_front_icon || settings.show_front_name) {
        return;
    }
    let running: Vec<(String, std::path::PathBuf)> = shared
        .snapshot
        .borrow()
        .entries
        .iter()
        .filter(|e| e.is_running)
        .map(|e| (paths::text(&e.profile), e.profile.clone()))
        .collect();
    if running.is_empty() {
        let landing = shared.front.borrow_mut().followed_records(None);
        if landing.changed {
            emit(Topic::Front);
        }
        return;
    }
    let watch = RECORDS.with(|r| r.clone());
    off_main(
        move || {
            let records = watch.lock().unwrap_or_else(|e| e.into_inner()).look_up(&[], &running, graft_pool::now());
            records
                .focused
                .iter()
                .max_by(|a, b| a.1.at.partial_cmp(&b.1.at).unwrap_or(std::cmp::Ordering::Equal))
                .and_then(|(key, _)| running.iter().find(|(k, _)| k == key).map(|(_, p)| p.clone()))
        },
        |found| {
            let shared = shared::shared();
            let landing = shared.front.borrow_mut().followed_records(found);
            if let Some(profile) = landing.came_forward {
                shared.monitor.claude_came_forward(&profile);
            }
            if landing.changed {
                emit(Topic::Front);
            }
        },
    );
}
