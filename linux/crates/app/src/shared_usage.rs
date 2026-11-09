//! Shared usage as the window and the tray's window show it: the accounts one
//! Claude may draw on, in that Claude's own page; the conversations the relay
//! is carrying, each with the account answering it and a menu to move it; and
//! the line saying lending is paused.
//!
//! Nothing about the Claude itself changes: it keeps its account, its chats
//! and its sidebar, and only the model requests its Claude Code makes can be
//! answered by an account listed here.

use crate::icons;
use crate::settings;
use crate::shared::{listen, shared, SignInPhase, Topic};
use crate::sharing;
use crate::ui::{self, identified};
use graft_pool::chats::{self, Chat};
use graft_pool::code_environment::State;
use graft_pool::controller::{Controller, Lending, ACCOUNT_PREFIX};
use graft_pool::policy;
use graft_pool::PoolMemberStatus;
use adw::prelude::*;
use std::cell::Cell;
use std::path::Path;
use std::rc::Rc;
use std::time::{Duration, UNIX_EPOCH};

pub const EXPLANATION: &str = "The Code tab in this Claude can be answered by the accounts added here. Each request goes to whichever has room and the soonest weekly reset, and a conversation stays on one account while its cache is still warm there. Chats, artifacts, connectors and Claude in Chrome stay with this Claude's own account, and the Chat tab is never shared.\n\nA request answered by another account carries the conversation to that account. Anthropic's terms for Claude plans may not allow one person to combine subscriptions this way, so read them before adding one.";

const OPEN_AT_LOGIN_NOTE: &str = "shared-usage.open-at-login";

fn time_of(seconds: f64) -> std::time::SystemTime {
    UNIX_EPOCH + Duration::from_secs_f64(seconds.max(0.0))
}

/// The accounts `claude` draws on, redrawn whenever lending, the figures or a
/// sign-in change.
pub fn section(claude: &str, folder: &str) -> gtk::Box {
    let column = gtk::Box::new(gtk::Orientation::Vertical, 6);
    let problem: Rc<std::cell::RefCell<Option<String>>> = Rc::default();
    let draw = {
        let column = column.clone();
        let claude = claude.to_string();
        let folder = folder.to_string();
        let problem = problem.clone();
        move || fill_section(&column, &claude, &folder, &problem)
    };
    draw();
    let redraw = draw.clone();
    listen(&column, &[Topic::Pool, Topic::Usage, Topic::SignIn, Topic::Settings, Topic::Store], redraw);
    column
}

fn fill_section(column: &gtk::Box, claude: &str, folder: &str, problem: &Rc<std::cell::RefCell<Option<String>>>) {
    ui::clear(column);
    let shared = shared();
    let pool = shared.pool.borrow();
    let lenders = pool.lenders_of(claude);
    let trailing = header_trailing(&pool, claude, &lenders, column);
    column.append(&ui::section_header("Shared usage", EXPLANATION, &ui::id(ui::WINDOW, Some(folder), "shared-usage-info"), Some(&trailing)));
    let card = ui::card();
    for lender in &lenders {
        card.append(&lender_row(&pool, claude, lender));
    }
    let lines = note_lines(&pool, claude, &lenders, problem.borrow().as_deref());
    if let Some((first, attention, id)) = lines.first() {
        let row = ui::card_row(8);
        let more = (lines.len() > 1).then(|| lines.iter().map(|l| l.0.clone()).collect::<Vec<_>>().join("\n\n"));
        let note = ui::note(first, more.as_deref(), "Shared usage", *attention, &ui::id(ui::WINDOW, Some(folder), id));
        note.set_hexpand(true);
        row.append(&note);
        // Only beside its own line: beside another, Turn On would read as
        // turning that on.
        if *id == OPEN_AT_LOGIN_NOTE {
            let button = gtk::Button::with_label("Turn On");
            button.add_css_class("flat");
            identified(&button, &ui::id(ui::WINDOW, Some(folder), "shared-usage.turn-on-open-at-login"), "Turn On Open at Login");
            let problem = problem.clone();
            button.connect_clicked(move |_| {
                *problem.borrow_mut() = settings::set_open_at_login(true);
                crate::shared::emit(Topic::Settings);
            });
            row.append(&button);
        }
        card.append(&row);
    }
    if lenders.is_empty() && lines.is_empty() {
        let none = ui::caption("No accounts added");
        ui::padded(&none, 12, 8);
        identified(&none, &ui::id(ui::WINDOW, Some(folder), "shared-usage.none"), "No accounts added");
        card.append(&none);
    }
    column.append(&card);
}

/// Most pressing first: something wrong, then what this Claude is waiting
/// on, then the reminder. One line is shown and the rest wait behind its
/// button: three under the list were three rows below the bottom of the
/// window.
pub fn note_lines(pool: &Controller, claude: &str, lenders: &[String], problem: Option<&str>) -> Vec<(String, bool, &'static str)> {
    let mut lines: Vec<(String, bool, &'static str)> = Vec::new();
    if !lenders.is_empty() {
        if let Some(problem) = problem.map(str::to_string).or_else(|| pool.problem.clone()) {
            lines.push((problem, true, "shared-usage.problem"));
        }
    }
    if let Some((text, attention)) = environment_line(pool.environment.get(claude)) {
        lines.push((text, attention, "shared-usage.environment"));
    }
    // The relay is what a connected Claude Code sends everything through, so
    // after a restart it has to be back before that Claude is used.
    if !lenders.is_empty() && !settings::login_item_enabled() {
        lines.push(("Open at Login keeps this working after a restart".into(), false, OPEN_AT_LOGIN_NOTE));
    }
    let (mut first, rest): (Vec<_>, Vec<_>) = lines.into_iter().partition(|l| l.1);
    first.extend(rest);
    first
}

pub fn environment_line(state: Option<&State>) -> Option<(String, bool)> {
    match state? {
        State::Waiting => Some(("Starts the next time this Claude opens.".into(), false)),
        State::Leaving => Some(("Stops when this Claude next closes.".into(), false)),
        State::Conflict { name, .. } => Some((format!("Not shared: this Claude already sets {name}"), true)),
        State::Unreadable => Some(("Not shared: this Claude's Code environment is unreadable".into(), true)),
        State::On | State::Off => None,
    }
}

/// Adding is in the heading, where it belongs to the whole list; a sign-in
/// under way, or one that failed, stands where Add is while it lasts.
fn header_trailing(pool: &Controller, claude: &str, lenders: &[String], anchor: &gtk::Box) -> gtk::Widget {
    let phase = shared().sign_in.borrow().clone();
    match phase {
        SignInPhase::Waiting(for_claude) if for_claude == claude => {
            let row = gtk::Box::new(gtk::Orientation::Horizontal, 6);
            row.append(&adw::Spinner::new());
            let words = ui::caption("Finish signing in in your browser.");
            identified(&words, "window.shared-usage.signing-in", "Finish signing in in your browser.");
            row.append(&words);
            let cancel = gtk::Button::with_label("Cancel");
            cancel.add_css_class("flat");
            identified(&cancel, "window.shared-usage.cancel-sign-in", "Cancel Signing In");
            cancel.connect_clicked(|_| sharing::cancel_sign_in());
            row.append(&cancel);
            row.upcast()
        }
        SignInPhase::Failed(for_claude, message) if for_claude == claude => {
            let row = gtk::Box::new(gtk::Orientation::Horizontal, 6);
            let words = ui::caption(&message);
            words.remove_css_class("dim-label");
            words.add_css_class("warning");
            words.set_tooltip_text(Some(&message));
            identified(&words, "window.shared-usage.sign-in-failed", &message);
            row.append(&words);
            let ok = gtk::Button::with_label("OK");
            ok.add_css_class("flat");
            identified(&ok, "window.shared-usage.dismiss-sign-in-failure", "Dismiss");
            ok.connect_clicked(|_| sharing::dismiss_sign_in_failure());
            row.append(&ok);
            row.upcast()
        }
        phase => {
            let (popover, menu) = graft_web::ui::menu_column();
            let taken: Vec<&str> = lenders.iter().map(String::as_str).chain(std::iter::once(claude)).collect();
            let candidates: Vec<String> = pool
                .claudes()
                .into_iter()
                .chain(pool.accounts.iter().map(|a| a.id.clone()))
                .filter(|k| !taken.contains(&k.as_str()))
                .collect();
            for key in &candidates {
                let name = pool.name_of(key);
                let key = key.clone();
                let for_claude = claude.to_string();
                graft_web::ui::menu_item(&menu, &popover, &name, &format!("window.shared-usage.add.{}", token(pool, &key)), move |_| {
                    let key = key.clone();
                    let for_claude = for_claude.clone();
                    sharing::change(move |p| p.add_lender(&key, &for_claude));
                });
            }
            if !candidates.is_empty() {
                menu.append(&gtk::Separator::new(gtk::Orientation::Horizontal));
            }
            let for_claude = claude.to_string();
            let anchor = anchor.clone();
            let sign_in = graft_web::ui::menu_item(&menu, &popover, "Sign In to Another Account...", "window.shared-usage.sign-in", move |_| {
                sharing::sign_in(&for_claude, Some(anchor.upcast_ref()));
            });
            sign_in.set_sensitive(!matches!(phase, SignInPhase::Waiting(_)));
            let label = if lenders.is_empty() { "Add an Account" } else { "Add Another Account" };
            let button = gtk::MenuButton::builder().popover(&popover).build();
            button.set_child(Some(&adw::ButtonContent::builder().icon_name("list-add-symbolic").label(label).build()));
            button.add_css_class("flat");
            identified(&button, "window.shared-usage.add", label);
            button.upcast()
        }
    }
}

/// A Claude goes by its profile folder, as every other control naming a
/// profile does, and an account by the id it was saved under, which is the
/// same in every language where the email on screen may not be unique.
pub fn token(pool: &Controller, key: &str) -> String {
    if let Some(profile) = pool.profile_of(key) {
        return profile.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    }
    key.strip_prefix(ACCOUNT_PREFIX).unwrap_or(key).to_string()
}

/// Five hours where there is such a window, the week, and their resets.
pub type Figures = (Option<i64>, i64, Option<std::time::SystemTime>, Option<std::time::SystemTime>);

/// A Claude's own figure where there is one, since Graft asks for it; an
/// account signed in here has only what the service said on its answers. The
/// service names a five-hour reset on every answer for an account that has
/// the window, so one without a reset is taken to have none rather than shown
/// at a 0% that never moves.
pub fn figures(entry: Option<&graft_usage::Entry>, reported: Option<&PoolMemberStatus>) -> Option<Figures> {
    if let Some(usage) = entry.and_then(|e| e.usage.as_ref()) {
        return Some((usage.five_hour, usage.week, usage.five_hour_reset, usage.week_reset));
    }
    let limits = reported?.limits.as_ref()?;
    let five = if limits.five_hour_reset.is_none() && limits.five_hour == 0.0 {
        None
    } else {
        Some((limits.five_hour * 100.0).round() as i64)
    };
    Some((five, (limits.week * 100.0).round() as i64, limits.five_hour_reset.map(time_of), limits.week_reset.map(time_of)))
}

pub fn figures_line(five: Option<i64>, week: i64) -> String {
    match five {
        Some(five) => format!("5h {five}% \u{b7} Week {week}%"),
        None => format!("Week {week}%"),
    }
}

/// What a lender's line says after its name.
pub fn condition(pool: &Controller, lender: &str, reported: Option<&PoolMemberStatus>, now: f64) -> String {
    let is_account = lender.starts_with(ACCOUNT_PREFIX);
    if reported.map(|r| r.token_refused).unwrap_or(false) {
        return if is_account { "Login refused \u{b7} sign in again".into() } else { "Login refused \u{b7} open that Claude to renew it".into() };
    }
    match pool.lending.get(lender) {
        None => return "Checking its login...".into(),
        Some(Lending::SignedOut) => return "Not signed in".into(),
        Some(Lending::Expired) => {
            return if is_account { "Login expired \u{b7} sign in again".into() } else { "Login expired \u{b7} open that Claude to renew it".into() };
        }
        Some(Lending::Unreadable) => return "Login locked \u{b7} unlock the keyring from the tray".into(),
        Some(Lending::Ready) => {}
    }
    if let Some(limits) = reported.and_then(|r| r.limits.as_ref()) {
        if !policy::has_room(Some(limits), now) {
            return match limits.refused_until.or(limits.five_hour_reset) {
                Some(until) => format!("Full until {}", graft_web::ui::clock(time_of(until))),
                None => "Full".into(),
            };
        }
    }
    match reported.map(|r| r.served).unwrap_or(0) {
        0 => "Ready".into(),
        served => format!("Ready \u{b7} answered {served} requests"),
    }
}

fn lender_row(pool: &Controller, claude: &str, lender: &str) -> gtk::Box {
    let shared = shared();
    let row = ui::card_row(8);
    let name = pool.name_of(lender);
    let reported = pool.status.as_ref().and_then(|s| s.members.iter().find(|m| m.id == lender));
    let entry = pool.profile_of(lender).and_then(|p| shared.snapshot.borrow().entry_for(&p).cloned());
    let own_uuid = pool.account_uuid_of(claude);
    let same_as_borrower = own_uuid.is_some() && pool.account_uuid_of(lender) == own_uuid;
    let mut parts = Vec::new();
    if same_as_borrower {
        parts.push("Same account as this Claude".to_string());
    } else if let Some(other) = pool.same_account(lender, claude) {
        parts.push(format!("Same account as {}", pool.name_of(&other)));
    }
    parts.push(condition(pool, lender, reported, graft_pool::now()));
    let state = parts.join(" \u{b7} ");
    let attention = same_as_borrower
        || reported.map(|r| r.token_refused).unwrap_or(false)
        || pool.lending.get(lender).map(|l| *l != Lending::Ready).unwrap_or(false);

    // Name and state are one element and the button beside them another:
    // spoken over the whole row, the button took the row's name and the
    // account was said twice and the action never.
    let who = gtk::Box::new(gtk::Orientation::Horizontal, 8);
    who.append(&icons::image(pool.preset_of(lender), 16));
    let title = gtk::Label::new(Some(&name));
    title.add_css_class("heading");
    title.set_ellipsize(gtk::pango::EllipsizeMode::End);
    who.append(&title);
    let words = ui::caption(&state);
    if attention {
        words.remove_css_class("dim-label");
        words.add_css_class("warning");
    }
    words.set_tooltip_text(Some(&state));
    who.append(&words);
    who.set_accessible_role(gtk::AccessibleRole::Group);
    let token = token(pool, lender);
    identified(&who, &format!("window.lender.{token}"), &name);
    who.update_property(&[gtk::accessible::Property::Description(&state)]);
    who.set_hexpand(true);
    row.append(&who);
    if let Some((five, week, five_reset, week_reset)) = figures(entry.as_ref(), reported) {
        let line = figures_line(five, week);
        let label = ui::caption(&line);
        if entry.as_ref().map(|e| e.is_dimmed()).unwrap_or(false) {
            label.add_css_class("graft-dimmed");
        }
        let help: Vec<String> = [
            five_reset.map(|r| format!("5 hours reset {}", graft_web::ui::clock(r))),
            week_reset.map(|r| format!("Week resets {}", graft_web::ui::clock(r))),
        ]
        .into_iter()
        .flatten()
        .collect();
        if !help.is_empty() {
            label.set_tooltip_text(Some(&help.join("\n")));
        }
        identified(&label, &format!("window.lender.{token}.usage"), &line);
        row.append(&label);
    }
    let remove = ui::icon_button("list-remove-symbolic", &format!("window.lender.{token}.remove"), &format!("Stop drawing on {name}"));
    remove.set_tooltip_text(Some("Stop drawing on this account"));
    let lender = lender.to_string();
    let claude = claude.to_string();
    remove.connect_clicked(move |_| {
        let lender = lender.clone();
        let claude = claude.clone();
        sharing::change(move |p| p.remove_lender(&lender, &claude));
    });
    row.append(&remove);
    row
}

/// The tray window's word that the relay is carrying on with the logins it
/// was last given, because this Graft cannot read the key to give it current
/// ones.
pub fn lending_paused() -> Option<gtk::Button> {
    if !shared().pool.borrow().lending_paused {
        return None;
    }
    let button = gtk::Button::new();
    button.set_child(Some(&adw::ButtonContent::builder().icon_name("changes-prevent-symbolic").label("Lending is paused until the keyring is unlocked").build()));
    button.add_css_class("flat");
    button.set_tooltip_text(Some("Graft cannot read Claude's logins until the keyring is unlocked, so it cannot hand them to the relay. The desktop asks once."));
    identified(&button, "dropdown.lending-paused", "Lending is paused until the keyring is unlocked");
    button.connect_clicked(|_| sharing::open_keyring());
    Some(button)
}

/// Who answered last, or who it was moved to and has not answered yet, and
/// whether that was chosen or left to the ranking: with Auto in front it may
/// move on its own, without it it stays where it was put.
pub fn answering(pool: &Controller, chat: &Chat) -> String {
    match chats::answering_key(chat) {
        None => "Automatic".into(),
        Some(key) if chat.assigned.is_none() => format!("Auto \u{b7} {}", pool.name_of(&key)),
        Some(key) => pool.name_of(&key),
    }
}

pub fn detail(pool: &Controller, chat: &Chat) -> String {
    match (&chat.assigned, chat.passed_over) {
        (Some(assigned), true) => format!("{} had no room", pool.name_of(assigned)),
        _ => format!("in {}", pool.name_of(&chat.claude)),
    }
}

/// The conversations the relay is carrying, the one in front first and on
/// its own, the rest behind a line saying how many: five rows of other
/// conversations pushed the figures out of a short screen.
pub fn conversations(front: Option<&Path>) -> Option<gtk::Box> {
    let shared = shared();
    let pool = shared.pool.borrow();
    let list = pool.chats(front, graft_pool::now());
    if list.is_empty() {
        return None;
    }
    let front_key = front.and_then(|f| pool.claude_key_for(f));
    let in_front = list.first().filter(|c| Some(&c.claude) == front_key.as_ref()).cloned();
    let others: Vec<Chat> = list.iter().filter(|c| Some(&c.id) != in_front.as_ref().map(|f| &f.id)).cloned().collect();
    let several_claudes = list.iter().map(|c| &c.claude).collect::<std::collections::BTreeSet<_>>().len() > 1;

    let column = gtk::Box::new(gtk::Orientation::Vertical, 6);
    ui::padded(&column, 14, 6);
    let heading = ui::caption("Claude Code");
    heading.set_accessible_role(gtk::AccessibleRole::Heading);
    identified(&heading, "dropdown.conversations", "Claude Code");
    column.append(&heading);
    let rest = gtk::Box::new(gtk::Orientation::Vertical, 6);
    for chat in &others {
        rest.append(&chat_row(&pool, chat, several_claudes, false));
    }
    if let Some(chat) = &in_front {
        column.append(&chat_row(&pool, chat, false, true));
        if !others.is_empty() {
            let shown = Rc::new(Cell::new(false));
            let words = format!("Other conversations \u{b7} {}", others.len());
            let toggle = gtk::Button::new();
            toggle.set_child(Some(&adw::ButtonContent::builder().icon_name("pan-end-symbolic").label(&words).build()));
            toggle.add_css_class("flat");
            identified(&toggle, "dropdown.conversations.others", &words);
            toggle.update_state(&[gtk::accessible::State::Expanded(Some(false))]);
            rest.set_visible(false);
            let rest_ref = rest.clone();
            toggle.connect_clicked(move |button| {
                shown.set(!shown.get());
                rest_ref.set_visible(shown.get());
                button.update_state(&[gtk::accessible::State::Expanded(Some(shown.get()))]);
                if let Some(content) = button.child().and_downcast::<adw::ButtonContent>() {
                    content.set_icon_name(if shown.get() { "pan-down-symbolic" } else { "pan-end-symbolic" });
                }
            });
            column.append(&toggle);
        }
    }
    column.append(&rest);
    Some(column)
}

fn chat_row(pool: &Controller, chat: &Chat, show_claude: bool, in_front: bool) -> gtk::Box {
    let row = gtk::Box::new(gtk::Orientation::Horizontal, 8);
    let title = chat.title.clone().unwrap_or_else(|| "Untitled conversation".into());
    let shows_detail = show_claude || chat.passed_over;
    let detail_text = if shows_detail { detail(pool, chat) } else { String::new() };
    let words = gtk::Box::new(gtk::Orientation::Vertical, 1);
    let heading = gtk::Label::new(Some(&title));
    heading.set_xalign(0.0);
    heading.set_ellipsize(gtk::pango::EllipsizeMode::End);
    if in_front {
        heading.add_css_class("heading");
    }
    words.append(&heading);
    if shows_detail {
        let under = ui::caption(&detail_text);
        if chat.passed_over {
            under.remove_css_class("dim-label");
            under.add_css_class("warning");
        }
        words.append(&under);
    }
    words.set_hexpand(true);
    words.set_accessible_role(gtk::AccessibleRole::Group);
    identified(&words, &format!("dropdown.conversation.{}", chat.id), &title);
    words.update_property(&[gtk::accessible::Property::Description(&detail_text)]);
    row.append(&words);

    // A list of radio buttons, so the choice is drawn the way any choice is,
    // with the account and Automatic side by side.
    let (popover, menu) = graft_web::ui::menu_column();
    let mut group: Option<gtk::CheckButton> = None;
    let mut options: Vec<(Option<String>, String)> = vec![(None, pool.automatic_title(chat))];
    options.extend(chat.choices.iter().map(|c| (Some(c.clone()), pool.name_of(c))));
    for (key, label) in options {
        let check = gtk::CheckButton::with_label(&label);
        if let Some(first) = &group {
            check.set_group(Some(first));
        } else {
            group = Some(check.clone());
        }
        check.set_active(chat.assigned == key);
        identified(&check, &format!("dropdown.conversation.{}.account.{}", chat.id, key.as_deref().unwrap_or("automatic")), &label);
        let session = chat.id.clone();
        let pop = popover.clone();
        check.connect_toggled(move |c| {
            if !c.is_active() {
                return;
            }
            pop.popdown();
            let session = session.clone();
            let key = key.clone();
            sharing::change(move |p| p.assign(&session, key.as_deref()));
        });
        menu.append(&check);
    }
    let answering_words = answering(pool, chat);
    let content = gtk::Box::new(gtk::Orientation::Horizontal, 4);
    if let Some(key) = chats::answering_key(chat) {
        content.append(&icons::image(pool.preset_of(&key), 14));
    }
    content.append(&ui::caption(&answering_words));
    let button = gtk::MenuButton::builder().popover(&popover).child(&content).build();
    button.add_css_class("flat");
    button.set_tooltip_text(Some("Which account answers this conversation"));
    // The button shows only the account, and five of them reading "Claude 2"
    // would not say which conversation each one moves.
    identified(&button, &format!("dropdown.conversation.{}.account", chat.id), &format!("Account answering \u{201c}{title}\u{201d}"));
    button.update_property(&[gtk::accessible::Property::Description(&answering_words)]);
    row.append(&button);
    row
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_lent_account_without_a_five_hour_reset_has_no_five_hour_window() {
        let reported = PoolMemberStatus {
            id: "account:x".into(),
            limits: Some(graft_pool::PoolLimits::from_percent(None, None, 94, Some(1_800_000_000.0), 0.0)),
            served: 3,
            last_served: None,
            last_seen: None,
            token_refused: false,
        };
        let (five, week, _, _) = figures(None, Some(&reported)).unwrap();
        assert_eq!(five, None, "a zero with no reset is no window, not a window at nothing");
        assert_eq!(week, 94);
        assert_eq!(figures_line(five, week), "Week 94%");
        assert_eq!(figures_line(Some(12), 40), "5h 12% \u{b7} Week 40%");
    }

    #[test]
    fn what_this_claude_waits_on_is_said_in_one_line_with_trouble_first() {
        assert_eq!(environment_line(Some(&State::Waiting)).map(|l| l.1), Some(false));
        assert_eq!(environment_line(Some(&State::Conflict { name: "HTTPS_PROXY".into(), holding: false })),
            Some(("Not shared: this Claude already sets HTTPS_PROXY".into(), true)), "a proxy a person set is said, never replaced");
        assert_eq!(environment_line(Some(&State::On)), None, "lending that works needs no line");
    }

    #[test]
    fn an_answering_account_says_whether_it_was_chosen() {
        let pool = Controller::default();
        let mut chat = Chat {
            id: "s".into(),
            title: None,
            claude: "main".into(),
            served_by: Some("main".into()),
            assigned: None,
            choices: vec!["main".into()],
            last_at: 0.0,
            passed_over: false,
            started_at: None,
            focused_at: None,
            prompted_at: None,
            carried: true,
        };
        assert_eq!(answering(&pool, &chat), "Auto \u{b7} Claude", "left to the ranking it says Auto");
        chat.assigned = Some("main".into());
        assert_eq!(answering(&pool, &chat), "Claude", "and chosen it names the account alone");
        chat.served_by = None;
        chat.assigned = None;
        assert_eq!(answering(&pool, &chat), "Automatic", "before anyone has answered it says Automatic");
    }
}
