//! Mirrored chat folders, and going back to a profile's own chats.

use super::*;
use crate::fsx::{exists, is_directory, is_symlink, stash_path};
use crate::{
    apply, digest, forget_mirrors, load_mirror_state, mirror_chat_folders, mirror_decision, mirror_known_pairs,
    mirror_state_file, save_mirror_state, MirrorAction, MirrorState, CHAT_STORES, STASH_SUFFIX,
};
use graft_core::GraftConfig;
use std::collections::BTreeMap;
use std::os::unix::fs::PermissionsExt;
use std::time::{Duration, SystemTime};

fn grafted(profile: &Path, source: Option<&Path>) -> GraftConfig {
    GraftConfig { profile_dir: paths::text(profile), source_dir: source.map(paths::text) }
}

fn org_of(profile: &Path, account: &str, org: &str) -> PathBuf {
    profile.join("claude-code-sessions").join(account).join(org)
}

fn pairs_are_empty() -> bool {
    load_mirror_state().pairs.is_empty()
}

fn entry_count(folder: &Path) -> usize {
    crate::fsx::names_or_empty(folder).len()
}

/// The records in one organization folder, by name.
fn records_in(folder: &Path) -> Vec<String> {
    let mut found: Vec<String> =
        crate::fsx::names_or_empty(folder).into_iter().filter(|n| n.starts_with("local_")).collect();
    found.sort();
    found
}

/// Make one organization the one a profile is working in, the way a new chat
/// there does: the newest folder under the account, in both stores.
fn work_in(org: &str, profile: &Path, account: &str) {
    let now = SystemTime::now();
    for store in CHAT_STORES {
        let account_dir = profile.join(store).join(account);
        for name in crate::fsx::names_or_empty(&account_dir) {
            if name.starts_with('.') {
                continue;
            }
            let when = if name == org { now } else { now - Duration::from_secs(600) };
            if let Ok(dir) = std::fs::File::open(account_dir.join(&name)) {
                let _ = dir.set_modified(when);
            }
        }
    }
}

/// Graft's way of deleting a profile: the folder first, then every pair with a
/// half inside it.
fn delete_profile(profile: &Path) {
    std::fs::remove_dir_all(profile).unwrap();
    forget_mirrors(profile);
}

fn names(list: &[&str]) -> Vec<String> {
    list.iter().map(|s| s.to_string()).collect()
}

// Mirrored chat folders

#[test]
fn a_record_on_one_side_alone_is_new_or_deleted_and_only_the_baseline_says_which() {
    let _world = World::new();
    // A record on one side and not the other is either one just written or one
    // just deleted, and on disk those are the same thing. What tells them
    // apart is whether the last pass saw it on both, so the baseline is the
    // whole rule, and getting it backwards either loses a new chat or brings a
    // deleted one back.
    assert_eq!(digest(b"a chat record"), digest(b"a chat record"), "the same bytes digest the same way twice");
    assert_ne!(digest(b"a chat record"), digest(b"a chat recorD"), "and one byte apart digests differently");

    use MirrorAction::*;
    assert_eq!(
        mirror_decision(Some("x"), Some("x"), Some("x")),
        Nothing,
        "two folders holding the same record have nothing to do"
    );
    assert_eq!(mirror_decision(None, None, Some("x")), Nothing, "and a record gone from both is gone");

    assert_eq!(
        mirror_decision(Some("x"), None, None),
        CopyToOther,
        "a record the last pass never saw is a new one, and it crosses over"
    );
    assert_eq!(mirror_decision(None, Some("x"), None), CopyToOne, "whichever side wrote it");

    assert_eq!(
        mirror_decision(Some("x"), None, Some("x")),
        RemoveFromOne,
        "a record both sides held, now gone from one, was deleted rather than never written"
    );
    assert_eq!(
        mirror_decision(None, Some("x"), Some("x")),
        RemoveFromOther,
        "and the deletion carries across rather than the record coming back"
    );

    assert_eq!(
        mirror_decision(Some("y"), Some("x"), Some("x")),
        CopyToOther,
        "the side that moved away from what both held is the side that changed"
    );
    assert_eq!(mirror_decision(Some("x"), Some("y"), Some("x")), CopyToOne, "read either way round");

    assert_eq!(
        mirror_decision(Some("y"), Some("z"), Some("x")),
        Conflict,
        "two sides that both moved is not a question this can answer"
    );
    assert_eq!(
        mirror_decision(Some("y"), Some("z"), None),
        Conflict,
        "nor is one with nothing remembered behind it"
    );
    assert_eq!(
        mirror_decision(Some("y"), None, Some("x")),
        Conflict,
        "and a record edited on one side while the other deleted it is the same question"
    );

    // The state file loads from a version that never wrote it, claiming
    // nothing, the way the session record state does.
    let _ = std::fs::remove_file(mirror_state_file());
    assert_eq!(load_mirror_state(), MirrorState::default(), "with no state file yet, a pass starts remembering nothing");
    let mut state = MirrorState::default();
    state.pairs.insert("a\u{0}b".into(), BTreeMap::from([("local_1.json".to_string(), "beef".to_string())]));
    save_mirror_state(&state);
    assert_eq!(load_mirror_state(), state, "and what one pass wrote down is what the next one reads back");
    write(&mirror_state_file(), "{}");
    assert_eq!(
        load_mirror_state(),
        MirrorState::default(),
        "a state file from before any of this existed loads as a pass with nothing remembered"
    );
}

#[test]
fn a_linked_profile_converts_to_a_real_folder_and_changes_cross_both_ways() {
    let _world = World::new();
    // A bundle written by a version that still had the choice carries a
    // `mirrorChats` key. Decoding ignores what it does not know, so nothing
    // has to be rewritten for one to migrate; it just stops being asked.
    let old: GraftConfig =
        serde_json::from_str("{\"profileDir\":\"/tmp/p\",\"sourceDir\":\"/tmp/s\",\"mirrorChats\":false}").unwrap();
    assert!(
        old.profile_dir == "/tmp/p" && old.source_dir.as_deref() == Some("/tmp/s"),
        "a shortcut written while linking was still an option reads back without complaint"
    );

    let source = make_profile("Claude-Src", Some("SRCACC"), Some("SRCORG"), &["one", "two"]);
    let borrower = make_profile("Claude-Mirror", Some("OWNACC"), Some("OWNORG"), &[]);
    let src_org = org_of(&source, "SRCACC", "SRCORG");
    let own_org = org_of(&borrower, "OWNACC", "OWNORG");

    // What a released version left on disk: the profile's own chats in the
    // stash and a link where they used to be. Built by hand because nothing
    // in this app makes one any more, which is the point of the test.
    let own_stash = own_org.parent().unwrap().join(".OWNORG.graft-own");
    write(&own_stash.join("local_mine.json"), "{}");
    std::fs::remove_dir_all(&own_org).unwrap();
    std::os::unix::fs::symlink(&src_org, &own_org).unwrap();
    assert!(is_symlink(&own_org), "the profile starts out with a link where its chats go");

    apply(&grafted(&borrower, Some(&source)));
    assert!(
        !is_symlink(&own_org) && is_directory(&own_org),
        "converting takes the link away and leaves a folder the profile can write into"
    );
    assert!(
        exists(&own_org.join("local_one.json")) && exists(&own_org.join("local_two.json")),
        "and the first pass fills it with the chats it was borrowing"
    );
    assert!(
        exists(&src_org.join("local_one.json")),
        "the source keeps its own, since a copy was made rather than a move"
    );

    // The whole point: the borrowing profile writes, and the change crosses.
    write(&own_org.join("local_one.json"), "{\"isArchived\":true}");
    mirror_chat_folders(&own_org, &src_org);
    assert_eq!(
        read(&src_org.join("local_one.json")).as_deref(),
        Some("{\"isArchived\":true}"),
        "a record the borrowing profile rewrote reaches the source"
    );

    // And the other way, which is how it sees anything new.
    write(&src_org.join("local_three.json"), "{\"title\":\"three\"}");
    mirror_chat_folders(&own_org, &src_org);
    assert!(
        exists(&own_org.join("local_three.json")),
        "a chat started on the source turns up in the profile borrowing from it"
    );

    // A delete is not an absence to be filled back in.
    std::fs::remove_file(own_org.join("local_two.json")).unwrap();
    mirror_chat_folders(&own_org, &src_org);
    assert!(
        !exists(&src_org.join("local_two.json")),
        "a chat deleted on one side is deleted on the other, not copied back"
    );
    mirror_chat_folders(&own_org, &src_org);
    assert!(!exists(&own_org.join("local_two.json")), "and it stays deleted through the pass after");

    // Both sides moved. The record that says it moved later is the one kept.
    write(&own_org.join("local_one.json"), "{\"lastActivityAt\":100}");
    write(&src_org.join("local_one.json"), "{\"lastActivityAt\":200}");
    mirror_chat_folders(&own_org, &src_org);
    assert_eq!(
        read(&own_org.join("local_one.json")).as_deref(),
        Some("{\"lastActivityAt\":200}"),
        "with both sides rewritten, the record that moved more recently wins"
    );

    // Everything else in the folder is the profile's own business.
    write(&own_org.join("scheduled-tasks.json"), "{}");
    mirror_chat_folders(&own_org, &src_org);
    assert!(
        !exists(&src_org.join("scheduled-tasks.json")),
        "a file that is not a record or a marker is left where its profile put it"
    );

    // A folder that cannot be listed is not an empty one, and here reading it
    // as empty would delete every record on the other side.
    let unreadable = borrower.join("not-a-folder");
    write(&unreadable, "{}");
    let before = entry_count(&src_org);
    assert_eq!(mirror_chat_folders(&unreadable, &src_org), 0, "a pass that could not read one side does nothing at all");
    assert_eq!(entry_count(&src_org), before, "so the side it could read is left exactly as it was");
}

#[test]
fn opening_the_source_puts_every_pair_this_app_knows_about_back_in_step() {
    let _world = World::new();
    // Archive a chat in the profile that borrowed it, then open the profile it
    // was borrowed from. The second one is not running `apply`, since it has
    // no source of its own, so unless a pass goes looking for every pair this
    // app knows about, the change sits there and the sidebar being built never
    // sees it. That is the whole workflow the mirror exists for.
    let host = make_profile("Claude-Host", Some("HOSTACC"), Some("HOSTORG"), &["shared"]);
    let guest = make_profile("Claude-Guest", Some("GUESTACC"), Some("GUESTORG"), &[]);
    let host_org = org_of(&host, "HOSTACC", "HOSTORG");
    let guest_org = org_of(&guest, "GUESTACC", "GUESTORG");

    apply(&grafted(&guest, Some(&host)));
    assert!(exists(&guest_org.join("local_shared.json")), "the borrowing profile starts with a copy of the chat");

    // The borrowing profile archives it, the way a Claude with a real folder
    // now can.
    write(&guest_org.join("local_shared.json"), "{\"isArchived\":true}");

    // And the source is opened. Nothing hands it the pair; it works it out
    // from what earlier passes wrote down.
    assert!(mirror_known_pairs() > 0, "opening the source puts every pair this app knows about back in step");
    assert_eq!(
        read(&host_org.join("local_shared.json")).as_deref(),
        Some("{\"isArchived\":true}"),
        "so the archive made in the borrowing profile is there when the source builds its sidebar"
    );
    assert_eq!(mirror_known_pairs(), 0, "and a pass with nothing left to carry moves nothing");
}

#[test]
fn everything_an_earlier_version_left_behind_is_converted_and_a_deleted_profile_stays_deleted() {
    let _world = World::new();
    let src = make_profile("Claude-Legacy-Src", Some("LSRC"), Some("LORG"), &["kept"]);
    let old = make_profile("Claude-Legacy", Some("LOWN"), Some("LOWNORG"), &["mine"]);
    let src_org = org_of(&src, "LSRC", "LORG");
    let own_org = org_of(&old, "LOWN", "LOWNORG");
    let stash = own_org.parent().unwrap().join(format!(".LOWNORG{STASH_SUFFIX}"));

    // Grafted the old way, which stashes what the profile had.
    link_the_old_way(&own_org, &src_org);
    assert!(
        is_symlink(&own_org) && exists(&stash),
        "a released version leaves a link with the profile's own chats stashed beside it"
    );

    apply(&grafted(&old, Some(&src)));
    assert!(
        !is_symlink(&own_org) && exists(&stash),
        "converting takes the link and leaves the stash exactly where it was"
    );
    assert!(exists(&own_org.join("local_kept.json")), "and the profile has its own copy of what it was borrowing");

    // The upgrade's whole promise. A released version put the profile's own
    // chats in the stash and left a link standing in for them, so the sidebar
    // showed the source's history instead of its own. Converting has to fetch
    // them back out and merge the two, or upgrading reads as a history that
    // never came back.
    assert_eq!(
        chats_visible(&old),
        names(&["local_kept.json", "local_mine.json"]),
        "the chats the link stood in for are fetched back out of the stash and merged in"
    );
    assert!(
        exists(&src_org.join("local_mine.json")),
        "and reach the profile it borrows from, which the link never let them do"
    );
    assert!(
        exists(&stash.join("local_mine.json")),
        "while the stash goes on naming them, so going back is still exact"
    );

    // Sent back to its own chats, it stops being mirrored: the pair lives in a
    // file every launcher reads, so left there it would go on being squared up
    // by whichever profile opened next.
    assert!(!pairs_are_empty(), "while grafted, the pair is remembered");
    apply(&grafted(&old, None));
    assert!(pairs_are_empty(), "going back to its own chats is the end of the mirroring, not a pair left running");
    write(&src_org.join("local_kept.json"), "{\"isArchived\":true}");
    mirror_known_pairs();
    assert_eq!(
        read(&src_org.join("local_kept.json")).as_deref(),
        Some("{\"isArchived\":true}"),
        "and nothing reaches across the two afterwards"
    );

    // A profile somebody deleted must not be built back up by a pass whose
    // only job is keeping two sidebars in step.
    let _ = std::fs::remove_file(mirror_state_file());
    let doomed = make_profile("Claude-Doomed", Some("DACC"), Some("DORG"), &[]);
    let doomed_org = org_of(&doomed, "DACC", "DORG");
    mirror_chat_folders(&doomed_org, &src_org);
    assert!(!pairs_are_empty(), "a pair mirrors while both profiles are there");
    std::fs::remove_dir_all(&doomed).unwrap();
    assert_eq!(
        mirror_chat_folders(&doomed_org, &src_org),
        0,
        "a pass over a profile that has been deleted carries nothing"
    );
    assert!(!exists(&doomed), "and does not put the deleted profile back to have somewhere to write");
}

// Going back to its own chats

#[test]
fn going_back_sends_what_was_owned_what_was_done_and_what_was_borrowed_each_somewhere_different() {
    let _world = World::new();
    // A mirrored folder is a real one full of copies, so unlike a link it
    // cannot simply be dropped. What the profile owned before the graft, what
    // it did during it, and what it was only ever borrowing all have to end up
    // somewhere, and each of the three somewhere different.
    let lender = make_profile("Claude-Lender", Some("LEND"), Some("LENDORG"), &["theirs"]);
    let keeper = make_profile("Claude-Keeper", Some("KEEP"), Some("KEEPORG"), &["ours"]);
    let lent_org = org_of(&lender, "LEND", "LENDORG");
    let kept_org = org_of(&keeper, "KEEP", "KEEPORG");
    let both = names(&["local_ours.json", "local_theirs.json"]);

    apply(&grafted(&keeper, Some(&lender)));
    assert_eq!(
        chats_visible(&keeper),
        both,
        "sharing a history shows both sets of chats rather than only the borrowed one"
    );
    assert_eq!(
        chats_visible(&lender),
        both,
        "and the profile lent from is left holding both of them as well"
    );

    // The stash still has to hold what the profile brought, even though the
    // same records are now in the shared set: it is the only thing that says
    // which of the merged records were this profile's, and going back has to
    // be exact rather than a guess at which record belonged to whom.
    let stash = kept_org.parent().unwrap().join(format!(".KEEPORG{STASH_SUFFIX}"));
    assert!(
        exists(&stash.join("local_ours.json")),
        "what the profile brought to the merge is written down as well as shared"
    );

    // Now it behaves like a profile: a chat started, one archived, and one of
    // its own archived too, which the stash holds an older copy of.
    write(&kept_org.join("local_during.json"), "{\"title\":\"during\"}");
    write(&kept_org.join("local_theirs.json"), "{\"isArchived\":true}");
    write(&kept_org.join("local_ours.json"), "{\"isArchived\":true}");

    apply(&grafted(&keeper, None));
    assert_eq!(
        chats_visible(&keeper),
        names(&["local_ours.json"]),
        "going back to its own chats gives back exactly what it started with"
    );
    assert_eq!(
        read(&kept_org.join("local_ours.json")).as_deref(),
        Some("{\"isArchived\":true}"),
        "and gives it back as it was left, rather than as the stash remembers it"
    );
    assert!(!exists(&stash), "with nothing left in it, the stash goes rather than arming the next graft");
    assert!(
        exists(&lent_org.join("local_during.json")),
        "a chat started while mirroring is handed over before the copies are taken out"
    );
    assert_eq!(
        read(&lent_org.join("local_theirs.json")).as_deref(),
        Some("{\"isArchived\":true}"),
        "and so is an archive it made, which is the whole reason for copying"
    );
    assert!(pairs_are_empty(), "the pair is dropped, so nothing squares the two up afterwards");
    assert!(
        exists(&lent_org.join("local_ours.json")),
        "what was merged in stays with the profile it was merged into, which is the price of sharing"
    );

    // Twice over is a thing a launcher does on every run.
    apply(&grafted(&keeper, None));
    assert_eq!(
        chats_visible(&keeper),
        names(&["local_ours.json"]),
        "and a second pass over an ungrafted profile changes nothing"
    );
}

#[test]
fn opening_the_profile_that_lends_leaves_the_merge_and_the_pair_alone() {
    let _world = World::new();
    // Opening the profile that lends. `apply` calls `ungraft` on anything with
    // no source of its own, so this runs every time that shortcut is pressed,
    // and undoing a graft used to mean every pair with a half under the
    // profile, which is every pair somebody else borrows from it. The lender
    // read its own folder as the borrowed one; a lender keeps no stash, so
    // nothing was held back; and a merge leaves both sides holding the same
    // bytes, so the test for what to take out matched all of it. One press,
    // the whole shared history gone from the sidebar being built.
    let lender = make_profile("Claude-Lends", Some("LENDS"), Some("LENDSORG"), &["theirs"]);
    let borrower = make_profile("Claude-Borrows", Some("BORROWS"), Some("BORROWSORG"), &["ours"]);
    let both = names(&["local_ours.json", "local_theirs.json"]);

    apply(&grafted(&borrower, Some(&lender)));
    assert_eq!(chats_visible(&lender), both, "sharing a history leaves both sets of chats in the profile lent from");

    apply(&grafted(&lender, None));
    assert_eq!(
        chats_visible(&lender),
        both,
        "and opening that profile leaves them there rather than stripping the merge back out"
    );
    assert_eq!(chats_visible(&borrower), both, "with the profile that borrowed them untouched as well");
    assert!(
        !pairs_are_empty(),
        "the pair belongs to the profile doing the borrowing, so the lender does not forget it"
    );

    // Which is what the borrower's next launch turns on. A pair that has been
    // forgotten reads as a first pass, and a first pass stashes.
    let stash = borrower.join("claude-code-sessions/BORROWS").join(format!(".BORROWSORG{STASH_SUFFIX}"));
    apply(&grafted(&borrower, Some(&lender)));
    assert!(
        !exists(&stash.join("local_theirs.json")),
        "so the launch after is not a first pass and the stash goes on naming what was brought"
    );
    assert_eq!(chats_visible(&borrower), both, "and both histories are still where they were");

    // The lender going back to its own chats is not a thing it can do: it never
    // borrowed. Going back is the borrower's, and it still works.
    apply(&grafted(&borrower, None));
    assert_eq!(
        chats_visible(&borrower),
        names(&["local_ours.json"]),
        "the profile that did borrow still gets back exactly what it brought"
    );
}

#[test]
fn a_second_first_pass_does_not_fold_the_borrowed_chats_into_the_stash() {
    let _world = World::new();
    // A pair dropped while the merge it set up is still sitting in the folder:
    // a source deleted, an organization moved, an older Graft ungrafting the
    // lender. The launch after looks like a first pass, and a first pass
    // stashes: `stash` folds a stash it finds back into the live folder before
    // moving the lot aside, so the borrowed records would go in beside the
    // profile's own and nothing would be left saying which were whose. That is
    // a stash growing from 155 to 159 a launch at a time, and the count is all
    // anybody would have seen.
    let src = make_profile("Claude-Refold-Src", Some("RFSRC"), Some("RFORG"), &["theirs"]);
    let own = make_profile("Claude-Refold", Some("RFOWN"), Some("RFOWNORG"), &["ours"]);
    let config = grafted(&own, Some(&src));
    let stash = own.join("claude-code-sessions/RFOWN").join(format!(".RFOWNORG{STASH_SUFFIX}"));

    apply(&config);
    assert!(
        exists(&stash.join("local_ours.json")) && !exists(&stash.join("local_theirs.json")),
        "a first pass writes down what the profile brought to the merge and nothing else"
    );

    let _ = std::fs::remove_file(mirror_state_file());
    apply(&config);
    assert!(
        !exists(&stash.join("local_theirs.json")),
        "and a second first pass does not fold the borrowed chats in beside them"
    );
    assert!(exists(&stash.join("local_ours.json")), "while what the profile did bring is still named there");
    assert_eq!(
        chats_visible(&own),
        names(&["local_ours.json", "local_theirs.json"]),
        "with both histories back in the folder the person actually reads"
    );

    // Which is the whole reason the stash is kept: hand the borrowed ones over,
    // keep the ones it came with.
    apply(&grafted(&own, None));
    assert_eq!(
        chats_visible(&own),
        names(&["local_ours.json"]),
        "so going back is still exact rather than a guess at which record belonged to whom"
    );
}

#[test]
fn a_lender_moving_between_its_organizations_takes_no_chats_from_one_to_the_other() {
    let _world = World::new();
    // The lender's account belongs to two organizations, and the person starts
    // working in the other one. The borrower's folder stays where it is while
    // what it borrows from moves, and reading that as a first pass stashed the
    // first organization's chats as the borrower's own and copied them into
    // the second, and back again the other way, until both held the union.
    let lender = make_profile("Claude-Two-Orgs", Some("TWOORGS"), Some("ORG-ONE"), &["one-a", "one-b"]);
    make_profile("Claude-Two-Orgs", Some("TWOORGS"), Some("ORG-TWO"), &["two-a"]);
    let borrower = make_profile("Claude-Two-Orgs-B", Some("TOBORROW"), Some("TOBORG"), &["b-own"]);
    let config = grafted(&borrower, Some(&lender));
    let account = lender.join("claude-code-sessions/TWOORGS");
    let (one, two) = (account.join("ORG-ONE"), account.join("ORG-TWO"));
    let mine = borrower.join("claude-code-sessions/TOBORROW/TOBORG");
    let stash = mine.parent().unwrap().join(format!(".TOBORG{STASH_SUFFIX}"));

    work_in("ORG-ONE", &lender, "TWOORGS");
    apply(&config);
    assert_eq!(
        records_in(&mine),
        names(&["local_b-own.json", "local_one-a.json", "local_one-b.json"]),
        "a borrower starts out with the organization its lender is working in"
    );

    write(&two.join("local_two-b.json"), "{}");
    work_in("ORG-TWO", &lender, "TWOORGS");
    apply(&config);
    assert_eq!(
        records_in(&two),
        names(&["local_b-own.json", "local_two-a.json", "local_two-b.json"]),
        "the organization the lender moved to holds only its own chats and the borrower's"
    );
    assert_eq!(
        records_in(&mine),
        names(&["local_b-own.json", "local_two-a.json", "local_two-b.json"]),
        "and the borrower follows it there, leaving the first organization's chats behind"
    );
    assert_eq!(
        records_in(&stash),
        names(&["local_b-own.json"]),
        "while its stash goes on naming what it brought and none of what it borrowed"
    );

    write(&one.join("local_one-c.json"), "{}");
    work_in("ORG-ONE", &lender, "TWOORGS");
    apply(&config);
    assert_eq!(
        records_in(&one),
        names(&["local_b-own.json", "local_one-a.json", "local_one-b.json", "local_one-c.json"]),
        "going back to the first organization brings none of the second's chats with it"
    );
    assert_eq!(
        records_in(&two),
        names(&["local_b-own.json", "local_two-a.json", "local_two-b.json"]),
        "and leaves the second as it was"
    );
    assert_eq!(records_in(&stash), names(&["local_b-own.json"]), "with the stash still naming only the borrower's own");

    apply(&grafted(&borrower, None));
    assert_eq!(
        chats_visible(&borrower),
        names(&["local_b-own.json"]),
        "so going back to its own chats keeps none of the lender's, whichever organization they came from"
    );
}

#[test]
fn a_lender_signed_into_a_second_account_hands_the_first_one_back_what_was_written_there() {
    let _world = World::new();
    // The lender's profile signs into a second account, which starts a folder
    // of its own. The first account's chats were copied into it, beside its own
    // and the borrower's.
    let lender = make_profile("Claude-Signs-In", Some("FIRSTACC"), Some("FIRSTORG"), &["first"]);
    let borrower = make_profile("Claude-Signs-In-B", Some("SIBORROW"), Some("SIBORG"), &["b-own"]);
    let config = grafted(&borrower, Some(&lender));
    let first = lender.join("claude-code-sessions/FIRSTACC/FIRSTORG");
    let second = lender.join("claude-code-sessions/SECONDACC/SECONDORG");
    let mine = borrower.join("claude-code-sessions/SIBORROW/SIBORG");
    let stash = mine.parent().unwrap().join(format!(".SIBORG{STASH_SUFFIX}"));

    apply(&config);
    write(&mine.join("local_during.json"), "{}");

    make_profile("Claude-Signs-In", Some("SECONDACC"), Some("SECONDORG"), &["second"]);
    apply(&config);
    assert_eq!(
        records_in(&second),
        names(&["local_b-own.json", "local_second.json"]),
        "a lender signed into a second account holds only that account's chats and the borrower's"
    );
    assert_eq!(
        records_in(&mine),
        names(&["local_b-own.json", "local_second.json"]),
        "and the borrower now reads that account's chats instead of the first one's"
    );
    assert_eq!(
        records_in(&stash),
        names(&["local_b-own.json"]),
        "without its stash taking the first account's chats for its own"
    );
    assert_eq!(
        records_in(&first),
        names(&["local_b-own.json", "local_during.json", "local_first.json"]),
        "a chat started while it borrowed the first account is handed back to it, the way going back would"
    );
}

#[test]
fn a_same_account_pair_turning_into_a_two_account_one_puts_down_no_second_stash() {
    let _world = World::new();
    // The same, starting from two profiles on one account: the pair moves from
    // the branch that stashes the whole store to the one that stashes an
    // organization, and a second stash appeared beside the first, holding the
    // lender's chats.
    let lender = make_profile("Claude-Turns", Some("TURNACC"), Some("TURNORG"), &["first"]);
    let borrower = make_profile("Claude-Turns-B", Some("TURNACC"), Some("TURNORG"), &["b-own"]);
    let config = grafted(&borrower, Some(&lender));
    let second = lender.join("claude-code-sessions/TURN2ACC/TURN2ORG");
    let mine = borrower.join("claude-code-sessions/TURNACC/TURNORG");
    let store_stash = borrower.join(format!(".claude-code-sessions{STASH_SUFFIX}/TURNACC/TURNORG"));

    apply(&config);
    assert_eq!(
        records_in(&store_stash),
        names(&["local_b-own.json"]),
        "two profiles on one account put the borrower's store away whole"
    );

    make_profile("Claude-Turns", Some("TURN2ACC"), Some("TURN2ORG"), &["second"]);
    apply(&config);
    assert_eq!(
        records_in(&second),
        names(&["local_b-own.json", "local_second.json"]),
        "a lender moving to another account takes none of the first account's chats with it"
    );
    assert_eq!(
        records_in(&mine),
        names(&["local_b-own.json", "local_second.json"]),
        "and the borrower reads the new account's chats and its own"
    );
    assert!(!exists(&stash_path(&mine)), "with no second stash put down beside the store's");
    assert_eq!(
        records_in(&store_stash),
        names(&["local_b-own.json"]),
        "and the store's stash still naming only what the borrower brought"
    );

    apply(&grafted(&borrower, None));
    assert_eq!(
        chats_visible(&borrower),
        names(&["local_b-own.json"]),
        "so going back still gives it exactly its own chats"
    );
}

#[test]
fn launching_a_mirrored_shortcut_again_stashes_nothing_borrowed_and_seeds_nothing_deleted() {
    let _world = World::new();
    // The pass that must never happen: `apply` runs on every launch, so a
    // second one that put the borrowed chats away as though they were the
    // profile's own would empty the sidebar and fetch it all again, for ever.
    let src = make_profile("Claude-Twice-Src", Some("TSRC"), Some("TORG"), &["one"]);
    let twice = make_profile("Claude-Twice", Some("TOWN"), Some("TOWNORG"), &["own"]);
    let config = grafted(&twice, Some(&src));
    let own_org = org_of(&twice, "TOWN", "TOWNORG");
    let stash = own_org.parent().unwrap().join(format!(".TOWNORG{STASH_SUFFIX}"));

    apply(&config);
    apply(&config);
    apply(&config);
    assert_eq!(
        chats_visible(&twice),
        names(&["local_one.json", "local_own.json"]),
        "launching a mirrored shortcut again leaves the merged set where it is"
    );
    assert!(
        exists(&stash.join("local_own.json")),
        "the record of what the profile brought stays put rather than being folded back in"
    );
    assert!(!exists(&stash.join("local_one.json")), "nothing borrowed is stashed away by the pass after the first");

    // The other half of that rule, and the one the stash arms: seeding the
    // profile's own chats into the shared set runs on the first pass alone.
    // Running it on every launch would fetch back every merged chat the person
    // had since deleted, which is the same endless loop pointed the other way.
    let _ = std::fs::remove_file(own_org.join("local_own.json"));
    let _ = std::fs::remove_file(org_of(&src, "TSRC", "TORG").join("local_own.json"));
    apply(&config);
    assert_eq!(
        chats_visible(&twice),
        names(&["local_one.json"]),
        "a merged chat deleted afterwards is not fetched back out of the stash"
    );
}

#[test]
fn with_the_source_gone_the_copies_stay_and_the_pair_goes() {
    let _world = World::new();
    // A source somebody deleted, or a folder that could not be read. Nothing
    // was handed over, so nothing may be taken away.
    let gone = make_profile("Claude-Gone", Some("GONE"), Some("GONEORG"), &["borrowed"]);
    let orphan = make_profile("Claude-Orphan", Some("ORPH"), Some("ORPHORG"), &[]);
    apply(&grafted(&orphan, Some(&gone)));
    assert_eq!(chats_visible(&orphan), names(&["local_borrowed.json"]), "the profile has its copy");

    let _ = std::fs::remove_dir_all(&gone);
    apply(&grafted(&orphan, None));
    assert_eq!(
        chats_visible(&orphan),
        names(&["local_borrowed.json"]),
        "with the source gone there is nowhere to hand the copies back, so they stay"
    );
    assert!(pairs_are_empty(), "the pair still goes, since there is nothing left to keep in step");
}

#[test]
fn going_back_hands_over_what_was_written_while_borrowing_and_puts_no_link_back() {
    let _world = World::new();
    // Going back to its own chats is the only way off, so it has to carry
    // everything: a record the profile wrote while it was borrowing belongs to
    // the profile it borrowed from, and a copy taken away before that handover
    // is a chat in nobody's sidebar.
    let host = make_profile("Claude-Back-Src", Some("BSRC"), Some("BORG"), &["shared"]);
    let guest = make_profile("Claude-Back", Some("BOWN"), Some("BOWNORG"), &["private"]);
    let guest_org = org_of(&guest, "BOWN", "BOWNORG");
    let host_org = org_of(&host, "BSRC", "BORG");

    apply(&grafted(&guest, Some(&host)));
    write(&guest_org.join("local_late.json"), "{\"title\":\"late\"}");

    apply(&grafted(&guest, None));
    assert!(
        exists(&host_org.join("local_late.json")),
        "going back hands over what the profile wrote while it was borrowing"
    );
    assert!(pairs_are_empty(), "with the pair dropped on the way past");
    assert!(
        !is_symlink(&guest_org) && is_directory(&guest_org),
        "and nothing anywhere puts a link back, since there is no link left to put"
    );
    assert_eq!(chats_visible(&guest), names(&["local_private.json"]), "so its own chats are what it comes back to");
}

#[test]
fn two_profiles_on_one_account_mirror_the_whole_store() {
    let _world = World::new();
    // Both profiles on one account. A link takes the whole store rather than
    // one organization inside it, so that is what a mirror has to stand in
    // for, and the folder it writes into is one nothing had made yet, which
    // is a same-account graft that mirrored not a single file.
    let first = make_profile("Claude-Same-A", Some("SAME"), Some("SAMEORG"), &["shared"]);
    let second = make_profile("Claude-Same-B", Some("SAME"), Some("SAMEORG"), &["alone"]);
    let second_org = org_of(&second, "SAME", "SAMEORG");
    let first_org = org_of(&first, "SAME", "SAMEORG");
    let both = names(&["local_alone.json", "local_shared.json"]);

    apply(&grafted(&second, Some(&first)));
    assert_eq!(
        chats_visible(&second),
        both,
        "two profiles on one account merge the whole store, organization by organization"
    );
    assert_eq!(
        chats_visible(&first),
        both,
        "and what the borrowing profile had is written down and shared rather than only put away"
    );

    write(&second_org.join("local_shared.json"), "{\"isArchived\":true}");
    mirror_known_pairs();
    assert_eq!(
        read(&first_org.join("local_shared.json")).as_deref(),
        Some("{\"isArchived\":true}"),
        "an archive made on one side reaches the other"
    );

    apply(&grafted(&second, None));
    assert_eq!(
        chats_visible(&second),
        names(&["local_alone.json"]),
        "and going back to its own chats brings a whole store out of the stash"
    );
}

#[test]
fn a_record_the_other_side_could_not_be_given_is_never_counted_as_handed_over() {
    let _world = World::new();
    // The other side holding the name is not the other side holding the chat.
    // A source that cannot be written to, read-only or simply full, takes none
    // of the last pass, and a copy dropped because a stale version of it
    // happened to sit over there is a chat lost for a reason nobody could see.
    let readonly = make_profile("Claude-RO-Src", Some("ROSRC"), Some("ROORG"), &["one", "two"]);
    let writer = make_profile("Claude-RO", Some("ROOWN"), Some("ROOWNORG"), &[]);
    let theirs = org_of(&readonly, "ROSRC", "ROORG");
    let mine = org_of(&writer, "ROOWN", "ROOWNORG");
    apply(&grafted(&writer, Some(&readonly)));

    // Archived here, and the handover cannot land.
    write(&mine.join("local_one.json"), "{\"isArchived\":true}");
    let set_mode = |mode: u32| {
        for store in CHAT_STORES {
            let dir = readonly.join(store).join("ROSRC").join("ROORG");
            let _ = std::fs::set_permissions(&dir, std::fs::Permissions::from_mode(mode));
        }
    };
    set_mode(0o555);
    apply(&grafted(&writer, None));
    let one_here = read(&mine.join("local_one.json"));
    let one_there = read(&theirs.join("local_one.json"));
    let two_here = exists(&mine.join("local_two.json"));
    // Put back before asserting, so a failure leaves a sandbox that can still
    // be cleared away.
    set_mode(0o755);
    assert_eq!(
        one_here.as_deref(),
        Some("{\"isArchived\":true}"),
        "a record the other side could not be given stays where the profile wrote it"
    );
    assert_eq!(
        one_there.as_deref(),
        Some("{}"),
        "and the stale copy over there is left as it was rather than counted as a handover"
    );
    assert!(!two_here, "while a record the other side really does hold, byte for byte, is taken out");
}

#[test]
fn a_whole_store_linked_by_a_released_version_converts_and_comes_back() {
    let _world = World::new();
    // Two shortcuts on one account, grafted by a released version, which links
    // the whole store rather than one organization inside it. Converting has
    // to stand in for that link and going back has to undo it, and the store
    // is a level above everything the ungraft walk was looking at.
    let elder = make_profile("Claude-One-A", Some("ONE"), Some("ONEORG"), &["shared"]);
    let junior = make_profile("Claude-One-B", Some("ONE"), Some("ONEORG"), &["alone"]);
    let store = junior.join("claude-code-sessions");
    let stash = junior.join(format!(".claude-code-sessions{STASH_SUFFIX}"));

    link_the_old_way(&store, &elder.join("claude-code-sessions"));
    assert!(
        is_symlink(&store) && exists(&stash),
        "a released version on one account links the whole store and stashes the whole store"
    );

    apply(&grafted(&junior, Some(&elder)));
    assert!(
        !is_symlink(&store) && chats_visible(&junior) == names(&["local_alone.json", "local_shared.json"]),
        "converting takes that link too, and merges the stashed store back in with the source's"
    );
    assert!(exists(&stash), "with the profile's own store still put away beside it");

    apply(&grafted(&junior, None));
    assert_eq!(
        chats_visible(&junior),
        names(&["local_alone.json"]),
        "and going back gives it the store it had before any of this"
    );
}

#[test]
fn a_source_deleted_out_from_under_a_profile_leaves_it_both_sets_of_chats() {
    let _world = World::new();
    // The source deleted out from under a profile that was copying from it.
    // The copies are all that is left of those chats, so they stay: whatever
    // else going back to its own chats means, it does not mean being the
    // second thing to delete them.
    let doomed = make_profile("Claude-Vanish-Src", Some("VANSRC"), Some("VANORG"), &["theirs"]);
    let survivor = make_profile("Claude-Vanish", Some("VANOWN"), Some("VANOWNORG"), &["ours"]);
    apply(&grafted(&survivor, Some(&doomed)));
    delete_profile(&doomed);
    assert!(pairs_are_empty(), "deleting the source ends the pair from its own side");

    apply(&grafted(&survivor, None));
    assert_eq!(
        chats_visible(&survivor),
        names(&["local_ours.json", "local_theirs.json"]),
        "and the profile keeps both what it owned and what it copied, having nowhere to hand it back"
    );
}

#[test]
fn deleting_a_shortcut_but_keeping_its_profile_stops_the_mirroring_and_moves_nothing() {
    let _world = World::new();
    // Deleting the shortcut but keeping the profile. Nothing runs its launcher
    // again, so nothing is left to undo the mirroring for it, and a pair that
    // outlives its shortcut goes on being squared up by whichever profile
    // opens next, for ever, and without anything on screen saying so.
    let src = make_profile("Claude-Bye-Src", Some("BYSRC"), Some("BYORG"), &["one"]);
    let bye = make_profile("Claude-Bye", Some("BYOWN"), Some("BYOWNORG"), &[]);
    // Installing a mirrored shortcut runs its graft; the installer itself is
    // another crate's, so the graft it runs stands in for it.
    apply(&grafted(&bye, Some(&src)));
    assert!(!pairs_are_empty(), "an installed mirrored shortcut leaves a pair");

    forget_mirrors(&bye);
    assert!(pairs_are_empty(), "deleting the shortcut stops the two folders being squared up");
    assert_eq!(
        chats_visible(&paths::profile_dir("Claude-Bye")),
        names(&["local_one.json"]),
        "and the chats it had are left exactly where they are, since the folder was kept"
    );
}

#[test]
fn deleting_the_profile_takes_its_pairs_with_it() {
    let _world = World::new();
    // Deleting the profile is the other way out of a pair: nothing is left to
    // settle, and a pair naming a folder that is gone would go on being tried
    // by every launcher that ran.
    let src = make_profile("Claude-Doom-Src", Some("DSRC"), Some("DSORG"), &["one"]);
    let doomed = make_profile("Claude-Doom", Some("DOWN"), Some("DOWNORG"), &[]);
    apply(&grafted(&doomed, Some(&src)));
    assert!(!pairs_are_empty(), "the pair is remembered while the profile is there");
    delete_profile(&doomed);
    assert!(pairs_are_empty(), "deleting the profile takes its pairs with it");
}
