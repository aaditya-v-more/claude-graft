//! The places a Linux machine keeps homes and drives, for telling a session
//! from another machine apart from one whose folder was tidied away.

use super::*;
use crate::session_folder_is_here;

#[test]
fn a_mac_home_or_drive_is_somewhere_else_on_linux() {
    let _world = World::new();
    assert!(!session_folder_is_here("/Users/aaditya/insta"), "a Mac's home folder is never on a Linux machine");
    assert!(!session_folder_is_here("/Volumes/SSD/Developer/x"), "nor is a Mac's drive");
}

#[test]
fn a_home_or_drive_that_is_not_here_says_the_session_happened_elsewhere() {
    let _world = World::new();
    let nobody = "graft-test-nobody-by-this-name";
    assert!(!session_folder_is_here(&format!("/home/{nobody}/work")), "a home folder this machine lacks");
    assert!(!session_folder_is_here(&format!("/var/home/{nobody}/work")), "including where atomic Fedora keeps homes");
    assert!(
        !session_folder_is_here(&format!("/run/media/{nobody}/Drive/work")),
        "a drive udisks would mount, from someone who is not here"
    );
    assert!(!session_folder_is_here(&format!("/mnt/{nobody}/work")), "a drive mounted by hand that is not mounted");
}

#[test]
fn a_folder_gone_from_a_place_that_is_here_is_still_this_machines_history() {
    let world = World::new();
    // A worktree cleaned up after its chat closed is gone while the place it
    // sat in is not, and the sidebar still reads that chat.
    let home = world.home();
    let gone = home.join("project/.worktrees/cleaned-up");
    assert!(!gone.exists());
    assert!(session_folder_is_here(&paths::text(&gone)), "a cleaned-up worktree is not another machine");
    assert!(session_folder_is_here("/tmp/graft-test-never-made/x"), "nor is a folder in /tmp that never existed");
    assert!(session_folder_is_here("relative/path"), "a path outside every home and drive files as it always did");
    assert!(session_folder_is_here(""), "and so does no path at all");
}

#[test]
fn a_claude_on_any_profile_counts_as_running() {
    let world = World::new();
    let elsewhere = world.home().join("somewhere/Claude-Outside");
    running::set_processes_override(Some(vec![running::stand_in(42, &elsewhere)]));
    let up = running::running_claudes();
    assert_eq!(up.len(), 1, "a Claude on a profile outside ~/.config still counts");
    assert!(paths::same_path(&up[0], &elsewhere), "and is named by the profile it has open");
    running::set_processes_override(Some(vec![running::stand_in(43, &paths::main_profile())]));
    assert!(running::is_running(&paths::main_profile()), "a Claude with no data folder is the main profile");
}
