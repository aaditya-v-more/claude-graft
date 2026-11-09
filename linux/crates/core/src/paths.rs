//! Where everything lives on a Linux desktop.
//!
//! Claude Desktop on Linux keeps its profile in `~/.config/Claude`, the place
//! Electron puts `app.getPath("userData")` for an app named Claude, so
//! `~/.config` plays the part Application Support plays on a Mac: Claude's own
//! profile, every grafted one and Graft's own state folder sit side by side in
//! it.
//!
//! The host's folders are named from `$HOME`, never from `$XDG_CONFIG_HOME`.
//! Inside a Flatpak that variable points at the sandbox's private
//! `~/.var/app/<id>/config`, which is not where Claude is, and a Graft that
//! read it would see no profiles at all and create new ones nobody can open.

use std::env;
use std::path::{Component, Path, PathBuf};
use std::sync::RwLock;

static CONFIG_OVERRIDE: RwLock<Option<PathBuf>> = RwLock::new(None);
static DATA_OVERRIDE: RwLock<Option<PathBuf>> = RwLock::new(None);
static HOME_OVERRIDE: RwLock<Option<PathBuf>> = RwLock::new(None);

/// Redirects every profile and state path, so the suite can never reach a
/// real profile. `GRAFT_CONFIG_HOME` does the same from outside a process,
/// for the scripts that run the binaries against a sandbox.
pub fn set_config_home_override(path: Option<PathBuf>) {
    *CONFIG_OVERRIDE.write().unwrap() = path;
}

/// Redirects the folders shortcuts and their icons are written into.
pub fn set_data_home_override(path: Option<PathBuf>) {
    *DATA_OVERRIDE.write().unwrap() = path;
}

/// Redirects the home folder itself, for the files Claude Code keeps there.
pub fn set_home_override(path: Option<PathBuf>) {
    *HOME_OVERRIDE.write().unwrap() = path;
}

pub fn home() -> PathBuf {
    if let Some(path) = HOME_OVERRIDE.read().unwrap().clone() {
        return path;
    }
    if let Some(path) = env::var_os("GRAFT_HOME") {
        return PathBuf::from(path);
    }
    env::var_os("HOME").map(PathBuf::from).unwrap_or_else(|| PathBuf::from("/"))
}

/// The host's `~/.config`: Claude's profile, every grafted one, Graft's state.
pub fn config_home() -> PathBuf {
    if let Some(path) = CONFIG_OVERRIDE.read().unwrap().clone() {
        return path;
    }
    if let Some(path) = env::var_os("GRAFT_CONFIG_HOME") {
        return PathBuf::from(path);
    }
    home().join(".config")
}

/// The host's `~/.local/share`, where launchers find `.desktop` files and icons.
pub fn data_home() -> PathBuf {
    if let Some(path) = DATA_OVERRIDE.read().unwrap().clone() {
        return path;
    }
    if let Some(path) = env::var_os("GRAFT_DATA_HOME") {
        return PathBuf::from(path);
    }
    home().join(".local/share")
}

pub const MAIN_PROFILE_NAME: &str = "Claude";
pub const GRAFT_FOLDER_NAME: &str = "ClaudeGraft";

/// The profile Claude uses when it is started with no `--user-data-dir`.
pub fn main_profile() -> PathBuf {
    config_home().join(MAIN_PROFILE_NAME)
}

/// Where Graft keeps its own state, beside the profiles it describes, so a
/// copy of one is a copy of the other.
pub fn graft_folder() -> PathBuf {
    config_home().join(GRAFT_FOLDER_NAME)
}

pub fn graft_file(name: &str) -> PathBuf {
    graft_folder().join(name)
}

pub fn shortcuts_file() -> PathBuf {
    graft_file("shortcuts.json")
}

pub fn state_lock_file() -> PathBuf {
    graft_file("state.lock")
}

/// Claude Code's own login for the whole machine.
pub fn claude_code_config_file() -> PathBuf {
    home().join(".claude.json")
}

pub fn profile_dir(folder: &str) -> PathBuf {
    config_home().join(folder)
}

/// Two folder names the disk would take for one.
///
/// Linux disks usually tell case apart, but the reserved names are still
/// compared ignoring it: a profile called `claude` beside `Claude` is a
/// profile nobody can tell from Claude's own in a file manager, and one called
/// `claudegraft` on a case-folding drive would put a profile inside Graft's
/// state, which deleting that profile would then remove.
pub fn is_named(folder: &str, name: &str) -> bool {
    folder.to_lowercase() == name.to_lowercase()
}

/// Why a profile folder name cannot be used, or `None` when it can.
///
/// It has to be one plain component sitting directly in `~/.config`. Anything
/// else could send a graft, or a delete, at somebody else's data.
pub fn validate_folder(folder: &str) -> Option<String> {
    let trimmed = folder.trim();
    if trimmed.is_empty() {
        return Some("The profile folder needs a name.".into());
    }
    if trimmed.contains('/') || trimmed.contains('\0') {
        return Some("The profile folder must be a single folder name, not a path.".into());
    }
    if trimmed == "." || trimmed == ".." || trimmed.starts_with('.') {
        return Some(format!("\u{201c}{trimmed}\u{201d} is not a usable folder name."));
    }
    if is_named(trimmed, MAIN_PROFILE_NAME) {
        return Some("That is Claude's own profile folder. Pick another name.".into());
    }
    if is_named(trimmed, GRAFT_FOLDER_NAME) {
        return Some("That folder belongs to Claude Graft itself. Pick another name.".into());
    }
    None
}

/// A path with `.` and `..` taken out and symlinks followed as far as they
/// exist, so two spellings of one place compare equal.
pub fn normalize(path: &Path) -> PathBuf {
    if let Ok(real) = std::fs::canonicalize(path) {
        return real;
    }
    // Resolve the longest existing ancestor and append the rest lexically,
    // which is what a path that does not exist yet can still be compared by.
    let mut missing: Vec<std::ffi::OsString> = Vec::new();
    let mut cursor = path.to_path_buf();
    loop {
        if let Ok(real) = std::fs::canonicalize(&cursor) {
            let mut out = real;
            for part in missing.iter().rev() {
                out.push(part);
            }
            return lexical(&out);
        }
        match (cursor.file_name().map(|n| n.to_os_string()), cursor.parent()) {
            (Some(name), Some(parent)) => {
                missing.push(name);
                cursor = parent.to_path_buf();
            }
            _ => return lexical(path),
        }
    }
}

fn lexical(path: &Path) -> PathBuf {
    let mut out = PathBuf::new();
    for part in path.components() {
        match part {
            Component::CurDir => {}
            Component::ParentDir => {
                out.pop();
            }
            other => out.push(other.as_os_str()),
        }
    }
    out
}

pub fn same_path(a: &Path, b: &Path) -> bool {
    normalize(a) == normalize(b)
}

pub fn is_main_profile(profile: &Path) -> bool {
    same_path(profile, &main_profile())
}

/// Text form of a path for JSON files and command lines. Paths here come from
/// folder names a person typed, which are UTF-8 on every desktop that matters;
/// a lossy conversion is what a file manager would show anyway.
pub fn text(path: &Path) -> String {
    path.to_string_lossy().into_owned()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reserved_names_are_refused_in_any_case() {
        assert!(validate_folder("claude").is_some(), "Claude's own folder in lower case is refused");
        assert!(validate_folder("CLAUDEGRAFT").is_some(), "Graft's folder in capitals is refused");
        assert!(validate_folder("../x").is_some(), "a path is not a folder name");
        assert!(validate_folder(".hidden").is_some(), "a hidden folder is refused");
        assert!(validate_folder("   ").is_some(), "an empty name is refused");
        assert!(validate_folder("Claude-2").is_none(), "an ordinary name is accepted");
    }

    #[test]
    fn a_missing_path_still_compares_by_its_parent() {
        let dir = tempfile::tempdir().unwrap();
        let a = dir.path().join("x/../y");
        let b = dir.path().join("y");
        assert!(same_path(&a, &b), "two spellings of a folder not made yet are one folder");
    }
}
