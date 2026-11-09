//! When quitting really quits, when closing the window may leave Graft
//! running with no window, and whether the tray item is somewhere a person
//! can reach it. Kept apart from GTK so the suite can drive the rules.

use std::time::Duration;

/// Whether the tray item can actually be clicked.
///
/// An item that was registered is not an item anybody can see. A desktop with
/// no `StatusNotifierWatcher` has nowhere to put one, and a watcher with no
/// host registered (GNOME without the AppIndicator extension, a panel whose
/// tray applet was removed) takes the item and shows it nowhere. Asked rather
/// than the setting, for the same reason the Mac asks where its item was
/// placed: the setting says what was wanted, not whether there was room.
pub struct TrayPlacement;

impl TrayPlacement {
    pub fn is_reachable(item_registered: bool, watcher_present: bool, host_registered: bool) -> bool {
        item_registered && watcher_present && host_registered
    }
}

/// Whether a request to quit really ends Graft.
///
/// Ctrl+Q is the reflex for putting a window away, and it used to take the
/// item with it: the part that does the reporting, and the part nobody asked
/// to close. So only Quit in the tray's window ends the app, along with the
/// session ending, which waits on nobody.
///
/// An update is no exception on Linux. Flatpak installs the new version beside
/// the running one and never asks this one to stop, so there is no relaunch to
/// let through.
///
/// The last clause is the way out: with nothing in the tray to go back to,
/// refusing a quit would leave no window, no item and no way to stop Graft but
/// a signal.
pub struct QuitPolicy;

impl QuitPolicy {
    pub fn ends_the_app(asked_for: bool, session_ending: bool, tray_showing: bool) -> bool {
        asked_for || session_ending || !tray_showing
    }
}

/// When the window may go away and leave Graft running with only its item.
///
/// A Linux app has no Dock icon to keep: with its window gone it is in no
/// taskbar and no switcher, and the item is the only way back short of
/// starting it again. So the setting is not enough. Closing the window hides
/// it only when the item can be reached; otherwise closing it quits, as a Mac
/// Graft whose item is behind the notch keeps its Dock icon, and the window is
/// still recorded as open so the next start brings one up.
pub struct WindowPolicy;

impl WindowPolicy {
    /// A start that comes up hidden asks again this long after, since the
    /// item has to be registered and a host has to have taken it first.
    pub const PLACEMENT_GRACE: Duration = Duration::from_secs(3);

    pub fn hides_window(show_in_tray: bool, has_shown_window: bool, tray_showing: bool) -> bool {
        show_in_tray && has_shown_window && tray_showing
    }

    pub fn shows_window_after_hidden_launch(tray_showing: bool, has_window: bool) -> bool {
        !tray_showing && !has_window
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn an_item_is_reachable_only_where_a_host_shows_it() {
        assert!(TrayPlacement::is_reachable(true, true, true), "an item a host has taken is somewhere a pointer can reach");
        assert!(!TrayPlacement::is_reachable(true, true, false), "a watcher with no host shows the item nowhere");
        assert!(!TrayPlacement::is_reachable(true, false, false), "and with no watcher at all there is nowhere for it to be");
        assert!(!TrayPlacement::is_reachable(false, true, true), "an item never registered is not one anybody can click");
    }

    #[test]
    fn ctrl_q_puts_the_window_away_and_only_quit_ends_it() {
        assert!(!QuitPolicy::ends_the_app(false, false, true), "Ctrl+Q puts the window away and leaves the tray item reporting");
        assert!(QuitPolicy::ends_the_app(true, false, true), "Quit in the tray's window is the one that ends it");
        assert!(QuitPolicy::ends_the_app(false, true, true), "the session ending is not something to argue with");
        assert!(QuitPolicy::ends_the_app(false, false, false), "with nothing in the tray to go back to, a quit is a quit");
    }

    #[test]
    fn closing_the_window_hides_it_only_with_the_item_in_reach() {
        assert!(WindowPolicy::hides_window(true, true, true), "closing the window with the item in reach leaves Graft in the tray");
        assert!(!WindowPolicy::hides_window(true, true, false), "closing it with no host to show the item quits instead");
        assert!(!WindowPolicy::hides_window(false, true, true), "with the item turned off closing the window quits");
        assert!(!WindowPolicy::hides_window(true, false, true), "a window that was never shown is not one being put away");
        assert!(WindowPolicy::shows_window_after_hidden_launch(false, false), "a hidden start whose item cannot be reached puts the window up after all");
        assert!(!WindowPolicy::shows_window_after_hidden_launch(true, false), "a hidden start whose item can be reached stays hidden");
        assert!(!WindowPolicy::shows_window_after_hidden_launch(false, true), "and one with a window already up has nothing to add");
    }
}
