//! Finding Claude Desktop, and telling its processes apart.
//!
//! There is no Claude Desktop for Linux from Anthropic's own downloads page;
//! what people run is a repackaging of the Windows build, installed as a deb,
//! an rpm, an AppImage or a Nix package. They agree on two things Graft
//! relies on: a launcher script on the `PATH` that sets Electron's switches
//! and passes the rest of its arguments through, and a main binary whose path
//! ends in `claude-desktop`. Starting Claude through the launcher rather than
//! the binary keeps whatever the package does to make Claude work on this
//! desktop (Wayland or X11, the sandbox switches, its own cleanup).

use crate::host;
use crate::paths;
use std::path::{Path, PathBuf};

/// Launcher scripts in the order they are preferred. The first that exists
/// on the host is the Claude Graft opens.
pub const LAUNCHER_CANDIDATES: &[&str] = &[
    "/usr/bin/claude-desktop-unofficial",
    "/usr/bin/claude-desktop",
    "/usr/local/bin/claude-desktop",
];

/// The Claude installed on this machine, or `None` when none was found.
///
/// `GRAFT_CLAUDE_LAUNCHER` names one explicitly, which is how the suite
/// points Graft at a stand-in and how somebody with an AppImage in an odd
/// place tells Graft where it is.
pub fn launcher() -> Option<PathBuf> {
    if let Ok(path) = std::env::var("GRAFT_CLAUDE_LAUNCHER") {
        if !path.is_empty() {
            return Some(PathBuf::from(path));
        }
    }
    let home_bin = paths::home().join(".local/bin/claude-desktop");
    LAUNCHER_CANDIDATES
        .iter()
        .map(PathBuf::from)
        .chain(std::iter::once(home_bin))
        .find(|path| host::host_path_exists(path))
}

/// Claude's version as the launcher reports it, for the window and the report.
pub fn version() -> Option<String> {
    let launcher = launcher()?;
    let out = host::output(&paths::text(&launcher), &["--version"]);
    let line = out.lines().next()?.trim().to_string();
    (!line.is_empty()).then_some(line)
}

/// The first word of a command line, which is the executable for every
/// process Claude starts.
fn executable(command: &str) -> &str {
    command.split_whitespace().next().unwrap_or("")
}

/// Claude itself rather than one of the processes it starts: the main binary,
/// not a zygote, renderer, GPU or utility helper (every one of which repeats
/// the profile's `--user-data-dir` and carries a `--type=`), and not the
/// launcher script, whose command line starts with the shell.
pub fn is_claude_process(command: &str) -> bool {
    let exe = executable(command);
    let name = exe.rsplit('/').next().unwrap_or(exe);
    name == "claude-desktop" && !command.contains(" --type=")
}

/// The main binary of Claude on no profile in particular.
pub fn is_default_instance(command: &str) -> bool {
    is_claude_process(command) && !command.contains("--user-data-dir=")
}

/// Whether a command line names this profile as its data folder.
///
/// Anchored at the end of the argument. Without that, one profile's path is a
/// prefix of another's (`.../Claude` sits inside `.../Claude-2`) and every
/// shorter-named profile looks like it is running as soon as a longer one is.
pub fn carries_data_dir(command: &str, profile: &Path) -> bool {
    let needle = format!("user-data-dir={}", paths::text(profile));
    let bytes = command.as_bytes();
    let mut from = 0;
    while let Some(found) = command[from..].find(&needle) {
        let end = from + found + needle.len();
        if end == bytes.len() || bytes[end] == b' ' {
            return true;
        }
        from = end;
    }
    false
}

/// One line of the host's process list.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Process {
    pub pid: i32,
    pub command: String,
}

/// Every process on the host, paired with the pid that owns it.
///
/// `ps` rather than `pgrep`, and not only for the pid: pgrep leaves out its
/// own ancestors, so a Claude that started the process doing the asking (a
/// terminal inside it, or Claude Code running there) is the one Claude pgrep
/// will not report.
pub fn processes() -> Vec<Process> {
    parse_processes(&host::output("ps", &["-eo", "pid=,args="]))
}

pub fn parse_processes(listing: &str) -> Vec<Process> {
    listing
        .lines()
        .filter_map(|line| {
            let text = line.trim();
            let (id, rest) = text.split_once(' ')?;
            Some(Process { pid: id.parse().ok()?, command: rest.trim_start().to_string() })
        })
        .collect()
}

/// Whether a Claude has this profile open, answered from a listing already in
/// hand.
pub fn is_running_in(listing: &[Process], profile: &Path) -> bool {
    pid_in(listing, profile).is_some()
}

/// The pid of the Claude holding this profile, if one holds it. Only the
/// browser process counts; a helper outliving its Claude for a moment is not
/// a Claude anybody can be shown.
pub fn pid_in(listing: &[Process], profile: &Path) -> Option<i32> {
    let main = paths::is_main_profile(profile);
    listing
        .iter()
        .filter(|p| is_claude_process(&p.command))
        .find(|p| {
            if main {
                is_default_instance(&p.command) || carries_data_dir(&p.command, profile)
            } else {
                carries_data_dir(&p.command, profile)
            }
        })
        .map(|p| p.pid)
}

pub fn is_running(profile: &Path) -> bool {
    is_running_in(&processes(), profile)
}

pub fn pid_of(profile: &Path) -> Option<i32> {
    pid_in(&processes(), profile)
}

pub fn default_instance_is_running() -> bool {
    processes().iter().any(|p| is_default_instance(&p.command))
}

/// The profile every running Claude has open. A Claude on no profile in
/// particular is the main one.
pub fn running_profiles_in(listing: &[Process]) -> Vec<PathBuf> {
    let mut found: Vec<PathBuf> = Vec::new();
    for process in listing.iter().filter(|p| is_claude_process(&p.command)) {
        let profile = data_dir_of(&process.command).unwrap_or_else(paths::main_profile);
        if !found.iter().any(|f| paths::same_path(f, &profile)) {
            found.push(profile);
        }
    }
    found
}

pub fn running_profiles() -> Vec<PathBuf> {
    running_profiles_in(&processes())
}

/// The folder a command line names with `--user-data-dir=`. A path holding a
/// space is read up to the next ` --`, since `ps` joins arguments with single
/// spaces and a profile folder is the one argument here that may hold one.
pub fn data_dir_of(command: &str) -> Option<PathBuf> {
    let start = command.find("--user-data-dir=")? + "--user-data-dir=".len();
    let rest = &command[start..];
    let end = rest.find(" --").unwrap_or(rest.len());
    let value = rest[..end].trim_end();
    (!value.is_empty()).then(|| PathBuf::from(value))
}

/// The arguments a launch passes after the launcher. The main profile is the
/// one launched with no `--user-data-dir` at all, and that absence is the only
/// mark it has: naming it would start a Claude that nothing afterwards
/// recognises as the main one.
///
/// A grafted Claude also gets a window class of its own, which is what the
/// desktop matches against a `.desktop` file's `StartupWMClass` to give it
/// its own name and icon in the dock. Chromium keeps the last `--class` it is
/// given, so this one wins over the launcher's.
pub fn launch_arguments(profile: &Path, window_class: Option<&str>) -> Vec<String> {
    if paths::is_main_profile(profile) {
        return Vec::new();
    }
    let mut args = vec![format!("--user-data-dir={}", paths::text(profile))];
    if let Some(class) = window_class {
        args.push(format!("--class={class}"));
    }
    args
}

#[cfg(test)]
mod tests {
    use super::*;

    const LISTING: &str = "\
  101 /usr/bin/bash /usr/bin/claude-desktop-unofficial
  102 /usr/lib/claude-desktop-unofficial/claude-desktop --class=com.anthropic.Claude --no-sandbox
  103 /usr/lib/claude-desktop-unofficial/claude-desktop --type=renderer --user-data-dir=/home/a/.config/Claude
  104 /usr/lib/claude-desktop-unofficial/claude-desktop --class=com.anthropic.Claude --user-data-dir=/home/a/.config/Claude-2 --class=ClaudeGraft.Claude-2
  105 /usr/lib/claude-desktop-unofficial/claude-desktop --type=gpu-process --user-data-dir=/home/a/.config/Claude-Work
";

    #[test]
    fn helpers_and_launcher_scripts_are_not_claude() {
        let list = parse_processes(LISTING);
        let claudes: Vec<i32> =
            list.iter().filter(|p| is_claude_process(&p.command)).map(|p| p.pid).collect();
        assert_eq!(claudes, vec![102, 104], "only the two browser processes are Claude");
    }

    #[test]
    fn a_shorter_profile_is_not_running_because_a_longer_one_is() {
        let list = parse_processes(LISTING);
        assert!(carries_data_dir(&list[3].command, Path::new("/home/a/.config/Claude-2")));
        assert!(
            !carries_data_dir(&list[3].command, Path::new("/home/a/.config/Claude")),
            "Claude is a prefix of Claude-2 and is not the same profile"
        );
        assert!(
            pid_in(&list, Path::new("/home/a/.config/Claude-Work")).is_none(),
            "a helper left behind is not an open profile"
        );
    }

    #[test]
    fn a_data_folder_with_a_space_is_read_whole() {
        let line = "/x/claude-desktop --user-data-dir=/home/a/.config/Claude Work --class=Y";
        assert_eq!(data_dir_of(line), Some(PathBuf::from("/home/a/.config/Claude Work")));
    }

    #[test]
    fn the_main_profile_is_launched_with_no_data_folder() {
        let _guard = crate::testing::Sandbox::new();
        assert!(launch_arguments(&paths::main_profile(), Some("x")).is_empty());
        let other = paths::profile_dir("Claude-2");
        assert_eq!(
            launch_arguments(&other, Some("ClaudeGraft.Claude-2")),
            vec![
                format!("--user-data-dir={}", other.display()),
                "--class=ClaudeGraft.Claude-2".to_string()
            ]
        );
    }
}
