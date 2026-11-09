//! Two Claudes on one chat store.
//!
//! Sharing chats is the point of this app, and it is also the one way it can
//! lose somebody something: both instances write the same files, so the same
//! conversation open in two of them can drop messages. So opening a profile
//! that something else is already reading is worth a question.
//!
//! This lives on its own because every route that opens a profile has to ask
//! it, and on the Mac only one of the three did. The window asked; the
//! dropdown did not, and Claude's own profile could not, having no shortcut to
//! stand for it. That last case is the common one: every shortcut grafted
//! from main reads exactly the files main is about to open.

use crate::store::Neighbour;
use graft_core::claude;
use std::path::Path;

pub const TITLE: &str = "Another Claude is open on these chats";
pub const OPEN_ANYWAY: &str = "Open Anyway";
pub const CANCEL: &str = "Cancel";

/// Reads as a sentence for any number of them, which is why it is one
/// function rather than a format string at each place that asks.
pub fn message(sharers: &[String]) -> String {
    let list = match sharers.len() {
        0 => String::new(),
        1 => sharers[0].clone(),
        2 => format!("{} and {}", sharers[0], sharers[1]),
        n => format!("{} and {}", sharers[..n - 1].join(", "), sharers[n - 1]),
    };
    let verb = if sharers.len() == 1 { "is" } else { "are" };
    format!(
        "{list} {verb} already open on the same Claude Code chats.\n\nBoth instances write to the same chat files. Opening the same conversation in two of them at once can lose messages."
    )
}

/// Who among `neighbours` is running, and whether that is worth asking about
/// at all. One process listing for all of them, which still means a host
/// command, so never on the main thread.
pub fn open_sharers(profile: &Path, neighbours: &[Neighbour]) -> Vec<String> {
    let listing = claude::processes();
    sharers_to_ask_about(
        claude::is_running_in(&listing, profile),
        neighbours
            .iter()
            .filter(|n| claude::is_running_in(&listing, &n.profile))
            .map(|n| n.name.clone())
            .collect(),
    )
}

/// A profile that is already open is not about to be opened: the Claude
/// sitting on those chats is only being brought forward, and it has been
/// reading them all along. Warning there describes a situation rather than one
/// the press is about to create, and it arrives attached to a button that will
/// not create one.
pub fn sharers_to_ask_about(profile_is_open: bool, open_neighbours: Vec<String>) -> Vec<String> {
    if profile_is_open {
        Vec::new()
    } else {
        open_neighbours
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_question_reads_as_a_sentence_for_any_number_of_others() {
        assert!(message(&["Claude 2".into()]).contains("Claude 2 is already open"), "one other instance reads as a sentence");
        assert!(message(&["Claude 2".into(), "Work".into()]).contains("Claude 2 and Work are"), "so does a pair");
        assert!(message(&["A".into(), "B".into(), "C".into()]).contains("A, B and C are"), "and a list longer than that");
        assert!(message(&["Claude 2".into()]).contains("can lose messages"), "and it says what is actually at stake, not just who is open");
    }

    #[test]
    fn only_a_press_that_adds_a_reader_is_asked_about() {
        assert_eq!(sharers_to_ask_about(false, vec!["Claude".into()]), vec!["Claude".to_string()],
            "opening a profile onto chats something else is reading is worth asking about");
        assert!(sharers_to_ask_about(true, vec!["Claude".into()]).is_empty(),
            "but a profile already open is only being brought forward, so nothing is asked");
        assert!(sharers_to_ask_about(false, vec![]).is_empty(), "and with nobody else on those chats there was never a question");
    }
}
