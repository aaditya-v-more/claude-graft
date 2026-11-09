//! Claude's own profile, shown alongside the shortcuts so its usage and state
//! are in the same place. Nothing here is editable: this is the installation
//! Graft borrows from, not something it created.

use crate::chat_conflict;
use crate::shared::{listen, shared, Topic};
use crate::shared_usage;
use crate::ui::{self, identified, off_main, BusyButton};
use crate::usage_view;
use graft_core::{claude, paths};
use graft_pool::controller::MAIN_KEY;
use graft_usage::Refresh;
use adw::prelude::*;
use std::rc::Rc;
use std::time::Duration;

const NOTE: &str = "The Claude you installed, started the ordinary way. Graft does not create or modify it: shortcuts borrow from it, and it can never be renamed, re-pointed or deleted from here.";

pub const SESSION_NOTE: &str = "Sends one short message to this account so its five-hour window opens. Nothing appears on screen.\n\nIt uses that profile's own login, borrowed the same way the usage figures are, so each account can be started separately.";

fn folder() -> String {
    paths::main_profile().file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default()
}

pub fn page(set_subtitle: impl Fn(&str) + 'static) -> gtk::Widget {
    let set_subtitle: Rc<dyn Fn(&str)> = Rc::new(set_subtitle);
    let folder = folder();
    let profile = paths::main_profile();
    let toolbar = adw::ToolbarView::new();
    let column = gtk::Box::new(gtk::Orientation::Vertical, 14);
    ui::padded(&column, 18, 14);

    // What it is and where it keeps its profile, on one row, as a shortcut's
    // page has them; whether it is running is the window's subtitle.
    let identity = ui::card();
    let row = ui::card_row(10);
    let house = gtk::Image::from_icon_name("go-home-symbolic");
    house.set_pixel_size(24);
    house.add_css_class("dim-label");
    house.set_accessible_role(gtk::AccessibleRole::Presentation);
    row.append(&house);
    let name = gtk::Label::new(Some("Claude"));
    name.add_css_class("heading");
    identified(&name, &ui::id(ui::WINDOW, Some(&folder), "name"), "Name, Claude");
    row.append(&name);
    row.append(&ui::info_button(NOTE, "Claude", &ui::id(ui::WINDOW, Some(&folder), "profile-info")));
    let spacer = gtk::Box::new(gtk::Orientation::Horizontal, 0);
    spacer.set_hexpand(true);
    row.append(&spacer);
    let folder_label = gtk::Label::new(Some(&folder));
    folder_label.add_css_class("monospace");
    folder_label.add_css_class("dim-label");
    folder_label.set_tooltip_text(Some("Profile folder"));
    identified(&folder_label, &ui::id(ui::WINDOW, Some(&folder), "folder"), &format!("Profile folder, {folder}"));
    row.append(&folder_label);
    let show = ui::icon_button("folder-open-symbolic", &ui::id(ui::WINDOW, Some(&folder), "show-in-files"), "Show in Files");
    {
        let profile = profile.clone();
        show.connect_clicked(move |b| ui::show_in_files(&profile, b));
    }
    row.append(&show);
    identity.append(&row);
    column.append(&identity);

    let usage_slot = gtk::Box::new(gtk::Orientation::Vertical, 0);
    column.append(&usage_slot);
    column.append(&shared_usage::section(MAIN_KEY, &folder));
    toolbar.set_content(Some(&column));

    // The bottom bar: Start Session and Open.
    let bar = gtk::Box::new(gtk::Orientation::Horizontal, 8);
    ui::padded(&bar, 14, 10);
    let spacer = gtk::Box::new(gtk::Orientation::Horizontal, 0);
    spacer.set_hexpand(true);
    bar.append(&spacer);
    let session_box = gtk::Box::new(gtk::Orientation::Horizontal, 4);
    let start = Rc::new(BusyButton::new("Start Session", &ui::id(ui::WINDOW, Some(&folder), "start-session"), "Start Session"));
    session_box.append(&start.button);
    session_box.append(&ui::info_button(SESSION_NOTE, "Start Session", &ui::id(ui::WINDOW, Some(&folder), "session-info")));
    bar.append(&session_box);
    let open = gtk::Button::with_label("Open");
    open.add_css_class("suggested-action");
    identified(&open, &ui::id(ui::WINDOW, Some(&folder), "open"), "Open Claude");
    bar.append(&open);
    toolbar.add_bottom_bar(&bar);

    let running = Rc::new(std::cell::Cell::new(false));
    let draw = {
        let usage_slot = usage_slot.clone();
        let session_box = session_box.clone();
        let open = open.clone();
        let folder = folder.clone();
        let set_subtitle = set_subtitle.clone();
        let running = running.clone();
        let profile = profile.clone();
        move || {
            let shared = shared();
            let entry = shared.snapshot.borrow().entry_for(&profile).cloned();
            ui::clear(&usage_slot);
            usage_slot.append(&usage_view::section(entry.as_ref(), profile.clone(), ui::WINDOW, &folder));
            session_box.set_visible(entry.as_ref().map(|e| e.may_start_session()).unwrap_or(true));
            let found = shared.claude.borrow().found;
            open.set_sensitive(found != Some(false));
            // A missing Claude is said where running is, and takes no row of
            // the page.
            set_subtitle(match found {
                Some(false) => "Claude Desktop was not found.",
                _ if running.get() => "Running",
                _ => "Not running",
            });
        }
    };
    draw();
    listen(&column, &[Topic::Usage, Topic::Claude], draw.clone());

    // Off the main loop and on a clock: whether it is running is a process
    // listing on the host.
    let refresh_running = {
        let running = running.clone();
        let draw = draw.clone();
        move || {
            let running = running.clone();
            let draw = draw.clone();
            off_main(|| claude::is_running(&paths::main_profile()), move |now| {
                running.set(now);
                draw();
            });
        }
    };
    refresh_running();
    ui::every(&column, Duration::from_secs(5), refresh_running.clone());

    // Selecting a profile is someone looking at its figures, the rung below a
    // press: current within the minute, and allowed to ask for the keyring
    // once if that is what stands in the way.
    shared().monitor.refresh(Refresh::looking());

    {
        let profile = profile.clone();
        open.connect_clicked(move |button| {
            // Every shortcut grafted from main reads the files this is about
            // to open, so this is the warning that matters most, and the one
            // profile that had no shortcut to ask through.
            chat_conflict::ask_then_open(button.upcast_ref(), profile.clone(), None, |_| {});
        });
    }
    {
        let start_ref = start.clone();
        let refresh_running = refresh_running.clone();
        start.button.connect_clicked(move |button| {
            start_ref.set_busy(true);
            let start_ref = start_ref.clone();
            let parent = button.clone();
            let refresh_running = refresh_running.clone();
            let profile = paths::main_profile();
            let monitor = shared().monitor.clone();
            off_main(
                move || {
                    let failure = graft_usage::start_session(&profile, true).err();
                    // The window this just opened is exactly what the stored
                    // reading predates, so it is dropped rather than waited out.
                    monitor.invalidate(&profile);
                    failure
                },
                move |failure| {
                    start_ref.set_busy(false);
                    if let Some(failure) = failure {
                        ui::tell(&parent, "Could not start a session", &failure.message());
                    }
                    refresh_running();
                    // Starting the session already asked the keyring for this
                    // press, so this asks nobody, and only about this account.
                    shared().monitor.refresh(Refresh::after_session_started(&paths::main_profile()));
                },
            );
        });
    }
    toolbar.upcast()
}
