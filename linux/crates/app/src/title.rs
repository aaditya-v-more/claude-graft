//! What the tray item says: the figure, whose figure it is, the conversation
//! on screen, and the same in words for a screen reader and the tooltip.
//!
//! A StatusNotifierItem is an icon, a title and a tooltip. KDE shows no text
//! beside an item at all, and GNOME's AppIndicator extension shows a label it
//! reads from a key of its own rather than the title, so the figure and the
//! name go into the title, for a host that shows one, and into the tooltip,
//! which every host shows. The Mac's pictures (the name on the icon's colour,
//! the lowered figure, the strip of lent accounts) are AppKit drawing inside
//! a status item, and a tray host draws its own; what each one said is said in
//! these words instead, and the icon carries the Claude in front.

use graft_core::IconPreset;
use graft_usage::{Entry, Headline, Usage};

/// A week's figure looks exactly like a five-hour one, and the two run out at
/// very different rates, so the week carries a letter saying which.
pub fn figure_text(figure: Headline) -> String {
    if figure.is_weekly {
        format!("W {}%", figure.percent)
    } else {
        format!("{}%", figure.percent)
    }
}

/// The figure read out, with the words for what the letter only hints at.
pub fn spoken(name: &str, figures: &Usage) -> String {
    match figures.five_hour {
        Some(five) => format!("{name}, {five}% of 5 hours"),
        None => format!("{name}, {}% of the week", figures.week),
    }
}

/// One account in the tooltip. One without a five-hour limit says so, where a
/// zero would pass for a window nobody has started yet.
pub fn tooltip_line(name: &str, figures: &Usage) -> String {
    let mut line = match figures.five_hour {
        Some(five) => format!("{name}: {five}% of 5 hours, {}% of the week", figures.week),
        None => format!("{name}: no 5-hour limit, {}% of the week", figures.week),
    };
    if let Some(fable) = figures.fable {
        line.push_str(&format!(", {fable}% Fable"));
    }
    line
}

/// The conversation on screen, as the tray says it: its title, or "New chat"
/// until it has one, and the account answering it. One from before sharing
/// began, opened to read, has no account answering it and shows its title
/// alone.
pub fn ticker_text(title: Option<&str>, account: Option<&str>) -> String {
    let name = title.unwrap_or("New chat");
    match account {
        Some(account) => format!("{name} \u{b7} {account}"),
        None => name.to_string(),
    }
}

pub fn ticker_spoken(title: Option<&str>, account: Option<&str>) -> String {
    let name = title.unwrap_or("New chat");
    match account {
        Some(account) => format!("Conversation on screen \u{201c}{name}\u{201d}, answered by {account}"),
        None => format!("Conversation on screen \u{201c}{name}\u{201d}"),
    }
}

/// What the tray is told to show, worked out from what the window knows.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct TrayLook {
    /// The item's title: what a host that shows text draws beside the icon.
    pub title: String,
    /// The tooltip's heading, which is also what the item says in words.
    pub spoken: String,
    /// The tooltip's lines.
    pub lines: Vec<String>,
    /// The icon of the Claude in front, or `None` for Graft's own.
    pub icon: Option<IconPreset>,
    /// The account answering the conversation on screen, drawn over the icon,
    /// where the Mac lights it in a strip of every account lent.
    pub overlay: Option<IconPreset>,
    /// The conversation on screen, with the accounts it may be moved to, for
    /// the item's menu.
    pub chat: Option<TrayChat>,
}

#[derive(Debug, Clone, PartialEq)]
pub struct TrayChat {
    pub id: String,
    pub heading: String,
    /// The first is Automatic, then every account by key.
    pub choices: Vec<(Option<String>, String)>,
    pub selected: usize,
}

/// What the look is built from.
pub struct LookInput<'a> {
    pub show_front_icon: bool,
    pub show_front_name: bool,
    /// `status_entry(front)` and whether it is the Claude in front by name.
    pub named: Option<Entry>,
    /// `status_entry(None)`: the tightest window, for a tray naming nobody.
    pub tightest: Option<Entry>,
    pub entries: &'a [Entry],
    pub front_preset: IconPreset,
    /// The conversation on screen in words, and what it says aloud.
    pub ticker: Option<(String, String)>,
    /// What the strip of lent accounts would have said, and the lit one.
    pub drawing: Option<(String, Option<IconPreset>)>,
    pub chat: Option<TrayChat>,
}

/// Built the same way with or without a Claude in front. A Claude nothing
/// could be shown for is not shown, and nor is its figure, which would
/// otherwise pass for the tightest window's.
pub fn look(input: LookInput<'_>) -> TrayLook {
    let wanted = input.show_front_icon || input.show_front_name;
    let named = input.named.filter(|_| wanted);
    let front = named.filter(|_| input.show_front_icon || input.show_front_name);
    let headline = front.clone().or(input.tightest);
    let figures = headline.as_ref().and_then(|e| e.current_usage().cloned());

    let mut title: Vec<String> = Vec::new();
    if let (Some(front), true) = (&front, input.show_front_name) {
        title.push(front.name.clone());
    }
    if let Some(figures) = &figures {
        title.push(figure_text(figures.headline()));
    }
    if let Some((text, _)) = &input.ticker {
        title.push(text.clone());
    }

    let spoken_figure = match (&headline, &figures) {
        (Some(entry), Some(figures)) => Some(spoken(&entry.name, figures)),
        _ => front.as_ref().map(|f| f.name.clone()),
    };
    let spoken_all: Vec<String> = [
        spoken_figure,
        input.drawing.as_ref().map(|(words, _)| words.clone()),
        input.ticker.as_ref().map(|(_, words)| words.clone()),
    ]
    .into_iter()
    .flatten()
    .filter(|s| !s.is_empty())
    .collect();

    let mut lines = Vec::new();
    match (&headline, &figures) {
        (Some(entry), Some(figures)) => {
            let mut first = tooltip_line(&entry.name, figures);
            if entry.is_running {
                first.push_str(" - open now");
            }
            lines.push(first);
            for other in input.entries.iter().filter(|e| e.id() != entry.id()) {
                if let Some(usage) = &other.usage {
                    lines.push(tooltip_line(&other.name, usage));
                }
            }
        }
        _ => lines.push("No usage reported yet".into()),
    }
    if let Some((text, _)) = &input.ticker {
        lines.push(text.clone());
    }

    let icon = front.as_ref().filter(|_| input.show_front_icon).map(|_| input.front_preset);
    TrayLook {
        title: title.join(" "),
        spoken: if spoken_all.is_empty() { "Claude Graft".into() } else { spoken_all.join(". ") },
        lines,
        icon,
        overlay: icon.and(input.drawing.and_then(|(_, lit)| lit)),
        chat: input.chat,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;
    use std::time::SystemTime;

    fn usage(five: Option<i64>, week: i64) -> Usage {
        Usage::new(five, week, None, SystemTime::now())
    }

    fn entry(name: &str, five: Option<i64>, week: i64, running: bool) -> Entry {
        let mut entry = Entry::new(name, PathBuf::from(format!("/h/.config/{name}")), Some(usage(five, week)), running);
        entry.is_live = true;
        entry
    }

    fn input<'a>(entries: &'a [Entry], named: Option<Entry>) -> LookInput<'a> {
        LookInput {
            show_front_icon: true,
            show_front_name: false,
            named,
            tightest: entries.first().cloned(),
            entries,
            front_preset: IconPreset::Blue,
            ticker: None,
            drawing: None,
            chat: None,
        }
    }

    #[test]
    fn a_week_figure_carries_a_letter_and_says_what_it_is() {
        assert_eq!(figure_text(Headline { percent: 34, is_weekly: false }), "34%");
        assert_eq!(figure_text(Headline { percent: 94, is_weekly: true }), "W 94%", "a week's figure is never read as five hours");
        let mut unlimited = usage(None, 94);
        unlimited.fable = Some(12);
        assert_eq!(tooltip_line("Claude 2", &unlimited), "Claude 2: no 5-hour limit, 94% of the week, 12% Fable");
        assert_eq!(spoken("Claude 2", &unlimited), "Claude 2, 94% of the week");
        let nothing = usage(Some(0), 12);
        assert_eq!(tooltip_line("Claude", &nothing), "Claude: 0% of 5 hours, 12% of the week",
            "an account with nothing spent says zero of its five hours, not that it has none");
        assert_eq!(spoken("Claude", &nothing), "Claude, 0% of 5 hours");
    }

    #[test]
    fn the_conversation_on_screen_is_named_with_the_account_answering_it() {
        assert_eq!(ticker_text(None, Some("Claude")), "New chat \u{b7} Claude");
        assert_eq!(ticker_text(Some("Before sharing began"), None), "Before sharing began",
            "one no account has answered shows its title alone");
        assert_eq!(ticker_spoken(Some("Before sharing began"), None), "Conversation on screen \u{201c}Before sharing began\u{201d}");
    }

    #[test]
    fn the_claude_in_front_brings_its_icon_and_its_own_figure() {
        let entries = vec![entry("Claude", Some(80), 10, false), entry("Work", Some(34), 50, true)];
        let front = Some(entries[1].clone());
        let look = look(input(&entries, front));
        assert_eq!(look.icon, Some(IconPreset::Blue), "the item wears the icon of the Claude in front");
        assert_eq!(look.title, "34%", "and shows that Claude's figure, never the tightest window's");
        assert!(look.spoken.starts_with("Work, 34% of 5 hours"), "and says whose figure it is in words");
        assert!(look.lines[0].ends_with(" - open now"), "the tooltip leads with it, saying it is open");

        let nobody = super::look(input(&entries, None));
        assert_eq!(nobody.icon, None, "with no Claude in front Graft's own icon comes back");
        assert_eq!(nobody.title, "80%", "beside the tightest window open");

        let mut named = input(&entries, Some(entries[1].clone()));
        named.show_front_name = true;
        assert_eq!(super::look(named).title, "Work 34%", "the name goes into the title only when asked for");

        let mut off = input(&entries, Some(entries[1].clone()));
        off.show_front_icon = false;
        let off = super::look(off);
        assert_eq!(off.icon, None, "a Claude nothing is shown for is not shown");
        assert_eq!(off.title, "80%", "and neither is its figure");
    }

    #[test]
    fn the_account_answering_is_drawn_only_over_a_claude_shown() {
        let entries = vec![entry("Work", Some(34), 50, true)];
        let mut drawing = input(&entries, Some(entries[0].clone()));
        drawing.drawing = Some(("Answering now: Claude 2".into(), Some(IconPreset::Green)));
        drawing.ticker = Some(("Is 391 prime? \u{b7} Claude 2".into(), "Conversation on screen".into()));
        let look = look(drawing);
        assert_eq!(look.overlay, Some(IconPreset::Green));
        assert!(look.title.ends_with("Is 391 prime? \u{b7} Claude 2"), "the conversation runs after the figure");
        assert!(look.spoken.contains("Answering now: Claude 2") && look.spoken.contains("Conversation on screen"));
    }
}
