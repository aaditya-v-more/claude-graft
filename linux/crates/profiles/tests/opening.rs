//! The launch paths, end to end, against a stand-in for Claude's launcher.
//!
//! The stand-in records the arguments it is given and then behaves the way
//! this build of Claude does: the first start on a profile becomes a process
//! that looks like Claude's main binary to `ps`, and a second start on a
//! profile already open hands over and exits. Host commands run inside the
//! suite's own sandbox, so the only Claudes this can see, start or stop are
//! the stand-ins it started; a real one on this machine is out of its reach.

use graft_core::{claude, host, paths, testing::Sandbox, IconPreset, Shortcut, Source};
use graft_profiles::{installer, launch, opener, NoSharing, NoSteps, OpenFailure};
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

const STAND_IN: &str = r#"#!/bin/bash
here="$(cd "$(dirname "$0")" && pwd)"
printf '%s\n' "$*" >> "$here/launches.log"
dir="$here/main"
for arg in "$@"; do
    case "$arg" in --user-data-dir=*) dir="${arg#--user-data-dir=}" ;; esac
done
exec 9>>"$here/$(printf '%s' "$dir" | tr '/ ' '__').lock"
if ! flock -n 9; then
    printf '%s\n' "$*" >> "$here/handovers.log"
    exit 0
fi
exec -a "$here/claude-desktop" bash -c 'trap "exit 0" TERM; while :; do sleep 0.1 9>&-; done' "$here/claude-desktop" "$@"
"#;

fn until(what: impl Fn() -> bool) -> bool {
    let deadline = Instant::now() + Duration::from_secs(20);
    while Instant::now() < deadline {
        if what() {
            return true;
        }
        std::thread::sleep(Duration::from_millis(50));
    }
    what()
}

fn pids(profile: &Path) -> Vec<i32> {
    claude::processes()
        .into_iter()
        .filter(|p| claude::is_claude_process(&p.command) && claude::carries_data_dir(&p.command, profile))
        .map(|p| p.pid)
        .collect()
}

fn claudes() -> Vec<i32> {
    claude::processes().into_iter().filter(|p| claude::is_claude_process(&p.command)).map(|p| p.pid).collect()
}

fn lines(file: &Path, profile: &Path) -> Vec<String> {
    let needle = format!("--user-data-dir={}", paths::text(profile));
    std::fs::read_to_string(file)
        .unwrap_or_default()
        .lines()
        .filter(|l| l.split(' ').any(|a| a == needle))
        .map(str::to_string)
        .collect()
}

/// How many times a shortcut's launcher ran for a profile, read from the
/// diagnostics log the way somebody would read it after the fact.
fn launcher_runs(profile: &Path) -> usize {
    let folder = profile.file_name().unwrap().to_string_lossy().into_owned();
    std::fs::read_to_string(graft_core::diagnostics::file())
        .unwrap_or_default()
        .lines()
        .filter_map(|l| serde_json::from_str::<serde_json::Value>(l).ok())
        .filter(|e| e["event"] == "launcher.run" && e["profile"] == folder.as_str())
        .count()
}

fn stop(profile: &Path) -> bool {
    for pid in pids(profile) {
        // SAFETY: a plain signal to a stand-in this test started.
        unsafe {
            libc::kill(pid, libc::SIGTERM);
        }
    }
    until(|| pids(profile).is_empty())
}

/// Every stand-in goes when the test does, passed or not.
struct StopAll(PathBuf);

impl Drop for StopAll {
    fn drop(&mut self) {
        let root = paths::text(&self.0);
        for process in claude::processes() {
            if process.command.contains(&root) {
                // SAFETY: as in `stop`.
                unsafe {
                    libc::kill(process.pid, libc::SIGTERM);
                }
            }
        }
    }
}

/// Open the way the window does, with the launcher's own steps run in place
/// of a `graft-launch` this crate does not build.
fn open(shortcut: &Shortcut) -> Result<(), OpenFailure> {
    opener::open_with(&shortcut.profile_dir(), Some(shortcut), &NoSteps, |_, config| {
        launch::read_target(&paths::text(config)).map(|t| launch::run(&t, &NoSteps)).unwrap_or(false)
    })
}

#[test]
fn opening_a_shortcut_starts_its_own_claude_once_and_shows_it_after_that() {
    let sandbox = Sandbox::new();
    host::set_run_directly(true);
    let bin = sandbox.root.path().join("stand-in");
    std::fs::create_dir_all(&bin).unwrap();
    let script = bin.join("launcher");
    std::fs::write(&script, STAND_IN).unwrap();
    std::fs::set_permissions(&script, std::os::unix::fs::PermissionsExt::from_mode(0o755)).unwrap();
    std::env::set_var("GRAFT_CLAUDE_LAUNCHER", &script);
    installer::set_launcher_override(Some(script.clone()));
    let _stop_all = StopAll(sandbox.root.path().to_path_buf());
    let launches = bin.join("launches.log");
    let handovers = bin.join("handovers.log");

    let existing = claudes();
    let mut first = Shortcut::new("Opening First", Some("Claude-Opening-First"), Source::Own, IconPreset::Blue);
    let mut second = Shortcut::new("Opening Second", Some("Claude-Opening-Second"), Source::Own, IconPreset::Green);
    first.installed_name = Some(first.name.clone());
    second.installed_name = Some(second.name.clone());
    installer::install(&first, None, None, &[], &NoSharing).unwrap();
    let original = installer::install(&second, None, None, &[], &NoSharing).unwrap();
    let renamed = installer::applications_dir().join("store-claude.desktop");
    std::fs::rename(&original, &renamed).unwrap();
    assert_eq!(opener::entry_for(&second), Some(renamed.clone()), "the entry renamed by hand is still found by its profile");

    assert_eq!(open(&first), Ok(()), "the selected shortcut opens without an error");
    assert!(until(|| pids(&first.profile_dir()).len() == 1), "the first selected profile launches its own Claude");
    let started = lines(&launches, &first.profile_dir());
    assert_eq!(started.len(), 1);
    assert!(started[0].ends_with(&format!("--class={}", installer::window_class(first.id))),
        "under the window class its entry names, so the dock shows it as that shortcut");

    assert_eq!(open(&second), Ok(()));
    assert!(until(|| pids(&second.profile_dir()).len() == 1), "the renamed second shortcut launches while the first is running");
    assert_eq!(launcher_runs(&second.profile_dir()), 1, "the selected shortcut actually reaches its launcher");
    let second_pid = pids(&second.profile_dir());

    assert_eq!(open(&first), Ok(()));
    assert!(until(|| lines(&handovers, &first.profile_dir()).len() == 1), "Open brings the first profile forward again by handing over to it");
    assert_eq!(lines(&handovers, &first.profile_dir()), started, "with the very arguments the open Claude was started with");
    assert_eq!(pids(&first.profile_dir()).len(), 1, "and without a second Claude on it");

    assert_eq!(open(&second), Ok(()));
    assert!(until(|| launcher_runs(&second.profile_dir()) >= 2), "a repeated click still runs the shortcut's profile check");
    assert!(until(|| lines(&handovers, &second.profile_dir()).len() == 1), "reopening the second profile shows its existing window");
    std::thread::sleep(Duration::from_millis(500));
    assert_eq!(pids(&second.profile_dir()), second_pid, "repeated Open never creates a second Claude on the same profile");
    assert_eq!(lines(&launches, &second.profile_dir()).len(), 2, "the launcher ran once to start it and once to show it, and the second run handed over");

    let hidden = sandbox.root.path().join("missing-shortcut.desktop");
    std::fs::rename(&renamed, &hidden).unwrap();
    assert_eq!(open(&second), Err(OpenFailure::MissingShortcut), "the missing shortcut is reported instead of falling back to another profile");
    assert_eq!(pids(&second.profile_dir()), second_pid, "and does not switch or duplicate the selected profile");
    assert!(pids(&paths::main_profile()).is_empty() && claudes().len() == existing.len() + 2, "nor open Claude's own");
    std::fs::rename(&hidden, &renamed).unwrap();

    assert!(stop(&second.profile_dir()), "the disposable profile exits cleanly");
    assert_eq!(open(&second), Ok(()));
    assert!(until(|| pids(&second.profile_dir()).len() == 1), "the renamed shortcut can open again after quitting");
    assert_eq!(launcher_runs(&second.profile_dir()), 3, "opening after quitting reaches the shortcut launcher again");

    let remaining: Vec<i32> = claude::processes()
        .into_iter()
        .filter(|p| claude::is_claude_process(&p.command))
        .filter(|p| !claude::carries_data_dir(&p.command, &first.profile_dir()) && !claude::carries_data_dir(&p.command, &second.profile_dir()))
        .map(|p| p.pid)
        .collect();
    assert_eq!(remaining, existing, "the checks neither launch nor close any other profile");
    assert!(stop(&first.profile_dir()) && stop(&second.profile_dir()), "and both stand-ins are gone at the end");
}

#[test]
fn claude_s_own_profile_opens_with_no_data_folder_and_shows_itself_after_that() {
    let sandbox = Sandbox::new();
    host::set_run_directly(true);
    let bin = sandbox.root.path().join("stand-in");
    std::fs::create_dir_all(&bin).unwrap();
    let script = bin.join("launcher");
    std::fs::write(&script, STAND_IN).unwrap();
    std::fs::set_permissions(&script, std::os::unix::fs::PermissionsExt::from_mode(0o755)).unwrap();
    std::env::set_var("GRAFT_CLAUDE_LAUNCHER", &script);
    let _stop_all = StopAll(sandbox.root.path().to_path_buf());
    let mine = |p: &claude::Process| p.command.contains(&paths::text(sandbox.root.path()));
    let main_pids = || {
        claude::processes()
            .into_iter()
            .filter(|p| claude::is_default_instance(&p.command) && mine(p))
            .map(|p| p.pid)
            .collect::<Vec<_>>()
    };
    // Run outside the SDK, the process list is the machine's, and a real
    // Claude on its own profile would be taken for this one and shown. The
    // stand-in would then be what was started, and nothing real is touched,
    // but the checks would be about the wrong Claude.
    if claude::processes().iter().any(|p| claude::is_default_instance(&p.command) && !mine(p)) {
        eprintln!("skipped: a Claude on its own profile is visible to this run");
        return;
    }

    assert_eq!(opener::open(&paths::main_profile(), None, &NoSteps), Ok(()), "Claude's own profile opens");
    assert!(until(|| main_pids().len() == 1), "as a Claude with no data folder named, which is how the main one is known");
    assert_eq!(std::fs::read_to_string(bin.join("launches.log")).unwrap(), "\n", "started with no arguments at all");
    assert_eq!(opener::open(&paths::main_profile(), None, &NoSteps), Ok(()));
    assert!(until(|| bin.join("handovers.log").exists()), "a second press hands over to the one already open");
    assert_eq!(main_pids().len(), 1, "and never starts a second");
    for pid in main_pids() {
        // SAFETY: a stand-in this test started.
        unsafe {
            libc::kill(pid, libc::SIGTERM);
        }
    }
    assert!(until(|| main_pids().is_empty()));
}
