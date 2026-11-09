//! What the mirror writes down, what the state report says, and the log itself.

use super::*;
use crate::fsx::{exists, resolved_text};
use crate::mirror::{
    forget_stale_pairs, load_mirror_state, mirror_chat_folders, mirror_pairs_borrowed_by, mirror_pairs_under,
    pair_halves, pair_key, save_mirror_state, MirrorState,
};
use crate::relink;
use crate::report::{command_line_login, device_identifier, state_report_text};
use graft_core::diagnostics;
use graft_core::lock::{self, FileLock, STATE_LOCK_PATIENCE};
use std::collections::BTreeMap;
use std::os::unix::fs::PermissionsExt;
use std::time::{Duration, Instant};

const OUTSIDE_EVENT: &str = "outside-holder";

fn gave_up_count() -> usize {
    let wanted = format!("{OUTSIDE_EVENT}.gave-up");
    read(&diagnostics::file())
        .unwrap_or_default()
        .lines()
        .filter_map(|line| serde_json::from_str::<Value>(line).ok())
        .filter(|line| line["event"] == wanted.as_str())
        .count()
}

/// Another process, stood in for by a second open of the lock file: flock
/// belongs to the open file, so this process's own passes wait on it exactly
/// as they would on a launcher's. `lock::take` hides whether it got the lock,
/// but it writes down every time it gives up, and that is the answer.
fn outside_holder() -> Option<FileLock> {
    let before = gave_up_count();
    let hold = lock::take(&paths::state_lock_file(), Duration::ZERO, OUTSIDE_EVENT);
    (gave_up_count() == before).then_some(hold)
}

fn chat(dir: &Path, name: &str) {
    write(&dir.join(name), &format!("chat {name}"));
}

fn mirror_pair(world: &World, name: &str) -> (PathBuf, PathBuf) {
    let one = world.support().join(name).join("one");
    let other = world.support().join(name).join("other");
    for dir in [&one, &other] {
        std::fs::create_dir_all(dir).unwrap();
    }
    (one, other)
}

#[test]
fn passes_in_different_processes_take_turns_with_the_state_files() {
    // A launcher and Graft's sweep are separate processes writing the same
    // two state files.
    let world = World::new();
    let (one, other) = mirror_pair(&world, "Locked");
    write(&one.join("local_lock-a.json"), "a");

    let held_elsewhere = lock::with_state_lock(outside_holder);
    assert!(held_elsewhere.is_none(), "while a pass runs, no other process can take the state lock");
    let afterwards = outside_holder();
    assert!(afterwards.is_some(), "and it is let go when the pass ends");

    let started = Instant::now();
    let letting_go = std::thread::spawn(move || {
        std::thread::sleep(Duration::from_millis(400));
        drop(afterwards);
    });
    mirror_chat_folders(&one, &other);
    assert!(
        started.elapsed() >= Duration::from_millis(300),
        "a pass waits for one another process is in the middle of"
    );
    assert!(exists(&other.join("local_lock-a.json")), "and then does its work");
    letting_go.join().unwrap();

    let nested = Instant::now();
    let mut finished = false;
    lock::with_state_lock(|| {
        lock::with_state_lock(|| {
            mirror_chat_folders(&one, &other);
            finished = true;
        })
    });
    assert!(
        finished && nested.elapsed() < Duration::from_secs(2),
        "a pass holding the lock may call one that takes it again without waiting on itself"
    );

    let in_launch = Instant::now();
    lock::with_launch_lock(|| mirror_chat_folders(&one, &other));
    assert!(
        in_launch.elapsed() < Duration::from_secs(2),
        "the launch lock and the state lock do not wait on each other"
    );
}

#[test]
fn a_pass_kept_waiting_past_its_patience_goes_ahead() {
    // The Mac suite turned the patience down to a fifth of a second for this.
    // Here it is a constant, so the pass is made to wait the whole of it.
    let world = World::new();
    let (one, other) = mirror_pair(&world, "Locked");
    let stuck = outside_holder();
    assert!(stuck.is_some(), "the stand-in for a hung pass holds the state lock");

    write(&one.join("local_lock-b.json"), "b");
    let started = Instant::now();
    mirror_chat_folders(&one, &other);
    assert!(started.elapsed() >= STATE_LOCK_PATIENCE, "the pass waited out its patience first");
    assert!(
        exists(&other.join("local_lock-b.json")),
        "a pass kept waiting past its patience goes ahead, so a hung one never stops a Claude opening"
    );
    drop(stuck);
}

#[test]
fn a_removal_the_filesystem_refused_is_not_written_down_as_one() {
    // The baseline is what makes an absence mean something, so it may only say
    // what the filesystem actually did. A removal was written down whether or
    // not it happened, and a removal that failed left a name on one side with
    // no baseline, which reads as a new chat, and put the deleted
    // conversation straight back where it had been deleted from.
    let world = World::new();
    let (one, other) = mirror_pair(&world, "Refused");
    for name in ["local_a.json", "local_b.json"] {
        chat(&one, name);
    }
    mirror_chat_folders(&one, &other);

    std::fs::remove_file(other.join("local_a.json")).unwrap();
    std::fs::set_permissions(&one, std::fs::Permissions::from_mode(0o555)).unwrap();
    mirror_chat_folders(&one, &other);
    std::fs::set_permissions(&one, std::fs::Permissions::from_mode(0o755)).unwrap();

    assert!(
        load_mirror_state().pairs.get(&pair_key(&one, &other)).and_then(|p| p.get("local_a.json")).is_some(),
        "a removal the filesystem refused is not written down as one"
    );

    mirror_chat_folders(&one, &other);
    assert!(
        !exists(&one.join("local_a.json")),
        "so the next pass carries the deletion across rather than undoing it"
    );
    assert!(!exists(&other.join("local_a.json")), "and the chat stays deleted on the side it was deleted from");
}

#[test]
fn a_name_gone_from_both_sides_leaves_the_baseline() {
    // A name neither side holds any more is never visited by a pass, so its
    // entry sat in the baseline for good, growing the file, and standing ready
    // to read the name coming back on one side alone as a deletion.
    let world = World::new();
    let (one, other) = mirror_pair(&world, "Gone");
    for name in ["local_a.json", "local_b.json", "local_c.json"] {
        chat(&one, name);
    }
    mirror_chat_folders(&one, &other);

    for dir in [&one, &other] {
        std::fs::remove_file(dir.join("local_a.json")).unwrap();
    }
    mirror_chat_folders(&one, &other);
    let baseline = load_mirror_state().pairs.get(&pair_key(&one, &other)).cloned().unwrap_or_default();
    assert_eq!(
        baseline.keys().cloned().collect::<Vec<_>>(),
        vec!["local_b.json", "local_c.json"],
        "a name gone from both sides is dropped from the baseline rather than kept for ever"
    );

    // And what that entry used to do if the name ever came back.
    chat(&one, "local_a.json");
    mirror_chat_folders(&one, &other);
    assert!(
        exists(&other.join("local_a.json")),
        "so a chat filed under that name again is a new chat, not a deletion to carry across"
    );
}

fn one_record(name: &str, digest: &str) -> BTreeMap<String, String> {
    BTreeMap::from([(name.to_string(), digest.to_string())])
}

#[test]
fn every_organization_still_mirrored_keeps_its_pair() {
    // Two profiles on one account mirror every organization the account has, so
    // no single folder is the pair worth keeping.
    let world = World::new();
    let store = world.support().join("Claude-Many/claude-code-sessions");
    let first = store.join("ACC-M/ORG-1");
    let second = store.join("ACC-M/ORG-2");
    let stale = store.join("ACC-M/ORG-OLD");
    let theirs = world.support().join("Claude-Many-Src/claude-code-sessions/S/S-ORG");
    for dir in [&first, &second, &stale, &theirs] {
        std::fs::create_dir_all(dir).unwrap();
    }
    let mut state = MirrorState::default();
    for folder in [&first, &second, &stale] {
        state.pairs.insert(pair_key(folder, &theirs), one_record("local_1.json", "beef"));
    }
    save_mirror_state(&state);

    forget_stale_pairs(&store, &[(first.clone(), theirs.clone()), (second.clone(), theirs.clone())]);
    let left = load_mirror_state().pairs;
    assert!(
        left.contains_key(&pair_key(&first, &theirs)) && left.contains_key(&pair_key(&second, &theirs)),
        "every organization still being mirrored keeps its pair"
    );
    assert!(
        !left.contains_key(&pair_key(&stale, &theirs)),
        "and only the one naming a folder nobody writes to is dropped"
    );
}

#[test]
fn a_store_that_lends_drops_only_the_pairs_it_borrows_through() {
    // A store lends as well as borrows. The pairs it lends are somebody else's
    // mirror, kept current by that profile's own launch, and dropping one here
    // reads as a first pass over there.
    let world = World::new();
    let support = world.support();
    let store = support.join("Claude-Lent/claude-code-sessions");
    let mine = store.join("ACC-L/ORG-L");
    let theirs = support.join("Claude-Lent-Src/claude-code-sessions/S/S-ORG");
    let moved = support.join("Claude-Lent-Src/claude-code-sessions/S/S-ORG-NEW");
    let guest = support.join("Claude-Lent-Guest/claude-code-sessions/G/G-ORG");
    for dir in [&mine, &theirs, &moved, &guest] {
        std::fs::create_dir_all(dir).unwrap();
    }
    let mut state = MirrorState::default();
    state.pairs.insert(pair_key(&mine, &theirs), one_record("local_1.json", "beef"));
    state.pairs.insert(pair_key(&guest, &mine), one_record("local_2.json", "cafe"));
    save_mirror_state(&state);

    assert_eq!(
        pair_halves(&pair_key(&mine, &theirs)).map(|(borrower, _)| borrower),
        Some(resolved_text(&mine)),
        "a pair is written with the borrowing folder first, which is the only record of which is which"
    );
    assert_eq!(mirror_pairs_borrowed_by(&mine).len(), 1, "so a folder is found by the half it borrows through");
    assert!(
        mirror_pairs_borrowed_by(&theirs).is_empty(),
        "and the folder lent from borrows through nothing, however many pairs name it"
    );
    assert_eq!(
        mirror_pairs_under(&theirs).len(),
        1,
        "though it is still under one, for the cases where both roles are equally finished"
    );

    forget_stale_pairs(&store, &[(mine.clone(), theirs.clone())]);
    assert!(
        load_mirror_state().pairs.contains_key(&pair_key(&mine, &theirs)),
        "the pair this store borrows through is kept, being the one this pass is mirroring"
    );

    // A source that moved leaves a pair as stale as a destination that moved,
    // and only the destination half was ever looked at.
    forget_stale_pairs(&store, &[(mine.clone(), moved.clone())]);
    assert!(
        !load_mirror_state().pairs.contains_key(&pair_key(&mine, &theirs)),
        "so a pair naming the organization the source has stopped writing to is dropped as well"
    );

    // And this profile signing in again moves the folder it lends from, which
    // says nothing about the guest borrowing through it: that pair is the
    // guest's mirror, its own launch is what squares it up, and dropping it
    // here is a first pass over there, the folder stashed, the merge fetched
    // back, the record of what the guest owns folded in with it.
    let elsewhere = store.join("ACC-L/ORG-NEW");
    std::fs::create_dir_all(&elsewhere).unwrap();
    forget_stale_pairs(&store, &[(elsewhere.clone(), moved.clone())]);
    assert!(
        load_mirror_state().pairs.contains_key(&pair_key(&guest, &mine)),
        "a pair this store only lends to is left to the profile that borrows through it"
    );
}

#[test]
fn a_stashed_file_against_a_live_directory_is_left_alone() {
    // Folding a stash back in is for a pair this can reason about. Two files
    // are one, and the live copy supersedes the stashed one; a directory
    // against a file is not, either way round, and guessing at it threw the
    // stashed one away.
    let world = World::new();
    let profile = world.support().join("Claude-Odd-Stash");
    std::fs::create_dir_all(profile.join("claude_desktop_config.json")).unwrap();
    let stash = profile.join(".claude_desktop_config.json.graft-own");
    write(&stash, "the profile's own");

    let source = world.support().join("Claude-Odd-Source");
    write(&source.join("claude_desktop_config.json"), "the source's");
    relink(&source.join("claude_desktop_config.json"), &profile.join("claude_desktop_config.json"));

    assert_eq!(
        read(&stash).as_deref(),
        Some("the profile's own"),
        "a stashed file against a live directory is left alone rather than guessed at"
    );
}

#[test]
fn the_state_report_names_the_one_login_every_profile_shares() {
    let _world = World::new();

    // The command line keeps one login for the whole machine, and it is the
    // reason a chat typed into one profile turns up in another's sidebar.
    // Every report leads with it because every report is read by somebody who
    // has just watched that happen and concluded the profiles are syncing.
    let owner = "AAAAAAAA-0000-0000-0000-000000000001";
    let borrower = "BBBBBBBB-0000-0000-0000-000000000002";
    let org = "CCCCCCCC-0000-0000-0000-000000000003";
    write(
        &paths::claude_code_config_file(),
        &json!({"oauthAccount": {"accountUuid": owner, "organizationUuid": org}}).to_string(),
    );

    assert_eq!(
        command_line_login().map(|(account, _)| account).as_deref(),
        Some(owner),
        "the account every session on this machine is stamped with is read off the command line"
    );

    let held = make_profile("Report-Owner", Some(owner), Some(org), &["r-1"]);
    let other = make_profile("Report-Borrower", Some(borrower), Some(org), &["r-2"]);

    let text = state_report_text(false);
    assert!(text.contains("held by Report-Owner"), "and the report says which profile is holding it");
    assert!(text.contains("Report-Borrower is signed into BBBBBBBB"), "a profile signed into something else is named");
    assert!(
        text.contains("not two profiles syncing"),
        "and the report says outright that one shared login is not two profiles syncing"
    );

    // The half-finished ungraft: a stash still holding what the profile owns
    // beside a folder holding almost nothing. Nothing was destroyed and
    // nothing looks wrong from inside the app, but the sidebar is short.
    let store = other.join("claude-code-sessions").join(borrower).join(org);
    let stash = store.parent().unwrap().join(format!(".{org}.graft-own"));
    for chat in ["r-3", "r-4", "r-5"] {
        write(&stash.join(format!("local_{chat}.json")), "{}");
    }

    let short = state_report_text(false);
    assert!(short.contains("3 stashed beside it"), "a stash beside a folder is counted rather than passed over");
    assert!(
        short.contains("the sidebar is short by 3"),
        "and a stash holding more than the folder reads as a graft undone without handing them back"
    );

    // A device is a profile, so chats made under a previous one are shown as
    // coming from somewhere else however plainly they belong to the account.
    write(&other.join("ant-did"), "QUFBQS1CQkJC");
    assert_eq!(
        device_identifier(&other).as_deref(),
        Some("AAAA-BBBB"),
        "the device a profile registered as is read back from its own file"
    );
    assert_eq!(
        device_identifier(&held),
        None,
        "and a profile that has never registered one has none rather than a guess"
    );
}

#[test]
fn each_event_is_one_line_of_its_own() {
    let world = World::new();
    let support = world.support();
    let _ = std::fs::remove_file(diagnostics::file());
    let speaker = diagnostics::who();
    diagnostics::set_who("suite");
    diagnostics::note("first", &[("folder", json!(paths::text(&support))), ("count", json!(2))]);
    diagnostics::note("second", &[("ok", json!(true))]);

    let text = read(&diagnostics::file()).unwrap_or_default();
    let lines: Vec<&str> = text.lines().collect();
    assert_eq!(lines.len(), 2, "one line per event, so a log half-written by a killed pass still reads");

    let first: Value = serde_json::from_str(lines[0]).unwrap_or(Value::Null);
    assert_eq!(first["event"], "first", "each line names its event");
    assert_eq!(first["who"], "suite", "and which process wrote it, since a launcher and the app run the same passes");
    assert_eq!(
        first["folder"].as_str(),
        Some(paths::text(&support).as_str()),
        "a URL is written as its path rather than costing the whole line"
    );

    // A field the encoder refuses would otherwise take the event with it, and
    // the events worth having are the ones written mid-incident. JSON has no
    // number for one that is not finite, which is the nearest thing here to
    // what the Mac encoder refused.
    diagnostics::note("third", &[("when", json!(f64::NAN)), ("what", json!(paths::text(Path::new("/tmp/x"))))]);
    let all = read(&diagnostics::file()).unwrap_or_default();
    assert_eq!(all.lines().count(), 3, "an event carrying something unserialisable is still written");

    let _ = std::fs::remove_file(diagnostics::file());
    diagnostics::set_who(&speaker);
}
