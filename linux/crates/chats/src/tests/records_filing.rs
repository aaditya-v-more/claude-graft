//! Filing session records: deletions read by name, stores out of sight,
//! profiles this app did not make, conversations carried on or asked again,
//! archived records, and what an absence is read as.

use super::*;
use crate::cache::sessions_of_record;
use crate::fsx::{exists, resolved_text, stash_path};
use crate::{
    file_missing_session_records_at, load_session_record_state, may_file_records, prompt_fingerprint,
    prompts_typed, resumed_copies, session_facts, session_store_contents, ConversationTrace, SessionFacts,
};
use graft_core::{config, diagnostics, files};
use std::collections::{BTreeMap, BTreeSet};
use std::time::{Duration, SystemTime};

fn pass(filing_into: &[PathBuf], now: SystemTime) -> Vec<SessionFacts> {
    file_missing_session_records_at(filing_into, now, Duration::from_secs(60))
}

fn soon() -> SystemTime {
    SystemTime::now() + Duration::from_secs(120)
}

fn org_folder(profile: &Path, account: &str, org: &str) -> PathBuf {
    profile.join("claude-code-sessions").join(account).join(org)
}

fn record_named(org: &Path, session: &str) -> PathBuf {
    org.join(format!("local_{session}.json"))
}

fn write_json_file(path: &Path, value: &Value) {
    std::fs::write(path, serde_json::to_vec(value).unwrap()).unwrap();
}

/// Writes a transcript that has moved on since an earlier pass read it. The
/// transcript cache knows a file by its modification time and size, and a
/// rewrite of the same length inside one tick of the filesystem clock would
/// read as the copy already parsed, so the time is pushed past any earlier one.
fn moved_on(transcript: &Transcript) -> PathBuf {
    let path = transcript.write();
    set_modified(&path, SystemTime::now() + Duration::from_secs(5));
    path
}

/// Every event the diagnostics log holds, in the order written.
fn events() -> Vec<Value> {
    read(&diagnostics::file())
        .unwrap_or_default()
        .lines()
        .filter_map(|line| serde_json::from_str::<Value>(line).ok())
        .collect()
}

/// A prompt a person typed, on a line of its own.
fn prompt(words: &str, id: &str, extra: &str) -> String {
    format!(
        "{{\"parentUuid\":null,\"promptId\":\"{id}\",\"cwd\":\"{WORK_CWD}\",\"type\":\"user\",\
         \"message\":{{\"role\":\"user\",\"content\":\"{words}\"}},{extra}\"uuid\":\"{id}-u\",\
         \"timestamp\":\"2026-08-29T17:05:00.000Z\"}}"
    )
}

const FIRST: &str = "2026-08-29T17:00:00.000Z";

#[test]
fn a_marker_carrying_the_sessions_own_name_is_a_delete_read_rather_than_inferred() {
    let _world = World::new();
    // Graft names its records for the session, so the marker Claude leaves
    // when one is deleted names the session too. That is a deletion read
    // rather than inferred, and it needs no memory of earlier passes.
    let account = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";
    let org = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee";
    let home = make_profile("Claude-6", Some(account), Some(org), &[]);
    let home_org = org_folder(&home, account, org);
    let id = "13131313-1313-4131-8131-131313131313";
    Transcript::new(id).owner(account, org).title("Named by its session").write();
    write(&home_org.join(format!("deleted_{id}")), "1788024768893");

    let now = soon();
    assert!(
        pass(&[home.clone()], now).is_empty(),
        "a marker carrying the session's own name is a delete, first pass and all"
    );
    assert!(!exists(&record_named(&home_org, id)), "so nothing is written for it");
    assert_eq!(
        load_session_record_state().withdrawn,
        vec![id.to_string()],
        "and it is written down, so the marker may be tidied away without the session coming back"
    );

    // The marker outlives the withdrawal, and the next pass meets it again.
    let _ = std::fs::remove_file(diagnostics::file());
    pass(&[home.clone()], now + Duration::from_secs(60));
    let events = events();
    assert!(
        !events.iter().any(|e| e["event"] == "sweep.withdraw"),
        "a session withdrawn on an earlier pass is not withdrawn again because its marker is still there"
    );
    let end = events.iter().rev().find(|e| e["event"] == "sweep.end").cloned().unwrap_or(Value::Null);
    assert!(
        end["withdrawn"].as_i64() == Some(0) && end["withdrawnInAll"].as_i64() == Some(1),
        "and the end of the pass counts what it withdrew, with the running total named as one"
    );
}

#[test]
fn a_store_this_pass_could_not_read_is_not_a_store_with_nothing_in_it() {
    let _world = World::new();
    // A store this pass could not read is not a store with nothing in it.
    // Everything sitting in one looks unfiled from outside, and a record
    // written fresh says the session is not archived, so a graft that
    // stashed a store had the sweep on the way past file its whole history
    // back, and every conversation archived by hand that day was in the
    // sidebar again by the afternoon.
    let account = "f1f1f1f1-f1f1-4f1f-8f1f-f1f1f1f1f1f1";
    let org = "f2f2f2f2-f2f2-4f2f-8f2f-f2f2f2f2f2f2";
    let home = make_profile("Claude-7", Some(account), Some(org), &[]);
    let home_org = org_folder(&home, account, org);
    let id = "14141414-1414-4141-8141-141414141414";
    Transcript::new(id).owner(account, org).title("Archived by hand").write();

    let start = soon();
    assert_eq!(pass(&[home.clone()], start).len(), 1, "a session that closed without a record gets one");
    let record = record_named(&home_org, id);

    // Archiving it is Claude writing the flag into the record this pass filed.
    let mut archived = json_of(&record);
    archived["isArchived"] = json!(true);
    write_json_file(&record, &archived);

    // And then the store goes behind a graft, which is this app stashing the
    // organization folder under a hidden name of its own.
    let stash = stash_path(&home_org);
    std::fs::rename(&home_org, &stash).unwrap();

    assert!(
        pass(&[home.clone()], start + Duration::from_secs(60)).is_empty(),
        "a pass that could not read the store files nothing back into it"
    );
    assert!(!exists(&home_org), "and does not build the organization folder back over the top of the stash");

    // Not unwrapped: a pass that rebuilt the folder leaves the stash with
    // nowhere to go, and the checks below are a better account of what went
    // wrong than a panic on the way there.
    let _ = std::fs::rename(&stash, &home_org);
    assert!(
        pass(&[home.clone()], start + Duration::from_secs(120)).is_empty(),
        "the store coming back into sight brings nothing with it either"
    );
    assert_eq!(
        json_of(&record)["isArchived"].as_bool(),
        Some(true),
        "so a conversation archived by hand is still archived"
    );
    assert_eq!(
        crate::fsx::names_or_empty(&home_org).len(),
        1,
        "and the profile has one record for it rather than two"
    );

    assert!(
        !may_file_records(&home_org, &BTreeSet::new()),
        "an organization folder no walk got into is not one to write records into"
    );
    assert!(
        may_file_records(&home_org, &BTreeSet::from([resolved_text(&home_org)])),
        "one the walk read is"
    );
    let never = home_org.parent().unwrap().join("f3f3f3f3-f3f3-4f3f-8f3f-f3f3f3f3f3f3");
    assert!(
        may_file_records(&never, &BTreeSet::new()),
        "and a folder that is simply not there yet is how a profile gets its first record"
    );
}

#[test]
fn a_profile_this_app_did_not_make_is_read_and_never_written_to() {
    let _world = World::new();
    // Every profile holding a chat store is read, because a record in one
    // means some Claude is already listing the session. Writing is a
    // different question with a different answer: a Claude profile this app
    // did not make belongs to whatever did make it, and filing into it puts
    // chats in somebody else's sidebar under names of this app's choosing.
    let list = paths::shortcuts_file();
    let _ = std::fs::remove_file(&list);

    let account = "f4f4f4f4-f4f4-4f4f-8f4f-f4f4f4f4f4f4";
    let org = "f5f5f5f5-f5f5-4f5f-8f5f-f5f5f5f5f5f5";
    let stranger = make_profile("Claude-Somebody-Else", Some(account), Some(org), &[]);
    let stranger_org = org_folder(&stranger, account, org);
    let id = "15151515-1515-4151-8151-151515151515";
    Transcript::new(id).owner(account, org).title("Somebody else's").write();

    let start = soon();
    assert!(
        pass(&[paths::main_profile()], start).is_empty(),
        "a session whose owner lives only on a profile this app never made is left where it is"
    );
    assert!(
        crate::fsx::names_or_empty(&stranger_org).is_empty(),
        "and nothing of this app's is written into that profile"
    );

    // A launcher knows its own profile, its source and nothing else, so the
    // list the window keeps is how a session owned by another of this app's
    // profiles is still filed by one.
    write_json_file(
        &list,
        &json!([{
            "id": "6F1E2D3C-4B5A-4978-8695-A4B3C2D1E0F9",
            "name": "Somebody Else",
            "folder": "Claude-Somebody-Else",
        }]),
    );
    assert_eq!(
        pass(&[paths::main_profile()], start + Duration::from_secs(60)).len(),
        1,
        "the same profile is filed into once it is one of this app's own"
    );
    assert!(
        config::record_filing_profiles(&[]).iter().any(|p| paths::same_path(p, &paths::main_profile())),
        "Claude's own profile is always one a record may be filed into"
    );
    let _ = std::fs::remove_file(&list);
    assert!(
        !config::record_filing_profiles(&[]).iter().any(|p| paths::same_path(p, &stranger)),
        "and a profile named in no list of this app's is not"
    );
}

#[test]
fn the_transcript_a_conversation_grew_out_of_is_not_a_session_anybody_lost() {
    let _world = World::new();
    // A conversation carried on past a compaction gets a new command line
    // session, and the record names the ones it grew out of. Those older
    // transcripts sit on disk whole with no record naming them, which is
    // exactly what a session that closed without one looks like, so the
    // sweep filed them, and the sidebar showed the same conversation twice:
    // once as the person left it, once fresh and unarchived.
    let account = "f6f6f6f6-f6f6-4f6f-8f6f-f6f6f6f6f6f6";
    let org = "f7f7f7f7-f7f7-4f7f-8f7f-f7f7f7f7f7f7";
    let home = make_profile("Claude-8", Some(account), Some(org), &[]);
    let home_org = org_folder(&home, account, org);

    let earlier = "16161616-1616-4161-8161-161616161616";
    let carried_on = "17171717-1717-4171-8171-171717171717";
    Transcript::new(earlier).owner(account, org).title("Long conversation").write();
    Transcript::new(carried_on).owner(account, org).title("Long conversation").write();

    // Claude's own record for the conversation as it stands now, naming the
    // session it carried on from, and archived, the way a person left it.
    let own = home_org.join("local_18181818-1818-4181-8181-181818181818.json");
    write_json_file(
        &own,
        &json!({
            "sessionId": "local_18181818-1818-4181-8181-181818181818",
            "cliSessionId": carried_on,
            "priorCliSessionIds": [earlier],
            "title": "Long conversation",
            "isArchived": true,
        }),
    );

    assert!(
        pass(&[home.clone()], soon()).is_empty(),
        "the transcript a conversation grew out of is not a session anybody lost"
    );
    assert!(
        !exists(&record_named(&home_org, earlier)),
        "so the conversation is listed once, as its own record has it, and not again beside itself"
    );

    let both = sessions_of_record(&own);
    assert!(
        both.cli_session_id.as_deref() == Some(carried_on) && both.prior == vec![earlier.to_string()],
        "a record says which session it holds now and which it has taken over"
    );
}

fn facts(id: &str, cwd: &str, owner: &str, org: &str, last: f64) -> SessionFacts {
    SessionFacts {
        cli_session_id: id.into(),
        bridge_ids: vec!["cse_x".into()],
        owner_account: owner.into(),
        owner_organization: org.into(),
        title: "Same title".into(),
        cwd: cwd.into(),
        created_at: 0.0,
        last_activity_at: last,
        model: "m".into(),
        effort: "e".into(),
        permission_mode: "auto".into(),
        prompts: 1,
        branches: Vec::new(),
    }
}

fn trace(asked: &[&str], opening: i64, work: &[(&str, i64)]) -> ConversationTrace {
    ConversationTrace {
        prompts: asked.iter().map(|s| s.to_string()).collect(),
        opening_size: opening,
        work: work.iter().map(|(k, v)| (k.to_string(), *v)).collect(),
    }
}

fn with_shared<'a>(own: &[(&'a str, i64)]) -> Vec<(&'a str, i64)> {
    let mut work = vec![("h1", 4_000), ("h2", 9_000)];
    work.extend_from_slice(own);
    work
}

#[test]
fn a_conversation_edited_or_resumed_is_listed_once_and_not_once_per_session() {
    let _world = World::new();
    // Editing a prompt or resuming a conversation starts a new session, and
    // the one left behind has no record naming it if the profile was grafted
    // at the time. Filing those put one conversation in a sidebar six times.
    let account = "e8e8e8e8-e8e8-4e8e-8e8e-e8e8e8e8e8e8";
    let org = "e9e9e9e9-e9e9-4e9e-8e9e-e9e9e9e9e9e9";
    let home = make_profile("Claude-Copies", Some(account), Some(org), &[]);
    let home_org = org_folder(&home, account, org);

    // What counts as a prompt, read off one transcript.
    let reading = Transcript::new("21212121-2121-4212-8212-212121212121")
        .owner(account, org)
        .said("Review the PR")
        .extra(&[
            // A tool's output comes back as a user line, with a prompt id.
            "{\"promptId\":\"p-2\",\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\
             [{\"tool_use_id\":\"t-1\",\"type\":\"tool_result\",\"content\":\"42 files\"}]},\
             \"timestamp\":\"2026-08-29T17:01:00.000Z\"}",
            // So does Claude's note about a command that ran.
            &prompt("<local-command-stdout>ok</local-command-stdout>", "p-3", "\"isMeta\":true,"),
            // And stopping a turn writes a line nobody typed.
            "{\"promptId\":\"p-5\",\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\
             [{\"type\":\"text\",\"text\":\"[Request interrupted by user]\"}]},\
             \"timestamp\":\"2026-08-29T17:01:30.000Z\"}",
            // A prompt that came with an image carries its words in a text block.
            "{\"promptId\":\"p-4\",\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\
             [{\"type\":\"image\",\"source\":{\"type\":\"base64\"}},{\"type\":\"text\",\"text\":\"What is this?\"}]},\
             \"timestamp\":\"2026-08-29T17:02:00.000Z\"}",
        ])
        .write();
    let typed = prompts_typed(&reading);
    let expected: Vec<String> =
        [prompt_fingerprint("Review the PR"), prompt_fingerprint("What is this?")].into_iter().flatten().collect();
    assert_eq!(
        typed, expected,
        "the prompts a person typed are read, and a tool's output, Claude's own notes and a stopped turn are not"
    );
    assert_eq!(
        prompt_fingerprint("Review  the PR\n"),
        prompt_fingerprint("Review the PR"),
        "a prompt pasted again with different spacing is still the same prompt"
    );
    let _ = std::fs::remove_file(&reading);

    // The rule on its own, over facts made up for it.
    let f = |id: &str, last: f64| facts(id, WORK_CWD, account, org, last);
    let none = BTreeMap::new();
    let copies = resumed_copies(
        &[
            (f("kept", 5.0), trace(&["a", "b", "c"], 40, &with_shared(&[("k1", 6_000)]))),
            (f("retried", 9.0), trace(&["a", "b"], 40, &with_shared(&[("r1", 80)]))),
            (f("drafted", 9.0), trace(&["a", "b"], 40, &with_shared(&[("d1", 9_097)]))),
            (f("implemented", 9.0), trace(&["a"], 40, &[("i1", 40 + ConversationTrace::EDIT_WEIGHT)])),
            (f("branch", 3.0), trace(&["a", "d"], 40, &[])),
            (f("twin-old", 1.0), trace(&["x"], 40, &[])),
            (f("twin-new", 2.0), trace(&["x"], 40, &[])),
            (f("hi-one", 1.0), trace(&["hi"], 2, &[])),
            (f("hi-two", 2.0), trace(&["hi", "sync my profiles"], 2, &[])),
            (facts("elsewhere", "/tmp/shared/other", account, org, 1.0), trace(&["a"], 40, &[])),
            (
                facts("someone-else", WORK_CWD, "f0f0f0f0-f0f0-4f0f-8f0f-f0f0f0f0f0f0", org, 1.0),
                trace(&["a"], 40, &[]),
            ),
            (f("other-opening", 1.0), trace(&["b"], 40, &[])),
        ],
        &none,
    );
    assert!(
        copies.contains("retried"),
        "a session whose every prompt a longer one also holds is a copy, however recently it was touched"
    );
    assert!(!copies.contains("kept"), "and the conversation holding them all is not");
    assert!(
        !copies.contains("drafted"),
        "the same questions are not the same conversation when one of them holds an answer the other lacks"
    );
    assert!(!copies.contains("implemented"), "and a session that edited a file is never a copy");
    assert!(!copies.contains("branch"), "a branch that asked anything of its own is not a copy");
    assert!(
        copies.contains("twin-old") && !copies.contains("twin-new"),
        "of two sessions that asked exactly the same, the one active last is the one kept"
    );
    assert!(!copies.contains("hi-one"), "an opening that says nothing identifies no conversation");
    assert!(
        !copies.contains("elsewhere") && !copies.contains("someone-else"),
        "the same prompt in another folder or another account is another conversation"
    );
    assert!(
        !copies.contains("other-opening"),
        "a retry starts where the original did, so a different opening is a different chat"
    );

    // A fork copies the whole conversation and asks more, which from the
    // transcripts alone is exactly what a resumed copy looks like.
    let forked_away = [
        (f("original", 5.0), trace(&["a", "b"], 40, &with_shared(&[]))),
        (f("forked", 9.0), trace(&["a", "b", "f"], 40, &with_shared(&[("f1", 3_000)]))),
        (f("retried-original", 1.0), trace(&["a"], 40, &[("h1", 4_000)])),
    ];
    assert!(
        resumed_copies(&forked_away, &none).contains("original"),
        "judged by transcripts alone, an original whose fork asked more reads as a copy of it"
    );
    let forks = BTreeMap::from([("forked".to_string(), "original".to_string())]);
    let fork_aware = resumed_copies(&forked_away, &forks);
    assert!(
        !fork_aware.contains("original") && !fork_aware.contains("forked"),
        "so neither half of a fork its record owns up to is ever a copy of the other"
    );
    assert!(fork_aware.contains("retried-original"), "while a retry of the original is still the copy it always was");
    let fresh_fork = [
        (f("source", 5.0), trace(&["a", "b"], 40, &with_shared(&[]))),
        (f("fresh-fork", 9.0), trace(&["a", "b"], 40, &with_shared(&[]))),
    ];
    assert!(
        resumed_copies(&fresh_fork, &BTreeMap::from([("fresh-fork".to_string(), "source".to_string())])).is_empty(),
        "and a fork that has asked nothing of its own yet leaves its source standing"
    );

    // And end to end, through a pass.
    let longer = "22222222-2222-4222-8222-222222222222";
    let retried = "23232323-2323-4232-8232-232323232323";
    let branched = "24242424-2424-4242-8242-242424242424";
    let answered = "25252525-2525-4252-8252-252525252525";
    let greeted = "26262626-2626-4262-8262-262626262626";
    let opening = "Two accounts, one history?";
    Transcript::new(longer)
        .owner(account, org)
        .title("Multi-account sync")
        .times(FIRST, "2026-08-29T18:00:00.000Z")
        .said(opening)
        .extra(&[&prompt("Then test it", "p-9", "")])
        .write();
    Transcript::new(retried)
        .owner(account, org)
        .title("Multi-account sync")
        .times(FIRST, "2026-08-29T19:00:00.000Z")
        .said(opening)
        .write();
    Transcript::new(branched)
        .owner(account, org)
        .title("Multi-account sync")
        .said(opening)
        .extra(&[&prompt("Give me a command to delete them", "p-8", "")])
        .write();
    // Asked only what the longer one asked, and answered it where the longer
    // one did not: the draft that exists nowhere else.
    let draft = "A finished comment, ready to paste. ".repeat(30);
    Transcript::new(answered)
        .owner(account, org)
        .title("Multi-account sync")
        .said(opening)
        .extra(&[&format!(
            "{{\"parentUuid\":\"u-1\",\"cwd\":\"{WORK_CWD}\",\"message\":{{\"id\":\"m-9\",\
             \"content\":[{{\"type\":\"text\",\"text\":\"{draft}\"}}]}},\"type\":\"assistant\",\
             \"uuid\":\"a-draft\",\"timestamp\":\"2026-08-29T17:20:00.000Z\"}}"
        )])
        .write();
    // And a greeting, which the longer one also happens to contain.
    Transcript::new(greeted).owner(account, org).said("hi").write();
    Transcript::new("27272727-2727-4272-8272-272727272727")
        .owner(account, org)
        .times(FIRST, "2026-08-29T20:00:00.000Z")
        .said("hi")
        .extra(&[&prompt("Now sync my profiles please", "p-7", "")])
        .write();
    let filed: BTreeSet<String> = pass(&[home.clone()], soon()).into_iter().map(|f| f.cli_session_id).collect();
    assert!(
        filed.contains(longer) && filed.contains(branched) && !filed.contains(retried),
        "a pass files the conversation and its real branch, and not the copy it left behind"
    );
    assert!(
        filed.contains(answered),
        "and files a retry that holds an answer of its own, since that answer is nowhere else"
    );
    assert!(filed.contains(greeted), "and files a chat that only said hi, which is no copy of anything");
    assert!(!exists(&record_named(&home_org, retried)), "so the sidebar lists that conversation once");
}

/// Claude archiving a record in place: the flag flipped, everything else left
/// as it was.
fn archive(file: &Path) {
    let mut record = json_of(file);
    assert!(
        record.is_object(),
        "there was a record at {} to archive",
        file.file_name().unwrap().to_string_lossy()
    );
    record["isArchived"] = json!(true);
    files::atomic_write(file, &serde_json::to_vec(&record).unwrap(), 0o644).unwrap();
}

fn archived(file: &Path) -> bool {
    json_of(file)["isArchived"].as_bool().unwrap_or(false)
}

#[test]
fn every_route_a_pass_takes_past_an_archived_record_leaves_the_flag_where_it_found_it() {
    let _world = World::new();
    // Archiving is a record write, and every route a pass can take past an
    // archived record has to leave the flag where it found it. These are the
    // four shapes an archived record turns up in.
    let account = "f8f8f8f8-f8f8-4f8f-8f8f-f8f8f8f8f8f8";
    let org = "f9f9f9f9-f9f9-4f9f-8f9f-f9f9f9f9f9f9";
    let home = make_profile("Claude-9", Some(account), Some(org), &[]);
    let home_org = org_folder(&home, account, org);
    let mut clock = soon();
    let mut pass = || {
        clock += Duration::from_secs(120);
        file_missing_session_records_at(&[home.clone()], clock, Duration::from_secs(60))
    };

    // One: a record this app filed, archived by hand, with nothing else
    // happening. The pass after has no reason to touch it and does not.
    let quiet = "19191919-1919-4191-8191-191919191919";
    Transcript::new(quiet).owner(account, org).title("Archived and left alone").write();
    assert_eq!(pass().len(), 1, "a session that closed without a record gets one");
    let quiet_record = record_named(&home_org, quiet);
    archive(&quiet_record);
    assert!(
        pass().is_empty() && archived(&quiet_record),
        "a record archived by hand is still archived after a pass that files nothing"
    );

    // Two: the same, but the transcript moves on afterwards, which is the
    // one thing that sends a pass back to a record it wrote. The flag says
    // Claude has been here since, so the record stops being this app's.
    let moving = "1a1a1a1a-1a1a-41a1-81a1-1a1a1a1a1a1a";
    Transcript::new(moving).owner(account, org).title("Still being written").write();
    pass();
    let moving_record = record_named(&home_org, moving);
    assert!(
        load_session_record_state().authored.contains_key(moving),
        "a record this app filed is one it knows it wrote"
    );
    archive(&moving_record);
    moved_on(
        &Transcript::new(moving)
            .owner(account, org)
            .title("Still being written")
            .times(FIRST, "2026-08-29T19:30:00.000Z"),
    );
    assert!(
        pass().is_empty() && archived(&moving_record),
        "and a transcript moving on past an archived record does not unarchive it"
    );
    assert!(
        !load_session_record_state().authored.contains_key(moving),
        "the pass gives up its claim instead, the record having been rewritten by another hand"
    );

    // Three: Claude takes the session over and re-files it under a name of
    // its own, archived. The name this app used is gone, and nothing brings
    // it back: the session is recorded, under the id the record carries.
    let renamed = "1b1b1b1b-1b1b-41b1-81b1-1b1b1b1b1b1b";
    Transcript::new(renamed).owner(account, org).title("Taken over").write();
    pass();
    let mine = record_named(&home_org, renamed);
    let mut host = json_of(&mine);
    host["sessionId"] = json!("local_1c1c1c1c-1c1c-41c1-81c1-1c1c1c1c1c1c");
    host["isArchived"] = json!(true);
    let theirs = home_org.join("local_1c1c1c1c-1c1c-41c1-81c1-1c1c1c1c1c1c.json");
    std::fs::remove_file(&mine).unwrap();
    write_json_file(&theirs, &host);
    moved_on(
        &Transcript::new(renamed)
            .owner(account, org)
            .title("Taken over")
            .times(FIRST, "2026-08-29T20:00:00.000Z"),
    );
    assert!(pass().is_empty(), "a session re-filed under Claude's own name is not a session missing a record");
    assert!(
        !exists(&mine) && archived(&theirs),
        "so the name this app used stays gone, and the archived record keeps its flag"
    );

    // Four: an archived record caught mid-write, which parses as nothing at
    // all. One pass cannot tell that from a delete and does not guess.
    let half = "1d1d1d1d-1d1d-41d1-81d1-1d1d1d1d1d1d";
    Transcript::new(half).owner(account, org).title("Caught mid-write").write();
    pass();
    let half_record = record_named(&home_org, half);
    archive(&half_record);
    let whole = std::fs::read(&half_record).unwrap();
    files::atomic_write(&half_record, b"{\"cliSess", 0o644).unwrap();
    assert!(pass().is_empty(), "a record that will not parse is not a record to be written again");
    files::atomic_write(&half_record, &whole, 0o644).unwrap();
    assert!(
        pass().is_empty() && archived(&half_record),
        "and once it reads again it is the archived record it always was"
    );
}

// Reading an absence.

#[test]
fn a_record_remembered_at_a_folder_that_has_gone_is_forgotten_rather_than_withdrawn() {
    let _world = World::new();
    // A folder that is not there, with nothing stashed above it, is a folder
    // nobody writes to: the profile was deleted, or signing in again moved
    // <account>/<org>. Remembering it left the session out of sight on every
    // pass from then on, the transcript whole on disk and the chat in nobody's
    // sidebar, with no finding naming it either.
    let account = "4a4a4a4a-4a4a-4a4a-8a4a-4a4a4a4a4a4a";
    let org = "4b4b4b4b-4b4b-4b4b-8b4b-4b4b4b4b4b4b";
    let session = "4c4c4c4c-4c4c-4c4c-8c4c-4c4c4c4c4c4c";
    let was = make_profile("Claude-Was-Here", Some(account), Some(org), &[]);
    Transcript::new(session).owner(account, org).title("Moved on").write();

    pass(&[was.clone()], SystemTime::now());
    let filed = record_named(&org_folder(&was, account, org), session);
    assert!(exists(&filed), "a first pass files the record where the account lives");
    assert!(
        load_session_record_state().records.contains_key(session),
        "and remembers which folder it put it in"
    );

    // The profile goes, and the account turns up on another one.
    std::fs::remove_dir_all(&was).unwrap();
    let now = make_profile("Claude-Is-Here", Some(account), Some(org), &[]);
    pass(&[now.clone()], SystemTime::now());
    assert!(
        exists(&record_named(&org_folder(&now, account, org), session)),
        "a record remembered at a folder that has gone is forgotten, so the session is filed where the account is now"
    );
    assert!(
        !load_session_record_state().withdrawn.contains(&session.to_string()),
        "and forgetting where it was is not the same as deciding somebody deleted it"
    );
}

#[test]
fn with_every_record_gone_at_once_a_fork_and_its_source_are_both_filed_again() {
    let _world = World::new();
    // A fork copies the whole conversation, so once every record is gone at
    // once, the profile deleted or a sign-in moving its folder, the original
    // reads as a copy its fork carried on, and a pass filing both again from
    // their transcripts would have brought back the fork alone.
    let account = "4d4d4d4d-4d4d-4d4d-8d4d-4d4d4d4d4d4d";
    let org = "4e4e4e4e-4e4e-4e4e-8e4e-4e4e4e4e4e4e";
    let original = "4f4f4f4f-4f4f-4f4f-8f4f-4f4f4f4f4f4f";
    let fork = "50505050-5050-4050-8050-505050505050";
    let opening = "Plan the move to the new Mac";
    Transcript::new(original).owner(account, org).title("Mac move").said(opening).write();
    Transcript::new(fork)
        .owner(account, org)
        .title("Mac move (fork)")
        .times(FIRST, "2026-08-29T18:00:10.000Z")
        .said(opening)
        .extra(&[&format!(
            "{{\"parentUuid\":\"a-1\",\"promptId\":\"p-side\",\"cwd\":\"{WORK_CWD}\",\"type\":\"user\",\
             \"message\":{{\"role\":\"user\",\"content\":\"And the routines on it?\"}},\
             \"uuid\":\"u-side\",\"timestamp\":\"2026-08-29T18:00:00.000Z\"}}"
        )])
        .write();
    let was = make_profile("Claude-Forked-Here", Some(account), Some(org), &[]);
    let was_org = org_folder(&was, account, org);
    let record = |name: &str, session: &str, source: Option<&str>| {
        let mut fields = json!({ "sessionId": name, "cliSessionId": session });
        if let Some(source) = source {
            fields["forkedFromSessionId"] = json!(source);
        }
        write_json_file(&was_org.join(format!("{name}.json")), &fields);
    };
    record("local_desktop-original", original, None);
    record("local_desktop-fork", fork, Some("local_desktop-original"));

    let untouched = pass(&[was.clone()], soon());
    assert!(untouched.is_empty(), "a fork and the conversation it came from, both on record, are left alone");
    assert_eq!(
        load_session_record_state().forks.get(fork).map(String::as_str),
        Some(original),
        "and a pass remembers which one the fork came from while both records stand"
    );

    std::fs::remove_dir_all(&was).unwrap();
    let now = make_profile("Claude-Forked-Now", Some(account), Some(org), &[]);
    let refiled: BTreeSet<String> = pass(&[now.clone()], soon()).into_iter().map(|f| f.cli_session_id).collect();
    assert_eq!(
        refiled,
        BTreeSet::from([original.to_string(), fork.to_string()]),
        "so with every record gone at once, the conversation a fork came from is filed again beside the fork"
    );
}

#[test]
fn a_marker_naming_a_session_takes_nothing_else_that_went_quiet_beside_it() {
    let _world = World::new();
    // The timing guess is for markers this app cannot read by name, a record
    // Claude wrote under an id no transcript carries. It used to see every
    // marker on the machine, and they accumulate, so one deletion went on
    // suppressing whatever else had gone quiet in the minute before it.
    let account = "5a5a5a5a-5a5a-4a5a-8a5a-5a5a5a5a5a5a";
    let org = "5b5b5b5b-5b5b-4b5b-8b5b-5b5b5b5b5b5b";
    let deleted = "5c5c5c5c-5c5c-4c5c-8c5c-5c5c5c5c5c5c";
    let neighbour = "5d5d5d5d-5d5d-4d5d-8d5d-5d5d5d5d5d5d";
    let profile = make_profile("Claude-Marker", Some(account), Some(org), &[]);
    let deleted_transcript = Transcript::new(deleted)
        .owner(account, org)
        .title("The one deleted")
        .times("2026-08-29T18:00:00.000Z", "2026-08-29T18:00:10.000Z")
        .write();
    Transcript::new(neighbour)
        .owner(account, org)
        .title("The one beside it")
        .times("2026-08-29T17:59:40.000Z", "2026-08-29T17:59:50.000Z")
        .write();

    // A marker naming the session outright, written twenty seconds after the
    // other one went quiet, which is well inside the window the guess uses.
    let store = org_folder(&profile, account, org);
    let when = session_facts(&deleted_transcript, deleted).unwrap().last_activity_at;
    write(&store.join(format!("deleted_{deleted}")), &format!("{when}"));

    pass(&[profile.clone()], SystemTime::now());
    assert!(
        !exists(&record_named(&store, deleted)),
        "a marker naming a session is read by name, and that session is not filed again"
    );
    assert!(
        exists(&record_named(&store, neighbour)),
        "and it takes nothing else that happened to go quiet beside it"
    );
    assert!(
        !load_session_record_state().records.contains_key(deleted),
        "a session withdrawn by a marker stops being remembered at the folder its record has gone from"
    );
}

#[test]
fn a_walk_of_the_chat_stores_does_not_take_the_plugin_folder_for_an_account() {
    let _world = World::new();
    // A plugin folder sits beside the account directories without being one. A
    // walk that took it for an account reported its organization folders as
    // chat stores of their own.
    let profile = make_profile("Claude-Plugin-Walk", Some("6a6a6a6a"), Some("6b6b6b6b"), &["p"]);
    std::fs::create_dir_all(profile.join("claude-code-sessions/skills-plugin/6b6b6b6b")).unwrap();
    let prefix = format!("{}/", resolved_text(&profile));
    let read = session_store_contents().stores.into_iter().filter(|s| s.starts_with(&prefix)).count();
    assert_eq!(read, 1, "a walk of the chat stores counts the account directories and not the plugin folder");
}

