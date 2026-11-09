//! The pages the main window embeds, and the pieces all three share.
//!
//! Every control is named and identified by hand, on the control itself:
//! `identified` gives it a widget name of `<surface>.<what>` that the
//! inspector and assistive tools can find it by whatever the language, and an
//! accessible label saying what it does and to which account, since six rows
//! each with a button reading "Open" say nothing. A page fits the 820 by 560
//! window without scrolling: a note is one line, its paragraph behind the
//! button beside it, and only the list itself scrolls.

pub mod artifacts;
pub mod groups;
pub mod projects;
pub mod search;

use crate::center::{AccountState, ArtifactCenter, Change};
use crate::model::{short, ArtifactAccount};
use crate::signin::SIGN_IN_HINT;
use gtk::glib;
use gtk::prelude::*;
use std::cell::{Cell, RefCell};
use std::path::Path;
use std::rc::Rc;
use std::time::{SystemTime, UNIX_EPOCH};

pub use artifacts::artifacts_page;
pub use projects::projects_page;
pub use search::search_page;

/// Names a control for assistive tools and identifies it for anything that
/// drives the window from outside.
pub fn identified(widget: &impl IsA<gtk::Widget>, id: &str, label: &str) {
    widget.set_widget_name(id);
    widget.upcast_ref::<gtk::Widget>().update_property(&[gtk::accessible::Property::Label(label)]);
}

/// What a control does, said to a screen reader and as its tooltip alike.
pub fn described(widget: &impl IsA<gtk::Widget>, help: &str) {
    widget.set_tooltip_text(Some(help));
    widget.upcast_ref::<gtk::Widget>().update_property(&[gtk::accessible::Property::Description(help)]);
}

thread_local! {
    static STYLED: Cell<bool> = const { Cell::new(false) };
}

/// The few looks the theme does not have: the account's dot and the bars at
/// the head and foot of a page.
pub fn ensure_style() {
    if STYLED.with(Cell::get) {
        return;
    }
    STYLED.with(|s| s.set(true));
    let Some(display) = gtk::gdk::Display::default() else { return };
    let provider = gtk::CssProvider::new();
    provider.load_from_string(
        ".graft-dot { min-width: 7px; min-height: 7px; border-radius: 4px; background-color: alpha(currentColor, 0.4); }
         .graft-dot.ready { background-color: @success_color; }
         .graft-dot.trouble { background-color: @warning_color; }
         .graft-foot { padding: 6px 16px; }
         .graft-head { padding: 8px 16px; }",
    );
    gtk::style_context_add_provider_for_display(&display, &provider, gtk::STYLE_PROVIDER_PRIORITY_APPLICATION);
}

/// A button showing a note's paragraph, beside the one line that stands for it.
pub fn info_button(note: &str, about: &str, id: &str) -> gtk::MenuButton {
    let text = gtk::Label::new(Some(note));
    text.set_wrap(true);
    text.set_max_width_chars(60);
    text.set_xalign(0.0);
    text.set_margin_top(8);
    text.set_margin_bottom(8);
    text.set_margin_start(8);
    text.set_margin_end(8);
    let popover = gtk::Popover::new();
    popover.set_child(Some(&text));
    let button = gtk::MenuButton::builder().icon_name("help-about-symbolic").popover(&popover).build();
    button.add_css_class("flat");
    identified(&button, id, &format!("About {about}"));
    described(&button, &format!("About {about}"));
    button
}

/// One line, and the rest behind its button.
pub fn one_line_note(text: &str, more: &str, topic: &str, id: &str) -> gtk::Box {
    let row = gtk::Box::new(gtk::Orientation::Horizontal, 4);
    let label = gtk::Label::new(Some(text));
    label.set_ellipsize(gtk::pango::EllipsizeMode::End);
    label.set_single_line_mode(true);
    identified(&label, id, text);
    row.append(&label);
    row.append(&info_button(more, topic, &format!("{id}-info")));
    row.set_halign(gtk::Align::Center);
    row
}

/// A time as the rows show it: the time alone today, the date as well before.
pub fn clock(time: SystemTime) -> String {
    let seconds = time.duration_since(UNIX_EPOCH).map(|d| d.as_secs() as i64).unwrap_or(0);
    let (Ok(date), Ok(now)) = (glib::DateTime::from_unix_local(seconds), glib::DateTime::now_local()) else {
        return String::new();
    };
    let same_day = date.ymd() == now.ymd();
    let format = if same_day { "%H:%M" } else { "%b %-d, %Y, %H:%M" };
    date.format(format).map(|s| s.to_string()).unwrap_or_default()
}

pub fn window_of(widget: &impl IsA<gtk::Widget>) -> Option<gtk::Window> {
    widget.root().and_then(|r| r.downcast::<gtk::Window>().ok())
}

/// In the browser, through the desktop's portal inside the sandbox.
pub fn open_url(url: &str, widget: &impl IsA<gtk::Widget>) {
    let launcher = gtk::UriLauncher::new(url);
    launcher.launch(window_of(widget).as_ref(), None::<&gtk::gio::Cancellable>, |_| {});
}

/// The transcript of a session claude.ai no longer has, picked out in the
/// file manager.
pub fn show_in_files(path: &Path, widget: &impl IsA<gtk::Widget>) {
    let launcher = gtk::FileLauncher::new(Some(&gtk::gio::File::for_path(path)));
    launcher.open_containing_folder(window_of(widget).as_ref(), None::<&gtk::gio::Cancellable>, |_| {});
}

/// Watches a center for as long as the widget lives, and stops when it goes.
pub fn watch(center: &Rc<ArtifactCenter>, widget: &impl IsA<gtk::Widget>, f: impl Fn(Change) + 'static) {
    let id = center.subscribe(f);
    let weak = Rc::downgrade(center);
    widget.connect_destroy(move |_| {
        if let Some(center) = weak.upgrade() {
            center.unsubscribe(id);
        }
    });
}

pub fn clear(container: &gtk::Box) {
    while let Some(child) = container.first_child() {
        container.remove(&child);
    }
}

/// One signed-in account, with its way to sign in again or out. The same chip
/// heads Artifacts, Projects and Search Chats, since the sign-in behind it is
/// one sign-in for all three.
///
/// The dot stands beside the menu rather than inside its button, so it is
/// never dropped with a label a theme draws as text alone.
pub fn account_chip(center: &Rc<ArtifactCenter>, account: &ArtifactAccount, scope: &str) -> gtk::Box {
    let state = center.state_of(account.id);
    let name = center.name_of(account.id);
    let chip = gtk::Box::new(gtk::Orientation::Horizontal, 4);
    let dot = gtk::Box::new(gtk::Orientation::Horizontal, 0);
    dot.add_css_class("graft-dot");
    match state {
        AccountState::Ready => dot.add_css_class("ready"),
        AccountState::SignedOut | AccountState::Failed => dot.add_css_class("trouble"),
        _ => {}
    }
    dot.set_valign(gtk::Align::Center);
    dot.update_property(&[gtk::accessible::Property::Label(state.help())]);
    dot.set_accessible_role(gtk::AccessibleRole::Presentation);
    chip.append(&dot);

    let menu = gtk::Box::new(gtk::Orientation::Vertical, 2);
    if let Some(email) = account.email.as_ref().filter(|e| **e != name) {
        let label = gtk::Label::new(Some(email));
        label.add_css_class("dim-label");
        label.set_margin_bottom(4);
        menu.append(&label);
    }
    let popover = gtk::Popover::new();
    let again = gtk::Button::with_label("Sign In Again...");
    again.add_css_class("flat");
    identified(&again, &format!("{scope}.account.{}.sign-in-again", short(account.id)), &format!("Sign In to {name} Again"));
    let out = gtk::Button::with_label("Sign Out");
    out.add_css_class("flat");
    out.add_css_class("destructive-action");
    identified(&out, &format!("{scope}.account.{}.sign-out", short(account.id)), &format!("Sign Out of {name}"));
    menu.append(&again);
    menu.append(&gtk::Separator::new(gtk::Orientation::Horizontal));
    menu.append(&out);
    popover.set_child(Some(&menu));

    let button = gtk::MenuButton::builder().label(&name).popover(&popover).build();
    button.add_css_class("flat");
    identified(&button, &format!("{scope}.account.{}", short(account.id)), &name);
    described(&button, state.help());
    chip.append(&button);

    let id = account.id;
    let weak = Rc::downgrade(center);
    let pop = popover.clone();
    again.connect_clicked(move |button| {
        pop.popdown();
        if let Some(center) = weak.upgrade() {
            center.sign_in_again(id, window_of(button).as_ref());
        }
    });
    let weak = Rc::downgrade(center);
    let pop = popover.clone();
    out.connect_clicked(move |_| {
        pop.popdown();
        if let Some(center) = weak.upgrade() {
            center.sign_out(id);
        }
    });
    chip
}

/// The heading row of a page that asks claude.ai: what it is, who is signed
/// in, and the way to sign in another.
pub struct Header {
    pub root: gtk::Box,
    pub refresh: Option<gtk::Button>,
    refresh_spinner: Option<gtk::Spinner>,
    chips: gtk::Box,
    add: gtk::Button,
    scope: String,
}

pub struct Refresh {
    pub label: &'static str,
    pub help: &'static str,
    pub run: Box<dyn Fn()>,
}

impl Header {
    pub fn new(center: &Rc<ArtifactCenter>, title: &str, icon: &str, note: &str, id: &str, refresh: Option<Refresh>) -> Rc<Header> {
        ensure_style();
        let root = gtk::Box::new(gtk::Orientation::Horizontal, 8);
        root.add_css_class("graft-head");
        let image = gtk::Image::from_icon_name(icon);
        image.add_css_class("dim-label");
        image.set_accessible_role(gtk::AccessibleRole::Presentation);
        root.append(&image);
        let heading = gtk::Label::new(Some(title));
        heading.add_css_class("heading");
        root.append(&heading);
        root.append(&info_button(note, title, &format!("{id}-info")));
        let chips = gtk::Box::new(gtk::Orientation::Horizontal, 6);
        let scroller = gtk::ScrolledWindow::builder()
            .hscrollbar_policy(gtk::PolicyType::External)
            .vscrollbar_policy(gtk::PolicyType::Never)
            .child(&chips)
            .hexpand(true)
            .propagate_natural_width(false)
            .build();
        root.append(&scroller);
        let add = gtk::Button::new();
        let add_content = adw::ButtonContent::builder().icon_name("list-add-symbolic").label("Sign In...").build();
        add.set_child(Some(&add_content));
        identified(&add, &format!("{id}.add-account"), "Sign In to Another claude.ai Account");
        described(&add, "Sign in to another claude.ai account");
        root.append(&add);
        let weak = Rc::downgrade(center);
        add.connect_clicked(move |button| {
            if let Some(center) = weak.upgrade() {
                center.sign_in_new_account(window_of(button).as_ref());
            }
        });
        let (refresh_button, refresh_spinner) = match refresh {
            Some(refresh) => {
                let stack = gtk::Stack::new();
                let icon = gtk::Image::from_icon_name("view-refresh-symbolic");
                let spinner = gtk::Spinner::new();
                stack.add_named(&icon, Some("icon"));
                stack.add_named(&spinner, Some("busy"));
                let button = gtk::Button::builder().child(&stack).build();
                button.add_css_class("flat");
                identified(&button, &format!("{id}.refresh"), refresh.label);
                described(&button, refresh.help);
                button.connect_clicked(move |_| (refresh.run)());
                root.append(&button);
                (Some(button), Some(spinner))
            }
            None => (None, None),
        };
        let header = Rc::new(Header {
            root,
            refresh: refresh_button,
            refresh_spinner,
            chips,
            add,
            scope: id.to_string(),
        });
        header.update(center, false);
        header
    }

    /// Redrawn whenever the accounts or what is known of them change.
    pub fn update(&self, center: &Rc<ArtifactCenter>, busy: bool) {
        clear(&self.chips);
        let accounts = center.accounts();
        for account in &accounts {
            self.chips.append(&account_chip(center, account, &self.scope));
        }
        if let Some(content) = self.add.child().and_downcast::<adw::ButtonContent>() {
            content.set_label(if accounts.is_empty() { "Sign In..." } else { "Add Account..." });
        }
        if let (Some(button), Some(spinner)) = (&self.refresh, &self.refresh_spinner) {
            button.set_sensitive(!accounts.is_empty() && !busy);
            spinner.set_spinning(busy);
            if let Some(stack) = button.child().and_downcast::<gtk::Stack>() {
                stack.set_visible_child_name(if busy { "busy" } else { "icon" });
            }
        }
    }
}

/// What a page shows with no account signed in.
pub fn sign_in_prompt(center: &Rc<ArtifactCenter>, text: &str, icon: &str, note: &str, topic: &str, id: &str) -> gtk::Box {
    let column = gtk::Box::new(gtk::Orientation::Vertical, 10);
    column.set_valign(gtk::Align::Center);
    column.set_halign(gtk::Align::Center);
    column.set_vexpand(true);
    let image = gtk::Image::from_icon_name(icon);
    image.set_pixel_size(34);
    image.add_css_class("dim-label");
    image.set_accessible_role(gtk::AccessibleRole::Presentation);
    column.append(&image);
    column.append(&one_line_note(text, note, topic, &format!("{id}.sign-in-note")));
    let button = gtk::Button::with_label("Sign In...");
    button.set_halign(gtk::Align::Center);
    button.add_css_class("pill");
    identified(&button, &format!("{id}.sign-in"), "Sign In to claude.ai");
    let weak = Rc::downgrade(center);
    button.connect_clicked(move |button| {
        if let Some(center) = weak.upgrade() {
            center.sign_in_new_account(window_of(button).as_ref());
        }
    });
    column.append(&button);
    let hint = gtk::Label::new(Some(SIGN_IN_HINT));
    hint.add_css_class("caption");
    hint.add_css_class("dim-label");
    hint.set_ellipsize(gtk::pango::EllipsizeMode::End);
    identified(&hint, &format!("{id}.sign-in-hint"), SIGN_IN_HINT);
    column.append(&hint);
    column
}

/// One line at the foot: what was found, or which account would not answer.
pub struct Foot {
    pub root: gtk::Box,
    label: gtk::Label,
    extra: RefCell<Option<gtk::Widget>>,
}

impl Foot {
    pub fn new(id: &str) -> Rc<Foot> {
        ensure_style();
        let root = gtk::Box::new(gtk::Orientation::Vertical, 0);
        root.append(&gtk::Separator::new(gtk::Orientation::Horizontal));
        let row = gtk::Box::new(gtk::Orientation::Horizontal, 8);
        row.add_css_class("graft-foot");
        let label = gtk::Label::new(None);
        label.add_css_class("caption");
        label.add_css_class("dim-label");
        label.set_ellipsize(gtk::pango::EllipsizeMode::End);
        label.set_xalign(0.0);
        label.set_hexpand(true);
        identified(&label, id, "Status");
        row.append(&label);
        root.append(&row);
        Rc::new(Foot { root, label, extra: RefCell::new(None) })
    }

    /// A control at the far end of the bar, which keeps the bar shown.
    pub fn set_end(&self, widget: &impl IsA<gtk::Widget>) {
        if let Some(row) = self.label.parent().and_downcast::<gtk::Box>() {
            row.append(widget);
        }
        *self.extra.borrow_mut() = Some(widget.clone().upcast());
        self.root.set_visible(true);
    }

    pub fn set(&self, text: Option<&str>) {
        let text = text.unwrap_or("");
        self.label.set_text(text);
        self.label.set_tooltip_text(if text.is_empty() { None } else { Some(text) });
        self.label.update_property(&[gtk::accessible::Property::Label(if text.is_empty() { "Status" } else { text })]);
        self.root.set_visible(!text.is_empty() || self.extra.borrow().is_some());
    }
}

/// A list that scrolls, or the one line saying why it is empty.
pub fn list_or_empty(list: &gtk::ListBox, empty: &gtk::Label, id: &str) -> gtk::Stack {
    list.add_css_class("boxed-list-separate");
    list.set_selection_mode(gtk::SelectionMode::None);
    let scroller = gtk::ScrolledWindow::builder()
        .hscrollbar_policy(gtk::PolicyType::Never)
        .child(list)
        .vexpand(true)
        .build();
    empty.add_css_class("dim-label");
    empty.set_vexpand(true);
    identified(empty, &format!("{id}.empty"), "Nothing to show");
    let stack = gtk::Stack::new();
    stack.add_named(&scroller, Some("list"));
    stack.add_named(empty, Some("empty"));
    stack.set_vexpand(true);
    stack
}

/// A row's title and the line under it, read out as one.
pub fn title_and_subtitle(title: &str, subtitle: &str, markup: bool, id: &str) -> gtk::Box {
    let column = gtk::Box::new(gtk::Orientation::Vertical, 1);
    column.set_hexpand(true);
    let heading = gtk::Label::new(None);
    if markup {
        heading.set_markup(title);
    } else {
        heading.set_text(title);
    }
    heading.set_xalign(0.0);
    heading.set_ellipsize(gtk::pango::EllipsizeMode::End);
    let under = gtk::Label::new(Some(subtitle));
    under.add_css_class("caption");
    under.add_css_class("dim-label");
    under.set_xalign(0.0);
    under.set_ellipsize(gtk::pango::EllipsizeMode::End);
    column.append(&heading);
    column.append(&under);
    let plain = if markup { gtk::pango::parse_markup(title, '\0').map(|(_, t, _)| t.to_string()).unwrap_or_default() } else { title.to_string() };
    column.set_accessible_role(gtk::AccessibleRole::Group);
    identified(&column, id, &plain);
    column.update_property(&[gtk::accessible::Property::Description(subtitle)]);
    column
}

pub fn icon_button(icon: &str, id: &str, label: &str, help: &str) -> gtk::Button {
    let button = gtk::Button::from_icon_name(icon);
    button.add_css_class("flat");
    identified(&button, id, label);
    described(&button, help);
    button
}

/// A menu button drawn as a picture, carrying its name itself, since a
/// picture alone is read out as whatever the picture is called.
pub fn icon_menu(icon: &str, id: &str, label: &str, help: &str, popover: &gtk::Popover) -> gtk::MenuButton {
    let button = gtk::MenuButton::builder().icon_name(icon).popover(popover).build();
    button.add_css_class("flat");
    identified(&button, id, label);
    described(&button, help);
    button
}

/// A popover menu of plain buttons: what the row's menu offers.
pub fn menu_column() -> (gtk::Popover, gtk::Box) {
    let column = gtk::Box::new(gtk::Orientation::Vertical, 2);
    column.set_margin_top(4);
    column.set_margin_bottom(4);
    let popover = gtk::Popover::new();
    popover.set_child(Some(&column));
    (popover, column)
}

pub fn menu_item(column: &gtk::Box, popover: &gtk::Popover, label: &str, id: &str, action: impl Fn(&gtk::Button) + 'static) -> gtk::Button {
    let button = gtk::Button::with_label(label);
    button.add_css_class("flat");
    if let Some(child) = button.child().and_downcast::<gtk::Label>() {
        child.set_xalign(0.0);
    }
    identified(&button, id, label.trim_end_matches("..."));
    let pop = popover.clone();
    button.connect_clicked(move |b| {
        pop.popdown();
        action(b);
    });
    column.append(&button);
    button
}
