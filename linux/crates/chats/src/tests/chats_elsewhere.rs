//! Chats an account switch left behind in another profile: the offer, and the
//! copy that answers it.

use super::*;
use crate::fsx::{exists, file_name, stash_path};
use crate::{adopt_chats, chats_elsewhere};
use graft_core::config::session_store_profiles;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

fn write_chat(id: &str, title: &str, dir: &Path, age: u64, last_active: Option<SystemTime>) {
    let mut record = Map::new();
    record.insert("cliSessionId".into(), json!(id));
    record.insert("title".into(), json!(title));
    if let Some(when) = last_active {
        let ms = when.duration_since(UNIX_EPOCH).unwrap().as_secs_f64() * 1000.0;
        record.insert("lastActivityAt".into(), json!(ms));
    }
    let file = dir.join(format!("local_{id}.json"));
    write(&file, &Value::Object(record).to_string());
    set_modified(&file, SystemTime::now() - Duration::from_secs(age));
}

fn work_folder(profile: &Path, store: &str) -> PathBuf {
    profile.join(store).join("work").join("ORG-W")
}

fn sessions(profile: &Path) -> PathBuf {
    work_folder(profile, "claude-code-sessions")
}

#[test]
fn chats_left_in_another_profile_are_offered_and_copied_without_taking_anything_away() {
    let _world = World::new();

    // The shape switching accounts inside one Claude leaves behind: a single
    // profile holding both histories, keyed by account, with the one it is
    // not signed into sitting there untouched.
    let main = make_profile("Claude", Some("personal"), Some("ORG-P"), &["p1"]);
    write_chat("w1", "First look at the codebase", &sessions(&main), 300, None);
    write_chat("w2", "Invoice parser", &sessions(&main), 120, None);
    write_chat("w3", "Rewriting the deploy script", &sessions(&main), 0, None);
    write_chat("w4", "A local agent chat", &work_folder(&main, "local-agent-mode-sessions"), 60, None);
    write(&sessions(&main).join("deleted_gone"), "1700000000");

    let second = make_profile("Claude-Work", Some("work"), None, &[]);
    let found = chats_elsewhere(&second, &session_store_profiles());

    assert_eq!(
        found.as_ref().map(|f| file_name(&f.profile)).as_deref(),
        Some("Claude"),
        "a profile signed in with nothing of its own finds the chats another profile holds for its account"
    );
    assert_eq!(
        found.as_ref().map(|f| f.account.as_str()),
        Some("work"),
        "and it is asked about the account this profile is signed into"
    );
    assert_eq!(found.as_ref().map(|f| f.count), Some(4), "both chat stores are counted, not only the sessions one");
    assert_eq!(
        found.as_ref().map(|f| f.merging),
        Some(false),
        "a profile with nothing of its own is told this is a history arriving, not two being merged"
    );
    assert_eq!(
        found.as_ref().and_then(|f| f.chats.first()).map(|c| c.title.as_str()),
        Some("Rewriting the deploy script"),
        "the newest chat is named first, since a count alone does not say which history this is"
    );
    assert!(
        found.as_ref().is_some_and(|f| f.chats.iter().any(|c| c.title == "A local agent chat")),
        "and the titles reach across both stores"
    );
    // The Swift field is optional; here it is always set, so the faithful
    // question is whether it holds a real moment rather than nothing at all.
    assert!(
        found.as_ref().and_then(|f| f.chats.first()).is_some_and(|c| c.last_active > UNIX_EPOCH),
        "each one carries when it was last active, which is the other half of recognising it"
    );

    assert_eq!(
        chats_elsewhere(&main, &session_store_profiles()),
        None,
        "a profile holding every chat the others have is offered nothing"
    );

    let signed_out = make_profile("Claude-New", None, None, &[]);
    assert_eq!(
        chats_elsewhere(&signed_out, &session_store_profiles()),
        None,
        "a profile that has never been signed in is offered nothing"
    );

    write(&signed_out.join("config.json"), "{ truncated");
    assert_eq!(
        chats_elsewhere(&signed_out, &session_store_profiles()),
        None,
        "a config caught mid-rename is not read as an account with chats waiting for it"
    );

    let busy = make_profile("Claude-Busy", Some("work"), Some("ORG-W"), &["own"]);
    assert_eq!(
        chats_elsewhere(&busy, &session_store_profiles()).map(|f| f.count),
        Some(4),
        "a profile with a history of its own is offered the chats it has not got as well"
    );
    assert_eq!(
        chats_elsewhere(&busy, &session_store_profiles()).map(|f| f.merging),
        Some(true),
        "and is told the two sets are merged rather than one standing in for the other"
    );
    write_chat("w1", "First look at the codebase", &sessions(&busy), 0, None);
    assert_eq!(
        chats_elsewhere(&busy, &session_store_profiles()).map(|f| f.count),
        Some(3),
        "the count is only what is missing, so it is the number that will actually arrive"
    );
    std::fs::remove_dir_all(&busy).unwrap();

    // Ordered by the moment inside the record, not by the file it sits in.
    let stamped = make_profile("Claude-Stamped", Some("stamps"), None, &[]);
    let stamped_source = main.join("claude-code-sessions").join("stamps").join("ORG-S");
    write_chat("late", "Touched last, active first", &stamped_source, 9000, Some(SystemTime::now()));
    write_chat(
        "early",
        "Touched first, active last",
        &stamped_source,
        0,
        Some(SystemTime::now() - Duration::from_secs(9000)),
    );
    assert_eq!(
        chats_elsewhere(&stamped, &session_store_profiles())
            .and_then(|f| f.chats.first().map(|c| c.title.clone()))
            .as_deref(),
        Some("Touched last, active first"),
        "a record says when it was last active, and that outranks when its file was written"
    );
    std::fs::remove_dir_all(&stamped).unwrap();

    let grafted = make_profile("Claude-Grafted", Some("work"), None, &[]);
    link_the_old_way(&grafted.join("claude-code-sessions"), &sessions(&main));
    assert_eq!(
        chats_elsewhere(&grafted, &session_store_profiles()),
        None,
        "a grafted profile's emptiness is this app's own doing, so it is offered nothing"
    );
    std::fs::remove_dir_all(&grafted).unwrap();

    // The copy itself.
    let copied = adopt_chats(&main, &second, "work");
    assert_eq!(copied.copied, 4, "the count answers in chats, so it is the same number the offer put on screen");
    assert_eq!(
        chats_visible(&second),
        vec!["local_w1.json", "local_w2.json", "local_w3.json"],
        "the chats arrive in the profile that was signed into that account"
    );
    assert!(
        exists(&work_folder(&second, "local-agent-mode-sessions").join("local_w4.json")),
        "and the other store is filled as well as the sessions one"
    );
    assert!(
        exists(&sessions(&second).join("deleted_gone")),
        "a chat deleted over there stays deleted here, rather than coming back as new"
    );
    assert!(
        chats_visible(&main).contains(&"local_w3.json".to_string()),
        "the originals stay where they are, so the other profile is unchanged by this"
    );
    assert!(
        exists(&sessions(&main).join("local_w3.json")),
        "and a profile this app did not make has nothing taken out of it"
    );

    assert_eq!(
        chats_elsewhere(&second, &session_store_profiles()),
        None,
        "once they are here the offer stops being made"
    );

    write(&sessions(&second).join("local_w3.json"), &json!({ "cliSessionId": "w3", "isArchived": true }).to_string());
    assert_eq!(
        adopt_chats(&main, &second, "work").copied,
        0,
        "a second run copies nothing, having nothing new to bring"
    );
    assert_eq!(
        json_of(&sessions(&second).join("local_w3.json"))["isArchived"].as_bool(),
        Some(true),
        "so a chat archived here since is not written back over with the copy it came from"
    );

    assert_eq!(
        adopt_chats(&main, &main, "work").copied,
        0,
        "a profile is never asked to copy its own chats onto themselves"
    );
    std::fs::remove_dir_all(&second).unwrap();
    std::fs::remove_dir_all(&signed_out).unwrap();

    // Nothing moves while any Claude is up, this profile's or anybody's.
    let waiting = make_profile("Claude-Waiting", Some("work"), None, &[]);
    running::set_processes_override(Some(vec![running::stand_in(1, &paths::main_profile())]));
    let refused = adopt_chats(&main, &waiting, "work");
    assert_eq!(
        refused.running.iter().map(|p| file_name(p)).collect::<Vec<_>>(),
        vec!["Claude"],
        "a copy asked for while a Claude is open says which one to quit"
    );
    assert!(
        refused.copied == 0 && chats_visible(&waiting).is_empty(),
        "and copies nothing, rather than landing records under an instance that may write over them"
    );
    running::set_processes_override(Some(Vec::new()));
    assert_eq!(
        adopt_chats(&main, &waiting, "work").copied,
        4,
        "the same press on a quiet machine brings them across"
    );
    std::fs::remove_dir_all(&waiting).unwrap();

    // A folder that is not the profile's to fill.
    let stashed = make_profile("Claude-Stashed", Some("work"), None, &[]);
    std::fs::create_dir_all(stash_path(&stashed.join("claude-code-sessions").join("work"))).unwrap();
    let landed = adopt_chats(&main, &stashed, "work").copied;
    assert!(
        chats_visible(&stashed).is_empty(),
        "a folder this app has stashed away is left alone rather than filled behind the graft holding it"
    );
    assert!(
        landed == 1 && exists(&work_folder(&stashed, "local-agent-mode-sessions").join("local_w4.json")),
        "and the refusal is per store, so the one that is still the profile's own is filled"
    );
    std::fs::remove_dir_all(&stashed).unwrap();

    // The shortened spelling a profile in local mode uses.
    let short = make_profile("Claude-Short", Some("workaccount-1234"), None, &[]);
    let short_work = short.join("claude-code-sessions").join("workacco");
    std::fs::create_dir_all(short_work.join("ORG-W")).unwrap();
    write_chat(
        "s1",
        "Long-named account",
        &main.join("claude-code-sessions").join("workaccount-1234").join("ORG-W"),
        0,
        None,
    );
    assert_eq!(
        adopt_chats(&main, &short, "workaccount-1234").copied,
        1,
        "a store spelling the account short is filled where it really keeps it"
    );
    assert!(
        exists(&short_work.join("ORG-W").join("local_s1.json")),
        "rather than beside it under the name the account was asked for"
    );
}
