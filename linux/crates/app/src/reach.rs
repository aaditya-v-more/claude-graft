//! Whether the tray item can be reached, asked of the session bus on a
//! thread of its own, so the main loop reads an answer and never waits for
//! one.

use crate::policy::TrayPlacement;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::Duration;

/// How often the bus is asked again. A tray host comes and goes (a shell
/// restarting, an extension turned off) and says so to nobody but the
/// watcher, and a close or a Ctrl+Q may land at any moment.
pub const LOOK_EVERY: Duration = Duration::from_secs(5);

#[derive(Default)]
pub struct Reach {
    /// The item's service registered with a watcher.
    pub registered: AtomicBool,
    pub watcher: AtomicBool,
    pub host: AtomicBool,
}

impl Reach {
    pub fn is_showing(&self) -> bool {
        TrayPlacement::is_reachable(
            self.registered.load(Ordering::SeqCst),
            self.watcher.load(Ordering::SeqCst),
            self.host.load(Ordering::SeqCst),
        )
    }
}

/// `org.kde.StatusNotifierWatcher` on the session bus, and whether it has a
/// host registered: the one thing that says an item will be drawn anywhere.
pub fn ask_bus(connection: &zbus::blocking::Connection) -> (bool, bool) {
    let proxy = zbus::blocking::Proxy::new(
        connection,
        "org.kde.StatusNotifierWatcher",
        "/StatusNotifierWatcher",
        "org.kde.StatusNotifierWatcher",
    );
    let Ok(proxy) = proxy else { return (false, false) };
    match proxy.get_property::<bool>("IsStatusNotifierHostRegistered") {
        Ok(host) => (true, host),
        Err(_) => (false, false),
    }
}

/// Looks every `LOOK_EVERY`, and calls `changed` on the thread that looked
/// whenever the answer moves.
pub fn watch(reach: Arc<Reach>, changed: impl Fn() + Send + 'static) {
    let _ = std::thread::Builder::new().name("graft-reach".into()).spawn(move || {
        let mut connection = None;
        loop {
            if connection.is_none() {
                connection = zbus::blocking::Connection::session().ok();
            }
            let (watcher, host) = match &connection {
                Some(c) => ask_bus(c),
                None => (false, false),
            };
            let before = reach.is_showing();
            reach.watcher.store(watcher, Ordering::SeqCst);
            reach.host.store(host, Ordering::SeqCst);
            if reach.is_showing() != before {
                changed();
            }
            std::thread::sleep(LOOK_EVERY);
        }
    });
}
