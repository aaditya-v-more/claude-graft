//! Removing a profile's data, which is a login and its chats.

use crate::sharing::Sharing;
use graft_core::{claude, paths};
use std::fmt;
use std::path::Path;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ProfileError {
    MainProfile,
    GraftFolder,
    OutsideConfig,
    Running,
    Failed,
}

impl fmt::Display for ProfileError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(match self {
            ProfileError::MainProfile => {
                "That folder belongs to Claude itself and will not be deleted."
            }
            ProfileError::GraftFolder => {
                "That folder belongs to Claude Graft itself and will not be deleted."
            }
            ProfileError::OutsideConfig => {
                "Only folders directly inside ~/.config can be deleted."
            }
            ProfileError::Running => "Claude is still running on this profile. Quit it first.",
            ProfileError::Failed => "The profile folder could not be removed.",
        })
    }
}

impl std::error::Error for ProfileError {}

/// Delete a profile's data. Guarded on every side: the folder has to sit
/// directly in `~/.config`, must not be Claude's own profile or Graft's own
/// state, and must not be in use. Losing one means losing a login and its
/// chats.
pub fn delete_profile(profile: &Path, sharing: &dyn Sharing) -> Result<(), ProfileError> {
    // `file_name` is empty for a path ending in `..`, and the parent is asked
    // where it leads, so `Claude/../Claude` is judged as the folder it names.
    let name = profile.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    let inside =
        profile.parent().map(|p| paths::same_path(p, &paths::config_home())).unwrap_or(false);
    if name.is_empty() || !inside {
        return Err(ProfileError::OutsideConfig);
    }
    // By name as well as by path. Two spellings that differ only in case are
    // one folder on a case-folding drive and look like one in a file manager,
    // and a link to Claude's profile resolves to it.
    if paths::is_main_profile(profile) || paths::is_named(&name, paths::MAIN_PROFILE_NAME) {
        return Err(ProfileError::MainProfile);
    }
    if paths::same_path(profile, &paths::graft_folder())
        || paths::is_named(&name, paths::GRAFT_FOLDER_NAME)
    {
        return Err(ProfileError::GraftFolder);
    }
    if claude::is_running(profile) {
        return Err(ProfileError::Running);
    }
    if !profile.is_dir() {
        return Ok(());
    }
    // A link sitting where the profile is goes, and what it points at stays:
    // remove_dir_all does not follow one, which is the behaviour wanted here.
    std::fs::remove_dir_all(profile).map_err(|_| ProfileError::Failed)?;
    sharing.forget_mirrors(profile);
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::sharing::NoSharing;
    use crate::testing::{make_profile, World};

    fn refusal(path: &Path) -> Option<ProfileError> {
        delete_profile(path, &NoSharing).err()
    }

    #[test]
    fn only_a_profile_folder_directly_in_the_config_folder_can_be_deleted() {
        let world = World::new();
        let main = make_profile("Claude", Some("AAAA"));
        let config = world.sandbox.config();
        assert_eq!(refusal(&main), Some(ProfileError::MainProfile), "Claude's own profile is refused as the main profile");
        assert!(main.exists(), "it is still there");
        assert_eq!(refusal(&config.join("Some/Nested/Path")), Some(ProfileError::OutsideConfig), "a nested folder is refused");
        assert_eq!(refusal(&world.sandbox.home()), Some(ProfileError::OutsideConfig), "the home folder is refused");
        assert_eq!(refusal(&config), Some(ProfileError::OutsideConfig), "the config folder itself is refused");
        assert_eq!(refusal(Path::new("/")), Some(ProfileError::OutsideConfig), "the root of the disk is refused");
        assert_eq!(
            refusal(&config.join("Claude/../Claude")),
            Some(ProfileError::MainProfile),
            "a path that walks back round to the main profile is still refused"
        );
        assert_eq!(refusal(&config.join("claude")), Some(ProfileError::MainProfile), "so is the main profile spelt in lower case");
        assert!(main.exists(), "which is still there");

        let graft = paths::graft_folder();
        std::fs::create_dir_all(&graft).unwrap();
        let marker = graft.join("graft-deletion-marker");
        std::fs::write(&marker, "x").unwrap();
        assert_eq!(refusal(&graft), Some(ProfileError::GraftFolder), "Graft's own folder is refused");
        assert_eq!(refusal(&config.join("claudegraft")), Some(ProfileError::GraftFolder), "and so is it in lower case");
        assert!(marker.exists(), "and what Graft keeps there survives");

        let work = make_profile("Claude-Work", Some("BBBB"));
        assert_eq!(refusal(&work), None, "an ordinary profile folder may go");
        assert!(!work.exists(), "and is gone");
    }

    #[test]
    fn deleting_a_profile_drops_its_mirror_pairs() {
        struct Forget(std::cell::RefCell<Vec<std::path::PathBuf>>);
        impl Sharing for Forget {
            fn forget_mirrors(&self, profile: &Path) {
                self.0.borrow_mut().push(profile.to_path_buf());
            }
        }
        let _world = World::new();
        let doomed = make_profile("Claude-Doom", Some("DOWN"));
        let forget = Forget(Default::default());
        delete_profile(&doomed, &forget).unwrap();
        assert_eq!(forget.0.borrow().as_slice(), std::slice::from_ref(&doomed), "deleting the profile takes its pairs with it");
    }
}
