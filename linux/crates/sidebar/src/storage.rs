//! One read or one write of every participant's sidebar, through the helper.
//!
//! The helper only reads and writes the browser records it is handed. What
//! goes into them, and the settings file beside them, is decided and written
//! here, so a refusal from any one profile is found before anything is
//! written to any of them.

use crate::helper;
use crate::records::{self, Change, Raw};
use crate::snapshot::Snapshot;
use crate::Failure;
use graft_core::{diagnostics, paths};
use serde_json::{json, Value};
use std::collections::{BTreeSet, HashMap};
use std::fs;
use std::io::Write;
use std::os::unix::fs::{MetadataExt, OpenOptionsExt, PermissionsExt};
use std::path::{Path, PathBuf};
use std::sync::Mutex;

pub const SETTINGS: &str = "claude_desktop_config.json";

/// What one read found in one profile, kept so the write that follows can
/// be checked against it and roll back to exactly it.
#[derive(Debug, Clone)]
struct Observed {
    path: String,
    origin: String,
    raw: Raw,
    prefs_text: Option<String>,
    prefs_mode: u32,
    prefs: Value,
    snapshot: Snapshot,
}

/// The last read's observations. A write names what it expects by the
/// fingerprint the read handed out, as the helper's own request did on a
/// Mac, and this is where that fingerprint's records are found again.
static OBSERVED: Mutex<Vec<Observed>> = Mutex::new(Vec::new());

/// A pass's own folder, for the request, the reply and the helper's
/// disposable browser files, removed whatever happens.
struct Scratch(PathBuf);

impl Scratch {
    fn new() -> Result<Scratch, Failure> {
        let path = paths::graft_file(&format!(".sidebar-{}", uuid::Uuid::new_v4()));
        fs::create_dir_all(&path).map_err(|_| Failure::new("storage-unavailable"))?;
        let _ = fs::set_permissions(&path, fs::Permissions::from_mode(0o700));
        Ok(Scratch(path))
    }
}

impl Drop for Scratch {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

/// Read every profile's sidebar, or write `changes` into each and hand back
/// what each now holds.
pub fn storage(
    profiles: &[PathBuf],
    changes: Option<&HashMap<String, Snapshot>>,
    shared: &HashMap<String, BTreeSet<String>>,
) -> Result<HashMap<String, Snapshot>, Failure> {
    let runtime = helper::prepare_helper()?;
    let backups = helper::backups_dir();
    fs::create_dir_all(&backups).map_err(|_| Failure::new("storage-unavailable"))?;
    let _ = fs::set_permissions(&backups, fs::Permissions::from_mode(0o700));
    helper::prune_backups(&backups, helper::KEPT_BACKUPS);
    match changes {
        None => {
            let observed = read(&runtime, profiles)?;
            Ok(observed.into_iter().map(|o| (o.path, o.snapshot)).collect())
        }
        Some(changes) => write(&runtime, profiles, changes, shared, &backups),
    }
}

fn reply_failure(reply: &Value) -> Failure {
    Failure(reply["error"].as_str().unwrap_or("storage-unavailable").to_string())
}

fn read(runtime: &Path, profiles: &[PathBuf]) -> Result<Vec<Observed>, Failure> {
    let scratch = Scratch::new()?;
    let output = scratch.0.join("result.json");
    let request = json!({"action": "read", "scratch": paths::text(&scratch.0), "output": paths::text(&output),
        "profiles": profiles.iter().map(|p| json!({"path": paths::text(p)})).collect::<Vec<_>>()});
    let reply = helper::run(runtime, &request, &scratch.0)?;
    if reply["ok"] != json!(true) {
        return Err(reply_failure(&reply));
    }
    let found = reply["profiles"].as_array().ok_or_else(|| Failure::new("storage-unavailable"))?;
    if found.len() != profiles.len() {
        return Err(Failure::new("storage-unavailable"));
    }
    let mut observed = Vec::new();
    for item in found {
        let path = item["path"].as_str().ok_or_else(|| Failure::new("storage-unavailable"))?.to_string();
        let origin = item["origin"].as_str().ok_or_else(|| Failure::new("storage-unavailable"))?.to_string();
        let raw: Raw = serde_json::from_value(item["raw"].clone()).map_err(|_| Failure::new("unsupported-storage"))?;
        let (prefs_text, prefs_mode, prefs) = read_settings(Path::new(&path))?;
        let mut snapshot = records::snapshot_of(&raw)?;
        snapshot.fingerprint = records::fingerprint(&raw, prefs_text.as_deref(), &origin);
        observed.push(Observed { path, origin, raw, prefs_text, prefs_mode, prefs, snapshot });
    }
    *OBSERVED.lock().unwrap_or_else(|e| e.into_inner()) = observed.clone();
    Ok(observed)
}

/// The settings file as text, its mode, and what it says. A link is refused:
/// writing through one would change another profile's permission choices.
fn read_settings(profile: &Path) -> Result<(Option<String>, u32, Value), Failure> {
    let file = profile.join(SETTINGS);
    match fs::symlink_metadata(&file) {
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok((None, 0o600, json!({}))),
        Err(_) => Err(Failure::new("unreadable-storage")),
        Ok(meta) if !meta.is_file() => Err(Failure::new("unsupported-storage")),
        Ok(meta) => {
            let text = fs::read_to_string(&file).map_err(|_| Failure::new("unreadable-storage"))?;
            let value = serde_json::from_str(&text).map_err(|_| Failure::new("unreadable-storage"))?;
            Ok((Some(text), meta.mode() & 0o777, value))
        }
    }
}

fn current_settings_text(profile: &Path) -> Result<Option<String>, Failure> {
    match fs::read_to_string(profile.join(SETTINGS)) {
        Ok(text) => Ok(Some(text)),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(None),
        Err(_) => Err(Failure::new("unreadable-storage")),
    }
}

/// The mode given when a file is created passes through the umask, so a
/// file that was wider than the umask allows would come back narrower
/// without the explicit chmod. A failed write leaves nothing behind in a
/// folder Claude reads.
pub fn atomic_write_text(file: &Path, text: &str, mode: u32) -> std::io::Result<()> {
    let name = file.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    let temporary = file.with_file_name(format!("{name}.graft-{}", uuid::Uuid::new_v4()));
    let result = (|| {
        let mut handle = fs::OpenOptions::new().write(true).create_new(true).mode(mode).open(&temporary)?;
        handle.write_all(text.as_bytes())?;
        handle.sync_all()?;
        fs::set_permissions(&temporary, fs::Permissions::from_mode(mode))?;
        fs::rename(&temporary, file)
    })();
    if result.is_err() {
        let _ = fs::remove_file(&temporary);
    }
    result
}

struct Plan {
    observed: Observed,
    next: Raw,
    next_text: String,
}

fn lookup(fingerprint: &str) -> Option<Observed> {
    OBSERVED.lock().unwrap_or_else(|e| e.into_inner()).iter().find(|o| o.snapshot.fingerprint == fingerprint).cloned()
}

/// The text as it was read, not the object it parsed to: a rollback that
/// reformats, reorders or drops a duplicate key is a rollback to something
/// the person never wrote.
fn restore_settings(plan: &Plan) {
    let file = Path::new(&plan.observed.path).join(SETTINGS);
    let result = match &plan.observed.prefs_text {
        Some(text) => atomic_write_text(&file, text, plan.observed.prefs_mode),
        None => match fs::remove_file(&file) {
            Err(e) if e.kind() != std::io::ErrorKind::NotFound => Err(e),
            _ => Ok(()),
        },
    };
    if let Err(e) = result {
        diagnostics::note("sidebar.settings-restore-failed", &[
            ("profile", paths::text(Path::new(&plan.observed.path)).into()),
            ("error", e.to_string().into()),
        ]);
    }
}

fn write(
    runtime: &Path,
    profiles: &[PathBuf],
    changes: &HashMap<String, Snapshot>,
    shared: &HashMap<String, BTreeSet<String>>,
    backups: &Path,
) -> Result<HashMap<String, Snapshot>, Failure> {
    let missing = || Failure::new("missing-profile");
    let mut wanted = Vec::new();
    for profile in profiles {
        let path = paths::text(profile);
        let snapshot = changes.get(&path).ok_or_else(missing)?;
        let ids = shared.get(&path).ok_or_else(missing)?;
        wanted.push((path, snapshot, ids));
    }
    if wanted.iter().any(|(_, s, _)| lookup(&s.fingerprint).is_none()) {
        read(runtime, profiles)?;
    }
    // Every participant is checked before the first write.
    let mut plans = Vec::new();
    for (_, snapshot, ids) in &wanted {
        let observed = lookup(&snapshot.fingerprint).ok_or_else(|| Failure::new("changed-during-sync"))?;
        let change = Change::from_snapshot(snapshot, ids);
        let next = records::patch(&observed.raw, &change)?;
        let next_prefs = records::patch_preferences(&observed.prefs, &change)?;
        let next_text = records::preferences_text(observed.prefs_text.as_deref(), &next_prefs);
        plans.push(Plan { observed, next, next_text });
    }

    // The saved values make an interrupted pass recoverable without copying
    // another account's browser database over this one.
    let backup: Vec<Value> = plans
        .iter()
        .map(|p| json!({"profile": p.observed.path, "origin": p.observed.origin, "raw": p.observed.raw,
            "preferences": p.observed.prefs.get("preferences").and_then(|v| v.get("epitaxyPrefs")).cloned()}))
        .collect();
    let backup_file = backups.join(format!("{}.json", uuid::Uuid::new_v4()));
    atomic_write_text(&backup_file, &Value::Array(backup).to_string(), 0o600)
        .map_err(|_| Failure::new("backup-failed"))?;

    // The settings first: they are Graft's to write and to put back byte for
    // byte, so a refusal from the databases leaves nothing of the pass behind.
    for (index, plan) in plans.iter().enumerate() {
        let profile = Path::new(&plan.observed.path);
        let unchanged = current_settings_text(profile).map(|t| t == plan.observed.prefs_text);
        let outcome = match unchanged {
            Ok(true) => atomic_write_text(&profile.join(SETTINGS), &plan.next_text, plan.observed.prefs_mode)
                .map_err(|_| Failure::new("write-failed")),
            Ok(false) => Err(Failure::new("changed-during-sync")),
            Err(failure) => Err(failure),
        };
        if let Err(failure) = outcome {
            plans[..index].iter().for_each(restore_settings);
            return Err(failure);
        }
    }

    let scratch = Scratch::new()?;
    let output = scratch.0.join("result.json");
    let request = json!({"action": "write", "scratch": paths::text(&scratch.0), "output": paths::text(&output),
        "profiles": plans.iter().map(|p| json!({"path": p.observed.path, "origin": p.observed.origin,
            "expected": p.observed.raw, "next": p.next})).collect::<Vec<_>>()});
    let reply = match helper::run(runtime, &request, &scratch.0) {
        Ok(reply) => reply,
        Err(failure) => {
            // Which databases were written is not known; the settings go
            // back, and the next pass sees both and brings them to agree.
            plans.iter().for_each(restore_settings);
            return Err(failure);
        }
    };
    let written: BTreeSet<String> = reply["written"]
        .as_array()
        .map(|items| items.iter().filter_map(|v| v.as_str().map(str::to_string)).collect())
        .unwrap_or_default();
    if reply["ok"] != json!(true) {
        plans.iter().filter(|p| !written.contains(&p.observed.path)).for_each(restore_settings);
        return Err(reply_failure(&reply));
    }
    OBSERVED.lock().unwrap_or_else(|e| e.into_inner()).clear();
    let mut result = HashMap::new();
    for plan in plans {
        if !written.contains(&plan.observed.path) {
            return Err(Failure::new("verification-failed"));
        }
        let mut snapshot = records::snapshot_of(&plan.next)?;
        snapshot.fingerprint = records::fingerprint(&plan.next, Some(&plan.next_text), &plan.observed.origin);
        result.insert(plan.observed.path.clone(), snapshot);
    }
    Ok(result)
}

#[cfg(test)]
mod tests {
    use super::*;
    use graft_core::files;
    use graft_core::testing::Sandbox;

    #[test]
    fn a_settings_file_written_back_keeps_its_permissions_and_leaves_nothing_beside_it() {
        let sandbox = Sandbox::new();
        let folder = sandbox.root.path().join("profile");
        fs::create_dir_all(&folder).unwrap();
        let file = folder.join(SETTINGS);
        fs::write(&file, "{}").unwrap();
        fs::set_permissions(&file, fs::Permissions::from_mode(0o664)).unwrap();
        let mode = fs::metadata(&file).unwrap().mode() & 0o777;
        atomic_write_text(&file, "{ \"kept\": true }", mode).unwrap();
        assert_eq!(fs::metadata(&file).unwrap().mode() & 0o777, 0o664);
        assert_eq!(fs::read_to_string(&file).unwrap(), "{ \"kept\": true }");
        assert_eq!(files::listing(&folder).unwrap(), vec![SETTINGS.to_string()]);
        let names: Vec<String> = fs::read_dir(&folder).unwrap()
            .map(|e| e.unwrap().file_name().to_string_lossy().into_owned()).collect();
        assert_eq!(names, vec![SETTINGS.to_string()], "no temporary file is left behind");
    }

    #[test]
    fn a_settings_file_rolled_back_is_written_back_byte_for_byte() {
        let sandbox = Sandbox::new();
        let folder = sandbox.root.path().join("profile");
        fs::create_dir_all(&folder).unwrap();
        let original = "{\n   \"a\": 1,\n   \"a\": 2\n}";
        fs::write(folder.join(SETTINGS), "{\"changed\":true}").unwrap();
        let plan = Plan {
            observed: Observed {
                path: paths::text(&folder), origin: "https://claude.ai".into(),
                raw: Raw { pin: None, local: Default::default() },
                prefs_text: Some(original.into()), prefs_mode: 0o640, prefs: json!({"a": 2}),
                snapshot: Snapshot::default(),
            },
            next: Raw { pin: None, local: Default::default() },
            next_text: String::new(),
        };
        restore_settings(&plan);
        assert_eq!(fs::read_to_string(folder.join(SETTINGS)).unwrap(), original,
            "a repeated key and an odd indent come back as they were written");
        assert_eq!(fs::metadata(folder.join(SETTINGS)).unwrap().mode() & 0o777, 0o640);

        let mut created = plan;
        created.observed.prefs_text = None;
        restore_settings(&created);
        assert!(!folder.join(SETTINGS).exists(), "a settings file the pass created is taken away again");
    }

    #[test]
    fn a_settings_file_that_is_a_link_is_never_written_through() {
        let sandbox = Sandbox::new();
        let folder = sandbox.root.path().join("profile");
        fs::create_dir_all(&folder).unwrap();
        fs::write(sandbox.root.path().join("other.json"), "{}").unwrap();
        std::os::unix::fs::symlink(sandbox.root.path().join("other.json"), folder.join(SETTINGS)).unwrap();
        assert_eq!(read_settings(&folder).unwrap_err(), Failure::new("unsupported-storage"));
        fs::remove_file(folder.join(SETTINGS)).unwrap();
        fs::write(folder.join(SETTINGS), "{ incomplete").unwrap();
        assert_eq!(read_settings(&folder).unwrap_err(), Failure::new("unreadable-storage"),
            "an unreadable settings file is not an empty one");
        fs::remove_file(folder.join(SETTINGS)).unwrap();
        assert_eq!(read_settings(&folder).unwrap(), (None, 0o600, json!({})), "a missing one is");
    }
}
