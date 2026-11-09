//! The two lines that send a Claude's Claude Code through the relay, put into
//! that Claude's own Code environment and taken out again.
//!
//! Claude keeps that environment in `ccd-environment-config.json` inside the
//! profile: `envVars` is the base64 of what Electron's safe storage made of a
//! JSON object of names and values, and it is what the Code tab's own editor
//! shows and saves. The running app decrypts it once and keeps the result, and
//! its editor saves over the whole field, so a write landing while that Claude
//! is open is not seen and is then written over. Every write here happens with
//! the profile closed, under the launch lock every route that opens a Claude
//! takes.
//!
//! Sealing needs Claude's safe-storage key, and on most desktops that key is
//! behind the keyring, so a launcher asking for it would put an unlock dialog
//! in front of every shortcut. So Graft does the reading and the sealing, and
//! leaves two finished values per profile in the journal: the field as it
//! stands without the lines, and the same with them. Putting one in place of
//! the other is all a launcher does, and only while the file still holds
//! exactly the one expected; anything else is an edit Graft has not read yet,
//! and is left for Graft to read.
//!
//! This is the one source that names the file, and the suite fails if
//! another does.

use crate::wire::{self, PoolCommand};
use graft_core::{diagnostics, files, host, lock, paths};
use serde::{Deserialize, Serialize};
use serde_json::{json, Map, Value};
use std::collections::BTreeMap;
use std::io::Write;
use std::os::unix::fs::{OpenOptionsExt, PermissionsExt};
use std::path::{Component, Path, PathBuf};
use std::process::{Command, Stdio};
use std::time::Duration;

pub const FILE_NAME: &str = "ccd-environment-config.json";
pub const FIELD: &str = "envVars";
pub const SCOPED_FIELD: &str = "scopedEnvVars";
pub const PROXY_NAME: &str = "HTTPS_PROXY";
pub const CERTIFICATE_NAME: &str = "NODE_EXTRA_CA_CERTS";

pub fn proxy_value() -> String {
    format!("http://127.0.0.1:{}", crate::PORT)
}

pub fn certificate_value() -> String {
    paths::text(&crate::certificate_file())
}

pub fn lines() -> BTreeMap<String, String> {
    BTreeMap::from([
        (PROXY_NAME.to_string(), proxy_value()),
        (CERTIFICATE_NAME.to_string(), certificate_value()),
    ])
}

pub fn journal_file() -> PathBuf {
    crate::directory().join("environment.json")
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct Journal {
    /// The relay a launcher may start, as the Graft that wrote this knows it.
    /// A launcher has no other way to find one.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub relay: Option<String>,
    /// By profile path.
    #[serde(default)]
    pub profiles: BTreeMap<String, Entry>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Entry {
    pub wanted: bool,
    /// The field exactly as it reads without the lines. `None` is no field.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub plain: Option<String>,
    pub pooled: String,
}

/// The field as it is on disk, read without decrypting anything.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Field {
    Absent,
    Value(String),
    /// A file that will not parse, or one Claude's own store is part way
    /// through writing: nothing is decided on it and nothing is written.
    Unreadable,
}

impl Field {
    pub fn value(&self) -> Option<&str> {
        match self {
            Field::Value(text) => Some(text),
            _ => None,
        }
    }

    pub fn of(value: Option<&str>) -> Field {
        value.map(|v| Field::Value(v.to_string())).unwrap_or(Field::Absent)
    }
}

/// The path a profile is filed under in the journal: written out with `.` and
/// `..` taken away and nothing else, so the same folder named twice is one
/// entry while a link is still the path a person gave.
pub fn key_for(profile: &Path) -> String {
    let mut out = PathBuf::new();
    for part in profile.components() {
        match part {
            Component::CurDir => {}
            Component::ParentDir => {
                out.pop();
            }
            other => out.push(other.as_os_str()),
        }
    }
    let text = paths::text(&out);
    if text.len() > 1 {
        text.trim_end_matches('/').to_string()
    } else {
        text
    }
}

pub fn file_in(profile: &Path) -> PathBuf {
    profile.join(FILE_NAME)
}

/// Claude's store recovers a torn write from this on its next start, so a
/// file with one beside it is about to be replaced by Claude itself.
pub fn is_mid_write(profile: &Path) -> bool {
    let mut journal = file_in(profile).into_os_string();
    journal.push(".journal");
    files::exists(Path::new(&journal))
}

/// The whole file as an object: empty when there is none yet, `None` when it
/// is there and cannot be read, or Claude is part way through writing it.
pub fn outer(profile: &Path) -> Option<Map<String, Value>> {
    if is_mid_write(profile) {
        return None;
    }
    match files::read_json(&file_in(profile)) {
        files::JsonRead::Missing => Some(Map::new()),
        files::JsonRead::Value(Value::Object(map)) => Some(map),
        _ => None,
    }
}

pub fn field(profile: &Path) -> Field {
    let Some(outer) = outer(profile) else { return Field::Unreadable };
    match outer.get(FIELD) {
        None | Some(Value::Null) => Field::Absent,
        Some(Value::String(text)) => Field::Value(text.clone()),
        Some(_) => Field::Unreadable,
    }
}

/// Replaces the one field and nothing else, by renaming a complete file over
/// the old one, which is how Claude's own store writes it too.
pub fn write(value: Option<&str>, name: &str, profile: &Path) -> bool {
    let Some(mut object) = outer(profile) else { return false };
    match value {
        Some(text) => {
            object.insert(name.to_string(), Value::String(text.to_string()));
        }
        None => {
            object.remove(name);
        }
    }
    let sorted: Value = serde_json::from_slice(&files::sorted_json(&Value::Object(object))).unwrap_or(Value::Null);
    let Ok(data) = serde_json::to_vec_pretty(&sorted) else { return false };
    let target = file_in(profile);
    let temporary = profile.join(format!(".{FILE_NAME}.graft-{}", uuid::Uuid::new_v4().simple()));
    let written = std::fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .mode(0o600)
        .open(&temporary)
        .and_then(|mut f| f.write_all(&data).and_then(|_| f.sync_all()));
    if written.is_err() {
        let _ = std::fs::remove_file(&temporary);
        return false;
    }
    let _ = std::fs::set_permissions(&temporary, std::fs::Permissions::from_mode(0o600));
    if std::fs::rename(&temporary, &target).is_err() {
        let _ = std::fs::remove_file(&temporary);
        return false;
    }
    true
}

/// The field of `envVars` alone, the one a launcher swaps.
pub fn write_field(value: Option<&str>, profile: &Path) -> bool {
    write(value, FIELD, profile)
}

pub fn read_journal() -> Journal {
    journal_on_disk().unwrap_or_default()
}

/// `None` when there is no journal, or none that reads: once Graft and its
/// folder are gone, the relay works from the copy it read last.
pub fn journal_on_disk() -> Option<Journal> {
    let bytes = std::fs::read(journal_file()).ok()?;
    serde_json::from_slice(&bytes).ok()
}

pub fn write_journal(journal: &Journal) {
    let dir = crate::directory();
    if std::fs::create_dir_all(&dir).is_ok() {
        let _ = std::fs::set_permissions(&dir, std::fs::Permissions::from_mode(0o700));
    }
    let Ok(data) = serde_json::to_vec(journal) else { return };
    let _ = files::atomic_write(&journal_file(), &data, 0o600);
}

fn note(event: &str, profile: &Path, extra: &[(&str, Value)]) {
    let name = profile.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    let mut fields: Vec<(&str, Value)> = vec![("profile", json!(name))];
    fields.extend(extra.iter().cloned());
    diagnostics::note(event, &fields);
}

/// Puts the right value in place for a profile about to be opened. Runs under
/// the launch lock with the profile known to be closed.
///
/// `relay_ready` is asked only when the lines are, or are about to be, in
/// place: they point Claude Code at a proxy, and a proxy that is not there
/// leaves it unable to reach the service at all. A Claude that cannot have its
/// relay opens without the lines rather than without Claude Code.
pub fn settle_before_opening(profile: &Path, relay_ready: impl FnOnce() -> bool) {
    let journal = read_journal();
    let Some(entry) = journal.profiles.get(&key_for(profile)) else { return };
    let current = field(profile);
    if current == Field::Unreadable {
        note("pool.environment unreadable", profile, &[]);
        return;
    }
    let value = current.value();
    let mut wanted = entry.wanted;
    if wanted && (value == Some(entry.pooled.as_str()) || value == entry.plain.as_deref()) && !relay_ready() {
        wanted = false;
        note("pool.environment no relay", profile, &[]);
    }
    let target = if wanted { Some(entry.pooled.as_str()) } else { entry.plain.as_deref() };
    if value == target {
        return;
    }
    let expected = if wanted { entry.plain.as_deref() } else { Some(entry.pooled.as_str()) };
    if value != expected {
        note("pool.environment changed since read", profile, &[]);
        return;
    }
    let written = write_field(target, profile);
    note("pool.environment written", profile, &[("lines", json!(wanted)), ("ok", json!(written))]);
}

/// What a launcher runs, under the launch lock, with the profile known to be
/// closed, just before it starts Claude there: the lines go in if the relay is
/// there or can be started, and come out if not.
pub fn prepare_for_launch(profile: &Path) {
    settle_before_opening(profile, || ensure_relay(read_journal().relay.as_deref()));
}

/// Puts every closed profile's environment back as it was without the lines,
/// for when Graft itself is going: `graft-launch --stop-sharing`, and the
/// relay once its own installation has been gone a while. Nothing else takes
/// the lines out of Claude's own profile, which has no launcher, and a Claude
/// still holding them after the relay has gone, at the next logout at the
/// latest, has no Claude Code at all. The swap needs no key: the journal holds
/// the finished value.
///
/// Every entry is marked unwanted, so a launcher left behind puts nothing
/// back. A Graft that is still installed sets them again on its next pass.
///
/// Answers the profiles that may still point at the relay: one that is open,
/// which would only write its own copy back, and one whose field is neither
/// value Graft left, which without the key cannot be read. Lines copied into a
/// folder's scoped values cannot be reached this way either.
pub fn take_out_lines(remembered: Option<&Journal>, running: impl Fn(&Path) -> bool) -> Vec<String> {
    let on_disk = journal_on_disk();
    let mut journal = on_disk.clone().or_else(|| remembered.cloned()).unwrap_or_default();
    // Graft's folder has gone. Taking the launch lock or writing a note would
    // make it again, and no launcher can be acting on a journal that is not
    // there.
    let present = paths::graft_folder().exists();
    let mut left = Vec::new();
    let keys: Vec<String> = journal.profiles.keys().cloned().collect();
    for path in keys {
        let entry = journal.profiles[&path].clone();
        let profile = PathBuf::from(&path);
        let restore = || -> bool {
            if !profile.exists() {
                return true;
            }
            if running(&profile) {
                return false;
            }
            let current = field(&profile);
            if current == Field::Unreadable {
                return false;
            }
            if current.value() == entry.plain.as_deref() {
                return true;
            }
            if current.value() != Some(entry.pooled.as_str()) {
                return false;
            }
            let written = write_field(entry.plain.as_deref(), &profile);
            if present {
                note("pool.environment taken out", &profile, &[("ok", json!(written))]);
            }
            written
        };
        let restored = if present { lock::with_launch_lock(restore) } else { restore() };
        if !restored {
            left.push(path.clone());
        }
        if let Some(entry) = journal.profiles.get_mut(&path) {
            entry.wanted = false;
        }
    }
    if on_disk.is_some() {
        write_journal(&journal);
    }
    left
}

/// What a launcher asks: the relay a running Graft keeps up, or one it starts
/// itself from the path Graft left. A relay started this way knows nobody
/// until Graft tells it, and passes everything through untouched.
pub fn ensure_relay(path: Option<&str>) -> bool {
    if relay_answers() {
        return true;
    }
    let Some(path) = path else { return false };
    if !is_executable(Path::new(path)) || !spawn_relay(Path::new(path)) {
        return false;
    }
    for _ in 0..40 {
        std::thread::sleep(Duration::from_millis(100));
        if relay_answers() {
            return true;
        }
    }
    false
}

pub fn relay_answers() -> bool {
    wire::send(&PoolCommand::status(), &crate::socket_file(), Duration::from_secs(2)).is_ok()
}

fn is_executable(path: &Path) -> bool {
    std::fs::metadata(path).map(|m| m.is_file() && m.permissions().mode() & 0o111 != 0).unwrap_or(false)
}

/// The command that starts a relay that outlives whoever started it: a Claude
/// pointed at the relay cannot reach the service without it.
///
/// Outside a Flatpak it is the binary itself in a session of its own. Inside
/// one, a process started in the sandbox dies with it, because the sandbox's
/// first process takes every other with it when it exits, and Graft's sandbox
/// ends when Graft quits or a launcher hands over. So the relay is started
/// through the Flatpak portal as a sandbox of its own, of this same app and
/// with its permissions, without `--watch-bus`, which is what would tie its
/// life to the caller's.
///
/// Named even though they are the defaults, so a Graft pointed somewhere else
/// for a test starts a relay that keeps its socket where that Graft will look
/// for it.
pub fn relay_command(executable: &Path) -> Command {
    let home = paths::text(&paths::home());
    let lang = std::env::var("LANG").unwrap_or_else(|_| "C.UTF-8".into());
    let arguments = [
        "--config-home".to_string(),
        paths::text(&paths::config_home()),
        "--home".to_string(),
        home.clone(),
    ];
    if host::in_flatpak() {
        let mut command = Command::new("flatpak-spawn");
        command
            .arg("--clear-env")
            .arg(format!("--env=HOME={home}"))
            .arg("--env=PATH=/app/bin:/usr/bin:/bin")
            .arg(format!("--env=LANG={lang}"))
            .arg(executable)
            .args(arguments);
        command
    } else {
        let mut command = Command::new(executable);
        command
            .args(arguments)
            .env_clear()
            .env("HOME", home)
            .env("PATH", "/usr/bin:/bin:/usr/sbin:/sbin")
            .env("LANG", lang);
        use std::os::unix::process::CommandExt;
        // SAFETY: setsid is async-signal-safe, which is all pre_exec asks.
        unsafe {
            command.pre_exec(|| {
                libc::setsid();
                Ok(())
            });
        }
        command
    }
}

/// Starts the relay and lets it go. Every descriptor Graft holds is opened
/// close-on-exec, so none reaches the relay; its output goes nowhere, since
/// nobody reads it, and it says what it does in the diagnostics log instead.
pub fn spawn_relay(executable: &Path) -> bool {
    let mut command = relay_command(executable);
    command.stdin(Stdio::null()).stdout(Stdio::null()).stderr(Stdio::null());
    match command.spawn() {
        Ok(mut child) => {
            // Waited on by a thread of its own, so a relay that exits, or the
            // portal's short-lived client, is not left a zombie of a Graft
            // that keeps running.
            std::thread::spawn(move || {
                let _ = child.wait();
            });
            true
        }
        Err(e) => {
            diagnostics::note("relay spawn failed", &[("error", json!(e.to_string()))]);
            false
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use graft_core::testing::Sandbox;

    fn profile(sandbox: &Sandbox, name: &str, object: Option<Value>) -> PathBuf {
        let url = sandbox.config().join("PoolProfiles").join(name);
        let _ = std::fs::remove_dir_all(&url);
        std::fs::create_dir_all(&url).unwrap();
        if let Some(object) = object {
            std::fs::write(file_in(&url), serde_json::to_vec_pretty(&object).unwrap()).unwrap();
        }
        url
    }

    fn entry(wanted: bool, plain: Option<&str>, pooled: &str) -> Entry {
        Entry { wanted, plain: plain.map(str::to_string), pooled: pooled.to_string() }
    }

    fn journal(entries: &[(&Path, Entry)]) {
        let mut written = Journal::default();
        for (path, entry) in entries {
            written.profiles.insert(key_for(path), entry.clone());
        }
        write_journal(&written);
    }

    fn value(profile: &Path) -> Option<String> {
        field(profile).value().map(str::to_string)
    }

    #[test]
    fn claude_code_is_pointed_at_a_port_on_this_machine_alone_and_told_to_trust_the_relay() {
        let _sandbox = Sandbox::new();
        let lines = lines();
        assert_eq!(lines[PROXY_NAME], "http://127.0.0.1:47823");
        assert_eq!(lines[CERTIFICATE_NAME], paths::text(&crate::certificate_file()));
    }

    #[test]
    fn a_claude_that_wants_the_lines_is_given_them_as_it_opens() {
        let sandbox = Sandbox::new();
        let swap = profile(&sandbox, "swap", Some(json!({"envVars": "P", "other": {"kept": true}})));
        journal(&[(&swap, entry(true, Some("P"), "Q"))]);
        settle_before_opening(&swap, || true);
        assert_eq!(value(&swap).as_deref(), Some("Q"));
        let rest: Value = serde_json::from_slice(&std::fs::read(file_in(&swap)).unwrap()).unwrap();
        assert_eq!(rest["other"]["kept"], json!(true), "and nothing else in the file changes");
        let mode = std::fs::metadata(file_in(&swap)).unwrap().permissions().mode() & 0o777;
        assert_eq!(mode, 0o600, "which stays readable by this user alone");

        settle_before_opening(&swap, || false);
        assert_eq!(value(&swap).as_deref(), Some("P"), "a Claude whose relay cannot be started opens without the lines");

        journal(&[(&swap, entry(false, None, "P"))]);
        let mut asked = false;
        settle_before_opening(&swap, || {
            asked = true;
            true
        });
        let rest: Value = serde_json::from_slice(&std::fs::read(file_in(&swap)).unwrap()).unwrap();
        assert!(
            value(&swap).is_none() && rest.get("other").is_some() && !asked,
            "lines no longer wanted come out, a field that was not there goes, and nobody asks after the relay"
        );
    }

    #[test]
    fn an_environment_changed_since_graft_read_it_is_left_for_graft_to_read_again() {
        let sandbox = Sandbox::new();
        let edited = profile(&sandbox, "edited", Some(json!({"envVars": "edited since"})));
        journal(&[(&edited, entry(true, Some("P"), "Q"))]);
        settle_before_opening(&edited, || true);
        assert_eq!(value(&edited).as_deref(), Some("edited since"));

        let torn = profile(&sandbox, "tornSwap", Some(json!({"envVars": "P"})));
        let mark = torn.join(format!("{FILE_NAME}.journal"));
        std::fs::write(&mark, b"").unwrap();
        journal(&[(&torn, entry(true, Some("P"), "Q"))]);
        settle_before_opening(&torn, || true);
        std::fs::remove_file(&mark).unwrap();
        assert_eq!(value(&torn).as_deref(), Some("P"), "and so is one Claude was part way through writing");
    }

    #[test]
    fn removing_graft_gives_a_closed_claude_back_what_it_held_before_sharing() {
        let sandbox = Sandbox::new();
        let closed = profile(&sandbox, "leftClosed", Some(json!({"envVars": "Q"})));
        let open = profile(&sandbox, "leftOpen", Some(json!({"envVars": "Q2"})));
        let edited = profile(&sandbox, "leftEdited", Some(json!({"envVars": "edited since"})));
        journal(&[
            (&closed, entry(true, Some("P"), "Q")),
            (&open, entry(true, None, "Q2")),
            (&edited, entry(true, Some("P"), "Q3")),
        ]);
        let open_key = key_for(&open);
        let still_held = take_out_lines(None, |p| key_for(p) == open_key);
        assert_eq!(value(&closed).as_deref(), Some("P"));
        let mut held = still_held.clone();
        held.sort();
        let mut expected = vec![key_for(&open), key_for(&edited)];
        expected.sort();
        assert!(
            value(&open).as_deref() == Some("Q2") && value(&edited).as_deref() == Some("edited since") && held == expected,
            "and leaves an open one, and one changed since Graft read it, saying both may still point at the relay"
        );
        assert!(
            read_journal().profiles.values().all(|e| !e.wanted),
            "while nothing is left wanting the lines, so a launcher left behind puts none back"
        );

        let remembered = read_journal();
        std::fs::remove_file(journal_file()).unwrap();
        let still_open = take_out_lines(Some(&remembered), |_| false);
        assert!(
            value(&open).is_none() && still_open == vec![key_for(&edited)] && !journal_file().exists(),
            "a journal remembered after the folder went still serves, and is not written back"
        );
    }

    #[test]
    fn nothing_of_grafts_that_was_removed_is_made_again() {
        let sandbox = Sandbox::new();
        let closed = profile(&sandbox, "closed", Some(json!({"envVars": "Q"})));
        journal(&[(&closed, entry(true, Some("P"), "Q"))]);
        let remembered = read_journal();
        std::fs::remove_dir_all(paths::graft_folder()).unwrap();
        let left = take_out_lines(Some(&remembered), |_| false);
        assert!(left.is_empty() && value(&closed).as_deref() == Some("P"));
        assert!(!paths::graft_folder().exists(), "neither the launch lock nor the log puts the folder back");
    }

    #[test]
    fn a_profile_is_filed_under_one_spelling_of_its_path() {
        assert_eq!(key_for(Path::new("/a/b/../c/./d/")), "/a/c/d");
    }

    #[test]
    fn an_unreadable_environment_is_not_an_empty_one() {
        let sandbox = Sandbox::new();
        let broken = profile(&sandbox, "broken", None);
        std::fs::write(file_in(&broken), b"{not json").unwrap();
        assert_eq!(field(&broken), Field::Unreadable);
        assert!(!write_field(Some("x"), &broken), "and nothing is written over it");
        let missing = profile(&sandbox, "missing", None);
        assert_eq!(field(&missing), Field::Absent, "while a profile with no file yet has no field");
    }
}
