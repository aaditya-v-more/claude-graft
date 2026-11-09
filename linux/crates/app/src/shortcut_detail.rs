//! One shortcut's page: what it is called, where its profile lives, where it
//! reads its chats from, chats an account switch left elsewhere, its usage,
//! the accounts it may draw on, and the buttons it is for.

use crate::chat_conflict;
use crate::icons;
use crate::main_detail::SESSION_NOTE;
use crate::shared::{change_store, listen, shared, Topic};
use crate::shared_usage;
use crate::ui::{self, identified, off_main, BusyButton};
use crate::usage_view;
use graft_chats::{adopt_chats, chats_elsewhere, ChatsElsewhere};
use graft_core::{claude, config, IconPreset, Shortcut, Source};
use graft_launch::ChatSharing;
use graft_pool::controller::Controller;
use graft_profiles::{installer, opener};
use graft_usage::Refresh;
use gtk::glib;
use adw::prelude::*;
use std::cell::{Cell, RefCell};
use std::path::PathBuf;
use std::rc::Rc;
use std::time::{Duration, SystemTime, UNIX_EPOCH};
use uuid::Uuid;

const SHORTCUT_NOTE: &str = "The name is what the shortcut is called in the app grid and the dock. The profile folder is where this account's login, chats and settings are kept, inside ~/.config.\n\nNaming an existing folder adopts that profile instead of starting an empty one.";

const ELSEWHERE_NOTE: &str = "Every Claude has to be closed first, this profile's and every other one. An instance builds its sidebar as it starts and rewrites records in its own shape as it runs, so chats copied underneath a running one are invisible at best and written over at worst.\n\nCopying leaves the originals where they are, so the other profile is unchanged and still has them if that account is ever signed back into over there. Nothing already here is written over either, so a chat this profile has archived or renamed keeps the shape it was given.\n\nOnly the small record files are copied; the messages themselves already live in ~/.claude and are shared either way.";

const SIDEBAR_DEFAULT: &str = "Pinned chats and sort order sync when the linked Claude apps are closed and you open a shortcut.";

fn source_explanation(source: Source, label: &str) -> String {
    match source {
        Source::Own => "This profile keeps its own Claude Code history, connectors and preferences. Nothing is shared.".into(),
        _ => format!("Merges this profile's Claude Code chats with {label}'s, and shares extensions and window state. Missing local MCP servers are copied; existing server settings are kept. Logins and permission choices stay separate. Choose permission modes in Claude's settings for each account; organization restrictions still apply."),
    }
}

/// Named rather than described, because the consequence worth reading twice
/// is the one about the other profile, and "the other profile" is not what
/// anybody calls it.
fn merge_note(label: &str) -> String {
    format!("The two histories are merged rather than swapped. This profile keeps the chats it already had and gains {label}'s, and both sidebars end up showing the combined set. Graft carries changes both ways each time either Claude is opened, which is what lets this profile archive, rename and delete them at all.\n\nSharing goes both ways, so this profile's existing chats are copied into {label} as well. Switching back to its own chats returns this profile to exactly what it had, but the copies already in {label} stay there: merging a history is not something Graft can take back.\n\nOnly the small record files are copied; the messages themselves already live in ~/.claude and are shared either way. Archive the same conversation differently in both between two openings and the one touched last wins.")
}

/// Reads as a sentence for any number of them.
pub fn sentence(names: &[String]) -> String {
    match names.len() {
        0 => String::new(),
        1 => names[0].clone(),
        2 => format!("{} and {}", names[0], names[1]),
        n => format!("{} and {}", names[..n - 1].join(", "), names[n - 1]),
    }
}

fn chat_date(time: SystemTime) -> String {
    let seconds = time.duration_since(UNIX_EPOCH).map(|d| d.as_secs() as i64).unwrap_or(0);
    glib::DateTime::from_unix_local(seconds).ok().and_then(|d| d.format("%b %-d, %Y").ok()).map(|s| s.to_string()).unwrap_or_default()
}

fn found_note(found: &ChatsElsewhere, holder: &str) -> String {
    format!(
        "{holder} is holding chats for this account that this profile does not have. Chats found: {}. {}",
        found.count,
        if found.merging {
            "This profile has chats of its own too, so they are merged rather than either set being replaced."
        } else {
            "This profile has none of its own yet."
        }
    )
}

/// Named, counted and quoted, because none of the three answers the question
/// on its own: which profile says where they went, the count whether it is
/// the history being missed, and the titles and dates say it in the only
/// terms anybody recognises their own chats by.
fn found_more(found: &ChatsElsewhere, holder: &str) -> String {
    let mut parts = vec![found_note(found, holder)];
    parts.extend(found.chats.iter().take(5).map(|c| format!("{} \u{b7} {}", c.title, chat_date(c.last_active))));
    parts.join("\n\n")
}

/// What a page looked up off the main loop, refreshed on a clock.
#[derive(Default)]
struct Looked {
    installed_at: Option<PathBuf>,
    running: bool,
    profile_exists: bool,
    sidebar: Option<String>,
    elsewhere: Option<ChatsElsewhere>,
}

struct State {
    id: Uuid,
    folder_id: RefCell<String>,
    looked: RefCell<Looked>,
    looked_once: Cell<bool>,
    installing: Cell<bool>,
    copying: Cell<bool>,
    copied_note: RefCell<Option<String>>,
    folder_custom: Cell<bool>,
    /// A created shortcut's folder as typed, kept here until Update applies
    /// it. Written straight into the list, every keystroke would be a profile
    /// change the next start carried into the launcher.
    folder_draft: RefCell<Option<String>>,
    /// Set while the page writes into its own fields, so it does not hear
    /// itself as a person typing.
    quiet: Cell<bool>,
}

impl State {
    fn shortcut(&self) -> Option<Shortcut> {
        shared().store.borrow().shortcut(self.id).cloned()
    }

    fn is_created(&self, shortcut: &Shortcut) -> bool {
        shortcut.installed_name.is_some() || self.looked.borrow().installed_at.is_some()
    }
}

pub fn page(id: Uuid, set_subtitle: impl Fn(&str) + 'static, request_delete: impl Fn(Uuid) + 'static) -> gtk::Widget {
    let Some(first) = shared().store.borrow().shortcut(id).cloned() else {
        return gtk::Label::new(Some("This shortcut is gone.")).upcast();
    };
    let folder_id = first.profile_dir().file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    let state = Rc::new(State {
        id,
        folder_id: RefCell::new(folder_id.clone()),
        looked: RefCell::new(Looked::default()),
        looked_once: Cell::new(false),
        installing: Cell::new(false),
        copying: Cell::new(false),
        copied_note: RefCell::new(None),
        folder_custom: Cell::new(false),
        folder_draft: RefCell::new(None),
        quiet: Cell::new(false),
    });
    let set_subtitle: Rc<dyn Fn(&str)> = Rc::new(set_subtitle);
    let request_delete: Rc<dyn Fn(Uuid)> = Rc::new(request_delete);
    let fid = |control: &str| ui::id(ui::WINDOW, Some(&folder_id), control);

    let toolbar = adw::ToolbarView::new();
    let column = gtk::Box::new(gtk::Orientation::Vertical, 14);
    ui::padded(&column, 18, 14);

    // Icon, name and folder share one row: the three say what the shortcut
    // is, and stacked they pushed everything else below the bottom of the
    // window.
    let identity = ui::card();
    let row = ui::card_row(10);
    let icon_slot = gtk::Box::new(gtk::Orientation::Horizontal, 0);
    let icon_button = gtk::MenuButton::new();
    icon_button.add_css_class("flat");
    identified(&icon_button, &fid("icon"), "Shortcut icon");
    icon_slot.append(&icon_button);
    row.append(&icon_slot);
    let name = gtk::Entry::new();
    name.set_text(&first.name);
    name.set_hexpand(true);
    identified(&name, &fid("name"), "Name");
    row.append(&name);
    let folder = gtk::Entry::new();
    folder.set_text(&first.folder);
    folder.set_width_chars(16);
    folder.add_css_class("monospace");
    folder.set_tooltip_text(Some("Profile folder"));
    identified(&folder, &fid("folder"), "Profile folder");
    row.append(&folder);
    let show = ui::icon_button("folder-open-symbolic", &fid("show-in-files"), "Show in Files");
    row.append(&show);
    row.append(&ui::info_button(SHORTCUT_NOTE, "Shortcut", &fid("shortcut-info")));
    identity.append(&row);
    let copied_slot = gtk::Box::new(gtk::Orientation::Vertical, 0);
    identity.append(&copied_slot);

    // Where chats come from, and under it whether they are shared, in one
    // row: a section of its own for one picker cost a heading and a gap.
    let source_row = ui::card_row(8);
    let source_words = gtk::Box::new(gtk::Orientation::Vertical, 2);
    source_words.set_hexpand(true);
    let source_heading = gtk::Box::new(gtk::Orientation::Horizontal, 5);
    source_heading.append(&gtk::Label::new(Some("Reads chats from")));
    let source_info_slot = gtk::Box::new(gtk::Orientation::Horizontal, 0);
    source_heading.append(&source_info_slot);
    source_words.append(&source_heading);
    let merge_slot = gtk::Box::new(gtk::Orientation::Vertical, 0);
    source_words.append(&merge_slot);
    source_row.append(&source_words);
    let sources = gtk::DropDown::from_strings(&[]);
    sources.set_valign(gtk::Align::Center);
    identified(&sources, &fid("chat-source"), "Reads chats from");
    source_row.append(&sources);
    identity.append(&gtk::Separator::new(gtk::Orientation::Horizontal));
    identity.append(&source_row);
    column.append(&identity);

    let elsewhere_slot = gtk::Box::new(gtk::Orientation::Vertical, 0);
    column.append(&elsewhere_slot);
    let usage_slot = gtk::Box::new(gtk::Orientation::Vertical, 0);
    column.append(&usage_slot);
    let sharing_slot = gtk::Box::new(gtk::Orientation::Vertical, 0);
    column.append(&sharing_slot);
    toolbar.set_content(Some(&column));

    // Delete on the left, what the shortcut is for on the right.
    let bar = gtk::Box::new(gtk::Orientation::Horizontal, 8);
    ui::padded(&bar, 14, 10);
    let delete = gtk::Button::with_label("Delete Shortcut...");
    delete.add_css_class("destructive-action");
    identified(&delete, &fid("delete"), "Delete Shortcut");
    bar.append(&delete);
    let spacer = gtk::Box::new(gtk::Orientation::Horizontal, 0);
    spacer.set_hexpand(true);
    bar.append(&spacer);
    let session_box = gtk::Box::new(gtk::Orientation::Horizontal, 4);
    let start = Rc::new(BusyButton::new("Start Session", &fid("start-session"), "Start Session"));
    session_box.append(&start.button);
    session_box.append(&ui::info_button(SESSION_NOTE, "Start Session", &fid("session-info")));
    bar.append(&session_box);
    let open = gtk::Button::with_label("Open");
    identified(&open, &fid("open"), "Open");
    bar.append(&open);
    let install = Rc::new(BusyButton::new("Create Shortcut", &fid("install"), "Create Shortcut"));
    install.button.add_css_class("suggested-action");
    bar.append(&install.button);
    toolbar.add_bottom_bar(&bar);

    // Everything that changes as the list, the figures and the lookups do.
    let sync: Rc<dyn Fn()> = {
        let state = state.clone();
        let icon_button = icon_button.clone();
        let copied_slot = copied_slot.clone();
        let source_info_slot = source_info_slot.clone();
        let merge_slot = merge_slot.clone();
        let sources = sources.clone();
        let usage_slot = usage_slot.clone();
        let sharing_slot = sharing_slot.clone();
        let delete = delete.clone();
        let session_box = session_box.clone();
        let open = open.clone();
        let install = install.clone();
        let set_subtitle = set_subtitle.clone();
        let identity_row = row.clone();
        Rc::new(move || {
            let Some(shortcut) = state.shortcut() else { return };
            let shared = shared();
            let folder_id = state.folder_id.borrow().clone();
            let fid = |control: &str| ui::id(ui::WINDOW, Some(&folder_id), control);
            let looked = state.looked.borrow();
            let store = shared.store.borrow();
            let label = store.label(shortcut.source);
            let installing = state.installing.get();
            identity_row.set_sensitive(!installing);

            icon_button.set_child(Some(&icons::image(shortcut.icon_preset, 28)));
            icon_button.update_property(&[gtk::accessible::Property::Description(shortcut.icon_preset.title())]);
            icon_button.set_popover(Some(&icon_picker(&state, shortcut.icon_preset, &folder_id)));

            ui::clear(&copied_slot);
            if let Some(note) = state.copied_note.borrow().as_deref() {
                let line = ui::note(note, None, "Chats", false, &fid("copied-note"));
                ui::padded(&line, 12, 4);
                copied_slot.append(&line);
            }

            ui::clear(&source_info_slot);
            source_info_slot.append(&ui::info_button(&source_explanation(shortcut.source, &label), "Chats", &fid("chats-info")));
            ui::clear(&merge_slot);
            if shortcut.source != Source::Own {
                let more = format!("{}\n\n{}", merge_note(&label), looked.sidebar.as_deref().unwrap_or(SIDEBAR_DEFAULT));
                merge_slot.append(&ui::note(&format!("Shared both ways with {label}"), Some(&more), "Chats", false, &fid("merge-note")));
            }
            let options = store.available_sources(&shortcut);
            let names: Vec<String> = options.iter().map(|s| store.label(*s)).collect();
            let refs: Vec<&str> = names.iter().map(String::as_str).collect();
            state.quiet.set(true);
            sources.set_model(Some(&gtk::StringList::new(&refs)));
            if let Some(index) = options.iter().position(|s| *s == shortcut.source) {
                sources.set_selected(index as u32);
            }
            state.quiet.set(false);
            sources.set_sensitive(!installing);

            let entry = shared.snapshot.borrow().entry_for(&shortcut.profile_dir()).cloned();
            ui::clear(&usage_slot);
            usage_slot.append(&usage_view::section(entry.as_ref(), shortcut.profile_dir(), ui::WINDOW, &folder_id));
            ui::clear(&sharing_slot);
            // A shortcut not created yet has no profile for anything to reach.
            if looked.installed_at.is_some() {
                sharing_slot.append(&shared_usage::section(&Controller::key_for(&shortcut), &folder_id));
            }

            let draft = looked.installed_at.is_none() && !looked.profile_exists && shortcut.installed_name.is_none();
            delete.set_label(if draft { "Discard" } else { "Delete Shortcut..." });
            delete.set_sensitive(!installing);
            session_box.set_visible(entry.as_ref().map(|e| e.may_start_session()).unwrap_or(true));
            open.set_sensitive(looked.installed_at.is_some() && !installing);
            let title = if looked.installed_at.is_none() { "Create Shortcut" } else { "Update Shortcut" };
            install.set_text(title);
            let found = shared.claude.borrow().found;
            install.button.set_sensitive(!installing && !shortcut.name.trim().is_empty() && found != Some(false));
            install.button.set_tooltip_text(looked.installed_at.as_ref().map(|p| p.display().to_string()).as_deref());

            // Created or not, and running or not, in one line; a missing
            // Claude is said here rather than in a section of its own.
            let created = if looked.installed_at.is_none() { "Not created yet" } else { "Created" };
            let running = match found {
                Some(false) => "Claude Desktop was not found.",
                _ if looked.running => "Running",
                _ => "Not running",
            };
            set_subtitle(&format!("{created} \u{b7} {running}"));
        })
    };

    // The elsewhere offer is drawn on its own, since it hangs off a lookup.
    let draw_elsewhere: Rc<dyn Fn()> = {
        let state = state.clone();
        let slot = elsewhere_slot.clone();
        let weak_sync = Rc::downgrade(&sync);
        Rc::new(move || {
            ui::clear(&slot);
            let found = state.looked.borrow().elsewhere.clone();
            let Some(found) = found else { return };
            let folder_id = state.folder_id.borrow().clone();
            let fid = |control: &str| ui::id(ui::WINDOW, Some(&folder_id), control);
            let holder = shared().store.borrow().name_of_profile(&found.profile);
            slot.append(&ui::section_header("Chats found elsewhere", ELSEWHERE_NOTE, &fid("elsewhere-info"), None));
            let card = ui::card();
            let row = ui::card_row(8);
            let note = ui::note(&format!("{holder} holds {} chats for this account", found.count), Some(&found_more(&found, &holder)), "Chats found elsewhere", false, &fid("elsewhere-note"));
            note.set_hexpand(true);
            row.append(&note);
            if state.copying.get() {
                row.append(&adw::Spinner::new());
            }
            let words = if found.merging { "Merge Them Here" } else { "Copy Them Here" };
            let copy = gtk::Button::with_label(words);
            copy.set_sensitive(!state.copying.get());
            identified(&copy, &fid("copy-chats"), words);
            let state = state.clone();
            let weak_sync = weak_sync.clone();
            copy.connect_clicked(move |_| {
                if let Some(sync) = weak_sync.upgrade() {
                    adopt(&state, found.clone(), sync, None);
                }
            });
            row.append(&copy);
            card.append(&row);
            slot.append(&card);
        })
    };

    let refresh: Rc<dyn Fn()> = {
        let state = state.clone();
        let sync = sync.clone();
        let draw_elsewhere = draw_elsewhere.clone();
        Rc::new(move || {
            let Some(target) = state.shortcut() else { return };
            let state = state.clone();
            let sync = sync.clone();
            let draw_elsewhere = draw_elsewhere.clone();
            off_main(
                move || {
                    let profile = target.profile_dir();
                    Looked {
                        installed_at: opener::entry_for(&target),
                        running: claude::is_running(&profile),
                        profile_exists: profile.exists(),
                        sidebar: graft_sidebar::status_message(&profile).map(str::to_string),
                        // Only for a profile keeping its own chats: one reading
                        // from a source is having its sidebar filled by the
                        // graft already, and the offer would describe chats it
                        // is about to be handed.
                        elsewhere: (target.source == Source::Own)
                            .then(|| chats_elsewhere(&profile, &config::session_store_profiles()))
                            .flatten(),
                    }
                },
                move |looked| {
                    // Kept whatever the answer was last time: saying no to
                    // being asked silences the question at the door, not the
                    // offer here, the one place left to change your mind.
                    let changed_offer = state.looked.borrow().elsewhere != looked.elsewhere;
                    *state.looked.borrow_mut() = looked;
                    state.looked_once.set(true);
                    sync();
                    if changed_offer {
                        draw_elsewhere();
                    }
                },
            );
        })
    };
    sync();
    refresh();
    {
        let refresh = refresh.clone();
        ui::every(&column, Duration::from_secs(5), move || refresh());
    }
    {
        let sync = sync.clone();
        listen(&column, &[Topic::Store, Topic::Usage, Topic::Claude], move || sync());
    }
    shared().monitor.refresh(Refresh::looking());

    // Editing.
    {
        let state = state.clone();
        let folder = folder.clone();
        name.connect_changed(move |entry| {
            if state.quiet.get() {
                return;
            }
            let text = entry.text().to_string();
            let created = state.shortcut().map(|s| state.is_created(&s)).unwrap_or(true);
            let custom = state.folder_custom.get();
            let mut suggested = None;
            change_store(|store| {
                store.update(state.id, |s| {
                    s.name = text.clone();
                    if !created && !custom {
                        s.folder = Shortcut::folder_name(&text);
                        suggested = Some(s.folder.clone());
                    }
                })
            });
            if let Some(suggested) = suggested {
                state.quiet.set(true);
                folder.set_text(&suggested);
                state.quiet.set(false);
            }
        });
    }
    {
        let state = state.clone();
        folder.connect_changed(move |entry| {
            if state.quiet.get() {
                return;
            }
            state.folder_custom.set(true);
            let text = entry.text().to_string();
            let created = state.shortcut().map(|s| state.is_created(&s)).unwrap_or(true);
            // Straight into the list until the shortcut is created, since
            // nothing runs it yet; held back after.
            if created {
                *state.folder_draft.borrow_mut() = Some(text);
            } else {
                change_store(|store| store.update(state.id, |s| s.folder = text.clone()));
            }
        });
    }
    {
        let state = state.clone();
        sources.connect_selected_notify(move |dropdown| {
            if state.quiet.get() {
                return;
            }
            let Some(shortcut) = state.shortcut() else { return };
            let options = shared().store.borrow().available_sources(&shortcut);
            if let Some(source) = options.get(dropdown.selected() as usize).copied() {
                if source != shortcut.source {
                    change_store(|store| store.update(state.id, |s| s.source = source));
                }
            }
        });
    }
    {
        let state = state.clone();
        show.connect_clicked(move |button| {
            if let Some(s) = state.shortcut() {
                ui::show_in_files(&s.profile_dir(), button);
            }
        });
    }
    {
        let state = state.clone();
        let request_delete = request_delete.clone();
        delete.connect_clicked(move |_| request_delete(state.id));
    }

    // Create and Update. Off the main loop, with the page held still until it
    // answers: it draws the icon, writes the entry and ends in a mirror pass
    // that on a newly shared profile copies every chat both ways. The answer
    // goes to the list by id, since the page may have been left meanwhile.
    {
        let state = state.clone();
        let sync = sync.clone();
        let refresh = refresh.clone();
        let install_button = install.clone();
        install.button.connect_clicked(move |button| {
            if state.installing.get() {
                return;
            }
            let Some(previous) = state.shortcut() else { return };
            let mut target = previous.clone();
            target.name = previous.name.trim().to_string();
            target.folder = state.folder_draft.borrow().clone().unwrap_or_else(|| previous.folder.clone()).trim().to_string();
            let (source_dir, list) = {
                let shared = shared();
                let store = shared.store.borrow();
                (store.source_dir(&target), store.shortcuts().to_vec())
            };
            state.installing.set(true);
            install_button.set_busy(true);
            sync();
            let state = state.clone();
            let sync = sync.clone();
            let refresh = refresh.clone();
            let install_button = install_button.clone();
            let parent = button.clone();
            off_main(
                move || {
                    let result = installer::install(&target, source_dir.as_deref(), Some(&previous), &list, &ChatSharing);
                    (target, result)
                },
                move |(target, result)| {
                    state.installing.set(false);
                    install_button.set_busy(false);
                    match result {
                        Ok(entry) => {
                            change_store(|store| store.record_installed(target.id, &target.name, &target.folder));
                            *state.folder_draft.borrow_mut() = None;
                            *state.folder_id.borrow_mut() = target.profile_dir().file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
                            state.looked.borrow_mut().installed_at = Some(entry);
                            sync();
                            refresh();
                        }
                        Err(failure) => {
                            sync();
                            ui::tell(&parent, "Could not create the shortcut", &failure.to_string());
                        }
                    }
                },
            );
        });
    }

    // Start Session.
    {
        let state = state.clone();
        let start_ref = start.clone();
        let refresh = refresh.clone();
        start.button.connect_clicked(move |button| {
            let Some(shortcut) = state.shortcut() else { return };
            let profile = shortcut.profile_dir();
            start_ref.set_busy(true);
            let start_ref = start_ref.clone();
            let refresh = refresh.clone();
            let parent = button.clone();
            let monitor = shared().monitor.clone();
            let asked = profile.clone();
            off_main(
                move || {
                    let failure = graft_usage::start_session(&asked, true).err();
                    // The window this just opened is exactly what the stored
                    // reading predates, so it is dropped rather than waited out.
                    monitor.invalidate(&asked);
                    failure
                },
                move |failure| {
                    start_ref.set_busy(false);
                    if let Some(failure) = failure {
                        ui::tell(&parent, "Could not start a session", &failure.message());
                    }
                    refresh();
                    // Starting the session already asked the keyring for this
                    // press, so this asks nobody, and only about this account.
                    shared().monitor.refresh(Refresh::after_session_started(&profile));
                },
            );
        });
    }

    // Open: the chats elsewhere first, since copying is what changes the
    // sidebar of the window about to open and Claude builds that sidebar as
    // it starts; then who else is open on these chats.
    {
        let state = state.clone();
        let sync = sync.clone();
        open.connect_clicked(move |button| {
            let Some(shortcut) = state.shortcut() else { return };
            if state.looked.borrow().installed_at.is_none() {
                return;
            }
            let found = state.looked.borrow().elsewhere.clone();
            let asked = shared().store.borrow().asked_about_chats.contains(&shortcut.id);
            match found {
                Some(found) if shortcut.stop_asking_chats_for.as_deref() != Some(found.account.as_str()) && !asked => {
                    ask_about_elsewhere(button.upcast_ref(), &state, found, sync.clone());
                }
                _ => check_sharers(button.upcast_ref(), &state),
            }
        });
    }
    toolbar.upcast()
}

fn icon_picker(state: &Rc<State>, current: IconPreset, folder_id: &str) -> gtk::Popover {
    let popover = gtk::Popover::new();
    let column = gtk::Box::new(gtk::Orientation::Vertical, 8);
    ui::padded(&column, 10, 10);
    let grid = gtk::Grid::new();
    grid.set_row_spacing(6);
    grid.set_column_spacing(6);
    for (index, preset) in IconPreset::ALL.iter().enumerate() {
        let button = gtk::Button::new();
        button.set_child(Some(&icons::image(*preset, 38)));
        button.add_css_class("flat");
        if *preset == current {
            button.add_css_class("suggested-action");
        }
        identified(&button, &ui::id(ui::WINDOW, Some(folder_id), &format!("icon.{}", preset.title().to_lowercase())), preset.title());
        button.update_property(&[gtk::accessible::Property::Description(if *preset == current { "Selected" } else { "" })]);
        button.set_tooltip_text(Some(preset.title()));
        let state = state.clone();
        let preset = *preset;
        let pop = popover.clone();
        button.connect_clicked(move |_| {
            pop.popdown();
            change_store(|store| store.update(state.id, |s| s.icon_preset = preset));
        });
        grid.attach(&button, (index % 6) as i32, (index / 6) as i32, 1, 1);
    }
    column.append(&grid);
    column.append(&ui::caption("Used in the app grid and the dock, and by this Claude's window once it runs."));
    popover.set_child(Some(&column));
    popover
}

fn ask_about_elsewhere(parent: &gtk::Widget, state: &Rc<State>, found: ChatsElsewhere, sync: Rc<dyn Fn()>) {
    let holder = shared().store.borrow().name_of_profile(&found.profile);
    let heading = if found.merging { "Merge this account's other chats in first?" } else { "Bring this account's chats across first?" };
    let body = format!(
        "{}\n\nClaude builds its sidebar as it starts, so bringing them over now is what puts them in the window about to open. Every other Claude has to be closed for that.",
        found_note(&found, &holder)
    );
    let dialog = adw::AlertDialog::new(Some(heading), Some(&body));
    dialog.add_response("cancel", "Cancel");
    dialog.add_response("never", "Do Not Show Again");
    dialog.add_response("without", "Open Without Them");
    dialog.add_response("copy", if found.merging { "Merge Them Here" } else { "Copy Them Here" });
    dialog.set_response_appearance("copy", adw::ResponseAppearance::Suggested);
    dialog.set_default_response(Some("copy"));
    dialog.set_close_response("cancel");
    let state = state.clone();
    let anchor = parent.clone();
    dialog.connect_response(None, move |_, response| match response {
        "copy" => {
            let state_then = state.clone();
            let anchor = anchor.clone();
            adopt(&state, found.clone(), sync.clone(), Some(Box::new(move || check_sharers(&anchor, &state_then))));
        }
        "without" => {
            // Lasts as long as Graft is up, so a press that changed nothing
            // is asked about again the next time.
            shared().store.borrow_mut().asked_about_chats.insert(state.id);
            check_sharers(&anchor, &state);
        }
        "never" => {
            let account = found.account.clone();
            change_store(|store| store.update(state.id, |s| s.stop_asking_chats_for = Some(account)));
            check_sharers(&anchor, &state);
        }
        _ => {}
    });
    dialog.present(Some(parent));
}

/// Off the main loop, because it copies files and asks what is running. The
/// continuation runs only when something was actually brought over: a copy
/// refused for a Claude still open has not done what the press asked for,
/// and opening a window then would bury the line saying which to quit.
fn adopt(state: &Rc<State>, found: ChatsElsewhere, sync: Rc<dyn Fn()>, then: Option<Box<dyn Fn()>>) {
    if state.copying.get() {
        return;
    }
    let Some(shortcut) = state.shortcut() else { return };
    state.copying.set(true);
    sync();
    let profile = shortcut.profile_dir();
    let source = found.profile.clone();
    let state = state.clone();
    off_main(
        move || adopt_chats(&found.profile, &profile, &found.account),
        move |result| {
            state.copying.set(false);
            let shared = shared();
            let note = {
                let store = shared.store.borrow();
                if !result.running.is_empty() {
                    let names: Vec<String> = result.running.iter().map(|p| store.name_of_profile(p)).collect();
                    format!("Nothing copied: quit {} first", sentence(&names))
                } else if result.copied == 0 {
                    "Nothing was copied".to_string()
                } else {
                    format!("Chats copied from {}: {}", store.name_of_profile(&source), result.copied)
                }
            };
            *state.copied_note.borrow_mut() = Some(note);
            sync();
            if result.running.is_empty() && result.copied > 0 {
                if let Some(then) = then {
                    then();
                }
            }
        },
    );
}

fn check_sharers(parent: &gtk::Widget, state: &Rc<State>) {
    let Some(shortcut) = state.shortcut() else { return };
    if state.looked.borrow().installed_at.is_none() {
        return;
    }
    let parent_for_problem = parent.clone();
    chat_conflict::ask_then_open(parent, shortcut.profile_dir(), Some(shortcut), move |problem| {
        if let Some(problem) = problem {
            ui::tell(&parent_for_problem, "Could not open the shortcut", &problem);
        }
    });
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_list_of_profiles_reads_as_a_sentence() {
        assert_eq!(sentence(&["Claude".into()]), "Claude");
        assert_eq!(sentence(&["Claude".into(), "Work".into()]), "Claude and Work");
        assert_eq!(sentence(&["A".into(), "B".into(), "C".into()]), "A, B and C");
    }

    #[test]
    fn the_merge_note_names_the_other_profile() {
        assert!(merge_note("Work").contains("gains Work's"), "the consequence is said about the profile by its name");
        assert!(source_explanation(Source::Own, "Its own chats").contains("Nothing is shared"));
    }
}
