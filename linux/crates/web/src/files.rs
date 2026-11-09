//! What Graft keeps about artifacts, beside the rest of its own state.

use crate::model::{upper, ArtifactAccount, ArtifactGroup};
use graft_core::{diagnostics, paths};
use serde::de::DeserializeOwned;
use serde::Serialize;
use serde_json::{json, Map, Value};
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};
use uuid::Uuid;

pub fn accounts_file() -> PathBuf {
    paths::graft_file("artifact-accounts.json")
}

pub fn groups_file() -> PathBuf {
    paths::graft_file("artifact-groups.json")
}

/// Every account's web store sits under one folder of Graft's, so the launch
/// sweep can see each store without asking WebKit, and nothing of anyone
/// else's is ever in it.
pub fn stores_folder() -> PathBuf {
    paths::graft_file("artifact-stores")
}

pub fn store_folder(id: Uuid) -> PathBuf {
    stores_folder().join(upper(id))
}

pub fn store_data_folder(id: Uuid) -> PathBuf {
    store_folder(id).join("data")
}

pub fn store_cache_folder(id: Uuid) -> PathBuf {
    store_folder(id).join("cache")
}

/// A file that is not there and one that is there but cannot be read are told
/// apart, because only the first may be written over: saving an empty list
/// over one that would not decode throws away every account or group in it,
/// and an account thrown away leaves its store with nobody to remove it.
#[derive(Debug, Clone, PartialEq)]
pub enum Reading<T> {
    Missing,
    Unreadable,
    Read(T),
}

pub fn read<T: DeserializeOwned>(path: &Path) -> Reading<T> {
    let bytes = match std::fs::read(path) {
        Ok(bytes) => bytes,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Reading::Missing,
        Err(_) => return Reading::Unreadable,
    };
    match serde_json::from_slice(&bytes) {
        Ok(value) => Reading::Read(value),
        Err(_) => Reading::Unreadable,
    }
}

/// A list read one entry at a time, so an entry a newer Graft or a damaged
/// write left unreadable costs that entry and not the whole list. The count
/// says how many were left out, which is when the file is set aside before
/// anything is saved over it.
pub fn read_each<T: DeserializeOwned>(path: &Path) -> Reading<(Vec<T>, usize)> {
    match read::<Vec<Value>>(path) {
        Reading::Missing => Reading::Missing,
        Reading::Unreadable => Reading::Unreadable,
        Reading::Read(entries) => {
            let total = entries.len();
            let values: Vec<T> = entries.into_iter().filter_map(|e| serde_json::from_value(e).ok()).collect();
            let lost = total - values.len();
            Reading::Read((values, lost))
        }
    }
}

/// Moves a file that could not be read out of the way, beside where it was,
/// so the next save starts a new one and the old one is still there to be
/// read by hand. `None` when it could not be moved, and then nothing may be
/// saved over it.
pub fn set_aside(path: &Path, now: SystemTime) -> Option<PathBuf> {
    let seconds = now.duration_since(UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0);
    let stem = path.file_stem()?.to_string_lossy().into_owned();
    let ext = path.extension().map(|e| e.to_string_lossy().into_owned()).unwrap_or_default();
    let target = path.with_file_name(format!("{stem}.unreadable-{seconds}.{ext}"));
    std::fs::rename(path, &target).ok().map(|_| target)
}

/// Pretty and with its keys in order, so the file reads by hand and a save of
/// what was read changes no bytes.
pub fn save<T: Serialize>(value: &T, path: &Path) -> bool {
    let Ok(value) = serde_json::to_value(value) else { return false };
    let Ok(bytes) = serde_json::to_vec_pretty(&sorted(&value)) else { return false };
    graft_core::files::atomic_write(path, &bytes, 0o600).is_ok()
}

fn sorted(value: &Value) -> Value {
    match value {
        Value::Object(map) => {
            let mut keys: Vec<&String> = map.keys().collect();
            keys.sort();
            let mut out = Map::new();
            for key in keys {
                out.insert(key.clone(), sorted(&map[key]));
            }
            Value::Object(out)
        }
        Value::Array(items) => Value::Array(items.iter().map(sorted).collect()),
        other => other.clone(),
    }
}

/// The accounts and groups as found at launch, and whether each file may be
/// written: false only when a file that would not be read could not be set
/// aside either, so saving would write over it.
#[derive(Debug, Clone, PartialEq)]
pub struct Loaded {
    pub accounts: Vec<ArtifactAccount>,
    /// Only a list that was read may name the stores that are stray: one set
    /// aside, or never written, may name the accounts those stores belong to.
    pub accounts_were_read: bool,
    pub may_write_accounts: bool,
    pub groups: Vec<ArtifactGroup>,
    pub may_write_groups: bool,
}

pub fn load(now: SystemTime) -> Loaded {
    let mut loaded = Loaded {
        accounts: Vec::new(),
        accounts_were_read: false,
        may_write_accounts: true,
        groups: Vec::new(),
        may_write_groups: true,
    };
    match read::<Vec<ArtifactAccount>>(&accounts_file()) {
        Reading::Read(list) => {
            loaded.accounts = list;
            loaded.accounts_were_read = true;
        }
        Reading::Missing => {}
        Reading::Unreadable => loaded.may_write_accounts = set_aside_noting(&accounts_file(), now),
    }
    match read_each::<ArtifactGroup>(&groups_file()) {
        Reading::Read((groups, lost)) => {
            // The whole file is kept aside before the groups that did read
            // are written back, so the one that did not is still on disk.
            if lost > 0 {
                loaded.may_write_groups = set_aside_noting(&groups_file(), now);
                if loaded.may_write_groups {
                    save(&groups, &groups_file());
                }
            }
            loaded.groups = groups;
        }
        Reading::Missing => {}
        Reading::Unreadable => loaded.may_write_groups = set_aside_noting(&groups_file(), now),
    }
    loaded
}

fn set_aside_noting(path: &Path, now: SystemTime) -> bool {
    let moved = set_aside(path, now);
    let name = |p: &Path| p.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    diagnostics::note(
        "artifacts.file unreadable",
        &[
            ("file", json!(name(path))),
            ("setAside", json!(moved.as_deref().map(name).unwrap_or_else(|| "could not move".into()))),
        ],
    );
    moved.is_some()
}

/// Stores no listed account is under: a removal that would not go, a sign-in
/// window closed before it finished, a Graft that quit before either. Only
/// folders named as a store is named are counted; anything else in the folder
/// is left alone.
pub fn stray_stores(listed: &[Uuid], in_use: impl Fn(Uuid) -> bool) -> Vec<Uuid> {
    let Some(names) = graft_core::files::listing(&stores_folder()) else { return Vec::new() };
    names
        .iter()
        .filter_map(|name| Uuid::parse_str(name).ok().filter(|id| upper(*id) == *name))
        .filter(|id| !listed.contains(id) && !in_use(*id))
        .collect()
}
