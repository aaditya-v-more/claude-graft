//! Starting Claude on a profile, or showing the one already on it.
//!
//! The launcher a shortcut runs and the window's own Open both end here. What
//! they do around it, the sweep, the sidebar sync and the shared-usage lines,
//! belongs to other areas and is handed in as `LaunchSteps`, at the points the
//! Mac build does them, so the locks are taken in the same order there too.

use crate::installer;
use graft_core::{claude, diagnostics, files, host, lock, paths, GraftConfig};
use std::fmt;
use std::path::{Path, PathBuf};
use uuid::Uuid;

/// The steps of opening that belong to other areas. Each has a default that
/// does nothing, so a caller supplies only what it has.
pub trait LaunchSteps {
    /// The profile is open already and its Claude has been shown. Records are
    /// still filed on the way past: the sweep has no timer, and this press is
    /// not going to start anything, so it is the only moment they arrive.
    fn already_open(&self, _filing: &[PathBuf]) {}

    /// The profile is closed and about to be started, under the state lock.
    /// A launcher puts its description into effect here before squaring up,
    /// so a record filed through a graft lands where the link now points.
    fn before_launch(&self, _config: Option<&GraftConfig>, _filing: &[PathBuf]) {}

    /// Under the launch lock, with the profile found closed a moment ago:
    /// where the sidebar is synced and the shared-usage lines go in, since
    /// both may only touch a Claude that is not running.
    fn before_start(&self, _profile: &Path) {}
}

pub struct NoSteps;

impl LaunchSteps for NoSteps {}

/// Start Claude on a profile through the installed launcher script, never the
/// binary: the script is where the package makes Claude work on this desktop.
/// `window_class` gives a grafted Claude its own name and icon in the dock.
pub fn start_claude(profile: &Path, window_class: Option<&str>) -> bool {
    let Some(launcher) = claude::launcher() else {
        diagnostics::note("claude.start-failed", &[("profile", folder(profile).into()), ("reason", "no-launcher".into())]);
        return false;
    };
    let args = claude::launch_arguments(profile, window_class);
    let started = host::spawn_detached(&paths::text(&launcher), &args, &[]);
    diagnostics::note(
        "claude.start",
        &[("profile", folder(profile).into()), ("started", started.into())],
    );
    started
}

/// Show the Claude already open on a profile.
///
/// This Claude takes Electron's single-instance lock, so a second start on a
/// profile that is open hands its arguments to the first and exits, and the
/// first shows its window, which is the reopen and the activation a Mac needs
/// two events for. It is started with the arguments the running one was
/// given, so nothing about the window it shows changes. False when nothing is
/// open there, since then a start would open a second Claude, not show one.
pub fn reveal(profile: &Path) -> bool {
    let listing = claude::processes();
    let Some(pid) = claude::pid_in(&listing, profile) else { return false };
    let class = listing.iter().find(|p| p.pid == pid).and_then(|p| graft_class_of(&p.command));
    diagnostics::note("claude.reveal", &[("profile", folder(profile).into()), ("pid", pid.into())]);
    start_claude(profile, class.as_deref())
}

/// The window class Graft gave a running Claude, if it gave it one. Chromium
/// keeps the last `--class`, and the launcher script passes one of its own
/// ahead of Graft's.
fn graft_class_of(command: &str) -> Option<String> {
    command
        .split(' ')
        .rev()
        .filter_map(|a| a.strip_prefix("--class="))
        .find(|c| c.starts_with("ClaudeGraft."))
        .map(str::to_string)
}

/// Start a profile that is closed, or show it when it is not, holding the
/// launch lock from deciding which to having done it, so two presses, or a
/// press and a launcher, cannot both find it closed and both start one.
pub fn launch(profile: &Path, window_class: Option<&str>, steps: &dyn LaunchSteps) -> bool {
    lock::with_launch_lock(|| {
        if claude::is_running(profile) {
            return reveal(profile);
        }
        steps.before_start(profile);
        start_claude(profile, window_class)
    })
}

/// Open a profile from the window, the way Claude's own profile is opened.
///
/// Opening a profile that is already open shows the Claude that is there:
/// nothing in Claude refuses a second process on one profile on a Mac, and
/// while this build's lock would turn one away, the question is asked first
/// so the sweep and the conflict question behave the same on both.
pub fn open(profile: &Path, window_class: Option<&str>, steps: &dyn LaunchSteps) -> bool {
    let filing = vec![profile.to_path_buf(), paths::main_profile()];
    if claude::is_running(profile) {
        let shown = reveal(profile);
        steps.already_open(&filing);
        return shown;
    }
    lock::with_state_lock(|| steps.before_launch(None, &filing));
    launch(profile, window_class, steps)
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum LaunchError {
    /// Nothing readable where the description should be.
    NoDescription(PathBuf),
}

impl fmt::Display for LaunchError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            LaunchError::NoDescription(path) => write!(f, "no profile description at {}", path.display()),
        }
    }
}

impl std::error::Error for LaunchError {}

/// What one run of `graft-launch` opens: the description it read, and the
/// shortcut that description belongs to when it sits in Graft's own folder.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LaunchTarget {
    pub id: Option<Uuid>,
    pub config_path: PathBuf,
    pub config: GraftConfig,
}

impl LaunchTarget {
    pub fn profile(&self) -> PathBuf {
        self.config.profile()
    }

    pub fn window_class(&self) -> Option<String> {
        self.id.map(installer::window_class)
    }

    /// The profiles a run files records into: its own, Claude's, and the one
    /// it borrows from.
    pub fn filing(&self) -> Vec<PathBuf> {
        let mut filing = vec![self.profile(), paths::main_profile()];
        filing.extend(self.config.source());
        filing
    }
}

/// Read what a launcher was asked to open. The argument is a shortcut's id,
/// as an entry's `Exec` passes it, or the path of a `graft.json`, as Open
/// passes it after finding an entry wherever it was moved to.
///
/// The description is what runs, not the list: the dock runs the entry and
/// asks Graft nothing, so this is read and nothing else is consulted.
pub fn read_target(arg: &str) -> Result<LaunchTarget, LaunchError> {
    let config_path = match Uuid::parse_str(arg) {
        Ok(id) => installer::config_path(id),
        Err(_) => PathBuf::from(arg),
    };
    let config = match files::read_json(&config_path) {
        files::JsonRead::Value(value) => serde_json::from_value::<GraftConfig>(value).ok(),
        _ => None,
    }
    .ok_or_else(|| LaunchError::NoDescription(config_path.clone()))?;
    Ok(LaunchTarget { id: installer::id_of_config(&config_path), config_path, config })
}

/// What a shortcut does when it is run.
///
/// While its profile is open, its files are open, so re-linking is off the
/// table and the Claude already on them is the whole answer; records are
/// still filed after the window is up. Otherwise the description is put into
/// effect and records filed under one hold of the state lock, let go before
/// `launch` takes the launch lock, so the two are never held in both orders.
pub fn run(target: &LaunchTarget, steps: &dyn LaunchSteps) -> bool {
    let profile = target.profile();
    diagnostics::set_who(&format!("launcher:{}", folder(&profile)));
    let _ = std::fs::create_dir_all(&profile);
    diagnostics::note(
        "launcher.run",
        &[
            ("profile", folder(&profile).into()),
            ("source", target.config.source_dir.clone().unwrap_or_default().into()),
        ],
    );
    let filing = target.filing();
    if claude::is_running(&profile) {
        reveal(&profile);
        steps.already_open(&filing);
        return true;
    }
    lock::with_state_lock(|| steps.before_launch(Some(&target.config), &filing));
    launch(&profile, target.window_class().as_deref(), steps)
}

fn folder(profile: &Path) -> String {
    profile.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::testing::World;

    #[test]
    fn a_launcher_reads_its_description_by_id_or_by_path() {
        let _world = World::new();
        let id = Uuid::new_v4();
        let path = installer::config_path(id);
        assert_eq!(read_target(&id.to_string()), Err(LaunchError::NoDescription(path.clone())), "a missing description is said so");
        files::write_json(&path, &serde_json::json!({"profileDir": "/p/Claude-2", "sourceDir": "/p/Claude"})).unwrap();
        let by_id = read_target(&id.to_string()).unwrap();
        assert_eq!(by_id.config.profile_dir, "/p/Claude-2");
        assert_eq!(by_id.window_class(), Some(installer::window_class(id)), "and the shortcut it belongs to names its window class");
        assert_eq!(read_target(&paths::text(&path)).unwrap(), by_id, "a path reaches the same description");
        assert!(by_id.filing().contains(&PathBuf::from("/p/Claude")), "a run files into the profile it borrows from too");

        let loose = paths::config_home().join("graft.json");
        files::write_json(&loose, &serde_json::json!({"profileDir": "/p/Claude-3"})).unwrap();
        assert_eq!(read_target(&paths::text(&loose)).unwrap().window_class(), None, "a description elsewhere names no shortcut");
    }

    #[test]
    fn only_graft_s_own_window_class_is_carried_into_a_reveal() {
        let line = "/x/claude-desktop --class=com.anthropic.Claude --user-data-dir=/p --class=ClaudeGraft.abc";
        assert_eq!(graft_class_of(line).as_deref(), Some("ClaudeGraft.abc"));
        assert_eq!(graft_class_of("/x/claude-desktop --class=com.anthropic.Claude"), None);
    }

    #[test]
    fn nothing_open_is_nothing_to_reveal() {
        let _world = World::new();
        assert!(!reveal(&paths::profile_dir("Claude-Never")), "a reveal never starts a Claude that is not there");
    }
}
