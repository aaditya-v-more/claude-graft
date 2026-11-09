//! The built launcher, run the way a dock runs it, against a sandboxed home
//! and a stand-in for Claude's launcher that only writes down what it was
//! asked. It never starts a real Claude: host commands run inside the suite's
//! sandbox and the stand-in exits at once.

use std::path::Path;
use std::process::Command;

fn launcher(home: &Path) -> Command {
    let mut cmd = Command::new(env!("CARGO_BIN_EXE_graft-launch"));
    cmd.env("GRAFT_RUN_DIRECTLY", "1")
        .env("GRAFT_HOME", home)
        .env("GRAFT_CONFIG_HOME", home.join(".config"))
        .env("GRAFT_DATA_HOME", home.join(".local/share"))
        .env("GRAFT_CLAUDE_LAUNCHER", home.join("stand-in"));
    cmd
}

fn sandbox() -> graft_core::testing::tempfile_lite::TempDir {
    let dir = graft_core::testing::tempfile_lite::TempDir::new();
    let home = dir.path();
    std::fs::create_dir_all(home.join(".config")).unwrap();
    std::fs::create_dir_all(home.join(".local/share")).unwrap();
    let stand_in = home.join("stand-in");
    std::fs::write(&stand_in, "#!/bin/sh\nprintf '%s\\n' \"$*\" >> \"$(dirname \"$0\")/launches.log\"\n").unwrap();
    use std::os::unix::fs::PermissionsExt;
    std::fs::set_permissions(&stand_in, std::fs::Permissions::from_mode(0o755)).unwrap();
    dir
}

#[test]
fn a_shortcut_whose_description_is_gone_opens_nothing_and_says_where_it_looked() {
    let dir = sandbox();
    let out = launcher(dir.path()).arg(dir.path().join("missing/graft.json")).output().unwrap();
    assert_eq!(out.status.code(), Some(1), "a missing description is a failure");
    let said = String::from_utf8_lossy(&out.stderr);
    assert!(said.contains("no profile description at"), "the launcher names what it could not read: {said}");
    assert!(!dir.path().join("launches.log").exists(), "a missing shortcut is never permission to open another profile");
}

#[test]
fn a_shortcut_starts_its_own_profile_through_claudes_launcher() {
    let dir = sandbox();
    let home = dir.path();
    let profile = home.join(".config/Claude-2");
    let config = home.join("graft.json");
    std::fs::write(&config, format!("{{\"profileDir\":\"{}\"}}", profile.display())).unwrap();
    let out = launcher(home).arg(&config).output().unwrap();
    assert!(out.status.success(), "the launcher succeeds: {}", String::from_utf8_lossy(&out.stderr));
    let log = home.join("launches.log");
    let deadline = std::time::Instant::now() + std::time::Duration::from_secs(10);
    while !log.exists() && std::time::Instant::now() < deadline {
        std::thread::sleep(std::time::Duration::from_millis(50));
    }
    let launches = std::fs::read_to_string(&log).unwrap_or_default();
    assert!(
        launches.contains(&format!("--user-data-dir={}", profile.display())),
        "Claude is started on the shortcut's own profile: {launches}"
    );
    assert!(profile.is_dir(), "the profile folder is made before Claude starts in it");
}

#[test]
fn no_argument_is_a_usage_error_not_an_open() {
    let dir = sandbox();
    let out = launcher(dir.path()).output().unwrap();
    assert_eq!(out.status.code(), Some(2));
    assert!(!dir.path().join("launches.log").exists());
}
