//! Opening the profile a person picked, and never another one.
//!
//! A shortcut is opened through its entry's own description, found by the
//! lookup the repairs and Delete use, so what Open reaches is what they keep
//! up to date. It runs the launcher as the dock does, so the launcher's own
//! check for a Claude already on that profile is never skipped.

use crate::installer;
use crate::launch::{self, LaunchSteps};
use graft_core::{diagnostics, host, paths, Shortcut};
use std::fmt;
use std::path::{Path, PathBuf};

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Destination {
    Main,
    /// The entry to run, wherever it is now.
    Shortcut(PathBuf),
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum OpenFailure {
    MissingShortcut,
    CouldNotOpen,
}

impl fmt::Display for OpenFailure {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(match self {
            OpenFailure::MissingShortcut => "This profile's shortcut could not be found. Open Claude Graft and update the shortcut, then try again.",
            OpenFailure::CouldNotOpen => "The selected profile could not be opened. Try again in a moment.",
        })
    }
}

impl std::error::Error for OpenFailure {}

/// The same lookup the repairs and Delete use.
pub fn entry_for(shortcut: &Shortcut) -> Option<PathBuf> {
    installer::installed_entry(shortcut, &[])
}

/// Where a press on a profile goes. A missing shortcut is never permission to
/// open another profile: falling through to Claude's own when a renamed
/// shortcut could not be found is what the Mac menu once did.
pub fn destination(profile: &Path, shortcut: Option<&Shortcut>) -> Result<Destination, OpenFailure> {
    if let Some(shortcut) = shortcut {
        if paths::same_path(&shortcut.profile_dir(), profile) {
            if let Some(entry) = entry_for(shortcut) {
                return Ok(Destination::Shortcut(entry));
            }
        }
        return Err(OpenFailure::MissingShortcut);
    }
    if paths::is_main_profile(profile) {
        Ok(Destination::Main)
    } else {
        Err(OpenFailure::MissingShortcut)
    }
}

/// Start a fresh `graft-launch` on a description, detached, the way the
/// desktop runs an entry. Inside the Flatpak it is a sandbox of its own on
/// the host, so it outlives this window.
pub fn spawn_launcher(config_path: &Path) -> bool {
    let Some(args) = installer::launcher_command(&paths::text(config_path)) else { return false };
    let (program, rest) = args.split_first().expect("a launcher command has a program");
    host::spawn_detached(program, rest, &[])
}

/// Open a profile: Claude's own through `launch::open`, a shortcut through
/// its launcher. Blocking, since it reads the process list; call it off the
/// main thread.
pub fn open(profile: &Path, shortcut: Option<&Shortcut>, steps: &dyn LaunchSteps) -> Result<(), OpenFailure> {
    open_with(profile, shortcut, steps, |_, config| spawn_launcher(config))
}

/// `open`, with how a shortcut's launcher is run handed in: given the entry
/// and the description it names. The suite runs the launcher's own steps in
/// place, since it has no `graft-launch` to start.
pub fn open_with(
    profile: &Path,
    shortcut: Option<&Shortcut>,
    steps: &dyn LaunchSteps,
    run_entry: impl FnOnce(&Path, &Path) -> bool,
) -> Result<(), OpenFailure> {
    let result = match destination(profile, shortcut) {
        Ok(Destination::Main) => {
            if launch::open(profile, None, steps) {
                Ok(())
            } else {
                Err(OpenFailure::CouldNotOpen)
            }
        }
        Ok(Destination::Shortcut(entry)) => {
            diagnostics::note(
                "profile.open",
                &[("profile", folder(profile).into()), ("entry", paths::text(&entry).into())],
            );
            match installer::config_path_of(&entry) {
                Some(config) if run_entry(&entry, &config) => Ok(()),
                _ => Err(OpenFailure::CouldNotOpen),
            }
        }
        Err(e) => Err(e),
    };
    if let Err(e) = &result {
        diagnostics::note(
            "profile.open-failed",
            &[("profile", folder(profile).into()), ("reason", e.to_string().into())],
        );
    }
    result
}

fn folder(profile: &Path) -> String {
    profile.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::sharing::NoSharing;
    use crate::testing::World;
    use graft_core::{IconPreset, Source};

    #[test]
    fn open_follows_the_selected_profile_and_never_falls_back_to_another() {
        let _world = World::new();
        let mut selected = Shortcut::new("Selected", None, Source::Own, IconPreset::Original);
        selected.installed_name = Some(selected.name.clone());
        let original = installer::install(&selected, None, None, &[], &NoSharing).unwrap();
        assert_eq!(destination(&selected.profile_dir(), Some(&selected)), Ok(Destination::Shortcut(original.clone())),
            "opening an installed shortcut selects its own entry");
        selected.name = "Unsaved name".into();
        assert_eq!(entry_for(&selected), Some(original.clone()), "editing the display name still finds the installed shortcut");

        let renamed = installer::applications_dir().join("renamed-by-hand.desktop");
        std::fs::rename(&original, &renamed).unwrap();
        assert_eq!(installer::installed_entry(&selected, &[]), Some(renamed.clone()), "the repairs and Delete find a renamed entry the way Open does");
        assert_eq!(destination(&selected.profile_dir(), Some(&selected)), Ok(Destination::Shortcut(renamed.clone())),
            "a rename by hand still opens the profile recorded in its description");

        let other = Shortcut::new(&selected.name, None, Source::Own, IconPreset::Original);
        let wrong = installer::install(&other, None, None, &[], &NoSharing).unwrap();
        assert_eq!(entry_for(&selected), Some(renamed.clone()), "a matching name cannot redirect the click to another profile");

        std::fs::remove_file(&renamed).unwrap();
        assert_eq!(destination(&selected.profile_dir(), Some(&selected)), Err(OpenFailure::MissingShortcut),
            "a missing shortcut fails instead of opening the default profile");
        assert_eq!(destination(&selected.profile_dir(), None), Err(OpenFailure::MissingShortcut),
            "a stale menu entry cannot fall back to the default profile either");
        assert_eq!(destination(&other.profile_dir(), Some(&selected)), Err(OpenFailure::MissingShortcut),
            "a shortcut for a different profile is refused");
        assert_eq!(destination(&paths::main_profile(), None), Ok(Destination::Main),
            "only the actual default profile selects the default launch route");

        std::fs::write(installer::config_path_of(&wrong).unwrap(), "broken").unwrap();
        assert_eq!(entry_for(&other), None, "an unreadable profile description is not trusted because its file is the shortcut's");
    }
}
