//! Mirrored chat folders: two real folders kept in step, record by record.

use crate::fsx::{self, exists, is_directory, names, resolved_text};
use graft_core::{diagnostics, files, lock, paths};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::collections::{BTreeMap, BTreeSet};
use std::path::{Path, PathBuf};
use std::sync::RwLock;

/// A stable digest of a record's bytes.
///
/// Stable across processes: the launcher and the app both have to agree on
/// whether a file has moved since the last pass, and they are different
/// processes every time. FNV-1a, as the Mac build writes it, so a state file
/// carried between the two still reads.
pub fn digest(data: &[u8]) -> String {
    let mut hash: u64 = 0xcbf2_9ce4_8422_2325;
    for byte in data {
        hash ^= *byte as u64;
        hash = hash.wrapping_mul(0x100_0000_01b3);
    }
    format!("{hash:x}")
}

/// What both sides of a mirrored pair agreed on last time, so a later pass can
/// tell a record somebody edited from one somebody deleted. Without a baseline
/// the two look identical: a name on one side and not the other.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
pub struct MirrorState {
    /// Pair of folders, then record name, then the digest both held.
    #[serde(default)]
    pub pairs: BTreeMap<String, BTreeMap<String, String>>,
}

pub fn mirror_state_file() -> PathBuf {
    paths::graft_file("mirrored-chats.json")
}

pub fn load_mirror_state() -> MirrorState {
    std::fs::read(mirror_state_file()).ok().and_then(|b| serde_json::from_slice(&b).ok()).unwrap_or_default()
}

type PairsSaved = fn(&BTreeSet<String>);

static ON_PAIRS_SAVED: RwLock<Option<PairsSaved>> = RwLock::new(None);

/// Told the pairs every time the mirror state is saved. The sidebar sync keeps
/// its own state per pair, and a pair dropped here has to go there too or the
/// next sync squares up a graft that no longer exists; this crate cannot
/// depend on that one, so it registers here instead.
pub fn set_on_pairs_saved(hook: Option<PairsSaved>) {
    *ON_PAIRS_SAVED.write().unwrap_or_else(|e| e.into_inner()) = hook;
}

pub fn save_mirror_state(state: &MirrorState) {
    let Ok(bytes) = serde_json::to_vec(state) else { return };
    if files::atomic_write(&mirror_state_file(), &bytes, 0o644).is_ok() {
        if let Some(hook) = *ON_PAIRS_SAVED.read().unwrap_or_else(|e| e.into_inner()) {
            hook(&state.pairs.keys().cloned().collect());
        }
    }
}

/// A byte no path can contain, so the two halves of a key always come back
/// apart the way they went together.
pub const PAIR_SEPARATOR: &str = "\u{0}";

pub fn pair_key(one: &Path, other: &Path) -> String {
    format!("{}{}{}", resolved_text(one), PAIR_SEPARATOR, resolved_text(other))
}

/// The two halves of a key, in the roles they were written in: borrower, then
/// source.
///
/// A pair is always written with the borrowing folder first, because
/// `mirror_chat_folders` is only ever called with the profile's own folder
/// ahead of the one it is borrowing from. That order is the only record of
/// which side is which: after a merge both folders hold the same bytes, and
/// nothing on disk tells a lender from a borrower.
pub fn pair_halves(key: &str) -> Option<(String, String)> {
    let halves: Vec<&str> = key.split(PAIR_SEPARATOR).collect();
    if halves.len() != 2 || !halves.iter().all(|h| h.starts_with('/')) {
        return None;
    }
    Some((halves[0].to_string(), halves[1].to_string()))
}

/// The two folders a key names, or `None` for anything that is not one.
pub fn pair_folders(key: &str) -> Option<(PathBuf, PathBuf)> {
    pair_halves(key).map(|(a, b)| (PathBuf::from(a), PathBuf::from(b)))
}

/// Every pair with a half at or inside this folder, whichever half it is. For
/// the cases where both roles are equally finished: a profile deleted, a
/// shortcut deleted. Undoing a graft is not one of them.
pub fn mirror_pairs_under(folder: &Path) -> Vec<String> {
    mirror_pairs_matching(folder, false)
}

/// Every pair this folder borrows through: the half its own Claude reads and
/// writes, never the half it is lending to somebody else.
///
/// Which is also the question "has mirroring been set up here before", asked
/// of the state file rather than of the stash, because a profile that had
/// nothing to put away leaves no stash behind and would otherwise look new for
/// ever, and looking new for ever means the pass after the first one stashes
/// away everything the first one mirrored in.
///
/// Asked of both halves instead, opening the profile others borrow from ran
/// that profile's own `ungraft` over their pairs: it took its folder to be the
/// borrowed one, and since a lender has no stash to say what it owns, every
/// record the merge had put there was removed. Both sidebars emptied, on every
/// launch, for as long as the shortcut existed.
pub fn mirror_pairs_borrowed_by(folder: &Path) -> Vec<String> {
    mirror_pairs_matching(folder, true)
}

fn mirror_pairs_matching(folder: &Path, borrowing_half_only: bool) -> Vec<String> {
    let path = resolved_text(folder);
    let inside = format!("{path}/");
    let mut keys: Vec<String> = load_mirror_state()
        .pairs
        .keys()
        .filter(|key| {
            let Some((borrower, source)) = pair_halves(key) else { return false };
            let sides = if borrowing_half_only { vec![borrower] } else { vec![borrower, source] };
            sides.iter().any(|s| *s == path || s.starts_with(&inside))
        })
        .cloned()
        .collect();
    keys.sort();
    keys
}

/// What one pass makes of one name held by two mirrored folders.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MirrorAction {
    Nothing,
    /// One side holds the newer copy; the other gets it.
    CopyToOther,
    CopyToOne,
    /// It was there last time and is gone from the other side now, which is a
    /// deletion to carry across rather than a copy to undo.
    RemoveFromOne,
    RemoveFromOther,
    /// Both sides moved since the last pass. Nothing here can say which is
    /// wanted, so the caller settles it on what the records say about
    /// themselves.
    Conflict,
}

/// Which way one record moves.
///
/// The baseline is what makes a missing file mean something. A name on one
/// side and not the other is either a record just written or a record just
/// deleted, and the two are the same on disk; the difference is only whether
/// the last pass saw it on both. Getting this backwards deletes a new chat or
/// resurrects a deleted one, so it is a function with no filesystem in it and
/// the suite drives every branch.
pub fn mirror_decision(one: Option<&str>, other: Option<&str>, baseline: Option<&str>) -> MirrorAction {
    if one == other {
        return MirrorAction::Nothing;
    }
    match (one, other) {
        (Some(one), None) => match baseline {
            None => MirrorAction::CopyToOther,
            Some(b) if b == one => MirrorAction::RemoveFromOne,
            Some(_) => MirrorAction::Conflict,
        },
        (None, Some(other)) => match baseline {
            None => MirrorAction::CopyToOne,
            Some(b) if b == other => MirrorAction::RemoveFromOther,
            Some(_) => MirrorAction::Conflict,
        },
        _ => {
            if one == baseline {
                MirrorAction::CopyToOne
            } else if other == baseline {
                MirrorAction::CopyToOther
            } else {
                MirrorAction::Conflict
            }
        }
    }
}

/// Bring every pair this app has ever mirrored back into line.
///
/// A shortcut being opened brings its own pair up to date on the way past,
/// which covers the profile doing the borrowing and nobody else. The source is
/// the case that matters and the case that misses: archive a chat in the
/// borrowing profile, open the source, and the source is not the one running
/// `apply`, so without this the change sits there and the sidebar it was meant
/// for never sees it.
///
/// The pairs are the keys of the state file, so a launcher that knows only its
/// own profile can still put everything in step.
pub fn mirror_known_pairs() -> usize {
    let mut moved = 0;
    let keys: Vec<String> = load_mirror_state().pairs.keys().cloned().collect();
    for key in keys {
        let Some((one, other)) = pair_folders(&key) else { continue };
        moved += mirror_chat_folders(&one, &other);
    }
    moved
}

/// The names a mirror pass is responsible for. A sidebar is built out of
/// records and the markers that say which have been deleted; everything else
/// in an organization folder belongs to the profile that wrote it and is left
/// where it is.
pub fn is_mirrored(name: &str) -> bool {
    (name.starts_with("local_") && name.ends_with(".json")) || name.starts_with("deleted_")
}

/// When a record says it last moved, for settling a name both sides have
/// rewritten since the last pass. The file's own timestamp is the fallback,
/// since a marker has no inside to read.
fn record_moment(path: &Path) -> f64 {
    let inside = std::fs::read(path)
        .ok()
        .and_then(|b| serde_json::from_slice::<Value>(&b).ok())
        .and_then(|v| v.get("lastActivityAt").and_then(Value::as_f64));
    inside.unwrap_or_else(|| fsx::seconds(fsx::modified(path)) * 1000.0)
}

/// Bring two organization folders into line, and remember what they agreed on
/// so the next pass can read an absence.
///
/// A folder that could not be read is not a folder with nothing in it, the
/// same rule the session sweep learned, and here the cost of getting it wrong
/// is worse than a duplicate: every record on the other side would look
/// deleted. So a walk that fails on either side does nothing at all rather
/// than half a sync.
pub fn mirror_chat_folders(one: &Path, other: &Path) -> usize {
    if paths::same_path(one, other) {
        return 0;
    }
    lock::with_state_lock(|| mirror_locked(one, other))
}

fn mirror_locked(one: &Path, other: &Path) -> usize {
    for folder in [one, other] {
        if exists(folder) {
            continue;
        }
        // Only the folder itself, never the path to it. A profile someone
        // deleted has no account directory left, and a pass whose whole job is
        // keeping two sidebars in step must not build a deleted profile back up
        // around a folder it wanted to write into. Setting a graft up is
        // `mirror_chat_stores`, and it makes the path there where it knows the
        // profile is real.
        if !is_directory(&fsx::parent(folder)) {
            return 0;
        }
        let _ = std::fs::create_dir(folder);
    }
    let (Some(one_names), Some(other_names)) = (names(one), names(other)) else {
        diagnostics::note(
            "mirror.unread",
            &[("one", json!(paths::text(one))), ("other", json!(paths::text(other)))],
        );
        return 0;
    };

    let mut state = load_mirror_state();
    let key = pair_key(one, other);
    let mut baseline = state.pairs.get(&key).cloned().unwrap_or_default();
    let mut moved = 0;

    let here_set: BTreeSet<String> = one_names.into_iter().filter(|n| is_mirrored(n)).collect();
    let there_set: BTreeSet<String> = other_names.into_iter().filter(|n| is_mirrored(n)).collect();

    // A side holding none of the names the baseline describes, while the other
    // still holds them, has not had its sidebar cleared one chat at a time. It
    // has been stashed, replaced, or emptied wholesale, and carrying that
    // across as deletions takes the other profile's history with it. Seen for
    // real: a sign-in moved where a mirror thought its own folder was, the live
    // set was stashed as though it were the profile's own, and the next pass
    // read 150 absences as 150 deletions and removed every one of them from the
    // profile they had been borrowed from. Forgetting the baseline instead makes
    // the survivors look new, so the emptied side is filled again rather than
    // the full one emptied.
    if !baseline.is_empty() {
        let here_has_none = here_set.iter().all(|n| !baseline.contains_key(n));
        let there_has_none = there_set.iter().all(|n| !baseline.contains_key(n));
        if here_has_none != there_has_none {
            baseline.clear();
        }
    }

    let read = |folder: &Path, name: &str| -> Option<(Vec<u8>, String)> {
        let data = std::fs::read(folder.join(name)).ok()?;
        let d = digest(&data);
        Some((data, d))
    };
    let put = |data: &[u8], folder: &Path, name: &str| -> bool {
        files::atomic_write(&folder.join(name), data, 0o644).is_ok()
    };
    // A removal that did not happen must not be written down as one. A removal
    // the filesystem refused left a name on one side, absent on the other, with
    // no baseline, which is a new chat, and the next pass copied the deleted
    // conversation straight back to the side it was deleted from.
    let drop = |folder: &Path, name: &str| -> bool { fsx::remove_item(&folder.join(name)) };

    let all: BTreeSet<String> = here_set.union(&there_set).cloned().collect();
    for name in &all {
        let here = read(one, name);
        let there = read(other, name);
        let mut action = mirror_decision(
            here.as_ref().map(|h| h.1.as_str()),
            there.as_ref().map(|t| t.1.as_str()),
            baseline.get(name).map(String::as_str),
        );
        // Both sides moved. What the records say about themselves is the only
        // thing left to go on, and a record that still exists beats one that
        // was deleted: a chat put back is a nuisance, a chat taken away for
        // good is not.
        if action == MirrorAction::Conflict {
            action = match (&here, &there) {
                (None, Some(_)) => MirrorAction::CopyToOne,
                (Some(_), None) => MirrorAction::CopyToOther,
                (Some(_), Some(_)) => {
                    if record_moment(&one.join(name)) >= record_moment(&other.join(name)) {
                        MirrorAction::CopyToOther
                    } else {
                        MirrorAction::CopyToOne
                    }
                }
                (None, None) => MirrorAction::Nothing,
            };
        }

        match action {
            MirrorAction::Nothing => {
                // Where a name held identically by both sides first earns a
                // baseline. `mirror_decision` answers this only when the two
                // digests are equal, so either side names it.
                if let Some((_, d)) = &here {
                    baseline.insert(name.clone(), d.clone());
                }
            }
            MirrorAction::CopyToOther | MirrorAction::CopyToOne => {
                let (source, destination) =
                    if action == MirrorAction::CopyToOther { (&here, other) } else { (&there, one) };
                let Some((data, d)) = source else { continue };
                if !put(data, destination, name) {
                    continue;
                }
                baseline.insert(name.clone(), d.clone());
                moved += 1;
            }
            MirrorAction::RemoveFromOne | MirrorAction::RemoveFromOther => {
                let folder = if action == MirrorAction::RemoveFromOne { one } else { other };
                if !drop(folder, name) {
                    continue;
                }
                baseline.remove(name);
                moved += 1;
            }
            MirrorAction::Conflict => {}
        }
    }

    // A name neither side holds any more is never visited by the loop above,
    // so its entry sat in the baseline for good, growing the state file, and
    // standing ready to read the name coming back on one side alone as a
    // deletion to carry across.
    baseline.retain(|name, _| all.contains(name));
    state.pairs.insert(key, baseline);
    save_mirror_state(&state);
    moved
}

/// Stop mirroring anything belonging to a profile, without moving a file.
///
/// The pairs outlive the graft that made them: they are the keys of a state
/// file every launcher reads, so a shortcut sent back to its own chats would
/// go on having its folder squared up against a source it no longer borrows
/// from, by whichever profile happened to open next. A profile that has been
/// deleted is the other way in: there is nothing left to settle, only a pair
/// to drop.
pub fn forget_mirrors(profile: &Path) {
    let stale: BTreeSet<String> = mirror_pairs_under(profile).into_iter().collect();
    if stale.is_empty() {
        return;
    }
    diagnostics::note(
        "mirror.forget",
        &[("profile", json!(fsx::file_name(profile))), ("pairs", json!(stale.len()))],
    );
    drop_pairs(&stale);
}

pub(crate) fn drop_pairs(stale: &BTreeSet<String>) {
    lock::with_state_lock(|| {
        let mut state = load_mirror_state();
        state.pairs.retain(|key, _| !stale.contains(key));
        save_mirror_state(&state);
    });
}

/// Drop the pairs this store borrows through that name a folder nobody writes
/// to any more.
///
/// Where a profile's chats live is `<account>/<org>`, and signing in again can
/// move both, on either side. The pair an earlier pass wrote down then names a
/// folder nobody reads, while `mirror_known_pairs` goes on squaring it up from
/// every launcher: a folder nobody writes to is one that looks emptied, which
/// is carried to the other profile as a deletion. The pairs this pass is
/// actually mirroring are the ones left alone, which is why it is given them
/// whole rather than a list of folders to find a half in.
///
/// A set rather than one pair, because two profiles on one account mirror
/// every organization the account has and no single one of them is the pair
/// worth keeping. Borrowed pairs alone: what this store lends to somebody else
/// is that profile's mirror, and dropping it here reads as a first pass over
/// there.
pub fn forget_stale_pairs(store: &Path, keeping: &[(PathBuf, PathBuf)]) {
    let kept: BTreeSet<String> = keeping.iter().map(|(mine, theirs)| pair_key(mine, theirs)).collect();
    let stale: BTreeSet<String> =
        mirror_pairs_borrowed_by(store).into_iter().filter(|k| !kept.contains(k)).collect();
    if stale.is_empty() {
        return;
    }
    let keeping_text: Vec<String> = kept
        .iter()
        .map(|k| pair_halves(k).map(|(b, s)| format!("{b} -> {s}")).unwrap_or_else(|| k.clone()))
        .collect();
    diagnostics::note(
        "mirror.stale",
        &[
            ("store", json!(paths::text(store))),
            ("keeping", json!(keeping_text)),
            ("dropped", json!(stale.len())),
            ("because", json!("these name a folder one side has stopped writing to")),
        ],
    );
    drop_pairs(&stale);
}
