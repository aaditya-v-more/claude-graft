//! What opens from the tray item: every account's usage windows, when each
//! resets, the conversations shared usage is carrying, and the few controls
//! worth having without opening the window.
//!
//! A tray item cannot host a popover, so this is a small window of its own,
//! opened on activation and closed when it loses the focus, as a popover
//! closes when it is clicked away from. Wayland lets nobody place a window,
//! so it opens where the compositor puts it.

use crate::chat_conflict;
use crate::links;
use crate::preferences::{self, Look};
use crate::shared::{self, listen, shared, Topic};
use crate::shared_usage;
use crate::sharing;
use crate::ui::{self, identified, off_main};
use crate::usage_view;
use graft_usage::{Entry, Refresh};
use adw::prelude::*;
use std::cell::RefCell;
use std::rc::Rc;
use std::time::Duration;

pub const TITLE: &str = "Claude Graft Menu";

thread_local! {
    static OPEN: RefCell<Option<gtk::Window>> = const { RefCell::new(None) };
}

/// The tray item's activation: open it, or close it when it is open.
pub fn toggle() {
    if let Some(window) = OPEN.with(|o| o.borrow().clone()) {
        window.close();
        return;
    }
    open();
}

#[derive(Default)]
struct Local {
    starting: Option<String>,
    problem: Option<String>,
}

pub fn open() {
    let shared = shared();
    let window = gtk::Window::new();
    window.set_application(Some(&shared.app));
    // The window server's name and the one a screen reader reads are the
    // same name, so nothing that compares the two refuses to act here.
    window.set_title(Some(TITLE));
    window.set_widget_name(ui::DROPDOWN);
    window.set_default_size(340, -1);
    window.set_resizable(false);
    window.set_decorated(false);
    window.add_css_class("background");
    let column = gtk::Box::new(gtk::Orientation::Vertical, 0);
    window.set_child(Some(&column));
    let local: Rc<RefCell<Local>> = Rc::default();

    let draw: Rc<dyn Fn()> = {
        let column = column.clone();
        let local = local.clone();
        let window = window.clone();
        Rc::new(move || fill(&column, &local, &window))
    };
    {
        let draw = draw.clone();
        DRAW.with(|d| *d.borrow_mut() = Some(draw.clone()));
        listen(&column, &[Topic::Usage, Topic::Pool, Topic::Records, Topic::Settings, Topic::Claude, Topic::Front, Topic::Store], move || draw());
    }
    draw();

    // Closed when somebody clicks away, as a popover is. A dialog drawn over
    // it is inside it, so asking a question keeps it open.
    window.connect_is_active_notify(|w| {
        if !w.is_active() {
            let w = w.clone();
            gtk::glib::timeout_add_local_once(Duration::from_millis(150), move || {
                if !w.is_active() && w.is_visible() {
                    w.close();
                }
            });
        }
    });
    window.connect_close_request(|_| {
        OPEN.with(|o| *o.borrow_mut() = None);
        DRAW.with(|d| *d.borrow_mut() = None);
        // Paired with the watch below here rather than in a view: a watch
        // never let go keeps the pool on its fast timer for good.
        sharing::unwatch();
        gtk::glib::Propagation::Proceed
    });
    OPEN.with(|o| *o.borrow_mut() = Some(window.clone()));

    // Opening it is somebody asking to see the figures, so it may ask for the
    // access those figures need, but not skip the back-off.
    shared.monitor.set_may_prompt_unasked(true);
    shared.monitor.refresh(Refresh::looking());
    // Cheap once the transcripts have been read once, and this is where the
    // line about what was recovered is shown.
    shared::sweep();
    sharing::watch();
    window.present();
}

thread_local! {
    static DRAW: RefCell<Option<Rc<dyn Fn()>>> = const { RefCell::new(None) };
}

fn redraw() {
    if let Some(draw) = DRAW.with(|d| d.borrow().clone()) {
        draw();
    }
}

fn separator() -> gtk::Separator {
    let line = gtk::Separator::new(gtk::Orientation::Horizontal);
    line.set_margin_top(4);
    line.set_margin_bottom(4);
    line
}

fn fill(column: &gtk::Box, local: &Rc<RefCell<Local>>, window: &gtk::Window) {
    ui::clear(column);
    let shared = shared();
    column.append(&crate::updates::status_line(ui::DROPDOWN, false));
    column.append(&separator());

    let snapshot = shared.snapshot.borrow().clone();
    if snapshot.entries.is_empty() {
        let none = ui::caption("No Claude profiles yet");
        ui::padded(&none, 14, 10);
        identified(&none, "dropdown.no-profiles", "No Claude profiles yet");
        column.append(&none);
    }
    for (index, entry) in snapshot.entries.iter().enumerate() {
        column.append(&usage_row(entry, local));
        if index + 1 < snapshot.entries.len() {
            let line = gtk::Separator::new(gtk::Orientation::Horizontal);
            line.set_margin_start(14);
            column.append(&line);
        }
    }
    let front = shared.front.borrow().profile.clone();
    if let Some(conversations) = shared_usage::conversations(front.as_deref()) {
        column.append(&conversations);
    }
    column.append(&separator());

    let problem = local.borrow().problem.clone().or(snapshot.live_problem.clone());
    if let Some(problem) = &problem {
        let line = ui::caption(problem);
        line.set_wrap(true);
        line.set_single_line_mode(false);
        line.set_ellipsize(gtk::pango::EllipsizeMode::None);
        ui::padded(&line, 14, 4);
        identified(&line, "dropdown.problem", problem);
        column.append(&line);
    }
    let paused = shared_usage::lending_paused();
    let is_paused = paused.is_some();
    if let Some(paused) = paused {
        paused.set_margin_start(8);
        column.append(&paused);
    }
    // One key opens both, so the notice saying lending stopped is the one
    // shown, not two buttons for one dialog.
    if problem.is_none() && snapshot.needs_keyring_access && !is_paused {
        let button = gtk::Button::new();
        button.set_child(Some(&adw::ButtonContent::builder().icon_name("changes-prevent-symbolic").label("Turn on live usage").build()));
        button.add_css_class("flat");
        button.set_margin_start(8);
        button.set_tooltip_text(Some("Reads each account's limits from Anthropic. The desktop asks once to unlock the keyring."));
        identified(&button, "dropdown.turn-on-live-usage", "Turn on live usage");
        button.connect_clicked(|_| crate::shared::shared().monitor.refresh(Refresh::pressed()));
        column.append(&button);
    }
    let recovered = shared.recovered.borrow().clone();
    if !recovered.is_empty() {
        let text = if recovered.len() == 1 {
            format!("Recovered \u{201c}{}\u{201d}, which had closed without a record", recovered[0])
        } else {
            format!("Recovered {} sessions that closed without records", recovered.len())
        };
        let line = ui::caption(&text);
        line.set_tooltip_text(Some(&text));
        ui::padded(&line, 14, 4);
        identified(&line, "dropdown.recovered", &text);
        column.append(&line);
    }

    // Each switch explains itself on hover, never underneath: a note shown
    // while a switch is on stays as long as it is.
    let switches = gtk::Box::new(gtk::Orientation::Vertical, 2);
    ui::padded(&switches, 12, 4);
    let local_ref = local.clone();
    let problem_sink: Rc<dyn Fn(Option<String>)> = Rc::new(move |p| {
        local_ref.borrow_mut().problem = p;
        redraw();
    });
    for switch in preferences::switches(Look::Check, ui::DROPDOWN, problem_sink) {
        switches.append(&switch);
    }
    column.append(&switches);
    column.append(&separator());

    let menu = gtk::Box::new(gtk::Orientation::Vertical, 0);
    ui::padded(&menu, 6, 4);
    let refresh_title = if snapshot.is_refreshing { "Refreshing..." } else { "Refresh Usage" };
    menu.append(&menu_button(refresh_title, "dropdown.refresh-usage", |_| crate::shared::shared().monitor.refresh(Refresh::pressed())));
    let win = window.clone();
    menu.append(&menu_button("Open Claude Graft", "dropdown.open-window", move |_| {
        win.close();
        crate::app::show_main_window();
    }));
    let win = window.clone();
    menu.append(&menu_button("Support Claude Graft...", "dropdown.support", move |_| ui::open_url(&links::support(), Some(win.upcast_ref()))));
    menu.append(&menu_button("Quit Claude Graft", "dropdown.quit", |_| crate::app::quit()));
    column.append(&menu);
}

/// A row that reads like a menu item rather than a button.
fn menu_button(title: &str, id: &str, action: impl Fn(&gtk::Button) + 'static) -> gtk::Button {
    let button = gtk::Button::with_label(title);
    button.add_css_class("flat");
    if let Some(label) = button.child().and_downcast::<gtk::Label>() {
        label.set_xalign(0.0);
    }
    identified(&button, id, title.trim_end_matches("..."));
    button.connect_clicked(action);
    button
}

fn usage_row(entry: &Entry, local: &Rc<RefCell<Local>>) -> gtk::Box {
    let folder = entry.profile.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    let column = gtk::Box::new(gtk::Orientation::Vertical, 6);
    ui::padded(&column, 14, 8);
    let head = gtk::Box::new(gtk::Orientation::Horizontal, 6);
    // The dot is the only thing saying whether that Claude is open, and a dot
    // says nothing to somebody who cannot see it, so the words go beside it.
    let who = gtk::Box::new(gtk::Orientation::Horizontal, 6);
    let dot = gtk::Box::new(gtk::Orientation::Horizontal, 0);
    dot.add_css_class("graft-open-dot");
    if entry.is_running {
        dot.add_css_class("open");
    }
    dot.set_valign(gtk::Align::Center);
    who.append(&dot);
    let name = gtk::Label::new(Some(&entry.name));
    name.add_css_class("heading");
    who.append(&name);
    if let Some(plan) = &entry.plan {
        who.append(&ui::caption(plan));
    }
    let status: Vec<String> = [entry.plan.clone(), Some(if entry.is_running { "Running".into() } else { "Not running".into() })].into_iter().flatten().collect();
    who.set_accessible_role(gtk::AccessibleRole::Heading);
    identified(&who, &ui::id(ui::DROPDOWN, Some(&folder), "account"), &entry.name);
    who.update_property(&[gtk::accessible::Property::Description(&status.join(", "))]);
    who.set_hexpand(true);
    head.append(&who);
    // Named for their account: every row has a pair, and a list of the
    // controls would otherwise read "Open" once per account.
    if entry.may_start_session() {
        if local.borrow().starting.as_deref() == Some(entry.id().as_str()) {
            let spinner = adw::Spinner::new();
            identified(&spinner, &ui::id(ui::DROPDOWN, Some(&folder), "start-session"), &format!("Starting a session for {}", entry.name));
            head.append(&spinner);
        } else {
            let start = gtk::Button::with_label("Start Session");
            start.add_css_class("flat");
            start.set_tooltip_text(Some("Sends one short message to this account to open its five-hour window"));
            identified(&start, &ui::id(ui::DROPDOWN, Some(&folder), "start-session"), &format!("Start Session for {}", entry.name));
            start.set_sensitive(local.borrow().starting.is_none());
            let local = local.clone();
            let profile = entry.profile.clone();
            let id = entry.id();
            start.connect_clicked(move |_| {
                if local.borrow().starting.is_some() {
                    return;
                }
                local.borrow_mut().starting = Some(id.clone());
                local.borrow_mut().problem = None;
                redraw();
                let monitor = shared().monitor.clone();
                let asked = profile.clone();
                let local = local.clone();
                let profile = profile.clone();
                off_main(
                    move || {
                        let failure = graft_usage::start_session(&asked, true).err();
                        // The window this just opened is exactly what the
                        // stored reading predates, so it is dropped.
                        monitor.invalidate(&asked);
                        failure
                    },
                    move |failure| {
                        local.borrow_mut().starting = None;
                        local.borrow_mut().problem = failure.map(|f| f.message());
                        redraw();
                        // Starting the session already asked the keyring for
                        // this press; asking here too would be a second dialog
                        // after a no, and without a focus one per Claude.
                        shared().monitor.refresh(Refresh::after_session_started(&profile));
                    },
                );
            });
            head.append(&start);
        }
    }
    let open = gtk::Button::with_label("Open");
    identified(&open, &ui::id(ui::DROPDOWN, Some(&folder), "open"), &format!("Open {}", entry.name));
    {
        let local = local.clone();
        let profile = entry.profile.clone();
        let shortcut_id = entry.shortcut;
        open.connect_clicked(move |button| {
            let shortcut = shortcut_id.and_then(|id| shared().store.borrow().shortcut(id).cloned());
            local.borrow_mut().problem = None;
            let local = local.clone();
            // The tray's window is where a Claude is opened most of the
            // time, and on the Mac it was the one route that never asked
            // who else was already on those chats.
            chat_conflict::ask_then_open(button.upcast_ref(), profile.clone(), shortcut, move |problem| {
                local.borrow_mut().problem = problem;
                redraw();
            });
        });
    }
    head.append(&open);
    column.append(&head);
    column.append(&usage_view::summary(Some(entry), ui::DROPDOWN, &folder));
    column.append(&usage_view::freshness(entry, entry.profile.clone(), ui::DROPDOWN, &folder));
    column
}
