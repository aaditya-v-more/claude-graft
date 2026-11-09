//! Reading a transcript for its record, deciding whether to file one, and the
//! sweep that writes the records a grafted profile's sessions never got.

use super::*;
use crate::fsx::exists;
use crate::records::{
    file_missing_session_records_at, load_session_record_state, session_filing, session_folder_is_here,
    session_record, session_record_state_file, session_update_decision, SessionFiling, SessionUpdate,
};
use crate::transcript::{session_facts, SessionFacts};
use std::collections::{BTreeSet, HashSet};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

/// The quiet window every sweep in the Mac suite ran with, its default.
const QUIET: Duration = Duration::from_secs(60);

fn sweep(filing: &[PathBuf], now: SystemTime) -> Vec<SessionFacts> {
    file_missing_session_records_at(filing, now, QUIET)
}

fn titles(filed: &[SessionFacts]) -> BTreeSet<String> {
    filed.iter().map(|f| f.title.clone()).collect()
}

fn set_of(items: &[&str]) -> BTreeSet<String> {
    items.iter().map(|s| s.to_string()).collect()
}

fn ids(items: &[&str]) -> HashSet<String> {
    items.iter().map(|s| s.to_string()).collect()
}

fn later_by(base: SystemTime, seconds: u64) -> SystemTime {
    base + Duration::from_secs(seconds)
}

fn org_folder(profile: &Path, account: &str, org: &str) -> PathBuf {
    profile.join("claude-code-sessions").join(account).join(org)
}

fn contains(path: &Path, text: &str) -> bool {
    read(path).is_some_and(|t| t.contains(text))
}

#[test]
fn a_transcript_is_read_for_everything_its_record_says() {
    let _world = World::new();
    let id = "11111111-1111-4111-8111-111111111111";
    let facts = session_facts(&Transcript::new(id).title("Test 4").write(), id);
    let facts = facts.as_ref();
    assert_eq!(facts.map(|f| f.title.as_str()), Some("Test 4"), "the title comes from the title line");
    assert!(
        facts.is_some_and(|f| f.owner_account == OWNER_ACCOUNT && f.owner_organization == OWNER_ORG),
        "the owner and the organization come from the bridge line"
    );
    assert!(
        facts.is_some_and(|f| f.cwd == WORK_CWD && f.permission_mode == "auto"),
        "so do the working directory and the permission mode"
    );
    assert!(
        facts.is_some_and(|f| f.model == "claude-opus-5" && f.effort == "max"),
        "the model and the effort come from the answer"
    );
    assert_eq!(
        facts.map(|f| f.bridge_ids.clone()),
        Some(vec!["cse_01BridgeSession0000".to_string()]),
        "the bridge id is the one thing a record cannot live without"
    );
    assert_eq!(
        facts.map(|f| f.branches.clone()),
        Some(vec!["main".to_string()]),
        "the branch the session ran on is written down"
    );
    if let Some(facts) = facts {
        assert!(
            facts.last_activity_at - facts.created_at == 10_000.0 && facts.created_at > 1_700_000_000_000.0,
            "the times are the transcript's first and last, in milliseconds"
        );
    }
    assert_eq!(facts.map(|f| f.prompts), Some(1), "one prompt is one turn");

    // Tool results carry their prompt's id, so a session heavy with them is
    // not a session of many turns.
    let busy_id = "22222222-2222-4222-8222-222222222222";
    let tool_result = format!(
        "{{\"parentUuid\":\"a-1\",\"isSidechain\":false,\"promptId\":\"prompt-one\",\"cwd\":\"{WORK_CWD}\",\
         \"type\":\"user\",\"message\":{{\"role\":\"user\",\"content\":[{{\"tool_use_id\":\"t-1\",\
         \"type\":\"tool_result\",\"content\":\"ok\"}}]}},\"uuid\":\"u-2\",\"timestamp\":\"2026-08-29T17:00:11.000Z\"}}"
    );
    let busy = session_facts(&Transcript::new(busy_id).extra(&[&tool_result]).write(), busy_id);
    assert_eq!(
        busy.as_ref().map(|f| f.prompts),
        Some(1),
        "tool results share their prompt's id and count as no turn of their own"
    );
    assert_eq!(
        busy.as_ref().map(|f| f.title.as_str()),
        Some("New session"),
        "a session never named is listed under the name Claude gives unnamed ones"
    );

    let terminal_id = "33333333-3333-4333-8333-333333333333";
    assert!(
        session_facts(&Transcript::new(terminal_id).no_bridge().write(), terminal_id).is_none(),
        "a transcript the terminal ran has no bridge line, and nothing is missing from it"
    );

    if let Some(facts) = facts {
        let record = session_record(facts);
        assert_eq!(
            record.get("sessionId").and_then(Value::as_str),
            Some("local_11111111-1111-4111-8111-111111111111"),
            "the record is named for its session, so a later pass can only find it in one place"
        );
        assert_eq!(
            record.get("bridgeSessionIds"),
            Some(&json!(["session_01BridgeSession0000"])),
            "the bridge id is renamed the way records spell it: cse_ becomes session_"
        );
        assert!(
            record.get("cliSessionId").and_then(Value::as_str) == Some(id)
                && record.get("isArchived").and_then(Value::as_bool) == Some(false)
                && record.get("titleSource").and_then(Value::as_str) == Some("auto"),
            "the rest of the record says what Claude's own records say about a session"
        );
        assert!(
            record.get("chromePermissionMode").is_none(),
            "and it says nothing about browser permission checks, so the person's own setting holds"
        );
        assert_eq!(
            record.get("permissionMode").and_then(Value::as_str),
            Some("auto"),
            "while the permission mode is the one the transcript names"
        );
    }
}

#[test]
fn a_transcript_that_names_no_permission_mode_is_filed_with_the_one_that_grants_least() {
    let _world = World::new();
    // Nothing in this one names a permission mode, and a record is what a
    // session continued in Claude starts from.
    let unnamed = "12121212-1212-4121-8121-121212121212";
    let silent = crate::records::claude_projects().join(format!("-Users-test-work/{unnamed}.jsonl"));
    write(
        &silent,
        &[
            format!(
                "{{\"type\":\"bridge-session\",\"sessionId\":\"{unnamed}\",\"bridgeSessionId\":\"cse_01Silent\",\
                 \"ownerAccountUuid\":\"{OWNER_ACCOUNT}\",\"ownerOrganizationUuid\":\"{OWNER_ORG}\"}}"
            ),
            format!(
                "{{\"promptId\":\"p-1\",\"cwd\":\"{WORK_CWD}\",\"type\":\"user\",\
                 \"message\":{{\"role\":\"user\",\"content\":\"hello\"}},\"uuid\":\"u-1\",\
                 \"timestamp\":\"2026-08-29T17:00:00.000Z\"}}"
            ),
        ]
        .join("\n"),
    );
    let silent_facts = session_facts(&silent, unnamed);
    assert_eq!(
        silent_facts.as_ref().map(|f| f.permission_mode.as_str()),
        Some("default"),
        "a transcript that names no permission mode is filed with the one that grants least"
    );
}

fn plain_facts(session: &str) -> SessionFacts {
    SessionFacts {
        cli_session_id: session.into(),
        bridge_ids: vec!["cse_x".into()],
        owner_account: OWNER_ACCOUNT.into(),
        owner_organization: OWNER_ORG.into(),
        title: "T".into(),
        cwd: "/w".into(),
        created_at: 0.0,
        last_activity_at: 1_000.0,
        model: "m".into(),
        effort: "low".into(),
        permission_mode: "auto".into(),
        prompts: 1,
        branches: Vec::new(),
    }
}

#[test]
fn whether_a_record_is_filed_turns_on_what_is_known_about_its_session() {
    let _world = World::new();
    let facts = plain_facts("s-1");
    let then = UNIX_EPOCH + Duration::from_secs(1);
    let later = then + Duration::from_secs(10 * 60);
    let owner = PathBuf::from("/tmp/nowhere");
    let window = Duration::from_secs(300);
    let none = HashSet::new();
    // The Mac signature defaults the owner to running and the folder to here.
    let filing = |facts: Option<&SessionFacts>,
                  recorded: &HashSet<String>,
                  withdrawn: &HashSet<String>,
                  deletions: &[f64],
                  last_write: SystemTime,
                  owner: Option<&Path>,
                  owner_is_running: bool,
                  folder_is_here: bool| {
        session_filing(
            facts,
            recorded,
            withdrawn,
            deletions,
            last_write,
            owner,
            owner_is_running,
            folder_is_here,
            later,
            window,
        )
    };

    assert_eq!(
        filing(None, &none, &none, &[], then, Some(&owner), true, true),
        SessionFiling::NotADesktopSession,
        "a transcript with no bridge line is not missing anything"
    );
    assert_eq!(
        filing(Some(&facts), &ids(&["s-1"]), &none, &[], then, Some(&owner), true, true),
        SessionFiling::AlreadyRecorded,
        "a session some Claude already wrote a record for is left alone"
    );
    assert_eq!(
        filing(Some(&facts), &none, &ids(&["s-1"]), &[], then, Some(&owner), true, true),
        SessionFiling::Withdrawn,
        "a session deleted in a sidebar once is never brought back"
    );
    assert_eq!(
        filing(Some(&facts), &none, &none, &[1_000.0 + 30_000.0], then, Some(&owner), true, true),
        SessionFiling::Withdrawn,
        "a session that had just gone quiet before a deletion marker is the one the marker took"
    );
    assert_eq!(
        filing(Some(&facts), &none, &none, &[1_000.0 + 10.0 * 60_000.0], then, Some(&owner), true, true),
        SessionFiling::File,
        "a marker long after the session closed is about some other session"
    );
    assert_eq!(
        filing(Some(&facts), &none, &none, &[1_000.0 - 5.0 * 60_000.0], then, Some(&owner), true, true),
        SessionFiling::File,
        "and a marker from before the session existed says nothing about it"
    );
    assert_eq!(
        filing(Some(&facts), &none, &none, &[], later, Some(&owner), true, true),
        SessionFiling::TooRecent,
        "a transcript written moments ago waits, in case Claude is about to write the record itself"
    );
    assert_eq!(
        filing(Some(&facts), &none, &none, &[], later, Some(&owner), false, true),
        SessionFiling::File,
        "but with no Claude up on that account there is nobody to wait for, and it files at once"
    );
    assert_eq!(
        filing(Some(&facts), &none, &none, &[], then, None, true, true),
        SessionFiling::NoOwnerProfile,
        "a session whose owner lives on no profile here waits for one to appear"
    );
    assert_eq!(
        filing(Some(&facts), &none, &none, &[], then, Some(&owner), true, false),
        SessionFiling::FolderElsewhere,
        "a session worked in on another Mac is not put in a sidebar that cannot open it"
    );
    assert_eq!(
        filing(Some(&facts), &none, &none, &[], then, Some(&owner), true, true),
        SessionFiling::File,
        "with everything in place, the record is written"
    );
}

#[test]
fn a_session_folder_is_here_when_its_home_or_drive_is() {
    let _world = World::new();
    // The person's own home, not the suite's, because the question is whether
    // this machine has the place a session's folder sits in.
    let home = std::env::var("HOME").unwrap_or_else(|_| "/".into());
    assert!(
        !session_folder_is_here("/Users/someone-who-never-lived-here/Developer/project"),
        "a project in a home folder this Mac has never had was worked in somewhere else"
    );
    assert!(
        !session_folder_is_here("/Volumes/a-drive-that-is-not-mounted/Developer/project"),
        "a drive that is not here is not here, until it is mounted and the next pass files it"
    );
    assert!(
        session_folder_is_here(&format!("{home}/a-worktree-cleaned-up-long-ago/after-the-chat")),
        "a folder gone from a home that is here is this Mac's own history, and is still filed"
    );
    assert!(
        session_folder_is_here(&format!("/Volumes/../Users/someone-else/../../{}/x", home.trim_start_matches('/'))),
        "a path is read for where it leads, not for how it is spelt"
    );
    assert!(
        session_folder_is_here("/tmp/gone-already") && session_folder_is_here(""),
        "a session with no folder, or one outside every home and drive, is filed as before"
    );
}

#[test]
fn a_record_is_kept_current_only_while_it_is_still_the_one_this_app_wrote() {
    let _world = World::new();
    let early = plain_facts("s-2");
    let mut later = early.clone();
    later.title = "T, continued".into();

    assert_eq!(
        session_update_decision(None, &later, true, false),
        SessionUpdate::Leave,
        "a record this app never wrote is not this app's to bring up to date"
    );
    assert_eq!(
        session_update_decision(Some(&early), &early, true, false),
        SessionUpdate::Leave,
        "a record that already says what the transcript says is left as it is"
    );
    assert_eq!(
        session_update_decision(Some(&early), &later, true, false),
        SessionUpdate::Refresh,
        "a record behind its transcript, still holding what this app wrote, is brought up"
    );
    assert_eq!(
        session_update_decision(Some(&early), &later, false, false),
        SessionUpdate::TakenOver,
        "once something else has rewritten the record, this app never touches it again"
    );
    assert_eq!(
        session_update_decision(Some(&early), &early, false, true),
        SessionUpdate::Refresh,
        "a record still in the shape an earlier version filed is rewritten even with nothing new to say"
    );
    assert_eq!(
        session_update_decision(None, &early, false, true),
        SessionUpdate::Leave,
        "though only one this app remembers filing"
    );
}

#[test]
fn a_record_filed_in_the_older_shape_is_rewritten_without_skipping_permission_checks() {
    let _world = World::new();
    // Every record filed before said to skip every browser permission check.
    // One still in that shape is this app's, and the next pass takes it out.
    let account = "2e2e2e2e-2e2e-4e2e-8e2e-2e2e2e2e2e2e";
    let org = "2f2f2f2f-2f2f-4f2f-8f2f-2f2f2f2f2f2f";
    let session = "2a2a2a2a-2a2a-4a2a-8a2a-2a2a2a2a2a2a";
    let profile = make_profile("Claude-Older-Shape", Some(account), Some(org), &[]);
    Transcript::new(session).owner(account, org).title("Filed before").write();
    let file = org_folder(&profile, account, org).join(format!("local_{session}.json"));

    let _ = std::fs::remove_file(graft_core::diagnostics::file());
    sweep(std::slice::from_ref(&profile), SystemTime::now());
    let log = read(&graft_core::diagnostics::file()).unwrap_or_default();
    assert!(
        log.contains("\"sweep.file\"") && log.contains(session) && !log.contains("Filed before"),
        "the log names a session it filed by its id, never by its title"
    );
    if let Some(authored) = load_session_record_state().authored.get(session) {
        let mut older = session_record(authored);
        older.insert("chromePermissionMode".into(), json!("skip_all_permission_checks"));
        std::fs::write(&file, graft_core::files::sorted_json(&Value::Object(older))).unwrap();
    }
    assert!(
        contains(&file, "skip_all_permission_checks"),
        "a record filed by an earlier version said to skip every browser permission check"
    );
    sweep(std::slice::from_ref(&profile), SystemTime::now());
    let record = json_of(&file);
    assert!(
        record["title"] == "Filed before" && record.get("chromePermissionMode").is_none(),
        "and the next pass rewrites it without that"
    );
    assert!(
        load_session_record_state().authored.contains_key(session),
        "keeping it this app's to bring up to date"
    );
}

fn carried_on_line(prompt: &str, uuid: &str, parent: &str, said: &str, at: &str) -> String {
    format!(
        "{{\"parentUuid\":\"{parent}\",\"isSidechain\":false,\"promptId\":\"{prompt}\",\"cwd\":\"{WORK_CWD}\",\
         \"type\":\"user\",\"message\":{{\"role\":\"user\",\"content\":\"{said}\"}},\"uuid\":\"{uuid}\",\
         \"timestamp\":\"{at}\"}}"
    )
}

#[test]
fn the_sweep_files_what_a_graft_lost_and_respects_every_record_it_did_not_write() {
    let _world = World::new();
    // The world as the machine holds it: a profile on the owner's account,
    // and a second profile grafted onto it, its organization directory a
    // link into the first.
    let home = make_profile("Claude-3", Some(OWNER_ACCOUNT), Some(OWNER_ORG), &[]);
    let second = make_profile("Claude-2", Some(BORROWER_ACCOUNT), Some(BORROWER_ORG), &[]);
    let home_org = org_folder(&home, OWNER_ACCOUNT, OWNER_ORG);
    let second_org = org_folder(&second, BORROWER_ACCOUNT, BORROWER_ORG);
    std::fs::remove_dir_all(&second_org).unwrap();
    std::os::unix::fs::symlink(&home_org, &second_org).unwrap();

    // The lost session: owned by the account Claude-3 holds, started from
    // the grafted profile, which is signed into another, so no Claude ever
    // wrote its record.
    let lost_id = "44444444-4444-4444-8444-444444444444";
    Transcript::new(lost_id).title("Lost work").write();
    let lost_record = home_org.join(format!("local_{lost_id}.json"));

    // Its twin, deleted by hand before Graft ever ran: no record anywhere,
    // and a marker carrying nothing but the moment of the press.
    let twin_id = "55555555-5555-4555-8555-555555555555";
    let twin = Transcript::new(twin_id)
        .title("Deleted by hand")
        .times("2026-08-29T16:40:00.000Z", "2026-08-29T16:40:10.000Z")
        .write();
    if let Some(twin_facts) = session_facts(&twin, twin_id) {
        write(
            &home_org.join("deleted_66666666-6666-4666-8666-666666666666"),
            &format!("{}", (twin_facts.last_activity_at + 30_000.0) as i64),
        );
    }

    // A session owned by the other account, whose record belongs on the far
    // side of the graft link.
    let borrowed_id = "77777777-7777-4777-8777-777777777777";
    Transcript::new(borrowed_id).owner(BORROWER_ACCOUNT, BORROWER_ORG).title("From the other account").write();

    // And a session whose owner no profile on this machine holds.
    let stranger_id = "88888888-8888-4888-8888-888888888888";
    let stranger_account = "99999999-9999-4999-8999-999999999999";
    let stranger_org = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    Transcript::new(stranger_id).owner(stranger_account, stranger_org).title("Stranger's session").write();

    // A session opened and closed with nothing said in it: a bridge line, a
    // stamp, and no conversation to lose.
    let quiet_id = "10101010-1010-4101-8101-101010101010";
    Transcript::new(quiet_id).silent().write();

    let filing = vec![paths::main_profile(), home.clone(), second.clone()];
    let start = SystemTime::now();

    // These transcripts were written a moment ago, and it makes no difference:
    // no Claude on either owner's account is running, so nothing is about to
    // write these records but this sweep, and there is nothing to wait for.
    let first = sweep(&filing, start);
    assert_eq!(
        titles(&first),
        set_of(&["Lost work", "From the other account"]),
        "with no Claude up on the owner's account, a session that closed without a record gets one at once"
    );
    assert!(
        exists(&lost_record),
        "the record lands in the owner's organization, where the session's own window and everything grafted onto it lists it"
    );
    let record = json_of(&lost_record);
    assert!(
        record["cliSessionId"] == lost_id && record["sessionId"] == format!("local_{lost_id}"),
        "the record names its session both of the ways the records do"
    );
    assert_eq!(
        record["bridgeSessionIds"],
        json!(["session_01BridgeSession0000"]),
        "the bridge id is spelled the way records spell it"
    );
    assert!(
        !exists(&home_org.join(format!("local_{twin_id}.json"))),
        "the session deleted by hand before the first pass is not brought back"
    );
    assert!(
        exists(&second_org.join(format!("local_{borrowed_id}.json"))),
        "a record filed through a graft lands where the link points, which is where both windows read"
    );
    assert!(!exists(&home_org.join(format!("local_{quiet_id}.json"))), "a session with nothing said in it gets no record");

    // A pass that finds everything already in place files nothing.
    assert!(
        sweep(&filing, later_by(start, 90)).is_empty(),
        "a session already recorded is left alone, however many passes go by"
    );

    // The session carries on: another prompt, another answer, a new name.
    // The record follows while it is still this app's to move.
    let answer = format!(
        "{{\"parentUuid\":\"u-3\",\"isSidechain\":false,\"cwd\":\"{WORK_CWD}\",\"type\":\"assistant\",\
         \"message\":{{\"model\":\"claude-opus-5\"}},\"uuid\":\"a-2\",\"timestamp\":\"2026-08-29T17:05:00.000Z\"}}"
    );
    Transcript::new(lost_id)
        .title("Lost work, continued")
        .times("2026-08-29T17:00:00.000Z", "2026-08-29T17:05:00.000Z")
        .extra(&[&carried_on_line("prompt-two", "u-3", "a-1", "more", "2026-08-29T17:04:50.000Z"), &answer])
        .write();
    assert!(
        sweep(&filing, later_by(start, 120)).is_empty(),
        "a transcript moving on is not a recovery, so nothing is reported as filed"
    );
    let carried = json_of(&lost_record);
    assert!(
        carried["title"] == "Lost work, continued" && carried["completedTurns"].as_i64() == Some(2),
        "but the record follows its session: the new name and the second turn are in it"
    );

    // Claude takes the session back: the owner's window opened it, and its
    // own record went down over the one this app filed. What a transcript
    // cannot recover must not be flattened away.
    write(
        &lost_record,
        &format!(
            "{{\"cliSessionId\":\"{lost_id}\",\"remoteMcpServersConfig\":\
             [{{\"name\":\"a server the transcript never heard of\"}}],\"title\":\"Lost work\"}}"
        ),
    );
    Transcript::new(lost_id)
        .title("Lost work, carried further")
        .times("2026-08-29T17:00:00.000Z", "2026-08-29T17:10:00.000Z")
        .write();
    assert!(
        sweep(&filing, later_by(start, 180)).is_empty(),
        "a record rewritten by Claude is left exactly as Claude wrote it"
    );
    assert!(
        contains(&lost_record, "a server the transcript never heard of"),
        "what the host wrote is still there word for word"
    );
    assert!(
        !load_session_record_state().authored.contains_key(lost_id),
        "and the sweep stops claiming the record as its own"
    );

    // Deleted in a sidebar: the record, removed by hand the way Claude
    // removes one, marker and all.
    std::fs::remove_file(home_org.join(format!("local_{borrowed_id}.json"))).unwrap();
    write(&home_org.join(format!("deleted_{borrowed_id}")), "1788024768893");
    assert!(
        sweep(&filing, later_by(start, 240)).is_empty(),
        "the sweep saw the record go, and does not put it back"
    );
    assert!(
        !exists(&second_org.join(format!("local_{borrowed_id}.json"))),
        "the delete in the sidebar sticks, through the link and all"
    );
    let withdrawn_state = load_session_record_state();
    assert!(
        withdrawn_state.withdrawn.iter().any(|w| w == borrowed_id),
        "the withdrawal is written down, so no later pass forgets it"
    );
    assert!(
        !withdrawn_state.authored.contains_key(borrowed_id),
        "and a withdrawn session's authored copy goes with it"
    );

    // The owner's account turns up on a new profile, and the session that
    // had nowhere to go now goes there.
    let late = make_profile("Claude-4", Some(stranger_account), Some(stranger_org), &[]);
    let with_late: Vec<PathBuf> = filing.iter().cloned().chain([late.clone()]).collect();
    let fifth = sweep(&with_late, later_by(start, 300));
    assert_eq!(
        titles(&fifth),
        set_of(&["Stranger's session"]),
        "a session whose owner arrived on a later pass is filed when the profile holding the account appears"
    );
    assert!(
        !exists(&home_org.join(format!("local_{twin_id}.json"))),
        "and the deletion marker holds its session back on every pass, not just the first"
    );

    // A state file from a version that kept no authored records still
    // loads, claiming nothing as this app's to move.
    let current = load_session_record_state();
    std::fs::write(
        session_record_state_file(),
        serde_json::to_vec(&json!({"records": current.records, "withdrawn": current.withdrawn})).unwrap(),
    )
    .unwrap();
    assert!(
        load_session_record_state().authored.is_empty(),
        "a state file without an authored map loads with nothing claimed as authored"
    );
    assert!(
        sweep(&with_late, later_by(start, 360)).is_empty(),
        "and a pass over that state touches nothing it does not own"
    );
    assert!(
        contains(&lost_record, "a server the transcript never heard of"),
        "the host's record is still there, passes later"
    );
}

#[test]
fn a_store_out_of_sight_is_never_read_as_a_store_somebody_emptied() {
    let world = World::new();
    // A store out of sight is not a store somebody emptied. Every directory
    // in the walk is read leniently, so a profile that has been deleted, or
    // stashed behind a graft, or is waiting on a permission, comes back
    // holding nothing at all, and reading that as a sidebar cleared by hand
    // withdrew every session it held, forever. Graft stashes org folders
    // itself, so its own graft was the surest way to trigger it.
    let account = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    let org = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
    let only = make_profile("Claude-5", Some(account), Some(org), &[]);
    let only_org = org_folder(&only, account, org);
    let id = "12121212-1212-4121-8121-121212121212";
    Transcript::new(id).owner(account, org).title("Only copy").write();
    let filing = std::slice::from_ref(&only);

    let start = later_by(SystemTime::now(), 120);
    assert_eq!(
        sweep(filing, start).len(),
        1,
        "a session whose owner is here and whose record is not gets one"
    );
    assert!(exists(&only_org.join(format!("local_{id}.json"))), "and it is on disk where the owner reads");

    // The profile goes out of sight the way a deleted one does: gone from
    // the folder every profile lives in entirely, so the walk finds nothing
    // of it.
    let aside = world.sandbox.root.path().join("Claude-5.aside");
    std::fs::rename(&only, &aside).unwrap();
    sweep(&[], later_by(start, 60));
    assert!(
        load_session_record_state().withdrawn.is_empty(),
        "a pass that could not look anywhere withdraws nothing"
    );
    std::fs::rename(&aside, &only).unwrap();
    sweep(filing, later_by(start, 120));
    assert!(
        load_session_record_state().withdrawn.is_empty(),
        "and the session is still the sweep's to keep once the store is back"
    );

    // Deleted by hand, with the store right there to be read: that is the
    // absence the rule is actually about, once the pass after agrees. Claude
    // re-files a session under a new record name as it runs, so there is a
    // moment when the old name has gone and the new one has not landed, and a
    // single pass cannot tell that from a delete.
    std::fs::remove_file(only_org.join(format!("local_{id}.json"))).unwrap();
    sweep(filing, later_by(start, 180));
    assert!(
        load_session_record_state().withdrawn.is_empty(),
        "one pass finding a record gone is a record being renamed, as often as not"
    );
    assert_eq!(
        load_session_record_state().vanished,
        vec![id.to_string()],
        "so the pass notes it and leaves the next one something to agree with"
    );
    sweep(filing, later_by(start, 240));
    assert_eq!(
        load_session_record_state().withdrawn,
        vec![id.to_string()],
        "a record still gone the pass after is one somebody deleted"
    );

    // And a record that comes back under a new name is not a deletion at all.
    let _ = std::fs::remove_file(session_record_state_file());
    sweep(filing, later_by(start, 300));
    let renamed = only_org.join(format!("local_{id}.json"));
    let on_disk = std::fs::read(&renamed).unwrap();
    std::fs::remove_file(&renamed).unwrap();
    sweep(filing, later_by(start, 360));
    std::fs::write(only_org.join("local_99999999-9999-4999-8999-999999999999.json"), on_disk).unwrap();
    sweep(filing, later_by(start, 420));
    assert!(
        load_session_record_state().withdrawn.is_empty(),
        "a session whose record turned up again under another name was never deleted"
    );
}
