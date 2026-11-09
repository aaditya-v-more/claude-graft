//! Folders this app emptied or put away whole, and sources with nothing under
//! the account to share.

use super::*;
use crate::fsx::{exists, is_symlink, resolved_text, stash_path};
use crate::{
    counterpart_directory, file_missing_session_records, forget_stale_pairs, graft, is_stashed_away,
    load_mirror_state, load_session_record_state, may_file_records, mirror_chat_folders, newest_child, pair_key,
    save_mirror_state, save_session_record_state, MirrorState, SessionRecordState,
};
use std::collections::{BTreeMap, BTreeSet};

// A folder this app emptied

#[test]
fn a_side_emptied_all_at_once_is_filled_again_rather_than_read_as_deletions() {
    let world = World::new();
    // The loss this section exists for. A mirror's first pass moves the
    // profile's own chats to the hidden sibling and leaves a real, readable,
    // empty folder in their place, and nothing downstream could tell that apart
    // from a person clearing their sidebar. Two things acted on it at once: the
    // pass that keeps two folders in step deleted every record from the profile
    // the chats had been borrowed from, and the sweep withdrew the lot for good.
    let one = world.support().join("Emptied-One");
    let other = world.support().join("Emptied-Other");
    for dir in [&one, &other] {
        std::fs::create_dir_all(dir).unwrap();
    }
    for name in ["local_a.json", "local_b.json", "local_c.json"] {
        write(&other.join(name), &format!("chat {name}"));
    }

    mirror_chat_folders(&one, &other);
    assert!(exists(&one.join("local_b.json")), "a first pass gives the empty side the records the other holds");
    assert_eq!(
        load_mirror_state().pairs.get(&pair_key(&one, &other)).map(|p| p.len()),
        Some(3),
        "and writes down what the two of them agreed on"
    );

    // The stash, as `open_for_mirror` performs it: everything out at once, a
    // real empty folder left standing in its place.
    for name in ["local_a.json", "local_b.json", "local_c.json"] {
        std::fs::remove_file(one.join(name)).unwrap();
    }
    mirror_chat_folders(&one, &other);
    assert!(
        exists(&other.join("local_a.json")) && exists(&other.join("local_b.json")) && exists(&other.join("local_c.json")),
        "a side emptied all at once is a folder that was moved, not a sidebar someone cleared"
    );
    assert!(
        exists(&one.join("local_a.json")),
        "so the emptied side is filled again rather than the full one emptied"
    );

    // And the guard must not swallow the deletions the pass was written for.
    // Ignored if it fails, since a broken guard leaves nothing here to remove
    // and the checks below are how that should be reported.
    let _ = std::fs::remove_file(one.join("local_b.json"));
    mirror_chat_folders(&one, &other);
    assert!(!exists(&other.join("local_b.json")), "one chat deleted out of three still carries across");
    assert!(
        exists(&other.join("local_a.json")) && exists(&other.join("local_c.json")),
        "and takes none of the others with it"
    );
}

#[test]
fn a_pair_naming_where_a_profiles_chats_used_to_live_is_dropped() {
    let world = World::new();
    // Where a profile's chats live is `<account>/<org>`, and signing in again
    // can move both. The pair an earlier pass wrote down then names a folder
    // nobody writes to, which is a folder that looks emptied, which is the
    // deletion above arriving by another road.
    let store = world.support().join("Claude-Moved/claude-code-sessions");
    let was = store.join("OLD-ACCOUNT/OLD-ORG");
    let now = store.join("NEW-ACCOUNT/NEW-ORG");
    let theirs = world.support().join("Claude-Moved-Src/claude-code-sessions/S/S-ORG");
    for dir in [&was, &now, &theirs] {
        std::fs::create_dir_all(dir).unwrap();
    }
    let mut state = MirrorState::default();
    state.pairs.insert(pair_key(&was, &theirs), BTreeMap::from([("local_1.json".to_string(), "beef".to_string())]));
    state.pairs.insert(pair_key(&now, &theirs), BTreeMap::from([("local_2.json".to_string(), "cafe".to_string())]));
    save_mirror_state(&state);

    forget_stale_pairs(&store, &[(now.clone(), theirs.clone())]);
    let left = load_mirror_state().pairs;
    assert!(
        left.contains_key(&pair_key(&now, &theirs)),
        "the pair for the folder the profile actually writes to is left alone"
    );
    assert!(
        !left.contains_key(&pair_key(&was, &theirs)),
        "and the one naming where its chats used to live is dropped before it can be squared up"
    );
}

#[test]
fn a_store_with_a_stash_beside_it_has_nothing_withdrawn() {
    let world = World::new();
    // The other half of the same emptying, and the half that made it
    // permanent. The store is real, readable, and holds nothing, so a sweep
    // read every record remembered there as a chat deleted by hand.
    // `may_file_records` has asked about the stash on the way in all along;
    // withdrawing is forever, so it has to be asked on the way out too.
    let profile = world.support().join("Claude-Emptied");
    let store = profile.join("claude-code-sessions/EEEE/ORG-E");
    std::fs::create_dir_all(&store).unwrap();
    let sibling = store.parent().unwrap().join(".ORG-E.graft-own");
    std::fs::create_dir_all(&sibling).unwrap();
    let remembered = resolved_text(&store);

    // A pass has already found it missing once, so the next one withdraws it.
    let twice_missing = || SessionRecordState {
        records: BTreeMap::from([("e-1".to_string(), remembered.clone())]),
        vanished: vec!["e-1".to_string()],
        ..SessionRecordState::default()
    };
    save_session_record_state(&twice_missing());
    file_missing_session_records(std::slice::from_ref(&profile));
    assert!(
        !load_session_record_state().withdrawn.contains(&"e-1".to_string()),
        "a store with a stash beside it is one this app emptied, so nothing in it is withdrawn"
    );
    assert_eq!(
        load_session_record_state().records.get("e-1"),
        Some(&remembered),
        "and where the record was is still remembered, for when the stash comes back"
    );

    // With the stash gone the folder is the profile's own again, and an empty
    // one really is a sidebar someone cleared.
    std::fs::remove_dir_all(&sibling).unwrap();
    save_session_record_state(&twice_missing());
    file_missing_session_records(std::slice::from_ref(&profile));
    assert!(
        load_session_record_state().withdrawn.contains(&"e-1".to_string()),
        "with no stash beside it, a record twice missing from a store that was read is one somebody deleted"
    );
}

#[test]
fn a_record_standing_in_a_mirrored_folder_is_filed_and_kept_current() {
    let _world = World::new();
    // Every mirrored folder has a stash beside it, and the sweep may file into
    // one: it is there and it was read. The stash is about records that cannot
    // be seen, though, and a record standing in the folder can.
    let account = "3e3e3e3e-3e3e-4e3e-8e3e-3e3e3e3e3e3e";
    let org = "3f3f3f3f-3f3f-4f3f-8f3f-3f3f3f3f3f3f";
    let session = "3a3a3a3a-3a3a-4a3a-8a3a-3a3a3a3a3a3a";
    let profile = make_profile("Claude-Mirrored-Own", Some(account), Some(org), &[]);
    let folder = profile.join(format!("claude-code-sessions/{account}/{org}"));
    std::fs::create_dir_all(stash_path(&folder)).unwrap();
    let file = folder.join(format!("local_{session}.json"));

    Transcript::new(session).owner(account, org).title("Filed beside a stash").write();
    file_missing_session_records(std::slice::from_ref(&profile));
    assert!(exists(&file), "a session is filed into a mirrored folder, which is there and was read");

    Transcript::new(session)
        .owner(account, org)
        .title("Renamed since")
        .times("2026-08-29T17:00:00.000Z", "2026-08-29T17:05:00.000Z")
        .write();
    file_missing_session_records(std::slice::from_ref(&profile));
    assert_eq!(
        json_of(&file)["title"].as_str(),
        Some("Renamed since"),
        "and the record standing there is kept current, since a stash beside it does not hide it"
    );
}

// A store put away whole

#[test]
fn a_store_put_away_whole_is_a_stash_two_levels_up() {
    let world = World::new();
    // There are two shapes of stash and only one of them leaves a sibling. A
    // cross-account graft puts the organization folder away and the sibling
    // sits beside it; two profiles on one account put the whole store away,
    // and then there is no folder left for a sibling to stand next to.
    // Everything that asked only about the sibling waved the second shape
    // through.
    let profile = world.support().join("Claude-Whole-Store");
    let org = profile.join("claude-code-sessions/WWWW/ORG-W");
    std::fs::create_dir_all(&org).unwrap();

    assert!(!is_stashed_away(&org), "a folder with nothing put away above it is not a folder this app stashed");

    let sibling = org.parent().unwrap().join(".ORG-W.graft-own");
    std::fs::create_dir_all(&sibling).unwrap();
    assert!(
        is_stashed_away(&org),
        "a stash beside the organization folder is the shape a cross-account graft leaves"
    );
    std::fs::remove_dir_all(&sibling).unwrap();

    // Exactly what `open_for_mirror` does to a store on a same-account first
    // pass.
    std::fs::rename(profile.join("claude-code-sessions"), profile.join(".claude-code-sessions.graft-own")).unwrap();
    std::fs::create_dir_all(profile.join("claude-code-sessions")).unwrap();
    assert!(
        is_stashed_away(&org),
        "and a store put away whole is the shape a same-account graft leaves, two levels up"
    );
    assert!(
        !may_file_records(&org, &BTreeSet::new()),
        "so nothing is filed into the folder standing in its place"
    );
}

#[test]
fn a_sweep_does_not_rebuild_a_store_this_app_put_away() {
    let _world = World::new();
    // The same thing again, as a whole pass rather than one rule: the store
    // put away, a transcript on disk with nobody's record naming it, and a
    // sweep that used to answer that by rebuilding the folder and filing the
    // lot into it: every chat a second time, none of them archived, the real
    // store orphaned in the stash beside it.
    let account = "1e1e1e1e-1e1e-4e1e-8e1e-1e1e1e1e1e1e";
    let org = "1f1f1f1f-1f1f-4f1f-8f1f-1f1f1f1f1f1f";
    let session = "1a1a1a1a-1a1a-4a1a-8a1a-1a1a1a1a1a1a";
    let profile = make_profile("Claude-Refiled", Some(account), Some(org), &["mine"]);
    Transcript::new(session).owner(account, org).title("Borrowed").write();

    let store = profile.join("claude-code-sessions");
    std::fs::rename(&store, profile.join(".claude-code-sessions.graft-own")).unwrap();
    std::fs::create_dir_all(&store).unwrap();

    file_missing_session_records(std::slice::from_ref(&profile));
    assert!(
        !exists(&store.join(account).join(org)),
        "a sweep does not build an organization folder back up inside a store this app emptied"
    );
    assert!(
        exists(&profile.join(".claude-code-sessions.graft-own").join(account).join(org).join("local_mine.json")),
        "and the profile's real history stays where the graft put it"
    );
}

// A source with nothing to share

#[test]
fn a_source_with_nothing_under_the_account_leaves_the_borrowers_chats_alone() {
    let _world = World::new();
    // The store went to the stash before the source was consulted, so a
    // source that turned out to hold nothing under this account left the
    // borrowing profile's whole history hidden behind an empty folder, and
    // wrote down no pair, so every launch after did it again.
    let account = "2a2a2a2a-2a2a-4a2a-8a2a-2a2a2a2a2a2a";
    let source = make_profile("Claude-Empty-Source", Some(account), Some("ORG-S"), &["theirs"]);
    let borrower = make_profile("Claude-Empty-Borrower", Some(account), Some("ORG-B"), &["own-1", "own-2"]);

    // One store the source can share and one it cannot: the account directory
    // under `local-agent-mode-sessions` is spelled some other way entirely.
    let agent = source.join("local-agent-mode-sessions");
    std::fs::remove_dir_all(agent.join(account)).unwrap();
    std::fs::create_dir_all(agent.join("somebody-else/ORG-X")).unwrap();

    graft(&source, &borrower);
    graft(&source, &borrower);

    let mine = borrower.join("local-agent-mode-sessions").join(account).join("ORG-B");
    assert!(
        exists(&mine.join("local_own-1.json")) && exists(&mine.join("local_own-2.json")),
        "a source holding nothing under this account leaves the profile's own chats where they are"
    );
    assert!(
        !exists(&borrower.join(".local-agent-mode-sessions.graft-own")),
        "and nothing of the profile's goes to the stash on the strength of a share that cannot happen"
    );
    assert!(
        exists(&borrower.join("claude-code-sessions").join(account).join("ORG-S").join("local_theirs.json")),
        "while the store the source can share is shared as usual"
    );
}

#[test]
fn a_store_spelling_an_account_short_is_still_that_accounts_store() {
    let world = World::new();
    // `<accountUuid>/<orgUuid>` is the shape, except where it is not: a
    // profile in local mode was measured naming both halves by their first
    // eight characters, in one store and not the other, for the same account.
    let account = "ed417e0f-5edd-45dd-9a81-a9925b2c57dc";
    let source = world.support().join("Claude-Short-Source");
    let borrower = world.support().join("Claude-Short-Borrower");
    for (profile, chat) in [(&source, "theirs"), (&borrower, "own")] {
        let dir = profile.join("local-agent-mode-sessions/ed417e0f/00000000");
        write(&dir.join(format!("local_{chat}.json")), "{}");
        write(&profile.join("config.json"), &json!({ "lastKnownAccountUuid": account }).to_string());
    }

    assert_eq!(
        counterpart_directory(&source.join("local-agent-mode-sessions"), account).as_deref(),
        Some("ed417e0f"),
        "a store spelling an account short is still that account's store"
    );
    assert_eq!(
        counterpart_directory(&source.join("local-agent-mode-sessions"), "ffffffff-5edd-45dd-9a81-a9925b2c57dc"),
        None,
        "and an account no directory there begins with is not in it at all"
    );

    graft(&source, &borrower);
    let mine = borrower.join("local-agent-mode-sessions/ed417e0f/00000000");
    assert!(exists(&mine.join("local_own.json")), "a graft between two such profiles keeps what the borrower already had");
    assert!(
        exists(&mine.join("local_theirs.json")),
        "and merges the source's chats into the folder the borrower actually reads"
    );
}

#[test]
fn the_newest_child_of_an_account_directory_is_a_directory() {
    let world = World::new();
    // Claude writes `<org>.profile-origin.json` beside the organization
    // folders as an organization is created, so for a moment it is the newest
    // thing in the account directory, and it was handed back as one.
    let account = world.support().join("Claude-Origin/claude-code-sessions/OOOO");
    std::fs::create_dir_all(account.join("ORG-O")).unwrap();
    write(&account.join("ORG-O.profile-origin.json"), "{\"mode\":\"local\"}");
    assert_eq!(
        newest_child(&account).as_deref(),
        Some("ORG-O"),
        "the newest child of an account directory is a directory, whatever else was written later"
    );
}

#[test]
fn two_profiles_on_one_account_share_the_plugin_folder() {
    let _world = World::new();
    // It sat behind the same-account branch's early exit, so two profiles on
    // one account never shared it while two on different accounts did.
    let account = "3a3a3a3a-3a3a-4a3a-8a3a-3a3a3a3a3a3a";
    let source = make_profile("Claude-Skills-Source", Some(account), Some("ORG-K"), &["k"]);
    let borrower = make_profile("Claude-Skills-Borrower", Some(account), Some("ORG-K"), &[]);
    std::fs::create_dir_all(source.join("claude-code-sessions/skills-plugin/ORG-K")).unwrap();

    graft(&source, &borrower);
    assert!(
        is_symlink(&borrower.join("claude-code-sessions/skills-plugin")),
        "two profiles on one account share the plugin folder, as two on different accounts always did"
    );
    assert!(
        exists(&borrower.join("claude-code-sessions").join(account).join("ORG-K").join("local_k.json")),
        "and the link is made after the store is opened, so it does not go to the stash with everything else"
    );
}
