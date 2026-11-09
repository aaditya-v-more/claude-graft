//! The usage bars, the line saying when a figure is from, and the button
//! that asks again, as the window's pages and the tray's window draw them.
//!
//! Fed from the monitor, never from disk, so the figure a page shows is the
//! one the tray is showing for the same account.

use crate::shared::shared;
use crate::ui::{self, identified};
use graft_usage::{countdown, Entry, Refresh};
use adw::prelude::*;
use std::path::PathBuf;
use std::time::{Duration, SystemTime};

/// On a clock of its own, because a bar whose figure stayed put would
/// otherwise sit at one countdown beside another that kept counting.
const COUNTDOWN_EVERY: Duration = Duration::from_secs(30);

fn tint(percent: i64) -> &'static str {
    match percent {
        p if p < 70 => "graft-ok",
        p if p < 90 => "graft-high",
        _ => "graft-full",
    }
}

/// What a bar says aloud: the figure and when it resets, where the bar alone
/// carries a bare fraction.
pub fn bar_words(percent: i64, remaining: Option<&str>) -> String {
    match remaining {
        Some(r) => format!("{percent}% used, resets in {r}"),
        None => format!("{percent}% used"),
    }
}

/// One line: the label, the bar, the figure and, inline, the time to reset.
/// Three bars with a line each under them were what pushed a page past the
/// bottom of the smallest window.
pub fn bar(label: &str, percent: i64, resets: Option<SystemTime>, dimmed: bool, id: &str) -> gtk::Box {
    let row = gtk::Box::new(gtk::Orientation::Horizontal, 8);
    let name = ui::caption(label);
    name.set_width_chars(7);
    row.append(&name);
    let progress = gtk::ProgressBar::new();
    progress.add_css_class("graft-bar");
    progress.add_css_class(tint(percent));
    progress.set_fraction((percent as f64 / 100.0).clamp(0.0, 1.0));
    progress.set_hexpand(true);
    progress.set_valign(gtk::Align::Center);
    row.append(&progress);
    let figure = ui::caption(&format!("{percent}%"));
    figure.set_width_chars(5);
    figure.set_xalign(1.0);
    row.append(&figure);
    let reset = ui::caption("");
    reset.set_width_chars(10);
    reset.set_xalign(1.0);
    row.append(&reset);
    if dimmed {
        row.add_css_class("graft-dimmed");
    }
    let update = {
        let progress = progress.clone();
        let reset = reset.clone();
        let label = label.to_string();
        let id = id.to_string();
        move || {
            let remaining = resets.and_then(|r| countdown(r, SystemTime::now()));
            reset.set_text(remaining.as_deref().unwrap_or(""));
            reset.set_tooltip_text(remaining.as_ref().map(|r| format!("Resets in {r}")).as_deref());
            identified(&progress, &id, &label);
            progress.update_property(&[gtk::accessible::Property::ValueText(&bar_words(percent, remaining.as_deref()))]);
        }
    };
    update();
    ui::every(&row, COUNTDOWN_EVERY, update);
    row
}

/// Where the five-hour bar would be, on an account that has no such window.
/// A bar at 0% reads as a window with nothing spent in it yet, which is not
/// the same thing, and it never moved however hard the account was used.
pub fn no_five_hour_limit(dimmed: bool, surface: &str, folder: &str) -> gtk::Label {
    let label = ui::caption("No 5-hour limit on this account");
    if dimmed {
        label.add_css_class("graft-dimmed");
    }
    identified(&label, &ui::id(surface, Some(folder), "no-five-hour-limit"), "No 5-hour limit on this account");
    label
}

/// The bars, or a line saying there is nothing to show yet.
pub fn summary(entry: Option<&Entry>, surface: &str, folder: &str) -> gtk::Box {
    let column = gtk::Box::new(gtk::Orientation::Vertical, 6);
    let dimmed = entry.map(Entry::is_dimmed).unwrap_or(false);
    match entry.and_then(|e| e.usage.as_ref()) {
        Some(usage) => {
            match usage.five_hour {
                Some(five) => column.append(&bar("5 hours", five, usage.five_hour_reset, dimmed, &ui::id(surface, Some(folder), "five-hour"))),
                None => column.append(&no_five_hour_limit(dimmed, surface, folder)),
            }
            column.append(&bar("Week", usage.week, usage.week_reset, dimmed, &ui::id(surface, Some(folder), "week")));
            if let Some(fable) = usage.fable {
                column.append(&bar("Fable", fable, usage.fable_reset, dimmed, &ui::id(surface, Some(folder), "fable")));
            }
        }
        None => {
            let none = ui::caption("No usage reported yet");
            identified(&none, &ui::id(surface, Some(folder), "no-usage"), "No usage reported yet");
            column.append(&none);
        }
    }
    column
}

/// When an account's figures were taken, where from, why they are not live
/// if they are not, and a way to ask again. Said under every account rather
/// than only once a figure has gone stale: a bar quietly five minutes behind
/// looks exactly like one that is current.
pub fn freshness(entry: &Entry, profile: PathBuf, surface: &str, folder: &str) -> gtk::Box {
    let row = gtk::Box::new(gtk::Orientation::Horizontal, 4);
    let when = ui::caption(&entry.freshness(SystemTime::now()));
    identified(&when, &ui::id(surface, Some(folder), "freshness"), &entry.freshness(SystemTime::now()));
    row.append(&when);
    {
        let when = when.clone();
        let entry = entry.clone();
        ui::every(&row, COUNTDOWN_EVERY, move || when.set_text(&entry.freshness(SystemTime::now())));
    }
    if let Some(note) = &entry.note {
        row.append(&ui::caption("\u{b7}"));
        let why = ui::caption(note);
        why.remove_css_class("dim-label");
        why.add_css_class("warning");
        why.set_tooltip_text(Some(note));
        identified(&why, &ui::id(surface, Some(folder), "usage-note"), note);
        row.append(&why);
    }
    let spacer = gtk::Box::new(gtk::Orientation::Horizontal, 0);
    spacer.set_hexpand(true);
    row.append(&spacer);
    row.append(&refresh_button(entry, profile, surface, folder));
    row
}

/// Spins only for the pass that is about this account, or about everyone.
pub fn refresh_button(entry: &Entry, profile: PathBuf, surface: &str, folder: &str) -> gtk::Widget {
    let snapshot = shared().snapshot.borrow().clone();
    let spinning = snapshot.is_refreshing
        && snapshot.refreshing_profile.as_ref().map(|p| graft_core::paths::same_path(p, &profile)).unwrap_or(true);
    if spinning {
        let spinner = adw::Spinner::new();
        identified(&spinner, &ui::id(surface, Some(folder), "refresh-usage"), &format!("Refreshing usage for {}", entry.name));
        return spinner.upcast();
    }
    let button = ui::icon_button(
        "view-refresh-symbolic",
        &ui::id(surface, Some(folder), "refresh-usage"),
        &format!("Refresh usage for {}", entry.name),
    );
    button.set_tooltip_text(Some("Ask Anthropic for this account's usage now"));
    button.connect_clicked(move |_| shared().monitor.refresh(Refresh::pressed_for(&profile)));
    button.upcast()
}

/// Which of the two sources the figure came from. They answer differently
/// enough to be worth naming: the endpoint is current whether or not that
/// Claude is running, the file is as old as the last time it ran.
pub fn explanation(entry: Option<&Entry>) -> String {
    let Some(entry) = entry else { return NOTHING_YET.into() };
    let Some(usage) = entry.usage.as_ref() else { return NOTHING_YET.into() };
    if entry.is_live {
        return "Asked of this account directly, using the login it already holds: the same figure the tray is showing. It stays current whether or not that Claude is running.".into();
    }
    if usage.is_stale() && usage.five_hour.is_none() {
        return "Recorded while this profile was last open, which was hours ago, so the week may have moved since. Unlock the keyring from the tray to have the account asked instead.".into();
    }
    if usage.is_stale() {
        return "Recorded while this profile was last open, which was long enough ago that the five-hour window has since reset. Unlock the keyring from the tray to have the account asked instead.".into();
    }
    format!(
        "Recorded by this profile at {}. It only updates while that Claude is running, and the reset times are worked out from its own history.",
        graft_usage::clock(usage.sampled, SystemTime::now())
    )
}

const NOTHING_YET: &str = "Claude records how much of each window it has spent while it runs, and the account itself is asked directly once its login is readable. Neither has answered yet.";

/// "Plan usage", its explanation, and on the right when the figure was taken
/// with the button that asks again.
pub fn section(entry: Option<&Entry>, profile: PathBuf, surface: &str, folder: &str) -> gtk::Box {
    let column = gtk::Box::new(gtk::Orientation::Vertical, 6);
    let trailing = entry.map(|e| freshness(e, profile.clone(), surface, folder).upcast::<gtk::Widget>());
    column.append(&ui::section_header("Plan usage", &explanation(entry), &ui::id(surface, Some(folder), "usage-info"), trailing.as_ref()));
    let card = ui::card();
    let bars = summary(entry, surface, folder);
    ui::padded(&bars, 12, 10);
    card.append(&bars);
    column.append(&card);
    column
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_bar_says_its_figure_and_its_reset_in_words() {
        assert_eq!(bar_words(6, Some("4h 1m")), "6% used, resets in 4h 1m");
        assert_eq!(bar_words(94, None), "94% used");
        assert_eq!(tint(69), "graft-ok");
        assert_eq!(tint(70), "graft-high");
        assert_eq!(tint(90), "graft-full");
    }
}
