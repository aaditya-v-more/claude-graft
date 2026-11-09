//! Two Claudes on one chat store, from the side that asks.
//!
//! The rule and its words are `graft_profiles::conflict`; this is where every
//! route that opens a profile asks it. The window asked on the Mac and the
//! dropdown did not, and Claude's own profile could not, having no shortcut
//! to stand for it, which is the common case: every shortcut grafted from
//! main reads exactly the files main is about to open. So each of the three
//! goes through `ask_then_open`, and the suite fails if one stops naming it.

use crate::shared::shared;
use crate::ui::off_main;
use graft_core::Shortcut;
use graft_launch::Steps;
use graft_profiles::{conflict, opener};
use adw::prelude::*;
use std::path::PathBuf;

/// The question, in a dialog over whatever surface asked. A libadwaita dialog
/// is drawn inside its window, so the tray's window keeps the focus it needs
/// to stay open while the question is up.
pub fn ask(parent: &gtk::Widget, sharers: &[String], then: impl Fn() + 'static) {
    let dialog = adw::AlertDialog::new(Some(conflict::TITLE), Some(&conflict::message(sharers)));
    dialog.add_response("cancel", conflict::CANCEL);
    dialog.add_response("open", conflict::OPEN_ANYWAY);
    dialog.set_response_appearance("open", adw::ResponseAppearance::Destructive);
    dialog.set_default_response(Some("cancel"));
    dialog.set_close_response("cancel");
    dialog.connect_response(None, move |_, response| {
        if response == "open" {
            then();
        }
    });
    dialog.present(Some(parent));
}

/// Who else is open on these chats is a process listing, so it is asked off
/// the main loop, and the open waits for the answer. `done` hears why an open
/// failed, or `None`.
pub fn ask_then_open(
    parent: &gtk::Widget,
    profile: PathBuf,
    shortcut: Option<Shortcut>,
    done: impl Fn(Option<String>) + 'static,
) {
    let neighbours = shared().store.borrow().chat_store_neighbours(shortcut.as_ref());
    let parent = parent.clone();
    let asking = profile.clone();
    off_main(
        move || conflict::open_sharers(&asking, &neighbours),
        move |sharers| {
            let done = std::rc::Rc::new(done);
            if sharers.is_empty() {
                open(profile, shortcut, move |r| done(r));
            } else {
                ask(&parent, &sharers, move || {
                    let done = done.clone();
                    open(profile.clone(), shortcut.clone(), move |r| done(r));
                });
            }
        },
    );
}

/// Through the opener, never a launch of its own: a profile already open is
/// shown, not opened twice, and a missing shortcut is never permission to
/// open another profile.
fn open(profile: PathBuf, shortcut: Option<Shortcut>, done: impl FnOnce(Option<String>) + 'static) {
    off_main(
        move || opener::open(&profile, shortcut.as_ref(), &Steps).err().map(|e| e.to_string()),
        done,
    );
}
