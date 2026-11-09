//! Settings, and the same switches as the tray's window has them. The two can
//! be open at once, which is why each switch's name carries its surface.

use crate::settings;
use crate::shared::{change_settings, emit, listen, shared, Topic};
use crate::ui::{self, identified};
use adw::prelude::*;
use std::rc::Rc;

pub const FOOTER: &str = "The tray item keeps reporting usage after the window is closed. Quit it from the tray itself.";
pub const FRONT_HELP: &str = "The tray shows which Claude you're using, beside that account's usage.";

/// A switch as Settings draws it or as the tray's window does: a row with a
/// switch, or a check box.
pub enum Look {
    Row,
    Check,
}

struct Switch {
    widget: gtk::Widget,
}

fn switch(look: &Look, title: &str, id: &str, active: bool, on_change: impl Fn(bool) + 'static) -> Switch {
    match look {
        Look::Row => {
            let row = adw::SwitchRow::new();
            row.set_title(title);
            row.set_active(active);
            identified(&row, id, title);
            row.connect_active_notify(move |r| on_change(r.is_active()));
            Switch { widget: row.upcast() }
        }
        Look::Check => {
            let check = gtk::CheckButton::with_label(title);
            check.set_active(active);
            identified(&check, id, title);
            check.connect_toggled(move |c| on_change(c.is_active()));
            Switch { widget: check.upcast() }
        }
    }
}

/// Open at Login, Show in Tray and the two about the Claude in front, in
/// that order. `problem` hears why the desktop refused.
pub fn switches(look: Look, surface: &str, problem: Rc<dyn Fn(Option<String>)>) -> Vec<gtk::Widget> {
    let shared = shared();
    let current = shared.settings.borrow().clone();
    let login = switch(&look, "Open at Login", &ui::id(surface, None, "open-at-login"), settings::login_item_enabled(), move |on| {
        problem(settings::set_open_at_login(on));
        emit(Topic::Settings);
    });
    let tray = switch(&look, "Show in Tray", &ui::id(surface, None, "show-in-tray"), current.show_in_tray, |on| {
        change_settings(|s| s.show_in_tray = on);
    });
    let icon = switch(&look, "Show the icon of the Claude in front", &ui::id(surface, None, "front-icon"), current.show_front_icon, |on| {
        change_settings(|s| s.show_front_icon = on);
    });
    let name = switch(&look, "Show the name of the Claude in front", &ui::id(surface, None, "front-name"), current.show_front_name, |on| {
        change_settings(|s| s.show_front_name = on);
    });
    for front in [&icon.widget, &name.widget] {
        front.set_tooltip_text(Some(FRONT_HELP));
        front.set_sensitive(current.show_in_tray);
    }
    vec![login.widget, tray.widget, icon.widget, name.widget]
}

pub fn present() {
    let Some(window) = crate::window::existing() else { return };
    let dialog = adw::Dialog::new();
    dialog.set_title("Settings");
    dialog.set_content_width(420);
    let view = adw::ToolbarView::new();
    view.add_top_bar(&adw::HeaderBar::new());
    let column = gtk::Box::new(gtk::Orientation::Vertical, 8);
    ui::padded(&column, 16, 12);
    let group = adw::PreferencesGroup::new();
    let footer = ui::caption(FOOTER);
    footer.set_wrap(true);
    footer.set_single_line_mode(false);
    footer.set_ellipsize(gtk::pango::EllipsizeMode::None);
    let footer_ref = footer.clone();
    let problem: Rc<dyn Fn(Option<String>)> = Rc::new(move |p| footer_ref.set_text(p.as_deref().unwrap_or(FOOTER)));
    for row in switches(Look::Row, ui::SETTINGS, problem) {
        group.add(&row);
    }
    column.append(&group);
    column.append(&footer);
    view.set_content(Some(&column));
    dialog.set_child(Some(&view));
    // The front switches mean nothing with the item turned off.
    let group_ref = group.clone();
    listen(&group, &[Topic::Settings], move || {
        let on = shared().settings.borrow().show_in_tray;
        let mut child = group_ref.first_child();
        while let Some(widget) = child {
            set_front_sensitive(&widget, on);
            child = widget.next_sibling();
        }
    });
    dialog.present(Some(&window));
}

fn set_front_sensitive(widget: &gtk::Widget, on: bool) {
    let name = widget.widget_name();
    if name.ends_with(".front-icon") || name.ends_with(".front-name") {
        widget.set_sensitive(on);
    }
    let mut child = widget.first_child();
    while let Some(inner) = child {
        set_front_sensitive(&inner, on);
        child = inner.next_sibling();
    }
}
