//! Chats an account switch left in another profile, and bringing them in.

use crate::fsx::{self, counterpart_directory, exists, is_directory, is_symlink, names_or_empty, resolved_text};
use crate::mirror::is_mirrored;
use crate::{running, CHAT_STORES, STASH_SUFFIX};
use graft_core::{diagnostics, files, paths};
use serde_json::{json, Value};
use std::collections::HashSet;
use std::path::{Path, PathBuf};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

/// One of the chats on offer. A count names a quantity; a title and a date
/// name a conversation, which is the only thing anybody recognises their own
/// history by.
#[derive(Debug, Clone, PartialEq)]
pub struct ElsewhereChat {
    pub title: String,
    pub last_active: SystemTime,
}

/// Chat records for this profile's account that another profile is holding
/// and this one has not got.
///
/// A store is keyed `<account>/<org>` and an instance reads only the folder for
/// the account it currently holds, so signing out of one account and into
/// another leaves the first one's history exactly where it was written.
/// Somebody who used to switch accounts inside a single Claude therefore has
/// both histories in that one profile, and the shortcut they make for the
/// second account comes up with an empty sidebar while its own chats sit in the
/// profile next door.
///
/// Nothing is broken and nothing is lost, which is precisely why it is worth
/// saying out loud: an empty sidebar is what a history that has gone looks like
/// too, and the first thing anybody does about it is reach for the chat source
/// picker, which merges two accounts' chats rather than recovering one
/// account's, and is the single thing this app cannot take back.
#[derive(Debug, Clone, PartialEq)]
pub struct ChatsElsewhere {
    /// The profile holding them.
    pub profile: PathBuf,
    /// The account, spelled the way the profile asking about it spells it.
    pub account: String,
    /// How many of them this profile has not got. Only ever the missing ones:
    /// copying takes nothing out of the profile they came from, so an offer
    /// counting everything over there would stand for ever afterwards, and the
    /// number it showed would never match the number that arrived.
    pub count: usize,
    /// Whether this profile has chats of its own under that account, which is
    /// the difference between recovering one history and merging two.
    pub merging: bool,
    /// A few of the newest, newest first.
    pub chats: Vec<ElsewhereChat>,
}

/// Whichever profile holds the most chats this one has not got, or `None` when
/// there is nothing to offer.
///
/// Offered to a profile that already has a history of its own as well as to an
/// empty one. The two are not the same question, recovering an account's chats
/// against merging a second set in, but they have the same answer, and a
/// profile that has been used for a week is still missing everything written
/// before it existed.
pub fn chats_elsewhere(profile: &Path, others: &[PathBuf]) -> Option<ChatsElsewhere> {
    // Unreadable is not signed out. A config caught mid-rename reads as "no
    // account", and every fallback below that point would be an offer to copy
    // somebody else's history into this profile.
    let config = files::readable_config_json(profile)?;
    let account = config.get("lastKnownAccountUuid").and_then(Value::as_str)?.to_string();

    // A grafted profile's own folder has been linked or moved aside, so what it
    // is holding is not its own and what it appears to be missing is already
    // coming in from its source.
    for store in CHAT_STORES {
        let dir = profile.join(store);
        if is_symlink(&dir) || fsx::is_stashed_away(&dir) {
            return None;
        }
    }

    let own = record_files(profile, &account);
    let here: HashSet<&str> = own.iter().map(|(k, _)| k.as_str()).collect();

    let mut best: Option<ChatsElsewhere> = None;
    for other in others {
        if paths::same_path(other, profile) {
            continue;
        }
        let missing: Vec<(String, PathBuf)> =
            record_files(other, &account).into_iter().filter(|(k, _)| !here.contains(k.as_str())).collect();
        if missing.is_empty() || missing.len() <= best.as_ref().map(|b| b.count).unwrap_or(0) {
            continue;
        }
        let files: Vec<PathBuf> = missing.iter().map(|(_, p)| p.clone()).collect();
        best = Some(ChatsElsewhere {
            profile: other.clone(),
            account: account.clone(),
            count: missing.len(),
            merging: !own.is_empty(),
            chats: newest_chats(&files, 5),
        });
    }
    best
}

/// Every chat record one profile holds under one account, across both stores
/// and each organization in them.
///
/// Keyed by store and file name rather than by name alone, since the two stores
/// hold different conversations and a name in one says nothing about the
/// other.
fn record_files(profile: &Path, account: &str) -> Vec<(String, PathBuf)> {
    let mut found = Vec::new();
    for store in CHAT_STORES {
        let dir = profile.join(store);
        let Some(account_dir) = counterpart_directory(&dir, account) else { continue };
        let under = dir.join(account_dir);
        for org in names_or_empty(&under) {
            if org.starts_with('.') || org.ends_with(STASH_SUFFIX) {
                continue;
            }
            let org_dir = under.join(&org);
            // `<org>.profile-origin.json` is a plain file sitting beside the
            // organization folders, and reading it as one of them is a mistake
            // this app has made before.
            if !is_directory(&org_dir) {
                continue;
            }
            for name in names_or_empty(&org_dir) {
                if name.starts_with("local_") && name.ends_with(".json") {
                    found.push((format!("{store}/{name}"), org_dir.join(&name)));
                }
            }
        }
    }
    found
}

/// The newest few, newest first, each with the moment it was last active.
///
/// Narrowed by the file's own timestamp and then ordered by the moment written
/// inside it. Sorting a long history properly means opening every record in
/// it, and this runs on a window's refresh; the filesystem knows enough to pick
/// a shortlist, and only what will be shown is read.
fn newest_chats(files: &[PathBuf], limit: usize) -> Vec<ElsewhereChat> {
    let mut by_time: Vec<(PathBuf, SystemTime)> = files.iter().map(|f| (f.clone(), fsx::modified(f))).collect();
    by_time.sort_by(|a, b| b.1.cmp(&a.1));
    let mut chats: Vec<ElsewhereChat> = by_time
        .into_iter()
        .take(limit * 4)
        .filter_map(|(path, when)| {
            let record: Value = serde_json::from_slice(&std::fs::read(&path).ok()?).ok()?;
            let title = record.get("title")?.as_str()?.to_string();
            if title.is_empty() {
                return None;
            }
            // Milliseconds, the way a record stamps everything else. The file's
            // own timestamp stands in for one written without it.
            let active = record
                .get("lastActivityAt")
                .and_then(Value::as_f64)
                .filter(|ms| *ms >= 0.0)
                .map(|ms| UNIX_EPOCH + Duration::from_secs_f64(ms / 1000.0))
                .unwrap_or(when);
            Some(ElsewhereChat { title, last_active: active })
        })
        .collect();
    chats.sort_by(|a, b| b.last_active.cmp(&a.last_active));
    chats.truncate(limit);
    chats
}

/// What one attempt to bring chats across did.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct Adoption {
    /// Chats copied. The markers saying which have been deleted travel with
    /// them and are counted separately: somebody offered four chats and told
    /// five arrived reads that as a bug, and a sidebar shows records.
    pub copied: usize,
    /// The profiles with a Claude on them when this was asked. Nothing is
    /// copied while any of them is up, so anything here means nothing
    /// happened.
    pub running: Vec<PathBuf>,
}

/// Copy another profile's records for one account into this profile, and take
/// nothing away.
///
/// Copied rather than moved. The profile they come from is one this app did
/// not make, `Claude` itself in the case this exists for, and the rule that has
/// kept every incident in this project from being worse is that nothing here
/// removes a record from a profile it does not own. So a wrong guess costs some
/// small JSON on disk instead of a history, and the folder they came from is
/// left exactly as it was for the day that account is signed back into over
/// there.
///
/// Additive on this side too: a name already here is left alone rather than
/// written over, so a second run copies nothing and a record this profile has
/// since archived or renamed keeps the shape it gave it.
pub fn adopt_chats(source: &Path, profile: &Path, account: &str) -> Adoption {
    if paths::same_path(source, profile) {
        return Adoption::default();
    }

    // Every Claude on the machine, not merely the two either side of the copy.
    // An instance builds its sidebar as it starts and writes records back in
    // its own shape as it runs, so records landing underneath one are at best
    // invisible until it is restarted and at worst about to be written over;
    // and any of them may be sharing these very folders through a graft.
    // Waiting for a quiet machine costs one restart and is the only version of
    // this that cannot go wrong.
    let running = running::running_claudes();
    if !running.is_empty() {
        diagnostics::note(
            "adopt.blocked",
            &[
                ("into", json!(fsx::file_name(profile))),
                ("running", json!(running.iter().map(|p| fsx::file_name(p)).collect::<Vec<_>>())),
                ("because", json!("records landing under a running Claude are ones it may write over")),
            ],
        );
        return Adoption { copied: 0, running };
    }

    let root = format!("{}/", resolved_text(profile));
    let (mut copied, mut markers) = (0, 0);

    for store in CHAT_STORES {
        let src = source.join(store);
        let dst = profile.join(store);
        let Some(source_account_dir) = counterpart_directory(&src, account) else { continue };
        let from = src.join(source_account_dir);
        let into = dst.join(counterpart_directory(&dst, account).unwrap_or_else(|| account.to_string()));

        // The rule the rest of this app is built on. A folder that resolves
        // outside the profile is one a graft has linked or stashed away, and
        // filling it files these records into the store next door rather than
        // into this one.
        if fsx::is_stashed_away(&into) || !resolved_text(&into).starts_with(&root) {
            diagnostics::note(
                "adopt.skipped",
                &[
                    ("profile", json!(paths::text(profile))),
                    ("store", json!(store)),
                    ("because", json!("this profile's folder for the account is not its own")),
                ],
            );
            continue;
        }

        for org in names_or_empty(&from) {
            if org.starts_with('.') || org.ends_with(STASH_SUFFIX) {
                continue;
            }
            let org_from = from.join(&org);
            if !is_directory(&org_from) {
                continue;
            }
            let org_into = into.join(counterpart_directory(&into, &org).unwrap_or_else(|| org.clone()));
            let _ = std::fs::create_dir_all(&org_into);
            for name in names_or_empty(&org_from).into_iter().filter(|n| is_mirrored(n)) {
                let to = org_into.join(&name);
                if exists(&to) || !fsx::copy_item(&org_from.join(&name), &to) {
                    continue;
                }
                if name.starts_with("local_") {
                    copied += 1;
                } else {
                    markers += 1;
                }
            }
        }
    }

    diagnostics::note(
        "adopt",
        &[
            ("from", json!(fsx::file_name(source))),
            ("into", json!(fsx::file_name(profile))),
            ("account", json!(account)),
            ("copied", json!(copied)),
            ("markers", json!(markers)),
        ],
    );
    Adoption { copied, running: Vec::new() }
}
