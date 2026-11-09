//! Which profile's Claude is in front, so the tray can show it.
//!
//! Every Claude on this desktop is one program, and a dock or a task switcher
//! shows the name and icon the window class gives it. A grafted Claude gets a
//! class of its own, but the tray is still the one place that says which
//! account is being used beside that account's figure.
//!
//! The Claude here runs under XWayland (`--ozone-platform=x11`), so on X11 and
//! under XWayland alike the window manager's `_NET_ACTIVE_WINDOW` names the
//! window in front and its `_NET_WM_PID` the process, which a command line
//! matches to a profile. A Wayland compositor tells no client which window is
//! in front, so where there is no X server at all the front Claude is the one
//! whose own records say it showed a conversation last, as the tray's
//! conversation already does.

use std::path::PathBuf;

/// The window in front, as the window manager describes it.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ActiveWindow {
    pub pid: Option<u32>,
    /// `WM_CLASS`, instance then class.
    pub class: Vec<String>,
}

/// What one window coming to the front says about which Claude is in use.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Meaning {
    /// A Claude, still to be matched to its profile by its command line.
    Claude(u32),
    /// Graft itself. Opening the tray's window is asking about the Claude that
    /// was just in use, and dropping it from the tray then would change the
    /// item under the window opened from it.
    Keep,
    /// Anything else, which means no Claude is in front.
    Clear,
}

/// Whether a window class is a Claude's: a grafted one wears
/// `ClaudeGraft.<id>`, and Claude's own carries its name.
pub fn is_claude_class(class: &[String], own_id: &str) -> bool {
    class.iter().any(|c| {
        c.starts_with("ClaudeGraft.")
            || (!c.eq_ignore_ascii_case(own_id) && c.to_ascii_lowercase().contains("claude"))
    })
}

/// `graft_in_front` is asked of Graft's own windows, since a Graft drawn
/// natively on Wayland is no X window at all and leaves the X server naming
/// nothing in front.
pub fn meaning(window: Option<&ActiveWindow>, graft_in_front: bool, own_id: &str) -> Meaning {
    if graft_in_front {
        return Meaning::Keep;
    }
    let Some(window) = window else { return Meaning::Clear };
    if window.class.iter().any(|c| c.eq_ignore_ascii_case(own_id) || c == "graft") {
        return Meaning::Keep;
    }
    match window.pid {
        Some(pid) if is_claude_class(&window.class, own_id) => Meaning::Claude(pid),
        _ => Meaning::Clear,
    }
}

/// The Claude in front, and the tickets that keep a slow lookup from naming
/// one that is no longer there.
#[derive(Default)]
pub struct FrontmostClaude {
    /// `None` while anything other than a Claude is in front.
    pub profile: Option<PathBuf>,
    generation: u64,
}

/// What a lookup landing did: whether the profile shown changed, and which
/// account to ask for a figure, if any.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct Landing {
    pub changed: bool,
    pub came_forward: Option<PathBuf>,
}

impl FrontmostClaude {
    /// Returns the ticket a Claude's lookup has to still hold when it lands,
    /// and whether clearing changed what is shown.
    pub fn activated(&mut self, meaning: Meaning) -> (Option<u64>, bool) {
        match meaning {
            Meaning::Keep => (None, false),
            Meaning::Clear => {
                self.generation += 1;
                let changed = self.profile.take().is_some();
                (None, changed)
            }
            Meaning::Claude(_) => {
                self.generation += 1;
                (Some(self.generation), false)
            }
        }
    }

    /// Matching a command line takes a process listing, and a flick past one
    /// Claude on the way to another can finish before it does. A lookup that
    /// lands after the next switch names a Claude that is no longer in front,
    /// so only the newest one is shown; the account it was about is still
    /// asked for its figure.
    pub fn resolved(&mut self, ticket: u64, found: Option<PathBuf>, switched: bool) -> Landing {
        let came_forward = if switched { found.clone() } else { None };
        if ticket != self.generation || self.profile == found {
            return Landing { changed: false, came_forward };
        }
        self.profile = found;
        Landing { changed: true, came_forward }
    }

    /// Where no window manager can be asked: the profile whose records were
    /// focused last. A change of profile is a Claude coming forward.
    pub fn followed_records(&mut self, found: Option<PathBuf>) -> Landing {
        self.generation += 1;
        if self.profile == found {
            return Landing::default();
        }
        self.profile = found.clone();
        Landing { changed: true, came_forward: found }
    }
}

/// Watches the X server's idea of the window in front, on a thread of its
/// own, calling `report` with each window that comes forward; the first call
/// is whatever is in front as the watch starts, with `switched` false.
/// Returns false when there is no X server to ask.
pub fn watch_x11(report: impl Fn(Option<ActiveWindow>, bool) + Send + 'static) -> bool {
    if std::env::var_os("DISPLAY").is_none() {
        return false;
    }
    let Ok((connection, screen)) = x11rb::connect(None) else { return false };
    std::thread::Builder::new()
        .name("graft-front".into())
        .spawn(move || {
            let _ = x11::run(connection, screen, report);
        })
        .is_ok()
}

mod x11 {
    use super::ActiveWindow;
    use x11rb::connection::Connection;
    use x11rb::protocol::xproto::{AtomEnum, ChangeWindowAttributesAux, ConnectionExt, EventMask, Window};
    use x11rb::protocol::Event;
    use x11rb::rust_connection::RustConnection;

    type Failure = Box<dyn std::error::Error>;

    pub fn run(
        connection: RustConnection,
        screen: usize,
        report: impl Fn(Option<ActiveWindow>, bool),
    ) -> Result<(), Failure> {
        let root = connection.setup().roots[screen].root;
        let active = connection.intern_atom(false, b"_NET_ACTIVE_WINDOW")?.reply()?.atom;
        let pid = connection.intern_atom(false, b"_NET_WM_PID")?.reply()?.atom;
        connection.change_window_attributes(root, &ChangeWindowAttributesAux::new().event_mask(EventMask::PROPERTY_CHANGE))?;
        connection.flush()?;
        let mut last: Option<Window> = None;
        let current = front(&connection, root, active)?;
        report(current.map(|w| describe(&connection, w, pid)), false);
        last = current.or(last);
        loop {
            let event = connection.wait_for_event()?;
            let Event::PropertyNotify(change) = event else { continue };
            if change.window != root || change.atom != active {
                continue;
            }
            let now = front(&connection, root, active)?;
            if now == last {
                continue;
            }
            last = now;
            report(now.map(|w| describe(&connection, w, pid)), true);
        }
    }

    fn front(connection: &RustConnection, root: Window, active: u32) -> Result<Option<Window>, Failure> {
        let reply = connection.get_property(false, root, active, AtomEnum::WINDOW, 0, 1)?.reply()?;
        Ok(reply.value32().and_then(|mut v| v.next()).filter(|w| *w != 0))
    }

    fn describe(connection: &RustConnection, window: Window, pid_atom: u32) -> ActiveWindow {
        let pid = connection
            .get_property(false, window, pid_atom, AtomEnum::CARDINAL, 0, 1)
            .ok()
            .and_then(|c| c.reply().ok())
            .and_then(|r| r.value32().and_then(|mut v| v.next()));
        let class = connection
            .get_property(false, window, AtomEnum::WM_CLASS, AtomEnum::STRING, 0, 256)
            .ok()
            .and_then(|c| c.reply().ok())
            .map(|r| {
                r.value
                    .split(|b| *b == 0)
                    .filter(|part| !part.is_empty())
                    .map(|part| String::from_utf8_lossy(part).into_owned())
                    .collect()
            })
            .unwrap_or_default();
        ActiveWindow { pid, class }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const OWN: &str = "io.github.aaditya_v_more.ClaudeGraft";

    fn window(pid: u32, class: &[&str]) -> ActiveWindow {
        ActiveWindow { pid: Some(pid), class: class.iter().map(|c| c.to_string()).collect() }
    }

    #[test]
    fn a_window_coming_forward_says_whether_a_claude_is_in_front() {
        assert_eq!(meaning(Some(&window(41, &["claude-desktop", "Claude"])), false, OWN), Meaning::Claude(41),
            "a Claude coming forward is looked up by its pid");
        assert_eq!(meaning(Some(&window(42, &["ClaudeGraft.7c2b", "ClaudeGraft.7c2b"])), false, OWN), Meaning::Claude(42),
            "and so is a grafted one under its own class");
        assert_eq!(meaning(Some(&window(7, &[OWN, OWN])), false, OWN), Meaning::Keep,
            "Graft coming forward for its tray window leaves the name where it was");
        assert_eq!(meaning(None, true, OWN), Meaning::Keep,
            "and so does a Graft drawn on Wayland, which the X server cannot name");
        assert_eq!(meaning(Some(&window(12, &["org.gnome.Nautilus", "Nautilus"])), false, OWN), Meaning::Clear,
            "anything else in front means no Claude is");
        assert_eq!(meaning(None, false, OWN), Meaning::Clear, "and nothing named in front is no Claude either");
    }

    #[test]
    fn only_the_newest_lookup_names_the_claude_in_front() {
        let two = PathBuf::from("/h/.config/Claude-2");
        let three = PathBuf::from("/h/.config/Claude-3");
        let mut front = FrontmostClaude::default();
        let (ticket, _) = front.activated(Meaning::Claude(41));
        let landing = front.resolved(ticket.unwrap(), Some(two.clone()), true);
        assert_eq!(front.profile.as_ref(), Some(&two), "a Claude brought forward is named once its profile is known");
        assert_eq!(landing.came_forward.as_ref(), Some(&two), "and its account is asked for its figure");

        let (late, _) = front.activated(Meaning::Claude(42));
        let (_, cleared) = front.activated(Meaning::Clear);
        assert!(cleared, "switching away clears the name at once");
        let landing = front.resolved(late.unwrap(), Some(three.clone()), true);
        assert_eq!(front.profile, None, "a lookup that lands after the switch away names nobody");
        assert_eq!(landing.came_forward.as_ref(), Some(&three), "though the account it was about is still asked for");

        let (opening, _) = front.activated(Meaning::Claude(43));
        front.activated(Meaning::Keep);
        front.resolved(opening.unwrap(), Some(three.clone()), true);
        assert_eq!(front.profile.as_ref(), Some(&three), "opening the tray's window mid-lookup does not lose the Claude behind it");

        let (start, _) = front.activated(Meaning::Claude(44));
        let landing = front.resolved(start.unwrap(), Some(two.clone()), false);
        assert!(front.profile.as_ref() == Some(&two) && landing.came_forward.is_none(),
            "the Claude in front at start is named without anybody's figure being asked for");
    }

    #[test]
    fn without_a_window_manager_to_ask_the_records_say_who_is_in_front() {
        let mut front = FrontmostClaude::default();
        let two = PathBuf::from("/h/.config/Claude-2");
        assert_eq!(front.followed_records(Some(two.clone())).came_forward, Some(two.clone()),
            "a profile whose records were focused last comes forward");
        assert!(!front.followed_records(Some(two)).changed, "and the same one again changes nothing");
    }
}
