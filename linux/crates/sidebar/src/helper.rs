//! The storage helper: a private copy of Claude's own Electron runtime that
//! runs one script of Graft's and nothing of Claude's.
//!
//! On a Mac the copy is an APFS clone of the whole app, free on disk, and the
//! framework has to be sealed and signed again. On Linux there is nothing to
//! seal or sign, and nothing to clone from: Claude's files sit in the host's
//! `/usr`, which on an image-based system is a read-only composefs mount, so a
//! hard link or a reflink is refused and only a real copy works. Only the main
//! binary is copied, because Electron finds its resources from the real path
//! of its own executable (`/proc/self/exe`), so a link to Claude's binary
//! would load Claude's `app.asar`. Everything else the runtime reads beside
//! it (the `.pak` files, ICU data, V8 snapshots, locales, `libffmpeg.so`,
//! `chrome-sandbox`) is linked, since those are opened by path and a link
//! leaves them where the package put them.

use crate::Failure;
use graft_core::{claude, diagnostics, files, host, paths};
use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use std::fs;
use std::io::Read;
use std::os::unix::fs::{MetadataExt, PermissionsExt};
use std::path::{Path, PathBuf};
use std::process::Stdio;
use std::time::{Duration, Instant, SystemTime};

/// The helper's script, carried inside Graft so a launcher does not depend on
/// anything else being installed.
pub const SCRIPT: &str = include_str!("sidebar-storage.js");

const PACKAGE: &str = r#"{"name":"graft-sidebar","version":"1.0.0","main":"main.js"}"#;

/// Part of the cache key, so a helper built by an earlier version of this
/// code is rebuilt rather than reused.
pub const HELPER_FORMAT: &str = "linux-headless-1";

pub const EXECUTABLE: &str = "sidebar-storage";
const MARKER: &str = "graft-sidebar.json";

/// Where packages put Claude's main binary when the launcher does not say.
pub const RUNTIME_CANDIDATES: &[&str] = &[
    "/usr/lib/claude-desktop-unofficial/claude-desktop",
    "/usr/lib/claude-desktop/claude-desktop",
];

/// Every pass that writes leaves one, each holding whole sidebar records for
/// every profile in it, and nothing removed them. An interrupted pass is
/// recovered from the newest; twenty is room for several bad days.
pub const KEPT_BACKUPS: usize = 20;

/// The helper gives itself twenty seconds; this is the outside limit, past
/// which the host's `timeout` kills it and everything it started.
const HELPER_LIMIT_SECONDS: u64 = 25;

pub fn cache_dir() -> PathBuf {
    paths::graft_file("sidebar-runtime")
}

pub fn backups_dir() -> PathBuf {
    paths::graft_file("sidebar-backups")
}

/// The Claude runtime a helper is copied from.
#[derive(Debug, Clone)]
pub struct Runtime {
    pub binary: PathBuf,
    pub folder: PathBuf,
    pub size: u64,
    pub hash: String,
}

impl Runtime {
    pub fn key(&self) -> &str {
        &self.hash[..20]
    }
}

/// The binary a launcher script starts, from its `app_exec=` line, which is
/// how the Linux packages of Claude name it. A value built from variables is
/// not followed: guessing at what it expands to is how the wrong runtime
/// would get copied.
pub fn app_exec_in(script: &str) -> Option<PathBuf> {
    for line in script.lines() {
        let Some(value) = line.trim().strip_prefix("app_exec=") else { continue };
        let value = value.trim().trim_matches(|c| c == '"' || c == '\'');
        if value.starts_with('/') && !value.contains('$') && !value.contains(' ') {
            return Some(PathBuf::from(value));
        }
    }
    None
}

/// The installed Claude's runtime and what identifies this build of it.
///
/// `GRAFT_CLAUDE_RUNTIME` names the binary outright, for a package whose
/// launcher does not say where it is.
pub fn installed_runtime() -> Result<Runtime, Failure> {
    let launcher_text = claude::launcher().and_then(|l| host::host_read(&l)).unwrap_or_default();
    let named = std::env::var("GRAFT_CLAUDE_RUNTIME").ok().filter(|v| !v.is_empty()).map(PathBuf::from);
    let binary = named
        .or_else(|| app_exec_in(&String::from_utf8_lossy(&launcher_text)))
        .filter(|path| host::host_path_exists(path))
        .or_else(|| RUNTIME_CANDIDATES.iter().map(PathBuf::from).find(|path| host::host_path_exists(path)))
        .ok_or_else(|| Failure::new("unsupported-runtime"))?;
    let folder = binary.parent().map(Path::to_path_buf).ok_or_else(|| Failure::new("unsupported-runtime"))?;
    // Size and time rather than a digest of the binary: hashing two hundred
    // megabytes stands in front of a window opening. An image-based system
    // stamps every file in /usr with time zero, which is why the launcher,
    // carrying the package's version, is part of the key as well.
    let stat = host::output("stat", &["-L", "-c", "%s %Y", &paths::text(&binary)]);
    let stat = stat.trim().to_string();
    let size: u64 = stat
        .split_whitespace()
        .next()
        .and_then(|s| s.parse().ok())
        .ok_or_else(|| Failure::new("unsupported-runtime"))?;
    let version = host::host_read(&folder.join("version")).unwrap_or_default();
    let mut digest = Sha256::new();
    for part in [&launcher_text[..], stat.as_bytes(), &version, paths::text(&binary).as_bytes(),
        SCRIPT.as_bytes(), HELPER_FORMAT.as_bytes()]
    {
        digest.update(part);
        digest.update([0u8]);
    }
    Ok(Runtime { binary, folder, size, hash: hex::encode(digest.finalize()) })
}

/// An Electron archive holding `files`, with the per-file integrity Electron
/// checks as it reads each one.
pub fn asar(files: &[(&str, &[u8])]) -> Vec<u8> {
    fn sha(data: &[u8]) -> String {
        hex::encode(Sha256::digest(data))
    }
    fn word(number: usize) -> [u8; 4] {
        (number as u32).to_le_bytes()
    }
    let mut entries = serde_json::Map::new();
    let mut body: Vec<u8> = Vec::new();
    for (name, data) in files {
        entries.insert(
            (*name).to_string(),
            json!({"size": data.len(), "offset": body.len().to_string(),
                "integrity": {"algorithm": "SHA256", "hash": sha(data), "blockSize": 4_194_304, "blocks": [sha(data)]}}),
        );
        body.extend_from_slice(data);
    }
    let header = files::sorted_json(&json!({"files": Value::Object(entries)}));
    let mut payload = word(header.len()).to_vec();
    payload.extend_from_slice(&header);
    payload.resize(payload.len() + (4 - payload.len() % 4) % 4, 0);
    let mut pickle = word(payload.len()).to_vec();
    pickle.extend_from_slice(&payload);
    let mut out = word(4).to_vec();
    out.extend_from_slice(&word(pickle.len()));
    out.extend_from_slice(&pickle);
    out.extend_from_slice(&body);
    out
}

pub fn helper_archive() -> Vec<u8> {
    asar(&[("package.json", PACKAGE.as_bytes()), ("main.js", SCRIPT.as_bytes())])
}

pub fn executable(runtime_dir: &Path) -> PathBuf {
    runtime_dir.join(EXECUTABLE)
}

/// A cached helper is used only when it was finished (the marker is written
/// last), holds the whole binary, and carries this script.
fn is_intact(destination: &Path, runtime: &Runtime, archive: &[u8]) -> bool {
    let marker = fs::read_to_string(destination.join(MARKER)).unwrap_or_default();
    let binary = fs::symlink_metadata(executable(destination));
    marker == runtime.hash
        && binary.is_ok_and(|m| m.is_file() && m.len() == runtime.size && m.mode() & 0o111 != 0)
        && fs::read(destination.join("resources/app.asar")).is_ok_and(|bytes| bytes == archive)
}

/// The helper for the installed Claude, built if the cache does not hold it.
pub fn prepare_helper() -> Result<PathBuf, Failure> {
    let runtime = installed_runtime()?;
    let cache = cache_dir();
    let destination = cache.join(runtime.key()).join("runtime");
    let archive = helper_archive();
    if is_intact(&destination, &runtime, &archive) {
        prune_helper_cache(&cache, runtime.key(), SystemTime::now());
        return Ok(destination);
    }
    fs::create_dir_all(&cache).map_err(|_| Failure::new("runtime-copy-failed"))?;
    let _ = fs::set_permissions(&cache, fs::Permissions::from_mode(0o700));
    let staging = cache.join(format!(".build-{}", uuid::Uuid::new_v4()));
    struct Remove(PathBuf);
    impl Drop for Remove {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }
    let _cleanup = Remove(staging.clone());
    let app = staging.join("runtime");
    build_runtime(&runtime, &app, &archive)?;
    install_helper(&app, &destination, &cache).map_err(|_| Failure::new("runtime-copy-failed"))?;
    prune_helper_cache(&cache, runtime.key(), SystemTime::now());
    Ok(destination)
}

fn build_runtime(runtime: &Runtime, app: &Path, archive: &[u8]) -> Result<(), Failure> {
    let copy_failed = |_| Failure::new("runtime-copy-failed");
    fs::create_dir_all(app.join("resources")).map_err(copy_failed)?;
    let names = host::output("ls", &["-A1", "--", &paths::text(&runtime.folder)]);
    let binary_name = runtime.binary.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    let names: Vec<&str> = names.lines().filter(|n| !n.is_empty() && !n.contains('/')).collect();
    if !names.contains(&binary_name.as_str()) {
        return Err(Failure::new("unsupported-runtime"));
    }
    // The copy is made by the host, since inside the sandbox /usr is the
    // runtime's own and Claude is not in it. A reflink is tried first for
    // the systems where /usr and the home folder share a filesystem.
    let target = executable(app);
    let copied = host::run_tool(
        "cp",
        &["--reflink=auto", "--", &paths::text(&runtime.binary), &paths::text(&target)],
    );
    if copied != 0 || fs::metadata(&target).map(|m| m.len()).ok() != Some(runtime.size) {
        return Err(Failure::new("runtime-copy-failed"));
    }
    for name in names {
        if name == binary_name || name == "resources" {
            continue;
        }
        std::os::unix::fs::symlink(runtime.folder.join(name), app.join(name)).map_err(copy_failed)?;
    }
    fs::write(app.join("resources/app.asar"), archive).map_err(copy_failed)?;
    fs::write(app.join(MARKER), runtime.hash.as_bytes()).map_err(copy_failed)?;
    Ok(())
}

/// A copy already at the destination is one `prepare_helper` found wanting
/// (a folder left half written by a pass that was killed, or one whose binary
/// or script no longer matches), and refusing over it refused every sync on
/// that Claude until somebody found the folder and deleted it by hand. It is
/// moved aside rather than deleted in place, because a removal that fails
/// part way leaves a third state, and the moved copy goes with the prune.
pub fn install_helper(app: &Path, destination: &Path, cache: &Path) -> std::io::Result<()> {
    if let Some(parent) = destination.parent() {
        fs::create_dir_all(parent)?;
    }
    if files::exists(destination) {
        let aside = cache.join(format!(".damaged-{}", uuid::Uuid::new_v4()));
        fs::rename(destination, aside)?;
        diagnostics::note("sidebar.helper-replaced", &[("reason", "cached copy failed verification".into())]);
    }
    fs::rename(app, destination)
}

/// The convention backup tools on Linux agree on for "leave this out"
/// (Borg, restic with `--exclude-caches`, tar with `--exclude-caches`), and
/// the nearest thing to a Mac's excluded-from-backup flag.
const CACHEDIR_TAG: &str = "Signature: 8a477f597d28d172789f06886806bc55\n\
# This file is a cache directory tag created by Claude Graft.\n\
# Every folder here is a copy of Claude's runtime, made again after each update.\n";

/// Kept for this machine's own Claude only. Every Claude update and every
/// change to the script makes a new key, and each copy holds the whole
/// Electron binary, two hundred megabytes that nothing shares on Linux.
/// Runs under the launch lock, so no other helper is running out of a folder
/// this removes; a staging folder is left for an hour all the same, in case
/// a caller without the lock is building in it. Nothing whose name this did
/// not make is touched.
pub fn prune_helper_cache(cache: &Path, keeping: &str, now: SystemTime) {
    let tag = cache.join("CACHEDIR.TAG");
    if cache.is_dir() && !tag.exists() {
        let _ = fs::write(&tag, CACHEDIR_TAG);
    }
    let Ok(entries) = fs::read_dir(cache) else { return };
    for entry in entries.filter_map(Result::ok) {
        let name = entry.file_name().to_string_lossy().into_owned();
        let is_key = name.len() == 20 && name.chars().all(|c| c.is_ascii_digit() || ('a'..='f').contains(&c));
        let age = entry
            .metadata()
            .and_then(|m| m.modified())
            .ok()
            .and_then(|modified| now.duration_since(modified).ok())
            .unwrap_or_default();
        let remove = (is_key && name != keeping)
            || name.starts_with(".damaged-")
            || (name.starts_with(".build-") && age > Duration::from_secs(3600));
        if remove {
            let path = entry.path();
            if fs::remove_dir_all(&path).is_err() {
                let _ = fs::remove_file(&path);
            }
        }
    }
}

pub fn prune_backups(backups: &Path, keeping: usize) {
    let Ok(entries) = fs::read_dir(backups) else { return };
    let mut found: Vec<(SystemTime, String, PathBuf)> = entries
        .filter_map(Result::ok)
        .filter(|e| e.path().extension().is_some_and(|x| x == "json"))
        .map(|e| {
            let modified = e.metadata().and_then(|m| m.modified()).unwrap_or(SystemTime::UNIX_EPOCH);
            (modified, e.file_name().to_string_lossy().into_owned(), e.path())
        })
        .collect();
    if found.len() <= keeping {
        return;
    }
    found.sort_by(|a, b| (b.0, &b.1).cmp(&(a.0, &a.1)));
    for (_, _, path) in found.into_iter().skip(keeping) {
        let _ = fs::remove_file(path);
    }
}

/// Run the helper once on `request` and hand back its reply.
///
/// It runs on the host, where Claude's runtime is, with an empty environment
/// apart from a path and a home folder inside the pass's own scratch folder.
/// No inherited Electron or Node switch can turn it into an inspector or send
/// it to another entry point, no display variable reaches it, and with no
/// session bus and the basic password store it cannot reach the keyring, so
/// nothing it does can put a dialog in front of anybody. The headless
/// platform keeps it off every screen.
pub fn run(runtime_dir: &Path, request: &Value, scratch: &Path) -> Result<Value, Failure> {
    let unavailable = || Failure::new("storage-unavailable");
    let request_file = scratch.join("request.json");
    let output = PathBuf::from(request["output"].as_str().ok_or_else(unavailable)?);
    fs::write(&request_file, files::sorted_json(request)).map_err(|_| unavailable())?;
    let home = scratch.join("home");
    fs::create_dir_all(&home).map_err(|_| unavailable())?;
    let limit = HELPER_LIMIT_SECONDS.to_string();
    let home_value = format!("HOME={}", paths::text(&home));
    let exe = paths::text(&executable(runtime_dir));
    let request_text = paths::text(&request_file);
    let mut command = host::command("timeout", &[]);
    command
        .args(["-k", "3", &limit, "env", "-i", "PATH=/usr/bin:/bin", &home_value, &exe])
        .args(["--ozone-platform=headless", "--disable-gpu", "--password-store=basic", &request_text])
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null());
    let mut child = command.spawn().map_err(|_| unavailable())?;
    let deadline = Instant::now() + Duration::from_secs(HELPER_LIMIT_SECONDS + 5);
    let status = loop {
        match child.try_wait() {
            Ok(Some(status)) => break status,
            Ok(None) if Instant::now() < deadline => std::thread::sleep(Duration::from_millis(50)),
            _ => {
                // flatpak-spawn passes a SIGTERM on to the host process; the
                // host's own timeout is what makes sure it ends.
                // SAFETY: signalling a child this function started and has not reaped.
                unsafe {
                    libc::kill(child.id() as i32, libc::SIGTERM);
                }
                std::thread::sleep(Duration::from_secs(3));
                let _ = child.kill();
                let _ = child.wait();
                return Err(Failure::new("storage-timeout"));
            }
        }
    };
    match status.code() {
        Some(0) => {}
        Some(124) | Some(137) => return Err(Failure::new("storage-timeout")),
        _ => return Err(unavailable()),
    }
    let mut text = String::new();
    fs::File::open(&output).and_then(|mut f| f.read_to_string(&mut text)).map_err(|_| unavailable())?;
    serde_json::from_str(&text).map_err(|_| unavailable())
}

#[cfg(test)]
mod tests {
    use super::*;
    use graft_core::testing::Sandbox;

    fn touch_back(path: &Path, seconds: u64) {
        let when = SystemTime::now() - Duration::from_secs(seconds);
        fs::File::open(path).unwrap().set_modified(when).unwrap();
    }

    #[test]
    fn the_helper_cache_keeps_one_helper_and_nothing_else_it_made() {
        let sandbox = Sandbox::new();
        let cache = sandbox.root.path().join("sidebar-runtime");
        let current = "0123456789abcdef0123";
        let older = "fedcba9876543210fedc";
        for name in [current, older, ".build-fresh", ".build-stale", ".damaged-x", "Notes"] {
            fs::create_dir_all(cache.join(name).join("runtime")).unwrap();
        }
        touch_back(&cache.join(".build-stale"), 7200);
        prune_helper_cache(&cache, current, SystemTime::now());
        let left: Vec<String> = files::listing(&cache).unwrap();
        let hidden: Vec<String> = fs::read_dir(&cache).unwrap()
            .map(|e| e.unwrap().file_name().to_string_lossy().into_owned()).collect();
        assert!(left.contains(&current.to_string()), "the helper this Claude needs is kept");
        assert!(!left.contains(&older.to_string()), "a helper built for an earlier Claude is removed");
        assert!(!hidden.contains(&".build-stale".to_string()), "a staging folder abandoned over an hour ago is removed");
        assert!(hidden.contains(&".build-fresh".to_string()), "a staging folder that may still be in use is left alone");
        assert!(!hidden.contains(&".damaged-x".to_string()), "a damaged copy set aside is removed");
        assert!(left.contains(&"Notes".to_string()), "and nothing the cache did not make is touched");
        let tag = fs::read_to_string(cache.join("CACHEDIR.TAG")).unwrap();
        assert!(tag.starts_with("Signature: 8a477f597d28d172789f06886806bc55"), "the helper cache is left out of backups");
    }

    #[test]
    fn a_damaged_cached_helper_is_replaced_by_the_fresh_one_rather_than_refusing_the_sync() {
        let sandbox = Sandbox::new();
        let cache = sandbox.root.path().join("sidebar-runtime");
        let current = "0123456789abcdef0123";
        let destination = cache.join(current).join("runtime");
        fs::create_dir_all(&destination).unwrap();
        fs::write(destination.join("leftover"), "half written").unwrap();
        let fresh = cache.join(".build-new/runtime");
        fs::create_dir_all(&fresh).unwrap();
        fs::write(fresh.join("marker"), "fresh").unwrap();
        install_helper(&fresh, &destination, &cache).unwrap();
        assert!(destination.join("marker").exists() && !destination.join("leftover").exists());
        prune_helper_cache(&cache, current, SystemTime::now());
        let names: Vec<String> = fs::read_dir(&cache).unwrap()
            .map(|e| e.unwrap().file_name().to_string_lossy().into_owned()).collect();
        assert!(!names.iter().any(|n| n.starts_with(".damaged-")), "and the copy it replaced is gone after the next prune");
    }

    #[test]
    fn only_the_newest_sidebar_backups_are_kept() {
        let sandbox = Sandbox::new();
        let backups = sandbox.root.path().join("backups");
        fs::create_dir_all(&backups).unwrap();
        for index in 0..25u64 {
            let file = backups.join(format!("backup-{index}.json"));
            fs::write(&file, "{}").unwrap();
            touch_back(&file, (25 - index) * 60);
        }
        fs::write(backups.join("notes.txt"), "kept").unwrap();
        prune_backups(&backups, KEPT_BACKUPS);
        let kept = files::listing(&backups).unwrap();
        assert_eq!(kept.iter().filter(|n| n.ends_with(".json")).count(), KEPT_BACKUPS);
        assert!(kept.contains(&"backup-24.json".to_string()) && !kept.contains(&"backup-4.json".to_string()));
        assert!(kept.contains(&"notes.txt".to_string()), "a file that is not a backup is left alone");
    }

    #[test]
    fn the_launcher_names_the_runtime_it_starts() {
        let script = "#!/usr/bin/bash\nsource x\n# comment\napp_exec=\"/usr/lib/claude-desktop-unofficial/claude-desktop\"\n";
        assert_eq!(app_exec_in(script), Some(PathBuf::from("/usr/lib/claude-desktop-unofficial/claude-desktop")));
        assert_eq!(app_exec_in("app_exec=\"$APPDIR/claude\"\n"), None, "a value built from variables is not guessed at");
        assert_eq!(app_exec_in("exec electron\n"), None);
    }

    #[test]
    fn an_archive_lays_out_its_header_and_files_the_way_electron_reads_them() {
        let archive = asar(&[("package.json", b"{}"), ("main.js", b"x")]);
        let size = |at: usize| u32::from_le_bytes(archive[at..at + 4].try_into().unwrap()) as usize;
        assert_eq!(size(0), 4, "the archive starts with the size of the size field");
        let header_len = size(12);
        let header: Value = serde_json::from_slice(&archive[16..16 + header_len]).unwrap();
        assert_eq!(header["files"]["main.js"]["offset"], "2", "a file's offset is counted from the end of the header");
        assert_eq!(header["files"]["main.js"]["integrity"]["hash"], hex::encode(Sha256::digest(b"x")));
        let body = 8 + size(4);
        assert_eq!(&archive[body..], b"{}x", "and the files follow the header in order");
    }
}
