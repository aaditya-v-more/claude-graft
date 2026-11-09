//! The filesystem moves every graft is made of: putting a profile's own things
//! aside, folding them back, and linking.

use crate::STASH_SUFFIX;
use graft_core::diagnostics;
use graft_core::files;
use graft_core::paths;
use serde_json::json;
use std::fs;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

/// `<store>/<account>/<org>` is three components, which is how far above an
/// organization folder this app can have put something away.
pub const CHAT_STORE_DEPTH: usize = 3;

pub fn is_symlink(path: &Path) -> bool {
    files::is_symlink(path)
}

pub fn exists(path: &Path) -> bool {
    files::exists(path)
}

pub fn is_directory(path: &Path) -> bool {
    files::is_directory(path)
}

/// Every name in a folder, hidden ones included, sorted so a walk visits them
/// in the same order every time. `None` when the folder could not be read,
/// which is a different answer from an empty one.
pub fn names(dir: &Path) -> Option<Vec<String>> {
    let entries = fs::read_dir(dir).ok()?;
    let mut out: Vec<String> = entries
        .filter_map(|e| e.ok())
        .map(|e| e.file_name().to_string_lossy().into_owned())
        .collect();
    out.sort();
    Some(out)
}

/// The same, read as empty when it cannot be read, for the walks where an
/// unreadable folder and an empty one lead to the same nothing.
pub fn names_or_empty(dir: &Path) -> Vec<String> {
    names(dir).unwrap_or_default()
}

/// Where a path really is: links followed as far as they exist and the rest
/// taken lexically, so a record filed through a link and one filed beside it
/// name the same folder.
pub fn resolve(path: &Path) -> PathBuf {
    paths::normalize(path)
}

pub fn resolved_text(path: &Path) -> String {
    paths::text(&resolve(path))
}

/// A path with `.` and `..` taken out and no link followed.
pub fn standardized(path: &Path) -> PathBuf {
    let mut out = PathBuf::new();
    for part in path.components() {
        match part {
            std::path::Component::CurDir => {}
            std::path::Component::ParentDir => {
                out.pop();
            }
            other => out.push(other.as_os_str()),
        }
    }
    out
}

pub fn file_name(path: &Path) -> String {
    path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default()
}

pub fn parent(path: &Path) -> PathBuf {
    path.parent().map(Path::to_path_buf).unwrap_or_else(|| PathBuf::from("/"))
}

/// When a file last changed, from one `lstat`; the start of time for one that
/// cannot be read, so it sorts as the oldest.
pub fn modified(path: &Path) -> SystemTime {
    files::modification_time(path).unwrap_or(UNIX_EPOCH)
}

pub fn seconds(time: SystemTime) -> f64 {
    match time.duration_since(UNIX_EPOCH) {
        Ok(d) => d.as_secs_f64(),
        Err(e) => -e.duration().as_secs_f64(),
    }
}

/// Take something away whatever it is: a link as a link, never what it points
/// at, and a folder with everything in it.
pub fn remove_item(path: &Path) -> bool {
    match fs::symlink_metadata(path) {
        Err(_) => false,
        Ok(meta) if meta.is_dir() => fs::remove_dir_all(path).is_ok(),
        Ok(_) => fs::remove_file(path).is_ok(),
    }
}

/// A file copied to a name that must not exist yet, so a copy never writes
/// over something already there.
pub fn copy_item(from: &Path, to: &Path) -> bool {
    if exists(to) {
        return false;
    }
    if is_directory(from) {
        return copy_tree(from, to);
    }
    fs::copy(from, to).is_ok()
}

fn copy_tree(from: &Path, to: &Path) -> bool {
    if fs::create_dir(to).is_err() {
        return false;
    }
    let mut ok = true;
    for name in names_or_empty(from) {
        ok &= copy_item(&from.join(&name), &to.join(&name));
    }
    ok
}

pub fn move_item(from: &Path, to: &Path) -> bool {
    !exists(to) && fs::rename(from, to).is_ok()
}

/// Hidden sibling, so the app never mistakes a stashed organization folder
/// for a real one when it scans the store.
pub fn stash_path(path: &Path) -> PathBuf {
    parent(path).join(format!(".{}{}", file_name(path), STASH_SUFFIX))
}

/// Whether this app has stashed a folder, or any folder it sits inside.
///
/// There are two shapes and `stashed_counterpart` has always known both: a
/// cross-account graft stashes the organization folder itself, and two
/// profiles on one account stash the whole store above it, leaving the same
/// folder one level further down. Everything that asked only about the
/// sibling saw a store put away whole as a profile that had never held
/// anything, and filed a whole history into the empty folder standing in its
/// place, unarchived, with the real one orphaned beside it.
pub fn is_stashed_away(folder: &Path) -> bool {
    let mut here = standardized(folder);
    for _ in 0..CHAT_STORE_DEPTH {
        if exists(&stash_path(&here)) {
            return true;
        }
        here = parent(&here);
    }
    false
}

/// How much is in a folder, for the log alone. A count is what makes a stash
/// line worth reading a week later.
pub fn entries(path: &Path) -> usize {
    names_or_empty(path).len()
}

/// Move a profile's own file or folder aside rather than destroying it.
///
/// A stash already sitting beside the link does not mean the item is a
/// redundant copy of the shared one. Claude writes config.json by renaming a
/// temporary file over it, and a rename replaces a symlink with a regular
/// file, so the profile quietly goes back to writing its own copy while the
/// stash still holds the pre-graft state. A chat directory Claude recreates
/// when it cannot follow the link does the same. Deleting in that case threw
/// away every chat written since the graft.
pub fn stash(path: &Path) {
    if !exists(path) || is_symlink(path) {
        return;
    }
    let stashed = stash_path(path);
    if exists(&stashed) {
        absorb(&stashed, path);
    }
    // Anything absorb could not move is still in there and is still this
    // profile's; leave both alone rather than write over one of them.
    if exists(&stashed) {
        diagnostics::note(
            "stash.blocked",
            &[
                ("folder", json!(paths::text(path))),
                ("because", json!("a stash is already there and could not be folded back in")),
            ],
        );
        return;
    }
    diagnostics::note(
        "stash",
        &[("folder", json!(paths::text(path))), ("held", json!(entries(path)))],
    );
    let _ = fs::rename(path, &stashed);
}

/// Fold a stash back into the copy the profile is using now, so that one item
/// holds everything it owns. A name that appears in both is the same chat, or
/// the same file written twice, and the live copy is the version the profile
/// went on using.
pub fn absorb(stashed: &Path, live: &Path) {
    let both_folders = is_directory(stashed)
        && is_directory(live)
        && !is_symlink(stashed)
        && !is_symlink(live);
    if !both_folders {
        // Two files: the profile overwrote the stashed one itself, so the live
        // copy supersedes it. Anything else is a directory against a file,
        // which is not a pair this can reason about either way round, and is
        // left alone rather than guessed at.
        if !is_directory(stashed) && !is_directory(live) {
            remove_item(stashed);
        }
        return;
    }
    for name in names_or_empty(stashed) {
        let from = stashed.join(&name);
        let to = live.join(&name);
        if exists(&to) {
            absorb(&from, &to);
        } else {
            let _ = fs::rename(&from, &to);
        }
    }
    // An empty shell left behind would read as a stash again on the next
    // launch. Anything still in there failed to move and stays.
    if names(stashed).map(|n| n.is_empty()).unwrap_or(false) {
        let _ = fs::remove_dir(stashed);
    }
}

/// Put back what this profile owns: the state it had before the graft, and
/// anything it has written since the link stopped being followed. Bailing out
/// because something already sits at the link is what left stashes orphaned,
/// and an orphaned stash is what armed the next graft to delete.
pub fn unstash(link: &Path) {
    if is_symlink(link) {
        let _ = fs::remove_file(link);
    }
    let stashed = stash_path(link);
    if !exists(&stashed) {
        return;
    }
    let held = entries(&stashed);
    if exists(link) {
        diagnostics::note(
            "unstash.absorb",
            &[
                ("folder", json!(paths::text(link))),
                ("returning", json!(held)),
                ("onto", json!(entries(link))),
            ],
        );
        absorb(&stashed, link);
        return;
    }
    diagnostics::note("unstash", &[("folder", json!(paths::text(link))), ("returning", json!(held))]);
    let _ = fs::rename(&stashed, link);
}

/// Point `link` at `target`, preserving anything already there.
pub fn relink(target: &Path, link: &Path) -> bool {
    if !exists(target) {
        return false;
    }
    // Linking something to itself would stash the real thing away and leave a
    // symlink pointing at its own empty name.
    if paths::same_path(target, link) {
        return false;
    }
    if is_symlink(link) {
        if fs::read_link(link).ok().as_deref() == Some(target) {
            return true;
        }
        let _ = fs::remove_file(link);
    } else if exists(link) {
        stash(link);
    }
    let _ = fs::create_dir_all(parent(link));
    std::os::unix::fs::symlink(target, link).is_ok()
}

/// Most recently touched child directory, used to resolve the active
/// organization inside an account directory.
///
/// A directory, and the test is not decoration. Claude writes
/// `<org>.profile-origin.json` as a plain file beside the organization folders
/// and writes it as the organization is created, so for a moment it is the
/// newest thing there, and handing that name back as an organization stashed
/// the profile's own copy of the file and built a directory where it had been.
pub fn newest_child(dir: &Path) -> Option<String> {
    let mut found: Vec<(String, SystemTime)> = names(dir)?
        .into_iter()
        .filter(|n| !n.starts_with('.') && !n.ends_with(STASH_SUFFIX))
        .filter(|n| is_directory(&dir.join(n)))
        .map(|n| {
            let when = modified(&dir.join(&n));
            (n, when)
        })
        .collect();
    // Newest first, and by name among equals, so two passes over the same
    // folder always agree.
    found.sort_by(|a, b| b.1.cmp(&a.1).then_with(|| a.0.cmp(&b.0)));
    found.into_iter().next().map(|(n, _)| n)
}

/// The directory `parent` keeps `name`'s contents in.
///
/// `<accountUuid>/<orgUuid>` is the shape, except where it is not. A profile
/// in local mode was measured naming both halves by their first eight
/// characters, `local-agent-mode-sessions/ed417e0f/00000000` against
/// `claude-code-sessions/ed417e0f-5edd-.../00000000-0000-...`, the same
/// account and the same organization in the same profile. Asking such a store
/// for the full uuid finds nothing at all, and a graft that read that as an
/// empty store put the borrowing profile's whole history away.
///
/// A shortened name is taken only when one of the two is a prefix of the
/// other, the shorter is at least eight characters, and it is the only
/// directory there that fits. Nothing here may put one account's chats into
/// another's folder, which is the mistake `readable_config_json` exists to
/// prevent; ambiguity gives back `None` and the caller falls back to the name
/// it was given.
pub fn counterpart_directory(parent_dir: &Path, name: &str) -> Option<String> {
    if is_directory(&parent_dir.join(name)) {
        return Some(name.to_string());
    }
    let candidates: Vec<String> = names_or_empty(parent_dir)
        .into_iter()
        .filter(|n| !n.starts_with('.') && !n.ends_with(STASH_SUFFIX))
        .filter(|n| {
            n.chars().count().min(name.chars().count()) >= 8
                && (n.starts_with(name) || name.starts_with(n.as_str()))
        })
        .filter(|n| is_directory(&parent_dir.join(n)))
        .collect();
    if candidates.len() == 1 {
        candidates.into_iter().next()
    } else {
        None
    }
}
