//! Claude Desktop's version, at the top of the tray's window and the foot of
//! the window's sidebar.
//!
//! On a Mac Graft checks Claude's feed and installs its releases. Here Claude
//! is a package somebody installed, and its updates arrive the way that
//! package's do, so Graft says which version is installed and never installs
//! one. Graft's own updates are Flatpak's.

use crate::shared::{listen, shared, Topic};
use crate::ui::{self, identified};
use adw::prelude::*;

pub const NOTE: &str = "Claude Desktop is updated by the package that installed it, the way the rest of this system is, and Claude Graft by Flatpak. Graft only says which version is here.";

/// What the line says.
pub fn line(found: Option<bool>, version: Option<&str>) -> String {
    match (found, version) {
        (None, _) => "Claude Desktop".into(),
        (Some(false), _) => "Claude Desktop was not found.".into(),
        (Some(true), Some(version)) => format!("Claude Desktop {version}"),
        (Some(true), None) => "Claude Desktop is installed".into(),
    }
}

/// One line, its explanation behind the button beside it, redrawn when the
/// look at the installation comes back.
pub fn status_line(surface: &str, stacked: bool) -> gtk::Box {
    let row = gtk::Box::new(gtk::Orientation::Horizontal, 6);
    ui::padded(&row, 14, if stacked { 8 } else { 6 });
    let icon = gtk::Image::from_icon_name("application-x-executable-symbolic");
    icon.add_css_class("dim-label");
    icon.set_accessible_role(gtk::AccessibleRole::Presentation);
    row.append(&icon);
    let label = ui::caption("Claude Desktop");
    label.set_hexpand(true);
    row.append(&label);
    row.append(&ui::info_button(NOTE, "Claude Desktop", &format!("{surface}.claude-update.info")));
    let draw = {
        let label = label.clone();
        let surface = surface.to_string();
        move || {
            let shared = shared();
            let claude = shared.claude.borrow();
            let text = line(claude.found, claude.version.as_deref());
            label.set_text(&text);
            label.set_tooltip_text(Some(&text));
            if claude.found == Some(false) {
                label.remove_css_class("dim-label");
                label.add_css_class("warning");
            }
            identified(&label, &format!("{surface}.claude-update.status"), &text);
        }
    };
    draw();
    listen(&row, &[Topic::Claude], draw);
    row
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_line_says_which_claude_is_installed_and_offers_no_update() {
        assert_eq!(line(Some(true), Some("0.14.10")), "Claude Desktop 0.14.10");
        assert_eq!(line(Some(false), None), "Claude Desktop was not found.");
        assert_eq!(line(None, None), "Claude Desktop", "before the look comes back nothing is claimed");
    }
}
