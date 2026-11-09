use super::*;
use crate::testing::{signed_in, tomorrow, StandIn};
use graft_core::testing::Sandbox;
use graft_core::{IconPreset, Shortcut, Source};
use std::sync::atomic::AtomicUsize;

const SECOND: Duration = Duration::from_secs(1);

fn secs(n: u64) -> Duration {
    Duration::from_secs(n)
}

// MARK: how hard the endpoint is asked

#[test]
fn a_tick_nobody_asked_for_reuses_a_figure_up_to_five_minutes_old() {
    assert_eq!(LIVE_INTERVAL, secs(300));
    assert!(may_use_cache(secs(120), Freshness::Cached, false), "two minutes old is fresh enough for a background pass");
    assert!(!may_use_cache(secs(120), Freshness::Recent, false), "but not for someone who just asked to see the number");
    assert!(may_use_cache(secs(10), Freshness::Recent, false), "asking twice in ten seconds still only asks the service once");
    assert!(RECENT_INTERVAL < LIVE_INTERVAL, "someone looking gets a shorter cache than a timer does");
    assert!(RECENT_INTERVAL >= secs(30), "with a floor, since a button can be pressed faster than a service wants to answer");
}

#[test]
fn someone_who_pressed_the_button_gets_the_service_asked_not_the_last_answer() {
    assert!(!may_use_cache(secs(30), Freshness::Now, false));
    assert!(may_use_cache(SECOND, Freshness::Now, false), "though a double-click is still one request");
    assert!(NOW_INTERVAL < RECENT_INTERVAL, "pressing for a figure beats merely looking at one");
}

#[test]
fn a_press_arriving_mid_pass_waits_for_its_turn_rather_than_being_lost() {
    assert_eq!(arrival(false, true, Freshness::Cached), Arrival::Start, "with nothing running, a press goes straight out");
    assert_eq!(arrival(false, false, Freshness::Cached), Arrival::Start, "and so does a tick");
    assert_eq!(arrival(true, true, Freshness::Cached), Arrival::Queue, "a press arriving mid-pass waits for its turn");
    assert_eq!(arrival(true, false, Freshness::Cached), Arrival::Drop, "but a tick is worth dropping, since the pass will answer it anyway");
    assert_eq!(arrival(true, false, Freshness::Recent), Arrival::Queue, "opening the tray mid-pass waits for its turn too");
    assert!(
        stronger(Freshness::Recent, Freshness::Now) == Freshness::Now
            && stronger(Freshness::Cached, Freshness::Recent) == Freshness::Recent,
        "two requests waiting on one pass become one request for the stronger"
    );
}

#[test]
fn two_requests_waiting_on_one_pass_keep_what_each_asked_for() {
    let a = PathBuf::from("/home/a/.config/Claude-2");
    let b = PathBuf::from("/home/a/.config/Claude-3");
    let first = merge_waiting(None, false, Prompting::OnceIfShut, Freshness::Recent, Some(a.clone()));
    assert_eq!(first.prompting, Prompting::OnceIfShut, "opening the tray mid-pass is not replayed as a press");
    assert!(!first.interactive);

    let pressed = merge_waiting(Some(first.clone()), true, Prompting::Yes, Freshness::Now, Some(a.clone()));
    assert!(pressed.interactive && pressed.prompting == Prompting::Yes && pressed.freshness == Freshness::Now);
    assert_eq!(pressed.focus, Some(a.clone()), "two requests about one profile stay about it");

    let ticked = merge_waiting(Some(pressed.clone()), false, Prompting::No, Freshness::Cached, Some(a.clone()));
    assert_eq!(ticked.prompting, Prompting::Yes, "a quieter request does not take a press's dialog away");

    let both = merge_waiting(Some(first), false, Prompting::OnceIfShut, Freshness::Recent, Some(b));
    assert_eq!(both.focus, None, "requests about two profiles become one about everybody");
}

#[test]
fn an_open_claudes_figure_is_asked_for_again_within_a_couple_of_minutes() {
    assert!(!may_use_cache(secs(150), Freshness::Cached, true));
    assert!(may_use_cache(secs(150), Freshness::Cached, false), "while a closed one keeps the five-minute interval");
    assert!(ACTIVE_INTERVAL >= secs(60), "and even an open one is not asked more than once a minute by the timer");
}

#[test]
fn a_record_the_profile_wrote_after_the_last_live_answer_is_the_better_figure() {
    let fetched = SystemTime::now();
    assert!(prefers_local(fetched, Some(fetched + secs(30))));
    assert!(!prefers_local(fetched, Some(fetched - secs(30))), "and an older one is not");
    assert!(!prefers_local(fetched, None), "and no record at all leaves the live answer standing");
}

#[test]
fn a_claude_brought_forward_is_matched_to_the_profile_it_was_launched_on() {
    let sandbox = Sandbox::new();
    let _ = &sandbox;
    let main = paths::main_profile();
    let two = paths::profile_dir("Claude-2");
    let twenty = paths::profile_dir("Claude-20");
    let binary = "/usr/lib/claude-desktop-unofficial/claude-desktop";
    let among = vec![main.clone(), twenty.clone(), two.clone()];
    assert_eq!(profile_launched_by(&format!("{binary} --user-data-dir={}", two.display()), &among), Some(two.clone()));
    assert_eq!(profile_launched_by(binary, &among), Some(main), "one launched with no profile at all is Claude's own");
    assert_eq!(
        profile_launched_by(&format!("{binary} --type=renderer --user-data-dir={}", two.display()), &among),
        None,
        "and a helper process is nobody's account"
    );
}

#[test]
fn failures_back_off_longer_each_time_and_settle_at_half_an_hour() {
    let first = retry_delay(1, None);
    let second = retry_delay(2, None);
    let fifth = retry_delay(5, None);
    assert!(first >= secs(60), "the first failure waits at least a minute");
    assert!(second > first, "and each further one waits longer");
    assert!(fifth >= secs(1800), "settling at half an hour");
    assert_eq!(retry_delay(50, None), fifth, "the wait is capped rather than growing without end");
    assert_eq!(retry_delay(1, Some(900.0)), secs(900), "a Retry-After from the service is honoured exactly");
    assert!(retry_delay(4, Some(30.0)) >= secs(60), "a suspiciously short one is still floored at a minute");
}

#[test]
fn a_press_may_skip_graft_s_own_wait_but_never_the_services() {
    let now = SystemTime::now();
    assert!(may_attempt(now, None, false, false), "nothing outstanding means go ahead");
    assert!(!may_attempt(now, Some(now + secs(300)), false, false), "a background pass waits its turn");
    assert!(may_attempt(now, Some(now + secs(300)), true, false), "a person pressing refresh may skip our own wait");
    assert!(!may_attempt(now, Some(now + secs(300)), true, true), "but not one the service asked for");
    assert!(may_attempt(now, Some(now - SECOND), false, true), "and an expired wait releases either way");
}

#[test]
fn only_the_services_own_answer_is_the_services_wait() {
    assert_eq!(wait_asked(&LiveError::Api(ApiFailure::Http { code: 429, retry_after: Some(900.0) })), Some(900.0));
    assert_eq!(wait_asked(&LiveError::Api(ApiFailure::Http { code: 500, retry_after: None })), None);
    assert_eq!(wait_asked(&LiveError::Api(ApiFailure::Unreachable("timed out".into()))), None);
}

#[test]
fn a_login_that_cannot_be_read_here_is_retried_on_the_next_pass_with_no_back_off() {
    let local = [
        Failure::NotSignedIn,
        Failure::Expired,
        Failure::ConfigUnreadable,
        Failure::NoKeyringAccess,
        Failure::KeyringDeclined,
    ];
    assert!(local.iter().all(|f| !came_from_service(&LiveError::Credentials(*f))));
    assert!(
        came_from_service(&LiveError::Api(ApiFailure::Http { code: 503, retry_after: None }))
            && came_from_service(&LiveError::Api(ApiFailure::Unreadable))
            && came_from_service(&LiveError::Api(ApiFailure::Unreachable("offline".into()))),
        "while an answer from the service, or none, backs off as before"
    );
    let anthropic = "Anthropic didn't answer";
    assert!(
        local.iter().all(|f| failure_line(&LiveError::Credentials(*f)) != anthropic),
        "and a local failure never reads as Anthropic not answering"
    );
    assert_eq!(failure_line(&LiveError::Api(ApiFailure::Unreachable("timed out".into()))), anthropic, "which is kept for the service");
}

#[test]
fn once_the_keyring_has_said_no_in_a_pass_no_profile_after_it_raises_another_dialog() {
    assert_eq!(prompting(Prompting::Yes, Prompting::OnceIfShut, true, false), Prompting::Yes, "a press asks for the account it is about");
    assert_eq!(prompting(Prompting::Yes, Prompting::OnceIfShut, false, false), Prompting::OnceIfShut, "and reads the others the way a tick would");
    assert_eq!(prompting(Prompting::Yes, Prompting::OnceIfShut, true, true), Prompting::No);
    assert_eq!(prompting(Prompting::Yes, Prompting::OnceIfShut, false, true), Prompting::No);
}

#[test]
fn the_refresh_after_a_session_starts_asks_nobody_and_only_about_that_account() {
    let profile = PathBuf::from("/home/a/.config/Claude-2");
    let r = Refresh::after_session_started(&profile);
    assert!(r.interactive && r.prompting == Some(Prompting::No) && r.focus == Some(profile));
    assert_eq!(Refresh::looking().prompting, Some(Prompting::OnceIfShut), "opening a window may raise the one dialog");
    assert!(!Refresh::looking().interactive, "but does not skip the back-off");
    assert_eq!(Refresh::for_lending().prompting, Some(Prompting::No), "sharing usage never raises one");
}

// MARK: what the bar shows

fn entry(name: &str, five_hour: Option<i64>, week: i64, running: bool, live: bool, sampled: SystemTime) -> Entry {
    let mut e = Entry::new(name, PathBuf::from(format!("/home/a/.config/{name}")), Some(Usage::new(five_hour, week, None, sampled)), running);
    e.is_live = live;
    e
}

fn open(name: &str, five_hour: Option<i64>, week: i64, running: bool) -> Entry {
    entry(name, five_hour, week, running, true, SystemTime::now())
}

fn names(entries: &[Entry]) -> Option<String> {
    headline_entry(entries).map(|e| e.name.clone())
}

fn headline_of(entries: &[Entry]) -> Option<Headline> {
    headline_entry(entries).and_then(|e| e.usage.as_ref()).map(Usage::headline)
}

#[test]
fn the_figure_follows_whatever_is_open() {
    let entries = [open("Claude", Some(100), 0, false), open("Claude 2", Some(46), 0, true)];
    assert_eq!(names(&entries).as_deref(), Some("Claude 2"), "the open account is the one shown");
    assert_eq!(headline_of(&entries), Some(Headline { percent: 46, is_weekly: false }), "and its five-hour figure is the one in the bar");

    let entries = [open("Claude", Some(30), 0, true), open("Claude 2", Some(80), 0, true)];
    assert_eq!(headline_of(&entries).map(|h| h.percent), Some(80), "with several open, the tightest window wins");

    let entries = [open("Claude", Some(100), 0, false), open("Claude 2", Some(46), 0, false)];
    assert_eq!(headline_of(&entries).map(|h| h.percent), Some(100), "with nothing open it falls back to the tightest");
}

#[test]
fn an_account_with_no_five_hour_limit_is_weighed_by_its_week() {
    let entries = [open("Claude", Some(30), 20, true), open("Claude 2", None, 90, true)];
    assert_eq!(names(&entries).as_deref(), Some("Claude 2"));
    assert_eq!(headline_of(&entries), Some(Headline { percent: 90, is_weekly: true }), "and the bar shows that week, marked as the week");

    let entries = [open("Claude", Some(30), 20, true), open("Claude 2", None, 10, true)];
    assert_eq!(names(&entries).as_deref(), Some("Claude"), "while a tighter five-hour window still outranks it");

    let entries = [open("Claude", Some(0), 5, false), open("Claude 2", None, 40, false)];
    assert_eq!(headline_of(&entries), Some(Headline { percent: 40, is_weekly: true }), "and with nothing open it can be the one closest to its limit");
}

#[test]
fn a_stale_figure_is_left_out_and_nothing_shows_when_there_is_nothing() {
    let old = SystemTime::now() - secs(8 * 3600);
    let entries = [entry("Claude", Some(99), 0, false, false, old), entry("Claude 2", Some(12), 0, false, false, SystemTime::now())];
    assert_eq!(headline_of(&entries).map(|h| h.percent), Some(12));
    assert_eq!(headline_of(&[]), None);
}

#[test]
fn a_named_claude_is_the_one_whose_figure_stands_beside_its_name() {
    let idle = open("Claude", Some(100), 0, true);
    let busy = open("Claude 2", Some(46), 0, true);
    let entries = vec![idle.clone(), busy.clone()];
    assert!(status_entry(&entries, None).map(|(e, named)| !named && e.name == "Claude") == Some(true), "with nobody named, the bar shows the tightest window");
    assert!(status_entry(&entries, Some(&busy.profile)).map(|(e, named)| named && e.name == "Claude 2") == Some(true));
    assert!(
        status_entry(&entries, Some(Path::new("/home/a/.config/Somebody Else"))).map(|(_, named)| !named) == Some(true),
        "a profile Graft does not know is not named"
    );
    let stale = entry("Claude 3", Some(99), 0, true, false, SystemTime::now() - secs(8 * 3600));
    let named = status_entry(&[idle, stale.clone()], Some(&stale.profile)).unwrap();
    assert!(named.1 && named.0.current_usage().is_none(), "a named Claude with only a stale figure is named without the figure");
}

#[test]
fn a_figure_says_when_it_was_taken_and_where_from() {
    let mut current = Entry::new("Live", PathBuf::from("/x"), None, false);
    current.is_live = true;
    current.refreshed = Some(SystemTime::now());
    assert_eq!(current.freshness(SystemTime::now()), "Updated just now from Anthropic", "a live figure taken a moment ago says so");
    assert_eq!(
        Entry::new("None", PathBuf::from("/x"), None, false).freshness(SystemTime::now()),
        "Not refreshed yet",
        "an account nobody has answered for yet says that instead of a time"
    );
    let mut own = Entry::new("Own", PathBuf::from("/x"), None, false);
    own.refreshed = Some(SystemTime::now() - secs(600));
    assert_eq!(own.freshness(SystemTime::now()), "Updated 10 min ago from this profile's own record");
}

#[test]
fn an_account_nobody_has_answered_for_keeps_its_start_session_button() {
    assert!(Entry::new("New", PathBuf::from("/x"), None, false).may_start_session());
    assert!(!open("Weekly", None, 3, false).may_start_session(), "an account with no five-hour window has nothing to open");
    assert!(open("Limited", Some(0), 3, false).may_start_session());
}

#[test]
fn the_clock_gives_the_time_alone_for_today_and_the_day_for_anything_older() {
    let now = SystemTime::now();
    let today = clock(now, now);
    assert_eq!(today.len(), 5, "HH:MM, got {today}");
    let older = clock(now - secs(3 * 86_400), now);
    assert!(older.len() > 5 && older.ends_with(&clock(now - secs(3 * 86_400), now - secs(3 * 86_400))), "got {older}");
}

// MARK: the monitor itself, against a sandbox

fn quiet() -> Sources {
    Sources { targets: Arc::new(targets_from_shortcuts), processes: Arc::new(Vec::new) }
}

fn make_shortcut(name: &str, folder: &str, installed: bool) -> Shortcut {
    let mut s = Shortcut::new(name, Some(folder), Source::Main, IconPreset::Blue);
    if installed {
        s.installed_name = Some(name.to_string());
    }
    s
}

fn now_ms() -> f64 {
    SystemTime::now().duration_since(SystemTime::UNIX_EPOCH).unwrap().as_millis() as f64
}

fn write_history(profile: &Path, samples: Value) {
    std::fs::create_dir_all(profile).unwrap();
    std::fs::write(profile.join(history::HISTORY_FILE), serde_json::to_vec(&json!({"version": 2, "samples": samples})).unwrap()).unwrap();
}

fn settle(monitor: &Monitor) {
    assert!(monitor.wait_until_idle(secs(10)), "the pass finished");
}

#[test]
fn a_monitor_is_willing_to_ask_until_something_says_otherwise() {
    let monitor = Monitor::new(quiet());
    assert!(monitor.may_prompt_unasked());
    monitor.set_may_prompt_unasked(false);
    assert!(!monitor.may_prompt_unasked());
}

#[test]
fn the_main_profile_and_each_created_shortcut_are_listed_with_their_own_figures() {
    let sandbox = Sandbox::new();
    let now = now_ms();
    write_history(&paths::main_profile(), json!([{"t": now, "org": "ORGM", "u": {"fh": 12, "sd": 34}}]));
    let listed = sandbox.config().join("Claude-Listed");
    write_history(&listed, json!([{"t": now, "org": "ORG1", "u": {"fh": 77, "sd": 88}}]));
    std::fs::create_dir_all(sandbox.config().join("Claude-Draft")).unwrap();
    let installed = make_shortcut("Listed", "Claude-Listed", true);
    graft_core::config::save_shortcuts(&[installed.clone(), make_shortcut("Draft", "Claude-Draft", false)]).unwrap();

    let monitor = Monitor::new(quiet());
    monitor.refresh(Refresh::tick());
    settle(&monitor);
    let entries = monitor.entries();
    assert_eq!(entries.len(), 2, "the main profile and each created shortcut are listed");
    assert_eq!(entries[0].name, "Claude", "the main profile comes first");
    assert!(!entries.iter().any(|e| e.name == "Draft"), "a draft shortcut is not listed");
    assert_eq!(entries[1].usage.as_ref().unwrap().five_hour, Some(77), "each entry carries its own figures");
    assert_eq!(entries[0].shortcut, None, "the main profile has no shortcut behind it");
    assert_eq!(entries[1].shortcut, Some(installed.id));
    assert_eq!(monitor.headline().map(|h| h.percent), Some(77), "the headline comes from the figures a refresh read off disk");

    assert_eq!(monitor.entry_for(&listed).and_then(|e| e.usage).and_then(|u| u.five_hour), Some(77), "the monitor answers for one profile by path");
    assert!(monitor.entry_for(&sandbox.config().join("Claude-Absent")).is_none(), "and says nothing about a profile it does not carry");

    let entry = monitor.entry_for(&listed).unwrap();
    let refreshed = entry.refreshed.unwrap();
    let ms = refreshed.duration_since(SystemTime::UNIX_EPOCH).unwrap().as_millis() as f64;
    assert!((ms - now).abs() < 1000.0 && !entry.is_live, "a figure read off the profile's own record is dated by that record");
    assert!(entry.freshness(refreshed + secs(600)).contains("own record"), "and the row says it came from the profile");
    assert!(entry.note.as_deref().unwrap_or("").contains("No saved login"), "and why it is not live");

    write_history(&listed, json!([{"t": now - 8.0 * 3_600_000.0, "org": "ORG1", "u": {"fh": 99, "sd": 99}}]));
    let second = Monitor::new(quiet());
    second.refresh(Refresh::tick());
    settle(&second);
    assert_eq!(second.headline().map(|h| h.percent), Some(12), "a stale figure is left out of the headline");
}

#[test]
fn an_account_with_no_five_hour_limit_is_carried_through_to_the_status_file() {
    let sandbox = Sandbox::new();
    let now = now_ms();
    let unlimited = sandbox.config().join("Claude-Unlimited");
    write_history(
        &unlimited,
        json!([{"t": now - 60_000.0, "org": "ORG3", "u": {"sd": 93}}, {"t": now, "org": "ORG3", "u": {"sd": 94}}]),
    );
    graft_core::config::save_shortcuts(&[make_shortcut("Unlimited", "Claude-Unlimited", true)]).unwrap();
    let monitor = Monitor::new(quiet());
    monitor.refresh(Refresh::tick());
    settle(&monitor);
    let entry = monitor.entry_for(&unlimited).unwrap();
    assert!(entry.usage.as_ref().map(|u| u.five_hour.is_none() && u.week == 94) == Some(true));
    assert!(!entry.may_start_session(), "and offers that account no session, since there is no window for one to open");

    let status: Value = serde_json::from_slice(&std::fs::read(status_file()).unwrap()).unwrap();
    let row = status["profiles"].as_array().unwrap().iter().find(|r| r["name"] == "Unlimited").unwrap().clone();
    assert!(
        row["noFiveHourLimit"] == json!(true) && row.get("fiveHour").is_none() && row["week"] == 94,
        "the status file says the account has no five-hour limit rather than recording a zero"
    );
    assert_eq!(status["keyringLocked"], json!(false));
    let text = String::from_utf8(std::fs::read(status_file()).unwrap()).unwrap();
    assert!(!text.contains("tok"), "and holds no token");
}

#[test]
fn an_open_claude_is_marked_running() {
    let sandbox = Sandbox::new();
    let two = sandbox.config().join("Claude-2");
    std::fs::create_dir_all(&two).unwrap();
    graft_core::config::save_shortcuts(&[make_shortcut("Claude 2", "Claude-2", true)]).unwrap();
    let line = format!("4242 /usr/lib/claude-desktop-unofficial/claude-desktop --user-data-dir={}", two.display());
    let sources = Sources {
        targets: Arc::new(targets_from_shortcuts),
        processes: Arc::new(move || claude::parse_processes(&line)),
    };
    let monitor = Monitor::new(sources);
    monitor.refresh(Refresh::tick());
    settle(&monitor);
    assert!(monitor.entry_for(&two).unwrap().is_running);
    assert!(!monitor.entries()[0].is_running, "and only that one");
}

// MARK: the live figure, against a stand-in service

struct World {
    sandbox: Sandbox,
    stand_in: StandIn,
    monitor: Monitor,
    two: PathBuf,
}

/// Claude's own profile signed in as `main-token`, one shortcut signed in as
/// `two-token`, and a stand-in answering both.
fn world() -> World {
    let sandbox = Sandbox::new();
    signed_in(&paths::main_profile(), &[("main-token", "user:profile user:inference", tomorrow())]);
    let two = sandbox.config().join("Claude-2");
    signed_in(&two, &[("two-token", "user:profile user:inference", tomorrow())]);
    graft_core::config::save_shortcuts(&[make_shortcut("Claude 2", "Claude-2", true)]).unwrap();
    let stand_in = StandIn::start();
    stand_in.answer(
        200,
        &[],
        r#"{"five_hour":{"utilization":40,"resets_at":"2099-01-01T00:00:00Z"},"seven_day":{"utilization":60},"subscription_type":"pro"}"#,
    );
    let monitor = Monitor::new(quiet());
    World { sandbox, stand_in, monitor, two }
}

#[test]
fn a_live_figure_wins_over_the_record_and_says_where_it_came_from() {
    let w = world();
    write_history(&w.two, json!([{"t": now_ms() - 60_000.0, "org": "ORG2", "u": {"fh": 5, "sd": 6}}]));
    w.monitor.refresh(Refresh::tick());
    settle(&w.monitor);
    let entry = w.monitor.entry_for(&w.two).unwrap();
    assert!(entry.is_live && entry.note.is_none());
    let usage = entry.usage.clone().unwrap();
    assert_eq!((usage.five_hour, usage.week), (Some(40), 60));
    assert_eq!(usage.organization.as_deref(), Some("ORG2"), "the organization still comes from the record");
    assert_eq!(entry.plan.as_deref(), Some("Pro"));
    assert!(entry.freshness(SystemTime::now()).contains("from Anthropic"));
    assert_eq!(w.stand_in.requests_with("two-token"), 1);
    let _ = &w.sandbox;
}

#[test]
fn a_tick_inside_the_cache_asks_the_service_nothing_and_a_press_asks_again() {
    let w = world();
    w.monitor.refresh(Refresh::tick());
    settle(&w.monitor);
    w.monitor.refresh(Refresh::tick());
    settle(&w.monitor);
    assert_eq!(w.stand_in.requests_with("two-token"), 1, "a second tick reuses the figure");
    w.monitor.refresh(Refresh::looking());
    settle(&w.monitor);
    assert_eq!(w.stand_in.requests_with("two-token"), 1, "opening a window inside the minute does too");
    std::thread::sleep(secs(2) + Duration::from_millis(100));
    w.monitor.refresh(Refresh::pressed_for(&w.two));
    settle(&w.monitor);
    assert_eq!(w.stand_in.requests_with("two-token"), 2, "a press past the double-click floor asks the service");
    assert_eq!(w.stand_in.requests_with("main-token"), 1, "and the other profiles are read as a tick reads them");
    let snapshot = w.monitor.snapshot();
    assert!(!snapshot.is_refreshing && snapshot.refreshing_profile.is_none(), "the spinner is down once the press is answered");
}

#[test]
fn invalidating_a_profile_makes_the_next_pass_ask_for_it_again() {
    let w = world();
    w.monitor.refresh(Refresh::tick());
    settle(&w.monitor);
    w.monitor.invalidate(&w.two);
    w.monitor.refresh(Refresh::after_session_started(&w.two));
    settle(&w.monitor);
    assert_eq!(w.stand_in.requests_with("two-token"), 2, "the reading a session made wrong is not reused");
    assert_eq!(w.stand_in.requests_with("main-token"), 1);
}

#[test]
fn a_failing_service_is_backed_off_and_a_press_may_skip_graft_s_own_wait() {
    let w = world();
    w.stand_in.answer_token("two-token", 500, &[], "{}");
    w.monitor.refresh(Refresh::tick());
    settle(&w.monitor);
    let entry = w.monitor.entry_for(&w.two).unwrap();
    assert!(!entry.is_live);
    assert_eq!(entry.note.as_deref(), Some("Anthropic didn't answer"));

    // Freshness `Now` gets past the cache for a profile with no reading at
    // all, so only the back-off holds a tick off.
    w.monitor.refresh(Refresh { freshness: Some(Freshness::Now), ..Refresh::tick() });
    settle(&w.monitor);
    assert_eq!(w.stand_in.requests_with("two-token"), 1, "a background pass waits out the back-off");
    let note = w.monitor.entry_for(&w.two).unwrap().note.unwrap();
    assert!(note.starts_with("Anthropic didn't answer; next try in"), "and the row says how long: {note}");

    w.monitor.refresh(Refresh::pressed_for(&w.two));
    settle(&w.monitor);
    assert_eq!(w.stand_in.requests_with("two-token"), 2, "a press skips a wait of Graft's own");
    assert_eq!(w.monitor.snapshot().live_problem.as_deref(), Some("The usage service answered 500."), "and says what the service said");
}

#[test]
fn a_wait_the_service_asked_for_holds_even_a_press_off() {
    let w = world();
    w.stand_in.answer_token("two-token", 429, &[("Retry-After", "900")], "{}");
    w.monitor.refresh(Refresh::tick());
    settle(&w.monitor);
    w.monitor.refresh(Refresh::pressed_for(&w.two));
    settle(&w.monitor);
    assert_eq!(w.stand_in.requests_with("two-token"), 1, "Retry-After is the one wait a press cannot shorten");
    let problem = w.monitor.snapshot().live_problem.unwrap();
    assert!(problem.starts_with("Next try in 1"), "the press is told when: {problem}");
}

#[test]
fn a_login_expired_on_this_computer_is_tried_again_on_the_very_next_pass() {
    let w = world();
    signed_in(&w.two, &[("old", "user:profile", 1_000.0)]);
    w.monitor.refresh(Refresh::tick());
    settle(&w.monitor);
    let note = w.monitor.entry_for(&w.two).unwrap().note.unwrap();
    assert_eq!(note, "Login expired; open that Claude to renew it", "the row names the local cause");
    signed_in(&w.two, &[("two-token", "user:profile", tomorrow())]);
    w.monitor.refresh(Refresh::tick());
    settle(&w.monitor);
    assert!(w.monitor.entry_for(&w.two).unwrap().is_live, "with no back-off in between, once it is put right");
}

#[test]
fn a_config_caught_mid_write_is_a_read_to_try_again() {
    let w = world();
    std::fs::write(w.two.join("config.json"), br#"{"oauth:tokenCacheV2": "djEw"#).unwrap();
    w.monitor.refresh(Refresh::tick());
    settle(&w.monitor);
    assert_eq!(w.monitor.entry_for(&w.two).unwrap().note.as_deref(), Some("Its settings were being saved; trying again"));
    assert_eq!(w.stand_in.requests_with("two-token"), 0);
}

#[test]
fn a_press_arriving_mid_pass_is_run_once_that_pass_is_out_of_the_way() {
    let w = world();
    // The first pass fails for this account, so there is no reading to reuse
    // and only the press itself can ask again.
    w.stand_in.answer_token("two-token", 503, &[], "{}");
    w.stand_in.delay(Duration::from_millis(300));
    w.monitor.refresh(Refresh::tick());
    std::thread::sleep(Duration::from_millis(100));
    w.monitor.refresh(Refresh::tick());
    w.monitor.refresh(Refresh::pressed_for(&w.two));
    let during = w.monitor.snapshot();
    assert!(during.is_refreshing && during.refreshing_profile == Some(w.two.clone()), "the press's spinner goes up at once, on its own row");
    settle(&w.monitor);
    assert_eq!(w.stand_in.requests_with("two-token"), 2, "the press was not lost inside the pass already running");
    assert_eq!(w.stand_in.requests_with("main-token"), 1, "and it asked only about its own account");
    assert!(!w.monitor.snapshot().is_refreshing, "and the spinner comes down when it is answered");
}

#[test]
fn subscribers_hear_each_change_and_only_changes() {
    let w = world();
    let heard = Arc::new(AtomicUsize::new(0));
    let counter = heard.clone();
    let id = w.monitor.subscribe(move |snapshot| {
        assert!(!snapshot.entries.is_empty() || snapshot.is_refreshing);
        counter.fetch_add(1, Ordering::SeqCst);
    });
    w.monitor.refresh(Refresh::tick());
    settle(&w.monitor);
    let after_first = heard.load(Ordering::SeqCst);
    assert_eq!(after_first, 1, "a pass that found something is heard once");
    w.monitor.refresh(Refresh::tick());
    settle(&w.monitor);
    assert_eq!(heard.load(Ordering::SeqCst), after_first, "a pass that changed nothing is not heard at all");
    w.monitor.unsubscribe(id);
    w.monitor.set_entries_for_testing(Vec::new());
    assert_eq!(heard.load(Ordering::SeqCst), after_first, "and an unsubscribed callback hears nothing more");
}

#[test]
fn the_timer_starts_once_and_a_second_start_is_a_tick() {
    let w = world();
    w.monitor.start(Duration::from_millis(150));
    w.monitor.start(Duration::from_millis(150));
    settle(&w.monitor);
    std::thread::sleep(Duration::from_millis(500));
    settle(&w.monitor);
    assert!(w.monitor.entry_for(&w.two).unwrap().is_live, "the timer's passes read the figures");
    assert_eq!(w.stand_in.requests_with("two-token"), 1, "and inside the cache they ask the service nothing more");
}
