//! Reading and writing the small JSON files everything here keeps.

use serde_json::{Map, Value};
use std::fs;
use std::io::Write;
use std::os::unix::fs::{MetadataExt, PermissionsExt};
use std::path::Path;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

/// Write a file so a reader in another process sees either the old bytes or
/// the new ones, never a truncated half: launchers read these while the
/// window writes them, and a file that will not parse is read as empty.
///
/// The mode is kept from the file being replaced, or `mode` when there was
/// none, so a file Claude made private stays private once Graft writes it.
pub fn atomic_write(path: &Path, data: &[u8], mode: u32) -> std::io::Result<()> {
    let dir = path.parent().unwrap_or(Path::new("."));
    fs::create_dir_all(dir)?;
    let keep = fs::metadata(path).map(|m| m.mode() & 0o7777).unwrap_or(mode);
    let mut temp = tempfile_in(dir, path)?;
    temp.1.write_all(data)?;
    temp.1.sync_all()?;
    fs::set_permissions(&temp.0, fs::Permissions::from_mode(keep))?;
    fs::rename(&temp.0, path)
}

fn tempfile_in(dir: &Path, target: &Path) -> std::io::Result<(std::path::PathBuf, fs::File)> {
    let base = target.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    for attempt in 0..100u32 {
        let name = format!(".{base}.{}.{attempt}.tmp", std::process::id());
        let candidate = dir.join(name);
        match fs::OpenOptions::new().write(true).create_new(true).open(&candidate) {
            Ok(file) => return Ok((candidate, file)),
            Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists => continue,
            Err(e) => return Err(e),
        }
    }
    Err(std::io::Error::other("no temporary name was free"))
}

/// JSON with its keys sorted, so writing back exactly what was read leaves
/// the bytes unchanged and a check comparing before and after can tell a real
/// change from a reordering.
pub fn sorted_json(value: &Value) -> Vec<u8> {
    fn sort(value: &Value) -> Value {
        match value {
            Value::Object(map) => {
                let mut keys: Vec<&String> = map.keys().collect();
                keys.sort();
                let mut out = Map::new();
                for key in keys {
                    out.insert(key.clone(), sort(&map[key]));
                }
                Value::Object(out)
            }
            Value::Array(items) => Value::Array(items.iter().map(sort).collect()),
            other => other.clone(),
        }
    }
    serde_json::to_vec(&sort(value)).unwrap_or_default()
}

pub fn write_json(path: &Path, value: &Value) -> std::io::Result<()> {
    atomic_write(path, &sorted_json(value), 0o644)
}

/// What a JSON file says, telling apart the three answers that matter: there
/// is no file, there is one and it says this, there is one and it cannot be
/// read. An unreadable file is not an empty one, and treating it as one is how
/// a file somebody else is halfway through writing gets saved over.
#[derive(Debug, Clone, PartialEq)]
pub enum JsonRead {
    Missing,
    Value(Value),
    Unreadable,
}

pub fn read_json(path: &Path) -> JsonRead {
    match fs::read(path) {
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => JsonRead::Missing,
        Err(_) => JsonRead::Unreadable,
        Ok(bytes) => match serde_json::from_slice::<Value>(&bytes) {
            Ok(value) => JsonRead::Value(value),
            Err(_) => JsonRead::Unreadable,
        },
    }
}

/// A profile's `config.json` as an object, or `None` when it exists and cannot
/// be read. A missing file is an empty object, since a profile Claude has not
/// started in yet has none.
pub fn readable_config_json(profile: &Path) -> Option<Map<String, Value>> {
    match read_json(&profile.join("config.json")) {
        JsonRead::Missing => Some(Map::new()),
        JsonRead::Value(Value::Object(map)) => Some(map),
        _ => None,
    }
}

/// The same, for a caller that only reads and can live with an empty answer.
pub fn config_json(profile: &Path) -> Map<String, Value> {
    readable_config_json(profile).unwrap_or_default()
}

/// The account a profile is signed into, as Claude last recorded it.
pub fn account_of(profile: &Path) -> Option<String> {
    config_json(profile)
        .get("lastKnownAccountUuid")
        .and_then(Value::as_str)
        .filter(|s| !s.is_empty())
        .map(str::to_string)
}

/// A file's modification time from one `lstat`, which does not follow a link
/// and reads nothing else.
pub fn modification_time(path: &Path) -> Option<SystemTime> {
    let meta = fs::symlink_metadata(path).ok()?;
    let secs = meta.mtime();
    let nanos = meta.mtime_nsec();
    if secs < 0 {
        return None;
    }
    Some(UNIX_EPOCH + Duration::new(secs as u64, nanos.clamp(0, 999_999_999) as u32))
}

pub fn is_symlink(path: &Path) -> bool {
    fs::symlink_metadata(path).map(|m| m.file_type().is_symlink()).unwrap_or(false)
}

/// Present, or a link whose target is missing, which is still something
/// sitting at that name.
pub fn exists(path: &Path) -> bool {
    path.exists() || is_symlink(path)
}

pub fn is_directory(path: &Path) -> bool {
    path.is_dir()
}

/// The names in a folder, hidden ones left out, sorted so every walk visits
/// them in the same order. `None` when the folder could not be read at all,
/// which is a different answer from an empty folder.
pub fn listing(dir: &Path) -> Option<Vec<String>> {
    let entries = fs::read_dir(dir).ok()?;
    let mut names: Vec<String> = entries
        .filter_map(|e| e.ok())
        .map(|e| e.file_name().to_string_lossy().into_owned())
        .filter(|n| !n.starts_with('.'))
        .collect();
    names.sort();
    Some(names)
}

pub fn digest(data: &[u8]) -> String {
    use sha2::{Digest, Sha256};
    hex::encode(Sha256::digest(data))
}
