//! The pieces every surface of the window, the tray's window and Settings
//! share.
//!
//! Every control is named and identified by hand, on the control itself:
//! `identified` gives it a widget name of `<surface>.<profile>.<control>`,
//! the surface because the tray's window and Settings carry the same switches
//! and can be open at once, the profile's folder because no two profiles
//! share one and it reads the same in every language, and an accessible label
//! saying what it does and to which account.

use gtk::glib;
use adw::prelude::*;
use std::cell::Cell;

pub use graft_web::ui::{described, info_button};

pub const WINDOW: &str = "window";
pub const DROPDOWN: &str = "dropdown";
pub const SETTINGS: &str = "settings";

/// `<surface>.<profile>.<control>`, the middle part only for the controls
/// that act on one profile.
pub fn id(surface: &str, profile: Option<&str>, control: &str) -> String {
    [Some(surface), profile, Some(control)].into_iter().flatten().filter(|s| !s.is_empty()).collect::<Vec<_>>().join(".")
}

/// Names a control for assistive tools and identifies it for anything that
/// drives the window from outside. Goes on the control itself: on a box it
/// would name every control inside, and one name for several names none.
pub fn identified(widget: &impl IsA<gtk::Widget>, id: &str, label: &str) {
    graft_web::ui::identified(widget, id, label);
}

/// Work that waits (a process listing, a sweep, a keyring read, an open),
/// run off the main loop, with what it found handed back to the main loop.
/// The answer may touch the window; the work may not.
pub fn off_main<T: Send + 'static>(work: impl FnOnce() -> T + Send + 'static, then: impl FnOnce(T) + 'static) {
    glib::MainContext::default().spawn_local(async move {
        if let Ok(value) = gtk::gio::spawn_blocking(work).await {
            then(value);
        }
    });
}

/// Something another thread found, handed to the main loop.
pub fn on_main(work: impl FnOnce() + Send + 'static) {
    glib::MainContext::default().invoke(work);
}

/// A label, small and dim, held to one line.
pub fn caption(text: &str) -> gtk::Label {
    let label = gtk::Label::new(Some(text));
    label.add_css_class("caption");
    label.add_css_class("dim-label");
    label.set_xalign(0.0);
    label.set_ellipsize(gtk::pango::EllipsizeMode::End);
    label.set_single_line_mode(true);
    label
}

/// A button whose picture says what it does, named in words.
pub fn icon_button(icon: &str, id: &str, label: &str) -> gtk::Button {
    let button = gtk::Button::from_icon_name(icon);
    button.add_css_class("flat");
    identified(&button, id, label);
    described(&button, label);
    button
}

/// A button that shows a spinner in place of its words while its work runs,
/// keeping its width so nothing beside it moves.
pub struct BusyButton {
    pub button: gtk::Button,
    stack: gtk::Stack,
    spinner: adw::Spinner,
    label: gtk::Label,
}

impl BusyButton {
    pub fn new(text: &str, id: &str, accessible: &str) -> BusyButton {
        let label = gtk::Label::new(Some(text));
        let spinner = adw::Spinner::new();
        let stack = gtk::Stack::new();
        stack.set_hhomogeneous(true);
        stack.add_named(&label, Some("text"));
        stack.add_named(&spinner, Some("busy"));
        let button = gtk::Button::new();
        button.set_child(Some(&stack));
        identified(&button, id, accessible);
        BusyButton { button, stack, spinner, label }
    }

    pub fn set_busy(&self, busy: bool) {
        self.stack.set_visible_child_name(if busy { "busy" } else { "text" });
        self.spinner.set_visible(busy);
        self.button.set_sensitive(!busy);
    }

    pub fn set_text(&self, text: &str) {
        self.label.set_text(text);
        self.button.update_property(&[gtk::accessible::Property::Label(text)]);
    }
}

/// A section heading with its explanation behind the button beside it, and
/// on the right whatever belongs to the whole section, so that it takes no
/// row of its own.
pub fn section_header(title: &str, info: &str, id: &str, trailing: Option<&gtk::Widget>) -> gtk::Box {
    let row = gtk::Box::new(gtk::Orientation::Horizontal, 5);
    let heading = gtk::Label::new(Some(title));
    heading.add_css_class("heading");
    heading.set_xalign(0.0);
    row.append(&heading);
    row.append(&info_button(info, title, id));
    let spacer = gtk::Box::new(gtk::Orientation::Horizontal, 0);
    spacer.set_hexpand(true);
    row.append(&spacer);
    if let Some(trailing) = trailing {
        row.append(trailing);
    }
    row
}

/// A note held to one line, with anything longer behind the button beside
/// it, so a page built from notes still fits without scrolling.
pub fn note(text: &str, more: Option<&str>, topic: &str, attention: bool, id: &str) -> gtk::Box {
    let row = gtk::Box::new(gtk::Orientation::Horizontal, 4);
    let label = caption(text);
    if attention {
        label.remove_css_class("dim-label");
        label.add_css_class("warning");
    }
    label.set_tooltip_text(Some(more.unwrap_or(text)));
    identified(&label, id, text);
    row.append(&label);
    if let Some(more) = more {
        row.append(&info_button(more, topic, &format!("{id}-info")));
    }
    row
}

/// A card of rows, the grouped form's look without a list to scroll.
pub fn card() -> gtk::Box {
    let card = gtk::Box::new(gtk::Orientation::Vertical, 6);
    card.add_css_class("card");
    card
}

/// A row inside a card, padded the way a grouped form pads its rows.
pub fn card_row(spacing: i32) -> gtk::Box {
    let row = gtk::Box::new(gtk::Orientation::Horizontal, spacing);
    padded(&row, 12, 6);
    row
}

pub fn padded(widget: &impl IsA<gtk::Widget>, horizontal: i32, vertical: i32) {
    widget.set_margin_start(horizontal);
    widget.set_margin_end(horizontal);
    widget.set_margin_top(vertical);
    widget.set_margin_bottom(vertical);
}

pub fn clear(container: &gtk::Box) {
    while let Some(child) = container.first_child() {
        container.remove(&child);
    }
}

pub fn window_of(widget: &impl IsA<gtk::Widget>) -> Option<gtk::Window> {
    widget.root().and_then(|r| r.downcast::<gtk::Window>().ok())
}

/// In the browser, or the mail program, through the desktop's portal inside
/// the sandbox.
pub fn open_url(url: &str, widget: Option<&gtk::Widget>) {
    let launcher = gtk::UriLauncher::new(url);
    let parent = widget.and_then(window_of);
    launcher.launch(parent.as_ref(), None::<&gtk::gio::Cancellable>, |_| {});
}

pub fn show_in_files(path: &std::path::Path, widget: &impl IsA<gtk::Widget>) {
    let launcher = gtk::FileLauncher::new(Some(&gtk::gio::File::for_path(path)));
    launcher.open_containing_folder(window_of(widget).as_ref(), None::<&gtk::gio::Cancellable>, |_| {});
}

/// A plain message with one button, for something a press could not do.
pub fn tell(parent: &impl IsA<gtk::Widget>, heading: &str, body: &str) {
    let dialog = adw::AlertDialog::new(Some(heading), Some(body));
    dialog.add_response("ok", "OK");
    dialog.set_default_response(Some("ok"));
    dialog.set_close_response("ok");
    dialog.present(Some(parent));
}

/// Runs `tick` every `every` while `widget` lives, and stops with it, so a
/// page's clock never outlives the page.
pub fn every(widget: &impl IsA<gtk::Widget>, every: std::time::Duration, tick: impl Fn() + 'static) {
    let weak = widget.downgrade();
    glib::timeout_add_local(every, move || {
        match weak.upgrade() {
            Some(w) if w.is_mapped() || w.parent().is_some() => {
                if w.is_mapped() {
                    tick();
                }
                glib::ControlFlow::Continue
            }
            _ => glib::ControlFlow::Break,
        }
    });
}

thread_local! {
    static STYLED: Cell<bool> = const { Cell::new(false) };
}

/// The looks the theme does not have: the usage bars' colours and the dot
/// beside a Claude that is open.
pub fn ensure_style() {
    graft_web::ui::ensure_style();
    if STYLED.with(Cell::get) {
        return;
    }
    STYLED.with(|s| s.set(true));
    let Some(display) = gtk::gdk::Display::default() else { return };
    let provider = gtk::CssProvider::new();
    provider.load_from_string(
        "progressbar.graft-bar trough, progressbar.graft-bar progress { min-height: 5px; border-radius: 3px; }
         progressbar.graft-ok progress { background-color: @success_color; }
         progressbar.graft-high progress { background-color: @warning_color; }
         progressbar.graft-full progress { background-color: @error_color; }
         .graft-dimmed { opacity: 0.45; }
         .graft-open-dot { min-width: 6px; min-height: 6px; border-radius: 3px; background-color: alpha(currentColor, 0.35); }
         .graft-open-dot.open { background-color: @success_color; }
         .graft-bar-row { padding: 6px 12px; }
         .warning { color: @warning_color; }",
    );
    gtk::style_context_add_provider_for_display(&display, &provider, gtk::STYLE_PROVIDER_PRIORITY_APPLICATION);
}
