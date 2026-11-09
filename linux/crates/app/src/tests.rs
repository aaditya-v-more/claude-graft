//! The rules about the app that live in how its sources are written: which
//! routes ask before opening, what may start a session, what may start a
//! Claude. Read from the sources themselves, as the Mac suite reads its own.

use std::path::PathBuf;

fn source_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("src")
}

fn read(name: &str) -> String {
    std::fs::read_to_string(source_dir().join(name)).unwrap_or_default()
}

/// Every source file but this one, which names what it looks for.
fn sources() -> Vec<(String, String)> {
    let mut out: Vec<(String, String)> = std::fs::read_dir(source_dir())
        .unwrap()
        .filter_map(|e| e.ok())
        .map(|e| e.file_name().to_string_lossy().into_owned())
        .filter(|n| n.ends_with(".rs") && n != "tests.rs")
        .map(|n| {
            let text = read(&n);
            (n, text)
        })
        .collect();
    out.sort();
    out
}

/// A file's lines with the test module left off, so a rule is checked
/// against what runs.
fn running_lines(text: &str) -> Vec<String> {
    let cut = text.find("#[cfg(test)]").unwrap_or(text.len());
    text[..cut].lines().map(str::to_string).collect()
}

#[test]
fn the_app_sources_are_there_to_be_read() {
    assert!(sources().len() > 10, "the app's own source is there to be read");
}

#[test]
fn every_route_that_opens_a_profile_asks_who_else_is_on_its_chats() {
    for file in ["main_detail.rs", "shortcut_detail.rs", "dropdown.rs"] {
        assert!(read(file).contains("chat_conflict::ask_then_open("), "{file} asks before opening a profile something else is on");
    }
    assert!(read("chat_conflict.rs").contains("conflict::open_sharers(") && read("chat_conflict.rs").contains("conflict::message("),
        "and the question is the profiles crate's rule, in its words");
}

#[test]
fn nothing_in_the_app_starts_a_claude_without_asking_who_is_there() {
    let forbidden = ["start_claude(", "launch::launch(", "launch::open(", "launch::reveal(", "launch::run("];
    for (file, text) in sources() {
        for line in running_lines(&text) {
            for word in forbidden {
                assert!(!line.contains(word), "{file} calls {word} directly instead of going through the opener: {line}");
            }
        }
    }
    let opens: Vec<String> = sources()
        .into_iter()
        .filter(|(_, text)| running_lines(text).iter().any(|l| l.contains("opener::open(")))
        .map(|(name, _)| name)
        .collect();
    assert_eq!(opens, vec!["chat_conflict.rs".to_string()], "and the one call to the opener is the one behind the question");
}

#[test]
fn only_the_three_start_session_buttons_ask_anthropic_for_a_session() {
    let runs_on_its_own = ["timeout_add", "idle_add", "connect_map", "connect_realize", "connect_show", "every(", "startup"];
    let mut callers = Vec::new();
    for (file, text) in sources() {
        for line in running_lines(&text) {
            if line.contains("graft_usage::start_session(") {
                callers.push(file.clone());
                assert!(line.contains(", true)"), "{file} says a person asked, which is what allows the keyring prompt: {line}");
                assert!(!runs_on_its_own.iter().any(|w| line.contains(w)), "{file} wires a session to something that runs on its own: {line}");
            }
        }
    }
    callers.sort();
    assert_eq!(callers, vec!["dropdown.rs", "main_detail.rs", "shortcut_detail.rs"], "only the three Start Session buttons ask Anthropic for a session");
    for file in ["dropdown.rs", "main_detail.rs", "shortcut_detail.rs"] {
        let text = read(file);
        assert!(text.contains("Refresh::after_session_started("), "{file}'s refresh after a session starts asks nobody, and only about that account");
        assert!(text.contains("monitor.invalidate("), "{file} drops the reading the session made stale");
    }
}

#[test]
fn nothing_reaches_for_the_unconditional_keyring_prompt_by_hand() {
    let mut naming = Vec::new();
    for (file, text) in sources() {
        let count = running_lines(&text).iter().filter(|l| l.contains("Prompting::Yes")).count();
        if count > 0 {
            naming.push((file, count));
        }
    }
    assert_eq!(naming, vec![("sharing.rs".to_string(), 1)], "only a sign-in a person pressed may raise the keyring, through one helper");
}

#[test]
fn a_new_claude_is_given_its_colour_once_and_never_claudes_own() {
    use graft_core::IconPreset;
    assert_eq!(IconPreset::next(&[IconPreset::Original]), IconPreset::Blue, "a new Claude takes the first colour nothing has");
    assert_eq!(IconPreset::next(&[IconPreset::Original, IconPreset::Blue, IconPreset::Violet]), IconPreset::Green);
    let mut all = vec![IconPreset::Original];
    all.extend(IconPreset::HANDED_OUT);
    all.extend(IconPreset::HANDED_OUT.iter().filter(|p| **p != IconPreset::Teal));
    assert_eq!(IconPreset::next(&all), IconPreset::Teal, "once every colour is in use, the least used comes round again");
    assert!(!IconPreset::HANDED_OUT.contains(&IconPreset::Original), "Claude's own icon is never handed to another Claude");
    let window = read("window.rs");
    assert!(window.contains("let colours = shared().pool.borrow().colours_in_use();") && window.contains("store.add_new(&colours)"),
        "a new Claude is given its colour when it is made, counting every lent account, and nothing recolours one that exists");
    let others = sources().into_iter().filter(|(n, t)| n != "window.rs" && running_lines(t).iter().any(|l| l.contains("IconPreset::next(")));
    assert_eq!(others.count(), 0, "nothing else hands out a colour");
}

#[test]
fn closing_and_quitting_ask_whether_the_item_can_be_reached() {
    let app = read("app.rs");
    assert!(app.contains("WindowPolicy::hides_window(show_in_tray, shared.has_shown_window.get(), shared.reach.is_showing())"),
        "the window closing asks whether the item can be reached before leaving Graft windowless");
    assert!(app.contains("glib::timeout_add_local_once(WindowPolicy::PLACEMENT_GRACE") && app.contains("WindowPolicy::shows_window_after_hidden_launch("),
        "a hidden start looks again once the item has had time to be placed");
    assert!(app.contains("QuitPolicy::ends_the_app(false, false, shared.reach.is_showing())"), "Ctrl+Q asks the policy, with the item's reach");
    assert!(app.contains("QuitPolicy::ends_the_app(true, false"), "and Quit is the one asked for");
    assert!(app.contains("\"<Control>q\""), "Ctrl+Q is wired to the policy rather than to quitting");
    assert!(!app.contains("show_in_tray &&") || app.contains("hidden = !settings.main_window_open && settings.show_in_tray"),
        "the setting alone decides only whether a start comes up hidden");
}

#[test]
fn nothing_that_waits_runs_on_the_main_loop() {
    // Every process listing, sweep, keyring read, install, delete and open
    // in the app goes through `off_main`, or runs on a thread of its own.
    let waits = [
        "claude::is_running(",
        "claude::processes(",
        "claude::version(",
        "records.sweep(",
        "installer::install(",
        "store.delete(",
        "opener::open(",
        "conflict::open_sharers(",
        "adopt_chats(",
        "chats_elsewhere(",
        "controller::run_pass(",
        "controller::peek(",
        "controller::open_keychain(",
        ".wait(own",
        "refresh_launchers(",
    ];
    for (file, text) in sources() {
        let lines = running_lines(&text);
        for (index, line) in lines.iter().enumerate() {
            if !waits.iter().any(|w| line.contains(w)) {
                continue;
            }
            let before = lines[..=index].iter().rev().take(15).cloned().collect::<Vec<_>>().join("\n");
            assert!(before.contains("off_main(") || before.contains("move ||"),
                "{file} waits on the main loop: {line}");
        }
    }
}

#[test]
fn every_control_is_named_and_identified_by_hand() {
    for (file, text) in sources() {
        let lines = running_lines(&text);
        let controls = lines
            .iter()
            .filter(|l| {
                ["gtk::Button::with_label(", "gtk::Button::new()", "gtk::CheckButton::with_label(", "adw::SwitchRow::new()", "gtk::Entry::new()", "gtk::DropDown::from_strings(", "gtk::MenuButton::new()", "gtk::MenuButton::builder()"]
                    .iter()
                    .any(|c| l.contains(c))
            })
            .count();
        let named = lines.iter().filter(|l| l.contains("identified(")).count();
        assert!(named >= controls, "{file} makes {controls} controls and names {named}");
    }
}

#[test]
fn the_tray_never_retitles_on_a_timer() {
    let tray = read("tray.rs");
    assert!(!tray.contains("timeout_add") && !tray.contains("thread::sleep"),
        "the tray is handed a new look when what it says changes, never on a tick");
    assert!(tray.contains("if self.last.as_ref() == Some(&look)"), "and a look that says the same is not sent at all");
}

#[test]
fn the_window_root_is_a_split_view_with_notices_inside_its_content() {
    let window = read("window.rs");
    assert!(window.contains("adw::NavigationSplitView::new()") && window.contains("window.set_content(Some(&split))"),
        "the window's root is a split view with a real sidebar");
    let content = window.find("let column = gtk::Box::new").unwrap_or(0);
    assert!(window[content..].contains("column.append(&tray_notice())"), "a notice sits inside the content column, never above the split view");
    assert!(window.contains("window.set_size_request(820, 560)"), "and the window goes no smaller than every page is laid out for");
}

#[test]
fn the_pages_are_a_list_the_sidebar_is_built_from() {
    let window = read("window.rs");
    for page in ["Page::Main =>", "Page::Artifacts =>", "Page::Projects =>", "Page::Search =>", "Page::Shortcut(id) =>"] {
        assert!(window.contains(page), "page_widget builds {page}");
    }
    assert!(window.contains("graft_web::ui::artifacts_page(&shared.artifacts)") && window.contains("graft_web::ui::search_page(&shared.projects)"),
        "the web pages come from the web crate, over the one centre each");
    let shared = read("shared.rs");
    assert_eq!(shared.matches("ArtifactCenter::new()").count(), 1, "one artifact centre for the process");
    assert_eq!(shared.matches("ProjectCenter::new(").count(), 1, "and one project centre following it");
}

#[test]
fn the_records_walk_dates_each_record_with_one_lstat() {
    let pool = std::fs::read_to_string(PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../pool/src/chats.rs")).unwrap();
    let walk = pool.split("pub fn look_up(").nth(1).unwrap_or("");
    let walk = walk.split("\n    }\n").next().unwrap_or("");
    assert!(walk.contains("modification_time(") && !walk.contains("std::fs::metadata("),
        "the walk the tray runs every two seconds dates each record with one lstat");
}

#[test]
fn sharing_is_driven_off_the_main_loop_and_paired_with_the_tray_window() {
    let sharing = read("sharing.rs");
    assert!(sharing.contains("PASS_INTERVAL_WATCHED") && sharing.contains("PEEK_INTERVAL"), "passes speed up while somebody watches, and looks run between them");
    let dropdown = read("dropdown.rs");
    assert!(dropdown.contains("sharing::watch();") && dropdown.contains("sharing::unwatch();"), "the tray's window watches the pool and lets it go where it closes");
    assert!(read("app.rs").contains("sharing::usage_changed()"), "a Claude that quit is noticed from the figures' process checks and settled");
}
