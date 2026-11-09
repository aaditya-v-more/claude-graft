//! Grafting a profile onto another's chats, and taking it back off.

use crate::fsx::{
    self, counterpart_directory, exists, is_directory, is_symlink, names, names_or_empty, newest_child, relink,
    resolved_text, stash, stash_path, unstash,
};
use crate::mirror::{self, is_mirrored, mirror_chat_folders, mirror_pairs_borrowed_by, pair_folders, pair_key};
use crate::{APPEARANCE_KEYS, CHAT_STORES, SHARED_ITEMS, STASH_SUFFIX};
use graft_core::{diagnostics, files, lock, paths, GraftConfig};
use serde_json::{json, Map, Value};
use std::collections::BTreeSet;
use std::io::Write;
use std::os::unix::fs::OpenOptionsExt;
use std::path::{Path, PathBuf};

/// Share the safe files, then mirror this profile's chats with the source's.
/// Chats are stored per account, so when the two profiles are on different
/// accounts the pairing is made one level deeper: the destination's own
/// `<account>/<org>` against the source's active one.
pub fn graft(source: &Path, profile: &Path) {
    // A profile pointed at itself would stash every one of its own files away
    // and replace them with links to nothing.
    if paths::same_path(source, profile) {
        return;
    }
    let _ = std::fs::create_dir_all(profile);
    for item in SHARED_ITEMS {
        relink(&source.join(item), &profile.join(item));
    }
    copy_desktop_servers(source, profile);
    mirror_chat_stores(source, profile);
    copy_appearance(source, profile);
}

/// Make a folder ready to be filled from the source: real, so the profile can
/// write into it at all, and emptied of what it held.
///
/// Real, because a link is the reason a grafted profile cannot archive
/// anything. Emptied, because the stash is the only record of which chats were
/// the profile's own before any of this, and going back to its own chats has
/// to be exact rather than a guess at which record belonged to whom. So the
/// profile's own chats go to the same hidden sibling a linked graft would have
/// moved them to, and `seed_own_chats` then copies them straight back in,
/// because sharing a history merges the two rather than standing one in for
/// the other.
///
/// `first_pass` is what stops the second launch stashing away everything the
/// first one mirrored in. `apply` runs on every launch, and without it this
/// would put the borrowed chats away and fetch them again on an endless loop.
/// It is also what stops the seeding running twice, which would fetch back
/// every shared chat the person had since deleted.
pub fn open_for_mirror(folder: &Path, first_pass: bool) {
    if is_symlink(folder) {
        let _ = std::fs::remove_file(folder);
    } else if first_pass {
        stash(folder);
    }
    let _ = std::fs::create_dir_all(folder);
}

/// Take the borrowed copies back out of a folder about to be stashed a second
/// time.
///
/// A first pass that finds a stash already in place is a second one: the pair
/// that would have said so was dropped, a source deleted, a folder moved,
/// while the merge it set up is still sitting in the folder. `stash` folds a
/// stash it finds back into the live folder before moving the lot aside, which
/// is right for a link the profile has written a real file over and wrong
/// here: it would put the borrowed records into the stash beside the profile's
/// own and leave nothing saying which of the merged records were whose.
///
/// A record the source is holding byte for byte that the stash does not name
/// is a borrowed one, the same test `unmirror_chat_stores` removes a copy on:
/// bytes that match are bytes nobody loses, and these come straight back in on
/// the pass that follows.
///
/// Only ever inside the profile. A shortcut left on the shape an older version
/// made, its own chats in the stash and a link where they used to be, has a
/// folder whose contents are the source's own files, and every one of them
/// matches the source byte for byte because it is the source: this would
/// delete the lender's whole history and call it tidying up. The link is not
/// always the folder itself either; a store linked whole leaves the
/// organization folder a real directory reached through a linked parent. What
/// both shapes have in common is that the folder resolves outside the profile.
pub fn drop_borrowed_copies(folder: &Path, source: &Path, store: &Path) -> usize {
    let profile = format!("{}/", resolved_text(&fsx::parent(store)));
    if !resolved_text(folder).starts_with(&profile) {
        return 0;
    }
    let Some(own) = stashed_counterpart(folder, store) else { return 0 };
    let mut dropped = 0;
    for name in names_or_empty(folder).into_iter().filter(|n| is_mirrored(n)) {
        let here = folder.join(&name);
        if exists(&own.join(&name)) {
            continue;
        }
        let (Ok(mine), Ok(theirs)) = (std::fs::read(&here), std::fs::read(source.join(&name))) else {
            continue;
        };
        if mine != theirs || !fsx::remove_item(&here) {
            continue;
        }
        dropped += 1;
    }
    if dropped == 0 {
        return 0;
    }
    diagnostics::note(
        "mirror.reborrowed",
        &[
            ("folder", json!(paths::text(folder))),
            ("dropped", json!(dropped)),
            ("because", json!("a stash already names what this profile brought, so these were borrowed")),
        ],
    );
    dropped
}

/// Put the profile's own chats into the shared set, so a graft merges the two
/// histories instead of replacing one with the other.
///
/// Copied rather than moved. The stash has to go on holding them: it is what
/// `unstash` hands back when the shortcut returns to its own chats, and with
/// the copies gone from the shared set by then it is the only thing that still
/// knows which of the merged records this profile brought to it.
pub fn seed_own_chats(folder: &Path, store: &Path) -> usize {
    let Some(own) = stashed_counterpart(folder, store) else { return 0 };
    let mut seeded = 0;
    for name in names_or_empty(&own).into_iter().filter(|n| is_mirrored(n)) {
        if fsx::copy_item(&own.join(&name), &folder.join(&name)) {
            seeded += 1;
        }
    }
    diagnostics::note("mirror.seed", &[("folder", json!(paths::text(folder))), ("own", json!(seeded))]);
    seeded
}

/// The stashed copy of an organization folder, whichever way it was put away:
/// a cross-account graft stashes the organization folder itself, while two
/// profiles on one account stash the whole store above it, leaving the same
/// records one level further down. `None` rather than a guess when neither
/// shape is there, since a profile that had no chats of its own leaves no
/// stash behind at all.
pub fn stashed_counterpart(folder: &Path, store: &Path) -> Option<PathBuf> {
    let sibling = stash_path(folder);
    if is_directory(&sibling) {
        return Some(sibling);
    }
    let account = fsx::file_name(&fsx::parent(folder));
    let inside = stash_path(store).join(account).join(fsx::file_name(folder));
    is_directory(&inside).then_some(inside)
}

fn account_in(config: &Map<String, Value>) -> Option<String> {
    config.get("lastKnownAccountUuid").and_then(Value::as_str).map(str::to_string)
}

/// Give a profile its own copy of the source's chats rather than a link to
/// them, and keep the two in step.
///
/// A link is the reason a grafted profile cannot archive, rename or delete
/// anything: Claude Desktop will not write a record into a folder that
/// resolves outside the profile. A real folder it will. So the folder is made
/// real and a pass carries records both ways, which costs a second copy of
/// some small JSON and buys a profile that behaves like a profile.
pub fn mirror_chat_stores(source: &Path, profile: &Path) {
    let Some(source_account) = account_in(&files::config_json(source)) else { return };
    // Unreadable is not the same as signed out. A config caught mid-rename read
    // as "no account, so the same account as the source" once linked an entire
    // store away; here the same mistake would copy one account's chats into
    // another's folder.
    let Some(own_config) = files::readable_config_json(profile) else { return };
    let own_account = account_in(&own_config);

    for store in CHAT_STORES {
        let src = source.join(store);
        let dst = profile.join(store);
        if !is_directory(&src) {
            continue;
        }
        match &own_account {
            Some(own) if *own != source_account => mirror_across_accounts(&src, &source_account, &dst, own),
            _ => mirror_within_one_account(&src, &dst, own_account.as_deref().unwrap_or(&source_account)),
        }
        // Keyed by organization rather than account, and not a sidebar, so it
        // stays a link.
        //
        // After both branches rather than inside one of them: a same-account
        // graft puts the whole store away and builds it back, taking any link
        // made first to the stash with everything else.
        let skills = src.join("skills-plugin");
        if is_directory(&skills) {
            relink(&skills, &dst.join("skills-plugin"));
        }
    }
}

/// One account on both sides: every organization it has is shared, so every
/// organization is mirrored. A link would have taken the whole store, so the
/// whole store is what goes to the stash.
fn mirror_within_one_account(src: &Path, dst: &Path, account: &str) {
    // Read the source before anything of this profile's is moved. Opening the
    // destination first was a store emptied on the strength of a source that
    // turned out to have nothing under this account: the profile's whole
    // history went to the stash, an empty folder stood where it had been, and
    // no pair was written down, so every launch after did it again.
    let Some(source_account_dir) = counterpart_directory(src, account) else {
        diagnostics::note(
            "mirror.nothingToShare",
            &[
                ("source", json!(paths::text(src))),
                ("account", json!(account)),
                ("because", json!("the source holds no folder for this account")),
            ],
        );
        return;
    };
    let their_account = src.join(&source_account_dir);
    let orgs: Vec<String> = names_or_empty(&their_account)
        .into_iter()
        .filter(|o| !o.starts_with('.') && !o.ends_with(STASH_SUFFIX))
        .filter(|o| is_directory(&their_account.join(o)))
        .collect();
    if orgs.is_empty() {
        diagnostics::note(
            "mirror.nothingToShare",
            &[
                ("source", json!(paths::text(&their_account))),
                ("account", json!(account)),
                ("because", json!("the source holds no organization folder under this account")),
            ],
        );
        return;
    }

    // The destination names the account the way the destination names it,
    // which is not always the way the source does.
    let own_account_dir = counterpart_directory(dst, account).unwrap_or_else(|| account.to_string());
    let own_root = dst.join(&own_account_dir);
    let mine: Vec<PathBuf> = orgs
        .iter()
        .map(|org| own_root.join(counterpart_directory(&own_root, org).unwrap_or_else(|| org.clone())))
        .collect();
    let theirs: Vec<PathBuf> = orgs.iter().map(|org| their_account.join(org)).collect();
    let live: Vec<(PathBuf, PathBuf)> = mine.iter().cloned().zip(theirs.iter().cloned()).collect();
    mirror::forget_stale_pairs(dst, &live);

    // Read before the store is opened, and held for every organization under
    // it: the first pass stashes the store as a whole, so asking again after
    // the first organization has been paired would say no to seeding all the
    // rest.
    let first_pass = mirror_pairs_borrowed_by(dst).is_empty();
    if first_pass {
        for (folder, source) in &live {
            drop_borrowed_copies(folder, source, dst);
        }
    }
    open_for_mirror(dst, first_pass);
    for (folder, source) in &live {
        // The path is made here, where the profile is known to be a real one
        // being configured, rather than in the keeping-in-step pass, which must
        // not build a deleted profile back up around a folder it wanted to
        // write into.
        open_for_mirror(folder, false);
        if first_pass {
            seed_own_chats(folder, dst);
        }
        mirror_chat_folders(folder, source);
    }
}

/// Two accounts: this profile's own `<account>/<org>` is paired with the
/// source's active one, because that is where each side's Claude reads.
fn mirror_across_accounts(src: &Path, source_account: &str, dst: &Path, own_account: &str) {
    let found = counterpart_directory(src, source_account)
        .and_then(|dir| newest_child(&src.join(&dir)).map(|org| (dir, org)));
    let Some((source_account_dir, source_org)) = found else {
        diagnostics::note(
            "mirror.nothingToShare",
            &[
                ("source", json!(paths::text(src))),
                ("account", json!(source_account)),
                ("because", json!("the source holds no organization folder under this account")),
            ],
        );
        return;
    };
    let own_account_dir = counterpart_directory(dst, own_account).unwrap_or_else(|| own_account.to_string());
    // The source is asked second, and asked about its own spelling of this
    // profile's account: a profile with no organization folder of its own yet
    // may still have one sitting in the store it is borrowing from.
    let org_held_by_source =
        counterpart_directory(src, own_account).and_then(|dir| newest_child(&src.join(dir)));
    let Some(own_org) = newest_child(&dst.join(&own_account_dir)).or(org_held_by_source) else { return };

    // A store-wide link, from when these two were on one account.
    if is_symlink(dst) {
        open_for_mirror(dst, false);
    }
    let mine = dst.join(&own_account_dir).join(&own_org);
    let theirs = src.join(&source_account_dir).join(&source_org);
    let _ = std::fs::create_dir_all(fsx::parent(&mine));

    // Asked before anything is dropped. The source half is the one that moves
    // when the lender signs into another account or starts working in another
    // organization, and a folder that was borrowing yesterday is not new today
    // because what it borrows from moved: read as a first pass, the stash
    // folded the old source's records in as this profile's own and the seeding
    // copied them into the new one.
    let current = pair_key(&mine, &theirs);
    let borrowed = mirror_pairs_borrowed_by(&mine);
    let earlier: Vec<String> = borrowed.iter().filter(|k| **k != current).cloned().collect();
    let first_pass = borrowed.is_empty();
    for key in earlier {
        let Some((_, old)) = pair_folders(&key) else { continue };
        repair(&mine, &old);
    }
    mirror::forget_stale_pairs(dst, &[(mine.clone(), theirs.clone())]);
    if first_pass {
        drop_borrowed_copies(&mine, &theirs, dst);
    }
    open_for_mirror(&mine, first_pass);
    if first_pass {
        seed_own_chats(&mine, dst);
    }
    mirror_chat_folders(&mine, &theirs);
}

/// Take what a folder borrowed from one source back out before it starts
/// borrowing from another, the way going back to its own chats would.
///
/// Left in, the old source's records are carried into the new one as though
/// this profile had brought them, so a lender that changed organization found
/// the other organization's chats in it, and changing back put the union in
/// both. A source that is not there any more has nothing to be handed back to,
/// and its copies are all that is left of those chats, so they stay.
fn repair(mine: &Path, old: &Path) {
    if !is_directory(old) {
        diagnostics::note(
            "mirror.repair.kept",
            &[
                ("folder", json!(paths::text(mine))),
                ("was", json!(paths::text(old))),
                ("because", json!("the folder it borrowed from is gone, so its copies are kept")),
            ],
        );
        return;
    }
    let returned = hand_back(mine, old);
    diagnostics::note(
        "mirror.repair",
        &[("folder", json!(paths::text(mine))), ("was", json!(paths::text(old))), ("returned", json!(returned))],
    );
}

/// One last pass, then out with every copy the other side holds byte for byte
/// that the stash does not name. Returns how many went.
///
/// The last pass carries what the profile did while it was borrowing, a chat
/// started, one archived, one deleted, over to the other side first, so what
/// goes is only ever bytes the other side already has. What the stash names is
/// the profile's own and stays: since the merge it has been on both sides too,
/// and the byte test alone would hand this profile's own history to the one it
/// borrowed from and keep nothing back.
fn hand_back(mine: &Path, theirs: &Path) -> usize {
    mirror_chat_folders(mine, theirs);
    let store = fsx::parent(&fsx::parent(mine));
    let own = stashed_counterpart(mine, &store);
    let mut removed = 0;
    for name in names_or_empty(mine).into_iter().filter(|n| is_mirrored(n)) {
        // Kept rather than removed and fetched back out of the stash, so that a
        // chat of its own the profile archived while the graft was up stays
        // archived. Restoring the stashed copy over it would put the
        // conversation back in the sidebar unarchived, which is the one symptom
        // this app gets reported for most.
        if let Some(own) = &own {
            if exists(&own.join(&name)) {
                continue;
            }
        }
        let (Ok(here), Ok(there)) = (std::fs::read(mine.join(&name)), std::fs::read(theirs.join(&name))) else {
            continue;
        };
        if here != there || !fsx::remove_item(&mine.join(&name)) {
            continue;
        }
        removed += 1;
    }
    removed
}

/// JSON pretty-printed with its keys sorted, the shape Claude writes its own
/// settings in, so a file Graft rewrote reads like one Claude wrote.
fn pretty_sorted(value: &Value) -> Vec<u8> {
    let sorted: Value = serde_json::from_slice(&files::sorted_json(value)).unwrap_or(Value::Null);
    serde_json::to_vec_pretty(&sorted).unwrap_or_default()
}

/// Permission opt-ins live beside MCP definitions. Linking that file lets one
/// profile replace the other's choices, and relinking on every launch discards
/// changes Claude saved by renaming over the link.
pub fn copy_desktop_servers(source: &Path, profile: &Path) {
    if paths::same_path(source, profile) {
        return;
    }
    let name = "claude_desktop_config.json";
    let destination = profile.join(name);
    let backup = stash_path(&destination);
    let linked = is_symlink(&destination);
    let local = if linked { backup } else { destination.clone() };

    let read = |path: &Path| -> Option<Map<String, Value>> {
        if !exists(path) {
            return Some(Map::new());
        }
        match std::fs::read(path).ok().and_then(|b| serde_json::from_slice::<Value>(&b).ok()) {
            Some(Value::Object(map)) => Some(map),
            _ => None,
        }
    };
    let servers_ok = |m: &Map<String, Value>| m.get("mcpServers").map(Value::is_object).unwrap_or(true);

    // A backup caught mid-write is not permission to discard it. Keep the old
    // link as well until both settings files can be read safely.
    let (Some(mut own), Some(borrowed)) = (read(&local), read(&source.join(name))) else {
        diagnostics::note("settings.unread", &[("profile", json!(fsx::file_name(profile)))]);
        return;
    };
    if !servers_ok(&own) || !servers_ok(&borrowed) {
        diagnostics::note("settings.unread", &[("profile", json!(fsx::file_name(profile)))]);
        return;
    }
    let mut servers = own.get("mcpServers").and_then(Value::as_object).cloned().unwrap_or_default();
    let mut added = false;
    if let Some(theirs) = borrowed.get("mcpServers").and_then(Value::as_object) {
        for (server, definition) in theirs {
            if !servers.contains_key(server) {
                servers.insert(server.clone(), definition.clone());
                added = true;
            }
        }
    }
    if !linked && !added {
        return;
    }
    if added {
        own.insert("mcpServers".into(), Value::Object(servers));
    }

    // Renaming a complete sibling over the link replaces the link itself; a
    // direct write through it would change the source's permissions.
    let staged = profile.join(format!(".graft-settings-{}.tmp", uuid::Uuid::new_v4().simple()));
    let result = (|| -> std::io::Result<()> {
        let mut file = std::fs::OpenOptions::new().write(true).create_new(true).mode(0o600).open(&staged)?;
        file.write_all(&pretty_sorted(&Value::Object(own)))?;
        file.sync_all()?;
        std::fs::rename(&staged, &destination)
    })();
    let _ = std::fs::remove_file(&staged);
    match result {
        Ok(()) => diagnostics::note(
            "settings.local",
            &[("profile", json!(fsx::file_name(profile))), ("replacedLink", json!(linked))],
        ),
        Err(e) => diagnostics::note(
            "settings.failed",
            &[("profile", json!(fsx::file_name(profile))), ("error", json!(e.to_string()))],
        ),
    }
}

/// Theme and locale sit in config.json beside this profile's credentials, so
/// those two keys are copied rather than the file being linked.
pub fn copy_appearance(source: &Path, profile: &Path) {
    let from = files::config_json(source);
    if from.is_empty() {
        return;
    }
    // Writing the two appearance keys over a config caught mid-rename took the
    // profile's login and the account its chats are filed under along with it.
    let Some(mut into) = files::readable_config_json(profile) else { return };

    // Nothing to gain from rewriting a file that holds a login, on every
    // launch, to put back the two values already in it.
    let wanted: Vec<&str> = APPEARANCE_KEYS.iter().copied().filter(|k| from.contains_key(*k)).collect();
    if !wanted.iter().any(|k| into.get(*k) != from.get(*k)) {
        return;
    }
    for key in wanted {
        into.insert(key.to_string(), from[key].clone());
    }
    let _ = files::atomic_write(&profile.join("config.json"), &pretty_sorted(&Value::Object(into)), 0o600);
}

/// Take a mirrored profile back off the shared set, and lose nothing on the
/// way.
///
/// A mirrored folder is a real one full of copies, so unlike a link it cannot
/// simply be dropped: what is in it has to be settled first. One last pass
/// carries everything the profile did while it was mirroring over to the
/// profile it borrowed from, and only then are the copies taken out.
///
/// Which copies is not a guess and does not trust the pass: a record goes only
/// when the other side is holding the same bytes this moment. So a source that
/// has been deleted, or a folder that could not be read, leaves every copy
/// where it is rather than taking a chat away on the strength of a sync that
/// did not happen.
///
/// What the profile brought to the merge is the exception, and the stash is
/// what names it. Those records are on both sides too, so taking them out
/// would hand this profile's own history to the one it borrowed from and keep
/// nothing back.
pub fn unmirror_chat_stores(profile: &Path) -> usize {
    // The pairs this profile borrows through, never the ones it lends. `apply`
    // calls `ungraft` on any profile with no source of its own, so being
    // nobody's borrower is the ordinary case here and the ordinary answer is to
    // do nothing at all.
    let keys = mirror_pairs_borrowed_by(profile);
    if keys.is_empty() {
        return 0;
    }
    let mut removed = 0;
    for key in &keys {
        let Some((one, other)) = pair_folders(key) else { continue };
        removed += hand_back(&one, &other);
    }
    // Only the pairs this pass settled. The ones it lends are somebody else's
    // mirror: a borrower whose pair has been dropped reads its next launch as a
    // first pass, stashes the merged folder, and the stash stops naming what
    // that profile brought to the merge.
    mirror::drop_pairs(&keys.iter().cloned().collect::<BTreeSet<_>>());
    diagnostics::note(
        "unmirror",
        &[
            ("profile", json!(fsx::file_name(profile))),
            ("pairs", json!(keys.len())),
            ("copiesRemoved", json!(removed)),
        ],
    );
    removed
}

/// Undo a graft: drop every link this profile holds so it falls back to its
/// own storage, and hand back what it put aside. Real files it wrote itself
/// are left alone.
pub fn ungraft(profile: &Path) {
    diagnostics::note("ungraft.begin", &[("profile", json!(fsx::file_name(profile)))]);
    unmirror_chat_stores(profile);
    for item in SHARED_ITEMS {
        unstash(&profile.join(item));
    }
    unstash(&profile.join("claude_desktop_config.json"));
    for store in CHAT_STORES {
        let dst = profile.join(store);
        if is_symlink(&dst) {
            unstash(&dst);
            continue;
        }
        for account in names_or_empty(&dst) {
            if account.ends_with(STASH_SUFFIX) {
                continue;
            }
            let account_dir = dst.join(&account);
            if is_symlink(&account_dir) {
                unstash(&account_dir);
                continue;
            }
            let Some(orgs) = names(&account_dir) else { continue };
            for org in orgs.into_iter().filter(|o| !o.ends_with(STASH_SUFFIX)) {
                unstash(&account_dir.join(org));
            }
        }
        // A whole store goes to the stash when both profiles are on one
        // account, and it has to come back whether what stands in its place is
        // the link that replaced it or the real folder a mirror made.
        unstash(&dst);
        unstash(&dst.join("skills-plugin"));
    }
    diagnostics::note("ungraft.end", &[("profile", json!(fsx::file_name(profile)))]);
}

/// Bring a profile's storage in line with its configuration.
pub fn apply(config: &GraftConfig) {
    lock::with_state_lock(|| {
        let profile = config.profile();
        let _ = std::fs::create_dir_all(&profile);
        match config.source() {
            Some(source) => graft(&source, &profile),
            None => ungraft(&profile),
        }
    });
}
