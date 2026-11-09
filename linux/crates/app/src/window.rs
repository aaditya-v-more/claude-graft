//! The window: a split view with a real sidebar, Claude's own profile and
//! the pages every account shares at the top, the shortcuts under them, and
//! the page for whichever is selected beside it.
//!
//! The split view is the window's root, so the sidebar runs the full height
//! and the title belongs to the content column alone; a notice sits inside
//! that column, never above the split view. The list of pages is data: a
//! `Page` names each one and `page_widget` builds it, so a page is added in
//! those two places and nowhere else.

use crate::links;
use crate::main_detail;
use crate::shared::{change_store, listen, shared, Topic};
use crate::shortcut_detail;
use crate::ui::{self, identified, off_main};
use graft_core::paths;
use graft_launch::ChatSharing;
use graft_profiles::{installer, ShortcutStore};
use gtk::gio;
use adw::prelude::*;
use std::cell::{Cell, RefCell};
use std::rc::Rc;
use uuid::Uuid;

/// Every page the sidebar can show.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Page {
    /// Claude's own profile, which has no shortcut standing for it.
    Main,
    /// The artifacts of every signed-in account, which belong to no profile.
    Artifacts,
    /// Every signed-in account's projects.
    Projects,
    /// One search across every signed-in account's chats.
    Search,
    Shortcut(Uuid),
}

/// What a sidebar row says about its page.
#[derive(Debug, Clone, PartialEq)]
pub struct Row {
    pub page: Page,
    pub title: String,
    pub subtitle: String,
    pub icon: &'static str,
    /// The control's name, by folder for a profile, which reads the same in
    /// every language.
    pub id: String,
}

/// The sidebar's two sections, in order.
pub fn rows(store: &ShortcutStore) -> (Vec<Row>, Vec<Row>) {
    let main_folder = paths::main_profile().file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    let top = vec![
        Row { page: Page::Main, title: "Claude".into(), subtitle: "Installed normally".into(), icon: "go-home-symbolic", id: format!("window.sidebar.{main_folder}") },
        Row { page: Page::Artifacts, title: "Artifacts".into(), subtitle: "Every account".into(), icon: "view-paged-symbolic", id: "window.sidebar.artifacts".into() },
        Row { page: Page::Projects, title: "Projects".into(), subtitle: "Every account".into(), icon: "folder-symbolic", id: "window.sidebar.projects".into() },
        Row { page: Page::Search, title: "Search Chats".into(), subtitle: "Every account".into(), icon: "system-search-symbolic", id: "window.sidebar.search".into() },
    ];
    let shortcuts = store
        .shortcuts()
        .iter()
        .map(|s| {
            let folder = s.profile_dir().file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
            // The symbol is the only thing that marks a borrowed history, so
            // the line under the name says it in words as well.
            let subtitle = if s.installed_name.is_none() {
                "Not created yet".to_string()
            } else if s.source == graft_core::Source::Own {
                store.label(s.source)
            } else {
                format!("Reads chats from {}", store.label(s.source))
            };
            Row {
                page: Page::Shortcut(s.id),
                title: s.name.clone(),
                subtitle,
                icon: if s.source == graft_core::Source::Own { "radio-symbolic" } else { "insert-link-symbolic" },
                id: format!("window.sidebar.{folder}"),
            }
        })
        .collect();
    (top, shortcuts)
}

/// The page's own widget. `subtitle` is how a page says, as the window's
/// subtitle, whether its Claude is running.
pub fn page_widget(page: Page, subtitle: Rc<dyn Fn(&str)>, request_delete: Rc<dyn Fn(Uuid)>) -> gtk::Widget {
    let shared = shared();
    match page {
        Page::Main => main_detail::page(move |s| subtitle(s)),
        Page::Artifacts => {
            subtitle("");
            graft_web::ui::artifacts_page(&shared.artifacts)
        }
        Page::Projects => {
            subtitle("");
            graft_web::ui::projects_page(&shared.projects)
        }
        Page::Search => {
            subtitle("");
            graft_web::ui::search_page(&shared.projects)
        }
        Page::Shortcut(id) => shortcut_detail::page(id, move |s| subtitle(s), move |id| request_delete(id)),
    }
}

fn title_of(page: Page) -> String {
    match page {
        Page::Main => "Claude".into(),
        Page::Artifacts => "Artifacts".into(),
        Page::Projects => "Projects".into(),
        Page::Search => "Search Chats".into(),
        Page::Shortcut(id) => shared().store.borrow().shortcut(id).map(|s| s.name.clone()).unwrap_or_default(),
    }
}

struct Window {
    window: adw::ApplicationWindow,
    list: gtk::ListBox,
    rows: RefCell<Vec<(gtk::ListBoxRow, Page)>>,
    selected: Cell<Page>,
    content: adw::NavigationPage,
    page_slot: gtk::Box,
    title: adw::WindowTitle,
    /// Set while the list is rebuilt, so the selection it puts back is not
    /// heard as a person choosing a page.
    quiet: Cell<bool>,
}

thread_local! {
    static WINDOW: RefCell<Option<Rc<Window>>> = const { RefCell::new(None) };
}

pub fn existing() -> Option<adw::ApplicationWindow> {
    WINDOW.with(|w| w.borrow().as_ref().map(|w| w.window.clone()))
}

pub fn build(app: &adw::Application) -> adw::ApplicationWindow {
    if let Some(window) = existing() {
        return window;
    }
    let window = adw::ApplicationWindow::new(app);
    window.set_title(Some("Claude Graft"));
    window.set_default_size(820, 560);
    // Every page is laid out for this, so the window goes no smaller. The
    // header bars are inside the window's content here, so the limit is the
    // window's whole size, not the size less whatever a titlebar takes.
    window.set_size_request(820, 560);

    let split = adw::NavigationSplitView::new();
    split.set_min_sidebar_width(200.0);
    split.set_max_sidebar_width(280.0);

    // The sidebar.
    let sidebar_view = adw::ToolbarView::new();
    let sidebar_header = adw::HeaderBar::new();
    sidebar_header.set_show_end_title_buttons(false);
    let add = ui::icon_button("list-add-symbolic", "window.new-shortcut", "New Shortcut");
    add.set_tooltip_text(Some("New shortcut (Ctrl+N)"));
    sidebar_header.pack_end(&add);
    sidebar_header.pack_start(&menu_button());
    sidebar_view.add_top_bar(&sidebar_header);
    let list = gtk::ListBox::new();
    list.add_css_class("navigation-sidebar");
    let scroller = gtk::ScrolledWindow::builder().hscrollbar_policy(gtk::PolicyType::Never).child(&list).vexpand(true).build();
    sidebar_view.set_content(Some(&scroller));
    sidebar_view.add_bottom_bar(&footer());
    let sidebar = adw::NavigationPage::new(&sidebar_view, "Claude Graft");
    split.set_sidebar(Some(&sidebar));

    // The content column: the notice, then the page.
    let content_view = adw::ToolbarView::new();
    let header = adw::HeaderBar::new();
    let title = adw::WindowTitle::new("Claude", "");
    header.set_title_widget(Some(&title));
    content_view.add_top_bar(&header);
    let column = gtk::Box::new(gtk::Orientation::Vertical, 0);
    column.append(&tray_notice());
    let page_slot = gtk::Box::new(gtk::Orientation::Vertical, 0);
    page_slot.set_vexpand(true);
    column.append(&page_slot);
    content_view.set_content(Some(&column));
    let content = adw::NavigationPage::new(&content_view, "Claude");
    split.set_content(Some(&content));
    window.set_content(Some(&split));

    let state = Rc::new(Window {
        window: window.clone(),
        list: list.clone(),
        rows: RefCell::new(Vec::new()),
        selected: Cell::new(Page::Main),
        content,
        page_slot,
        title,
        quiet: Cell::new(false),
    });
    WINDOW.with(|w| *w.borrow_mut() = Some(state.clone()));

    {
        let state = state.clone();
        list.connect_row_selected(move |_, row| {
            if state.quiet.get() {
                return;
            }
            let Some(row) = row else { return };
            let page = state.rows.borrow().iter().find(|(r, _)| r == row).map(|(_, p)| *p);
            if let Some(page) = page {
                show_page(&state, page);
            }
        });
    }
    list.connect_row_activated(|_, row| {
        if row.widget_name() == "window.sidebar.new-shortcut" {
            add_shortcut();
        }
    });
    add.connect_clicked(|_| add_shortcut());
    {
        let state = state.clone();
        let keys = gtk::EventControllerKey::new();
        keys.connect_key_pressed(move |_, key, _, _| {
            if key == gtk::gdk::Key::Delete {
                if let Page::Shortcut(id) = state.selected.get() {
                    request_deletion(id);
                    return gtk::glib::Propagation::Stop;
                }
            }
            gtk::glib::Propagation::Proceed
        });
        list.add_controller(keys);
    }
    let new_action = gio::SimpleAction::new("new-shortcut", None);
    new_action.connect_activate(|_, _| add_shortcut());
    window.add_action(&new_action);
    app.set_accels_for_action("win.new-shortcut", &["<Control>n"]);
    let settings_action = gio::SimpleAction::new("settings", None);
    settings_action.connect_activate(|_, _| crate::preferences::present());
    window.add_action(&settings_action);
    app.set_accels_for_action("win.settings", &["<Control>comma"]);
    for (name, url) in [
        ("help", links::site()),
        ("whats-new", links::releases()),
        ("report", links::new_issue()),
        ("linkedin", links::LINKEDIN.to_string()),
        ("source", links::source()),
        ("support", links::support()),
    ] {
        let action = gio::SimpleAction::new(name, None);
        let window_ref = window.clone();
        action.connect_activate(move |_, _| ui::open_url(&url, Some(window_ref.upcast_ref())));
        window.add_action(&action);
    }
    let mail = gio::SimpleAction::new("email", None);
    let window_ref = window.clone();
    mail.connect_activate(move |_, _| {
        let url = links::mail(installer::graft_version(), &links::system_name());
        ui::open_url(&url, Some(window_ref.upcast_ref()));
    });
    window.add_action(&mail);

    fill_list(&state);
    show_page(&state, Page::Main);
    {
        let state = state.clone();
        crate::shared::listen_forever(&[Topic::Store], move || {
            fill_list(&state);
            // A shortcut renamed renames its page.
            let selected = state.selected.get();
            state.content.set_title(&title_of(selected));
            state.title.set_title(&title_of(selected));
        });
    }
    window.connect_close_request(crate::app::close_requested);
    window.connect_destroy(|_| WINDOW.with(|w| *w.borrow_mut() = None));
    window
}

/// The Help menu's items, and Settings. GNOME puts both behind the window's
/// main menu; the tray's window keeps its own Support item, since this menu
/// is only on screen while the window is.
fn menu_button() -> gtk::MenuButton {
    let menu = gio::Menu::new();
    let first = gio::Menu::new();
    first.append(Some("Settings"), Some("win.settings"));
    menu.append_section(None, &first);
    let help = gio::Menu::new();
    help.append(Some("Claude Graft Help"), Some("win.help"));
    help.append(Some("What's New in Claude Graft"), Some("win.whats-new"));
    menu.append_section(None, &help);
    let contact = gio::Menu::new();
    contact.append(Some("Report a Problem..."), Some("win.report"));
    contact.append(Some("Email the Developer..."), Some("win.email"));
    contact.append(Some("Aaditya More on LinkedIn"), Some("win.linkedin"));
    menu.append_section(None, &contact);
    let source = gio::Menu::new();
    source.append(Some("View Source on GitHub"), Some("win.source"));
    source.append(Some("Support Claude Graft..."), Some("win.support"));
    menu.append_section(None, &source);
    let button = gtk::MenuButton::builder().icon_name("open-menu-symbolic").menu_model(&menu).build();
    identified(&button, "window.main-menu", "Main Menu");
    button
}

/// The foot of the sidebar: which Claude Desktop is installed, and where the
/// app came from and who pays for it, since there is no About panel worth
/// opening.
fn footer() -> gtk::Box {
    let column = gtk::Box::new(gtk::Orientation::Vertical, 0);
    column.append(&gtk::Separator::new(gtk::Orientation::Horizontal));
    let claude = crate::updates::status_line(ui::WINDOW, true);
    column.append(&claude);
    column.append(&gtk::Separator::new(gtk::Orientation::Horizontal));
    let links_row = gtk::Box::new(gtk::Orientation::Horizontal, 0);
    ui::padded(&links_row, 14, 8);
    let support = gtk::Button::new();
    support.set_child(Some(&adw::ButtonContent::builder().icon_name("emblem-favorite-symbolic").label("Support").build()));
    support.add_css_class("flat");
    support.set_tooltip_text(Some("Claude Graft is free. A tip on Ko-fi is what keeps it worked on."));
    identified(&support, "window.support", "Support Claude Graft");
    support.connect_clicked(|b| ui::open_url(&links::support(), Some(b.upcast_ref())));
    links_row.append(&support);
    let spacer = gtk::Box::new(gtk::Orientation::Horizontal, 0);
    spacer.set_hexpand(true);
    links_row.append(&spacer);
    let source = gtk::Button::new();
    source.set_child(Some(&adw::ButtonContent::builder().icon_name("text-x-script-symbolic").label("Source").build()));
    source.add_css_class("flat");
    source.set_tooltip_text(Some(&links::source()));
    identified(&source, "window.source", "View Source");
    source.connect_clicked(|b| ui::open_url(&links::source(), Some(b.upcast_ref())));
    links_row.append(&source);
    column.append(&links_row);
    column
}

/// Said inside the content column while no tray host can show the item:
/// closing the window then quits, and a person ought to know why it did.
fn tray_notice() -> adw::Banner {
    let banner = adw::Banner::new("No tray here shows Claude Graft's item, so closing this window quits it.");
    identified(&banner, "window.tray-notice", "No tray here shows Claude Graft's item, so closing this window quits it.");
    let update = {
        let banner = banner.clone();
        move || {
            let shared = shared();
            let wanted = shared.settings.borrow().show_in_tray;
            banner.set_revealed(wanted && !shared.reach.is_showing());
        }
    };
    update();
    listen(&banner, &[Topic::Reach, Topic::Settings], update);
    banner
}

fn fill_list(state: &Rc<Window>) {
    state.quiet.set(true);
    while let Some(child) = state.list.first_child() {
        state.list.remove(&child);
    }
    let (top, shortcuts) = {
        let shared = shared();
        let store = shared.store.borrow();
        rows(&store)
    };
    let mut made = Vec::new();
    state.list.append(&heading_row("Claude"));
    for row in &top {
        let widget = sidebar_row(row);
        state.list.append(&widget);
        made.push((widget, row.page));
    }
    state.list.append(&heading_row("Shortcuts"));
    for row in &shortcuts {
        let widget = sidebar_row(row);
        if let Page::Shortcut(id) = row.page {
            add_context_menu(&widget, id);
        }
        state.list.append(&widget);
        made.push((widget, row.page));
    }
    let new = gtk::ListBoxRow::new();
    new.set_selectable(false);
    new.set_child(Some(&adw::ButtonContent::builder().icon_name("list-add-symbolic").label("New Shortcut").build()));
    new.set_widget_name("window.sidebar.new-shortcut");
    new.update_property(&[gtk::accessible::Property::Label("New Shortcut")]);
    state.list.append(&new);
    let selected = state.selected.get();
    if let Some((row, _)) = made.iter().find(|(_, p)| *p == selected) {
        state.list.select_row(Some(row));
    }
    *state.rows.borrow_mut() = made;
    state.quiet.set(false);
}

fn heading_row(text: &str) -> gtk::ListBoxRow {
    let row = gtk::ListBoxRow::new();
    row.set_selectable(false);
    row.set_activatable(false);
    let label = ui::caption(text);
    label.add_css_class("heading");
    label.set_margin_top(8);
    label.set_accessible_role(gtk::AccessibleRole::Heading);
    row.set_child(Some(&label));
    row
}

fn sidebar_row(row: &Row) -> gtk::ListBoxRow {
    let line = gtk::Box::new(gtk::Orientation::Horizontal, 8);
    let icon = gtk::Image::from_icon_name(row.icon);
    icon.add_css_class("dim-label");
    icon.set_accessible_role(gtk::AccessibleRole::Presentation);
    line.append(&icon);
    let words = gtk::Box::new(gtk::Orientation::Vertical, 1);
    let title = gtk::Label::new(Some(&row.title));
    title.set_xalign(0.0);
    title.set_ellipsize(gtk::pango::EllipsizeMode::End);
    words.append(&title);
    words.append(&ui::caption(&row.subtitle));
    line.append(&words);
    let widget = gtk::ListBoxRow::new();
    widget.set_child(Some(&line));
    // Read as one thing: "Work, Reads chats from Claude".
    identified(&widget, &row.id, &row.title);
    widget.update_property(&[gtk::accessible::Property::Description(&row.subtitle)]);
    widget
}

fn add_context_menu(row: &gtk::ListBoxRow, id: Uuid) {
    let click = gtk::GestureClick::new();
    click.set_button(gtk::gdk::BUTTON_SECONDARY);
    let anchor = row.clone();
    click.connect_pressed(move |_, _, _, _| {
        let (popover, column) = graft_web::ui::menu_column();
        popover.set_parent(&anchor);
        graft_web::ui::menu_item(&column, &popover, "Delete Shortcut...", &format!("window.sidebar.{id}.delete"), move |_| request_deletion(id));
        popover.connect_closed(|p| p.unparent());
        popover.popup();
    });
    row.add_controller(click);
}

fn show_page(state: &Rc<Window>, page: Page) {
    state.selected.set(page);
    ui::clear(&state.page_slot);
    let title = state.title.clone();
    let subtitle: Rc<dyn Fn(&str)> = Rc::new(move |s: &str| title.set_subtitle(s));
    subtitle("");
    let widget = page_widget(page, subtitle, Rc::new(request_deletion));
    widget.set_vexpand(true);
    state.page_slot.append(&widget);
    let name = title_of(page);
    state.content.set_title(&name);
    state.title.set_title(&name);
}

pub fn select(page: Page) {
    WINDOW.with(|w| {
        if let Some(state) = w.borrow().as_ref() {
            show_page(state, page);
            fill_list(state);
        }
    });
}

/// A new Claude starts with chats of its own: borrowing another's is a
/// choice made afterwards on its page. It is given its colour here, once,
/// the first nothing wears, so no existing Claude changes under anybody.
pub fn add_shortcut() {
    let colours = shared().pool.borrow().colours_in_use();
    let shortcut = change_store(|store| store.add_new(&colours));
    select(Page::Shortcut(shortcut.id));
}

/// A shortcut that was never created holds nothing, so it just goes. Whether
/// it is a draft means looking for its entry among every launcher, which is
/// a walk of a folder, so it is asked off the main loop.
pub fn request_deletion(id: Uuid) {
    let Some(shortcut) = shared().store.borrow().shortcut(id).cloned() else { return };
    let list = shared().store.borrow().shortcuts().to_vec();
    let target = shortcut.clone();
    off_main(
        move || target.installed_name.is_none() && !target.profile_dir().exists() && installer::installed_entry(&target, &list).is_none(),
        move |draft| {
            if draft {
                change_store(|store| {
                    let rest: Vec<_> = store.shortcuts().iter().filter(|s| s.id != id).cloned().collect();
                    store.set_shortcuts(rest);
                });
                select(Page::Main);
                return;
            }
            let Some(window) = existing() else { return };
            let heading = format!("Delete \u{201c}{}\u{201d}?", shortcut.name);
            let body = format!(
                "The shortcut is removed from the app grid either way.\n\nIts profile folder, {}, holds that account's login and chat history. Deleting it cannot be undone.",
                shortcut.folder
            );
            let dialog = adw::AlertDialog::new(Some(&heading), Some(&body));
            dialog.add_response("cancel", "Cancel");
            dialog.add_response("shortcut", "Delete Shortcut Only");
            dialog.add_response("profile", "Delete Shortcut and Profile");
            dialog.set_response_appearance("profile", adw::ResponseAppearance::Destructive);
            dialog.set_default_response(Some("cancel"));
            dialog.set_close_response("cancel");
            dialog.connect_response(None, move |dialog, response| {
                let also = match response {
                    "shortcut" => false,
                    "profile" => true,
                    _ => return,
                };
                delete(id, also, dialog.upcast_ref());
            });
            dialog.present(Some(&window));
        },
    );
}

/// Off the main loop: removing a profile walks every file in it, and asking
/// whether it is open is a process listing. The list is read from disk on
/// the worker, which is where the window saves every change as it makes it.
fn delete(id: Uuid, also_profile: bool, _anchor: &gtk::Widget) {
    select(Page::Main);
    off_main(
        move || {
            let mut store = ShortcutStore::load();
            let problem = store.delete(id, also_profile, &ChatSharing);
            (problem, store.shortcuts().to_vec())
        },
        |(problem, list)| {
            change_store(|store| store.set_shortcuts(list));
            crate::sharing::pass();
            if let (Some(problem), Some(window)) = (problem, existing()) {
                ui::tell(&window, "The shortcut was deleted", &problem);
            }
        },
    );
}

#[cfg(test)]
mod tests {
    use super::*;
    use graft_core::testing::Sandbox;
    use graft_core::{IconPreset, Shortcut, Source};

    #[test]
    fn the_sidebar_lists_claude_the_shared_pages_and_then_every_shortcut() {
        let _sandbox = Sandbox::new();
        let mut store = ShortcutStore::default();
        let mut work = Shortcut::new("Work", Some("Claude-Work"), Source::Own, IconPreset::Blue);
        work.installed_name = Some("Work".into());
        let borrowing = Shortcut::new("Borrowing", Some("Claude-B"), Source::Main, IconPreset::Green);
        store.set_shortcuts(vec![work.clone(), borrowing.clone()]);
        let (top, shortcuts) = rows(&store);
        assert_eq!(top.iter().map(|r| r.page).collect::<Vec<_>>(), vec![Page::Main, Page::Artifacts, Page::Projects, Page::Search]);
        assert_eq!(shortcuts[0].subtitle, "Its own chats");
        assert_eq!(shortcuts[1].subtitle, "Not created yet", "a shortcut not yet created says so whatever it reads from");
        assert_eq!(shortcuts[0].id, "window.sidebar.Claude-Work", "a row is named by its folder, which reads the same in every language");
        assert_eq!(top[0].id, "window.sidebar.Claude");
    }
}
