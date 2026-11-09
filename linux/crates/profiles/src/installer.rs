//! Writes, finds, repairs and removes the `.desktop` file a shortcut becomes.
//!
//! A shortcut is three files: its entry in `~/.local/share/applications`, the
//! `graft.json` the entry names under `~/.local/share/claude-graft`, and the
//! icon the entry shows. The entry carries no copy of the launcher, as a Mac
//! bundle does; its `Exec` runs the `graft-launch` installed with Graft, so
//! what a shortcut runs is always this Graft's launcher and only the way it is
//! reached can go stale.

use crate::desktop::{self, DesktopEntry};
use crate::icon::{self, IconError};
use crate::sharing::Sharing;
use graft_core::{claude, diagnostics, files, host, paths, GraftConfig, Shortcut};
use std::fmt;
use std::path::{Path, PathBuf};
use std::sync::RwLock;
use uuid::Uuid;

/// The key that makes an entry Graft's. Entries are recognised by it and by
/// nothing else, never by their name or file name: an earlier Mac version
/// matched on name and deleted Claude itself.
pub const CONFIG_KEY: &str = "X-Claude-Graft-Config";
/// The version of Graft that wrote the entry, which is what says whether the
/// way it reaches the launcher is the current one.
pub const VERSION_KEY: &str = "X-Claude-Graft-Version";

/// Names that belong to Claude itself. A launcher called either would sit in
/// the app grid indistinguishable from the real one.
pub const RESERVED_NAMES: [&str; 2] = ["Claude", "Claude Graft"];

static LAUNCHER_OVERRIDE: RwLock<Option<PathBuf>> = RwLock::new(None);

/// Name the `graft-launch` an entry runs. For the suite, which has none
/// beside its test binary, and for a development build kept elsewhere.
pub fn set_launcher_override(path: Option<PathBuf>) {
    *LAUNCHER_OVERRIDE.write().unwrap() = path;
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum InstallError {
    MissingLauncher,
    MissingName,
    ReservedName(String),
    NameTaken(PathBuf),
    BadFolder(String),
    SelfSource,
    MissingIcon,
    IconCreationFailed,
    WriteFailed(String),
}

impl fmt::Display for InstallError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            InstallError::MissingLauncher => {
                f.write_str("This copy of Claude Graft is missing its launcher.")
            }
            InstallError::MissingName => f.write_str("The shortcut needs a name."),
            InstallError::ReservedName(name) => {
                write!(f, "\u{201c}{name}\u{201d} is the name of Claude itself. Pick something else.")
            }
            InstallError::NameTaken(path) => write!(
                f,
                "There is already a launcher at {} that Claude Graft did not create. Remove it, then try again.",
                path.display()
            ),
            InstallError::BadFolder(reason) => f.write_str(reason),
            InstallError::SelfSource => f.write_str(
                "This shortcut is set to borrow chats from its own profile. Choose a different source.",
            ),
            InstallError::MissingIcon => f.write_str("Claude's application icon could not be read."),
            InstallError::IconCreationFailed => {
                f.write_str("The selected shortcut icon could not be created.")
            }
            InstallError::WriteFailed(detail) => f.write_str(detail),
        }
    }
}

impl std::error::Error for InstallError {}

// MARK: - Where things go

/// Where the desktop finds launchers, and where a menu editor puts the ones
/// it changes.
pub fn applications_dir() -> PathBuf {
    paths::data_home().join("applications")
}

pub fn shortcuts_root() -> PathBuf {
    paths::data_home().join("claude-graft/shortcuts")
}

pub fn icon_dir() -> PathBuf {
    paths::data_home().join("icons/hicolor/256x256/apps")
}

fn id_text(id: Uuid) -> String {
    id.hyphenated().to_string()
}

/// The entry's own file name. By id rather than by name, so a rename never
/// has to move it, and two shortcuts given one name are still two files.
pub fn entry_path(id: Uuid) -> PathBuf {
    applications_dir().join(format!("claude-graft-{}.desktop", id_text(id)))
}

pub fn config_path(id: Uuid) -> PathBuf {
    shortcuts_root().join(id_text(id)).join("graft.json")
}

pub fn icon_path(id: Uuid) -> PathBuf {
    icon_dir().join(format!("claude-graft-{}.png", id_text(id)))
}

/// What the desktop matches a window against to show it under this
/// shortcut's name and icon, and what the launcher passes Claude as `--class`.
pub fn window_class(id: Uuid) -> String {
    format!("ClaudeGraft.{}", id_text(id))
}

/// The shortcut a canonical `graft.json` belongs to, read off its folder.
pub fn id_of_config(path: &Path) -> Option<Uuid> {
    let folder = path.parent()?;
    if !paths::same_path(folder.parent()?, &shortcuts_root()) {
        return None;
    }
    Uuid::parse_str(&folder.file_name()?.to_string_lossy()).ok()
}

/// Only a `graft.json` in a shortcut folder of Graft's own is ever written or
/// removed, whatever an entry names. An entry is a text file anybody can
/// edit, and the path in it is not permission to write somewhere else.
fn config_is_ours(path: &Path) -> bool {
    path.file_name().map(|n| n == "graft.json").unwrap_or(false) && id_of_config(path).is_some()
}

fn icon_is_ours(path: &Path) -> bool {
    let name = path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    path.parent().map(|p| paths::same_path(p, &icon_dir())).unwrap_or(false)
        && name.starts_with("claude-graft-")
        && name.ends_with(".png")
}

/// The running Graft's version, stamped into every entry it writes.
pub fn graft_version() -> &'static str {
    env!("CARGO_PKG_VERSION")
}

// MARK: - The launcher an entry runs

/// The `graft-launch` beside the running program, the one a build outside
/// the Flatpak installs with it.
pub fn launcher_program() -> Option<PathBuf> {
    if let Some(path) = LAUNCHER_OVERRIDE.read().unwrap().clone() {
        return path.exists().then_some(path);
    }
    if host::in_flatpak() {
        let path = PathBuf::from("/app/bin/graft-launch");
        return path.exists().then_some(path);
    }
    let path = std::env::current_exe().ok()?.parent()?.join("graft-launch");
    path.exists().then_some(path)
}

/// What an entry's `Exec` runs, as arguments, ending with `arg`.
///
/// Inside the Flatpak the entry is run by the host's desktop, which cannot
/// reach into the sandbox, so it asks Flatpak to start the launcher in a
/// sandbox of its own.
pub fn launcher_command(arg: &str) -> Option<Vec<String>> {
    if LAUNCHER_OVERRIDE.read().unwrap().is_none() && host::in_flatpak() {
        let app = host::flatpak_id()?;
        launcher_program()?;
        return Some(vec![
            "flatpak".into(),
            "run".into(),
            "--command=graft-launch".into(),
            app,
            arg.to_string(),
        ]);
    }
    let program = launcher_program()?;
    Some(vec![paths::text(&program), arg.to_string()])
}

fn exec_for(id: Uuid) -> Option<String> {
    launcher_command(&id_text(id)).map(|args| desktop::exec_line(&args))
}

// MARK: - Recognising and finding entries

pub fn is_graft_entry(path: &Path) -> bool {
    path.extension().map(|e| e == "desktop").unwrap_or(false)
        && DesktopEntry::read(path)
            .and_then(|e| e.get(CONFIG_KEY))
            .map(|v| !v.is_empty())
            .unwrap_or(false)
}

/// The `graft.json` an entry of Graft's names.
pub fn config_path_of(entry: &Path) -> Option<PathBuf> {
    let value = DesktopEntry::read(entry)?.get(CONFIG_KEY)?;
    (!value.is_empty()).then(|| PathBuf::from(value))
}

/// What an entry's `graft.json` says, or `None` without a readable one.
pub fn described_config(entry: &Path) -> Option<GraftConfig> {
    let config = config_path_of(entry)?;
    let files::JsonRead::Value(value) = files::read_json(&config) else { return None };
    serde_json::from_value(value).ok()
}

/// The profile an entry opens, or `None` without a readable description.
pub fn described_profile(entry: &Path) -> Option<PathBuf> {
    described_config(entry).map(|c| c.profile())
}

fn describes(entry: &Path, profile: &Path) -> bool {
    described_profile(entry).map(|p| paths::same_path(&p, profile)).unwrap_or(false)
}

/// Every entry of Graft's in the applications folder, in name order so two
/// passes find the same one first. A file that does not even mention the key
/// is passed over unparsed, since the folder holds every app on the machine.
fn graft_entries() -> Vec<PathBuf> {
    let dir = applications_dir();
    files::listing(&dir)
        .unwrap_or_default()
        .into_iter()
        .filter(|n| n.ends_with(".desktop"))
        .map(|n| dir.join(n))
        .filter(|p| {
            std::fs::read(p)
                .map(|b| String::from_utf8_lossy(&b).contains(CONFIG_KEY))
                .unwrap_or(false)
        })
        .filter(|p| is_graft_entry(p))
        .collect()
}

/// The entry a shortcut was installed as, wherever it is and whatever it is
/// called now: its own file first, then any entry whose `graft.json` names its
/// profile. Blind to anything Graft did not create, and to a description that
/// will not parse.
///
/// Looking only under the shortcut's own file name misses an entry renamed by
/// hand or copied by a menu editor, so those were never repaired, and deleting
/// the shortcut left one behind still naming a source, ready to graft again
/// the next time anybody opened it. There is one lookup, and Open uses it too.
///
/// `among` is the whole list. Two shortcuts on one profile folder have an
/// entry each describing it, and only the file name says which is whose, so
/// for those the scan is not made: guessing would hand one shortcut's entry
/// to the other's Delete.
pub fn installed_entry(shortcut: &Shortcut, among: &[Shortcut]) -> Option<PathBuf> {
    let own = entry_path(shortcut.id);
    let profile = shortcut.profile_dir();
    if is_graft_entry(&own) && describes(&own, &profile) {
        return Some(own);
    }
    if among.iter().any(|o| o.id != shortcut.id && paths::same_path(&o.profile_dir(), &profile)) {
        return None;
    }
    graft_entries().into_iter().find(|e| describes(e, &profile))
}

/// An entry of Graft's under the shortcut's own file name that describes no
/// other listed shortcut's profile, whatever profile it does describe.
///
/// Only for the steps that do not care which profile an entry opens:
/// refreshing how it reaches the launcher, deleting it with its shortcut, and
/// finding the entry an Update writes over. `refresh_config` takes it only
/// when its description will not parse. An entry naming another folder than
/// the list is a folder change nobody applied, and applying it unasked at
/// start is what left a shortcut opening an empty profile.
pub fn named_entry(shortcut: &Shortcut, among: &[Shortcut]) -> Option<PathBuf> {
    let own = entry_path(shortcut.id);
    if !is_graft_entry(&own) {
        return None;
    }
    if let Some(profile) = described_profile(&own) {
        if among.iter().any(|o| o.id != shortcut.id && paths::same_path(&o.profile_dir(), &profile)) {
            return None;
        }
    }
    Some(own)
}

/// The version of Graft that wrote an entry.
pub fn built_by(entry: &Path) -> Option<String> {
    if !is_graft_entry(entry) {
        return None;
    }
    DesktopEntry::read(entry)?.get(VERSION_KEY)
}

// MARK: - Installing

fn new_entry(shortcut: &Shortcut, exec: &str) -> DesktopEntry {
    let mut entry = DesktopEntry::parse("[Desktop Entry]");
    entry.set("Type", "Application");
    entry.set("Version", "1.5");
    entry.set("Name", &shortcut.name);
    entry.set("Comment", "Claude on a profile of its own");
    entry.set_raw("Exec", exec);
    entry.set("Icon", &paths::text(&icon_path(shortcut.id)));
    entry.set("Terminal", "false");
    entry.set("Categories", "Office;Utility;");
    entry.set("StartupWMClass", &window_class(shortcut.id));
    entry.set(CONFIG_KEY, &paths::text(&config_path(shortcut.id)));
    entry.set(VERSION_KEY, graft_version());
    entry
}

fn write_failed(e: std::io::Error) -> InstallError {
    InstallError::WriteFailed(e.to_string())
}

/// Write a shortcut's entry, description and icon, replacing the entry it
/// was installed as.
///
/// `previous` is the shortcut as it was last installed, when this press
/// changes its folder: the entry to replace is the one describing the old
/// profile, and the new one is described by nothing yet. `among` is the whole
/// list, for the lookups.
///
/// Everything is drawn and checked before anything is written, so a missing
/// icon leaves a working shortcut exactly as it was, its profile included.
pub fn install(
    shortcut: &Shortcut,
    source_dir: Option<&Path>,
    previous: Option<&Shortcut>,
    among: &[Shortcut],
    sharing: &dyn Sharing,
) -> Result<PathBuf, InstallError> {
    let exec = exec_for(shortcut.id).ok_or(InstallError::MissingLauncher)?;
    let name = shortcut.name.trim();
    if name.is_empty() {
        return Err(InstallError::MissingName);
    }
    if RESERVED_NAMES.iter().any(|r| r.to_lowercase() == name.to_lowercase()) {
        return Err(InstallError::ReservedName(shortcut.name.clone()));
    }
    if let Some(reason) = paths::validate_folder(&shortcut.folder) {
        return Err(InstallError::BadFolder(reason));
    }
    let profile = shortcut.profile_dir();
    if let Some(source) = source_dir {
        if paths::same_path(source, &profile) {
            return Err(InstallError::SelfSource);
        }
    }

    let existing = previous
        .and_then(|p| installed_entry(p, among))
        .or_else(|| installed_entry(shortcut, among))
        .or_else(|| named_entry(shortcut, among));
    let target = entry_path(shortcut.id);
    // Something is already there and it is not a shortcut of ours. Checked
    // before anything is written, so a refused install leaves both intact.
    if files::exists(&target) && !is_graft_entry(&target) {
        return Err(InstallError::NameTaken(target));
    }

    let config = GraftConfig {
        profile_dir: paths::text(&profile),
        source_dir: source_dir.map(paths::text),
    };
    let icon_bytes = icon::png(shortcut.icon_preset, icon::PIXELS).map_err(|e| match e {
        IconError::Missing => InstallError::MissingIcon,
        IconError::Failed => InstallError::IconCreationFailed,
    })?;
    let config_value = serde_json::to_value(&config).map_err(|e| InstallError::WriteFailed(e.to_string()))?;
    // Keys a menu editor or a person added to the entry being replaced are
    // kept; the ones that make it this shortcut are set again.
    let mut entry = existing
        .as_deref()
        .and_then(DesktopEntry::read)
        .unwrap_or_else(|| new_entry(shortcut, &exec));
    for (key, value) in new_entry(shortcut, &exec).pairs() {
        entry.set_raw(&key, &value);
    }

    std::fs::create_dir_all(&profile).map_err(write_failed)?;
    files::atomic_write(&icon_path(shortcut.id), &icon_bytes, 0o644).map_err(write_failed)?;
    files::write_json(&config_path(shortcut.id), &config_value).map_err(write_failed)?;
    // Last, and atomically: the desktop watches this folder and reads an
    // entry the moment it appears, so it must not see one naming a
    // description or an icon that is not there yet.
    files::atomic_write(&target, entry.text().as_bytes(), 0o644).map_err(write_failed)?;

    // A renamed or moved entry goes only once its replacement is in place.
    if let Some(old) = existing {
        if !paths::same_path(&old, &target) && is_graft_entry(&old) {
            remove_entry(&old, Some(shortcut.id));
        }
    }
    diagnostics::note(
        "shortcut.installed",
        &[("shortcut", shortcut.name.clone().into()), ("profile", shortcut.folder.clone().into())],
    );

    // Put into effect straight away when nothing holds the profile open;
    // otherwise the launcher does it the next time it runs. The very
    // description the entry was given rather than a second one built to
    // match, so what runs now and what runs from the dock later cannot drift.
    if !claude::is_running(&profile) {
        sharing.apply(&config);
    }
    Ok(target)
}

/// Remove an entry, and its description and icon when they are Graft's and
/// nothing else still names them. `keep` is a shortcut whose own files stay,
/// for the entry an Update just replaced.
fn remove_entry(entry: &Path, keep: Option<Uuid>) {
    let parsed = DesktopEntry::read(entry);
    let config = parsed.as_ref().and_then(|e| e.get(CONFIG_KEY)).map(PathBuf::from);
    let icon = parsed.as_ref().and_then(|e| e.get("Icon")).map(PathBuf::from);
    if std::fs::remove_file(entry).is_err() {
        return;
    }
    let others = graft_entries();
    let named_elsewhere = |key: &str, path: &Path| {
        others.iter().any(|o| {
            DesktopEntry::read(o)
                .and_then(|e| e.get(key))
                .map(|v| paths::same_path(Path::new(&v), path))
                .unwrap_or(false)
        })
    };
    let kept = keep.map(|id| (config_path(id), icon_path(id)));
    if let Some(config) = config.filter(|c| config_is_ours(c)) {
        let is_kept = kept.as_ref().map(|k| paths::same_path(&k.0, &config)).unwrap_or(false);
        if !is_kept && !named_elsewhere(CONFIG_KEY, &config) {
            let _ = std::fs::remove_file(&config);
            if let Some(folder) = config.parent() {
                let _ = std::fs::remove_dir(folder);
            }
        }
    }
    if let Some(icon) = icon.filter(|i| icon_is_ours(i)) {
        let is_kept = kept.as_ref().map(|k| paths::same_path(&k.1, &icon)).unwrap_or(false);
        if !is_kept && !named_elsewhere("Icon", &icon) {
            let _ = std::fs::remove_file(&icon);
        }
    }
}

// MARK: - Repairs at start

/// Bring every installed shortcut up to date with this Graft and with the
/// list. Returns how many were changed.
pub fn refresh_launchers(shortcuts: &[(Shortcut, Option<PathBuf>)]) -> usize {
    let list: Vec<Shortcut> = shortcuts.iter().map(|(s, _)| s.clone()).collect();
    shortcuts
        .iter()
        .filter(|(shortcut, source)| {
            let launcher = refresh_launcher(shortcut, &list);
            let config = refresh_config(shortcut, source.as_deref(), &list);
            launcher || config
        })
        .count()
}

/// Bring the way an entry reaches the launcher up to date.
///
/// Nothing re-saves a shortcut on its own, and the routes where that matters
/// most never touch this app at all: the dock and the app grid run the entry
/// and ask nothing of Graft. So every stale entry is rewritten at start, by
/// its version stamp: the `Exec`, the window class and the stamp, and nothing
/// else. The name, the icon and the profile it points at are the person's,
/// and the name may still be the old one, waiting for a save that renames it.
pub fn refresh_launcher(shortcut: &Shortcut, among: &[Shortcut]) -> bool {
    let version = graft_version();
    let Some(exec) = exec_for(shortcut.id) else { return false };
    let Some(path) = installed_entry(shortcut, among).or_else(|| named_entry(shortcut, among)) else {
        return false;
    };
    if built_by(&path).as_deref() == Some(version) {
        return false;
    }
    let Some(mut entry) = DesktopEntry::read(&path) else { return false };
    entry.set_raw("Exec", &exec);
    entry.set("StartupWMClass", &window_class(shortcut.id));
    entry.set(VERSION_KEY, version);
    // Swapped in whole rather than truncated and rewritten. This runs while
    // Graft starts, which on a login is exactly when a shortcut may be
    // starting too, and a half-written entry opens nothing.
    files::atomic_write(&path, entry.text().as_bytes(), 0o644).is_ok()
}

/// Bring an entry's `graft.json` back into line with the list.
///
/// What a shortcut does is written down twice: in `shortcuts.json`, which the
/// window edits, and in the `graft.json` the entry names, which is what
/// actually runs. The desktop starts the entry and asks Graft nothing, so
/// where the two disagree the entry wins and the window is describing a
/// shortcut that does not exist. On a Mac they disagreed: the list said a
/// profile was back on its own chats while the bundle still named a source,
/// so opening it from the Dock grafted it again and put all 152 of its own
/// chats in the stash.
///
/// Not gated on the version, because the two can fall out of step at any
/// version. Only the description is written; the entry is somebody else's.
///
/// The source is repaired and the profile is never moved. An entry is found
/// here by the profile it already opens, or by its own file name when its
/// description will not parse, so a folder typed into the list and never
/// applied cannot re-point it; and the checks Update makes are made here too,
/// for a list edited by hand.
pub fn refresh_config(shortcut: &Shortcut, source_dir: Option<&Path>, among: &[Shortcut]) -> bool {
    if let Some(reason) = paths::validate_folder(&shortcut.folder) {
        diagnostics::note(
            "shortcut.config-skipped",
            &[("shortcut", shortcut.name.clone().into()), ("reason", reason.into())],
        );
        return false;
    }
    let profile = shortcut.profile_dir();
    if source_dir.map(|s| paths::same_path(s, &profile)).unwrap_or(false) {
        diagnostics::note(
            "shortcut.config-skipped",
            &[("shortcut", shortcut.name.clone().into()), ("reason", "self-source".into())],
        );
        return false;
    }
    let unreadable = named_entry(shortcut, among).filter(|e| described_profile(e).is_none());
    let Some(entry) = installed_entry(shortcut, among).or(unreadable) else { return false };
    let Some(file) = config_path_of(&entry).filter(|c| config_is_ours(c)) else {
        diagnostics::note(
            "shortcut.config-skipped",
            &[("shortcut", shortcut.name.clone().into()), ("reason", "not-ours".into())],
        );
        return false;
    };
    let wanted = GraftConfig { profile_dir: paths::text(&profile), source_dir: source_dir.map(paths::text) };
    if described_config(&entry).as_ref() == Some(&wanted) {
        return false;
    }
    let Ok(value) = serde_json::to_value(&wanted) else { return false };
    files::write_json(&file, &value).is_ok()
}

/// Remove the entry a shortcut was installed as, with its description and
/// icon. An entry Graft did not write is never touched.
pub fn uninstall(shortcut: &Shortcut, among: &[Shortcut]) {
    let Some(entry) = installed_entry(shortcut, among).or_else(|| named_entry(shortcut, among)) else {
        return;
    };
    if !is_graft_entry(&entry) {
        return;
    }
    // A copy a menu editor made names the same description, and left behind
    // it would go on opening a profile nothing lists.
    if let Some(config) = config_path_of(&entry) {
        for copy in graft_entries() {
            let same = config_path_of(&copy).map(|c| paths::same_path(&c, &config)).unwrap_or(false);
            if same && !paths::same_path(&copy, &entry) {
                let _ = std::fs::remove_file(&copy);
            }
        }
    }
    remove_entry(&entry, None);
}

#[cfg(test)]
pub(crate) mod tests;
