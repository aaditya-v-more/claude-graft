//! A durable note of what each pass saw and what it did about it.
//!
//! Every rule about chat stores turns on telling two situations apart that
//! look identical from inside the code, and getting one wrong shows up hours
//! later naming none of it. The launchers write here too, and they are the
//! reason this exists rather than a debug print: a launcher runs the sweep in
//! front of a window opening, when nobody is looking at a terminal, and it is
//! gone by the time anyone asks what it saw.

use crate::paths;
use serde_json::{Map, Value};
use std::io::Write;
use std::os::unix::fs::OpenOptionsExt;
use std::path::PathBuf;
use std::sync::{Mutex, RwLock};

/// Kept small enough to read and to paste. A pass writes a handful of lines,
/// so this is months of them.
pub const SIZE_LIMIT: u64 = 4 * 1024 * 1024;

static WHO: RwLock<Option<String>> = RwLock::new(None);
static WRITE: Mutex<()> = Mutex::new(());

pub fn file() -> PathBuf {
    paths::graft_file("diagnostics.log")
}

/// What is true now, rewritten in full each pass. The log says what happened;
/// this says the current shape of things, which is the question asked first.
pub fn report_file() -> PathBuf {
    paths::graft_file("state-report.txt")
}

/// Which process is speaking. A launcher and the app run the same sweep, and
/// knowing which one filed a record is most of knowing why it filed it then.
pub fn set_who(who: &str) {
    *WHO.write().unwrap() = Some(who.to_string());
}

pub fn who() -> String {
    WHO.read().unwrap().clone().unwrap_or_else(|| {
        std::env::args()
            .next()
            .map(|a| a.rsplit('/').next().unwrap_or(&a).to_string())
            .unwrap_or_else(|| "graft".into())
    })
}

pub fn now_stamp() -> String {
    let now = time::OffsetDateTime::now_utc();
    let format = time::macros::format_description!(
        "[year]-[month]-[day]T[hour]:[minute]:[second].[subsecond digits:3]Z"
    );
    now.format(&format).unwrap_or_default()
}

/// Write one event down. Never fails, never blocks on anything but the file,
/// and is safe to call midway through moving a profile's chats around, which
/// is exactly when it is worth calling.
pub fn note(event: &str, fields: &[(&str, Value)]) {
    let mut line = Map::new();
    for (key, value) in fields {
        line.insert((*key).to_string(), value.clone());
    }
    line.insert("at".into(), now_stamp().into());
    line.insert("who".into(), who().into());
    line.insert("pid".into(), std::process::id().into());
    line.insert("event".into(), event.into());
    let mut bytes = crate::files::sorted_json(&Value::Object(line));
    bytes.push(b'\n');

    let _guard = WRITE.lock().unwrap_or_else(|e| e.into_inner());
    let path = file();
    rotate_if_large(&path);
    if let Some(dir) = path.parent() {
        let _ = std::fs::create_dir_all(dir);
    }
    // O_APPEND rather than a handle held open, because the app and any number
    // of launchers write here at once, each of them short-lived.
    if let Ok(mut f) =
        std::fs::OpenOptions::new().append(true).create(true).mode(0o644).open(&path)
    {
        let _ = f.write_all(&bytes);
    }
}

/// One generation kept. The interesting window is the last few passes.
fn rotate_if_large(path: &PathBuf) {
    let Ok(meta) = std::fs::symlink_metadata(path) else { return };
    if meta.len() <= SIZE_LIMIT {
        return;
    }
    let previous = path.with_file_name("diagnostics.log.1");
    let _ = std::fs::remove_file(&previous);
    let _ = std::fs::rename(path, previous);
}

/// Replace the state report with `text`.
pub fn write_report(text: &str) {
    let _ = crate::files::atomic_write(&report_file(), text.as_bytes(), 0o644);
}
