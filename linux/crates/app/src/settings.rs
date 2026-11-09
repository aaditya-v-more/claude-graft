//! The handful of preferences the tray's window and Settings expose, and
//! opening at login, which on Linux is a file in the desktop's autostart
//! folder rather than state the system keeps.

use graft_core::{files, host, paths};
use serde::{Deserialize, Serialize};
use std::path::PathBuf;

pub const APP_ID: &str = "io.github.aaditya_v_more.ClaudeGraft";

/// Every field has its own default, so a key nobody has written reads as the
/// default while a later change to false survives a restart: the Mac needs a
/// seeded marker for that, a missing JSON key gives it for nothing.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Settings {
    #[serde(default = "yes")]
    pub show_in_tray: bool,
    /// On unless somebody turns it off. It changes the item's icon only while
    /// a Claude is in front, the moment somebody running several needs to
    /// know which one it is.
    #[serde(default = "yes")]
    pub show_front_icon: bool,
    /// Off unless somebody turns it on: the name is words in the item's title,
    /// which a host that shows titles has to find room for.
    #[serde(default)]
    pub show_front_name: bool,
    /// Whether the window was open when Graft last stopped, so the next start,
    /// one at login among them, comes back the way it was left.
    #[serde(default = "yes")]
    pub main_window_open: bool,
}

fn yes() -> bool {
    true
}

impl Default for Settings {
    fn default() -> Settings {
        Settings { show_in_tray: true, show_front_icon: true, show_front_name: false, main_window_open: true }
    }
}

pub fn settings_file() -> PathBuf {
    paths::graft_file("app-settings.json")
}

impl Settings {
    pub fn load() -> Settings {
        match files::read_json(&settings_file()) {
            files::JsonRead::Value(value) => serde_json::from_value(value).unwrap_or_default(),
            _ => Settings::default(),
        }
    }

    pub fn save(&self) {
        if let Ok(value) = serde_json::to_value(self) {
            let _ = files::write_json(&settings_file(), &value);
        }
    }
}

/// Where the desktop looks for what to start at login. Built from `$HOME`
/// like every host path, since inside the Flatpak `XDG_CONFIG_HOME` is the
/// sandbox's own folder, which no session manager reads.
pub fn autostart_file() -> PathBuf {
    paths::config_home().join("autostart").join(format!("{APP_ID}.desktop"))
}

/// What the autostart entry runs: the Flatpak by its id, or this binary where
/// it is when Graft is run outside one.
pub fn autostart_exec() -> String {
    if host::in_flatpak() {
        let id = host::flatpak_id().unwrap_or_else(|| APP_ID.to_string());
        format!("flatpak run {id}")
    } else {
        std::env::current_exe().map(|p| paths::text(&p)).unwrap_or_else(|_| "graft".into())
    }
}

pub fn autostart_entry(exec: &str) -> String {
    format!(
        "[Desktop Entry]\nType=Application\nName=Claude Graft\nComment=Keeps reporting Claude usage from the tray\nExec={exec}\nIcon={APP_ID}\nX-GNOME-Autostart-enabled=true\nX-Claude-Graft-Autostart=true\n"
    )
}

/// What the file says, not a value of Graft's own: a person can turn an
/// autostart entry off in their desktop's settings, which leaves the file
/// with `Hidden` or the GNOME key set.
pub fn login_item_enabled() -> bool {
    let Ok(text) = std::fs::read_to_string(autostart_file()) else { return false };
    let off = text.lines().map(str::trim).any(|l| {
        l.eq_ignore_ascii_case("Hidden=true") || l.eq_ignore_ascii_case("X-GNOME-Autostart-enabled=false")
    });
    !off
}

/// Returns a message when the desktop's folder refused, so the caller can say
/// why.
pub fn set_open_at_login(wanted: bool) -> Option<String> {
    let file = autostart_file();
    if wanted {
        if let Some(folder) = file.parent() {
            if let Err(e) = std::fs::create_dir_all(folder) {
                return Some(format!("The autostart folder could not be made: {e}"));
            }
        }
        files::atomic_write(&file, autostart_entry(&autostart_exec()).as_bytes(), 0o644)
            .err()
            .map(|e| format!("Opening at login could not be turned on: {e}"))
    } else {
        match std::fs::remove_file(&file) {
            Ok(()) => None,
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => None,
            Err(e) => Some(format!("Opening at login could not be turned off: {e}")),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use graft_core::testing::Sandbox;

    #[test]
    fn a_settings_file_nobody_wrote_reads_as_the_defaults() {
        let _sandbox = Sandbox::new();
        let settings = Settings::load();
        assert!(settings.show_in_tray, "the tray item is on for everybody until turned off");
        assert!(settings.show_front_icon, "and so is the icon of the Claude in front");
        assert!(!settings.show_front_name, "the name is off until somebody asks for it");
        assert!(settings.main_window_open, "and a first start puts the window up");
    }

    #[test]
    fn a_switch_turned_off_stays_off_across_a_restart() {
        let _sandbox = Sandbox::new();
        let mut settings = Settings::load();
        settings.show_in_tray = false;
        settings.main_window_open = false;
        settings.save();
        let again = Settings::load();
        assert!(!again.show_in_tray && !again.main_window_open, "a false written down is read back as false");
        std::fs::write(settings_file(), "{\"showFrontName\": true}").unwrap();
        let older = Settings::load();
        assert!(older.show_front_name && older.show_in_tray, "a file missing newer keys gives them their defaults");
    }

    #[test]
    fn opening_at_login_is_the_autostart_entry_and_its_switch() {
        let _sandbox = Sandbox::new();
        assert!(!login_item_enabled(), "with no entry Graft does not open at login");
        assert_eq!(set_open_at_login(true), None);
        assert!(login_item_enabled(), "turning it on writes an entry the session starts");
        let text = std::fs::read_to_string(autostart_file()).unwrap();
        assert!(text.contains("Exec=") && text.contains("Type=Application"), "and the entry is one a desktop can run");
        std::fs::write(autostart_file(), text.replace("X-GNOME-Autostart-enabled=true", "X-GNOME-Autostart-enabled=false")).unwrap();
        assert!(!login_item_enabled(), "an entry the desktop's own settings turned off reads as off");
        assert_eq!(set_open_at_login(false), None);
        assert!(!autostart_file().exists(), "turning it off takes the entry away");
        assert_eq!(set_open_at_login(false), None, "and turning off what is already off is no failure");
    }
}
