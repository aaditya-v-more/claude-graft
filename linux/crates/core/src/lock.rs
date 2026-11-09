//! The locks that keep passes in different processes from interleaving.
//!
//! The window and any number of launchers run the same sweep over the same
//! state files. Each is a read-modify-write, and two of them interleaved lose
//! whichever wrote first. `flock` on a file beside the state is the one thing
//! every one of those processes can agree on, whatever language wrote it.

use crate::{diagnostics, paths};
use std::cell::Cell;
use std::fs::{File, OpenOptions};
use std::os::unix::io::AsRawFd;
use std::path::{Path, PathBuf};
use std::sync::{Mutex, MutexGuard};
use std::time::{Duration, Instant};

/// A launcher stands in front of a window opening, and one that waited forever
/// on a pass that had hung would never open it; going ahead is what every pass
/// did before there was a lock.
pub const STATE_LOCK_PATIENCE: Duration = Duration::from_secs(20);

static STATE_LOCAL: Mutex<()> = Mutex::new(());

thread_local! {
    static STATE_DEPTH: Cell<usize> = const { Cell::new(0) };
}

/// A held file lock, let go when dropped.
pub struct FileLock {
    file: Option<File>,
}

impl Drop for FileLock {
    fn drop(&mut self) {
        if let Some(file) = self.file.take() {
            // SAFETY: the descriptor belongs to `file`, which is still open.
            unsafe {
                libc::flock(file.as_raw_fd(), libc::LOCK_UN);
            }
        }
    }
}

/// Take an exclusive lock on a file, waiting up to `patience`. A lock that
/// cannot be had in that time is given up on and noted, and the caller goes
/// ahead without it.
pub fn take(path: &Path, patience: Duration, event: &str) -> FileLock {
    if let Some(dir) = path.parent() {
        let _ = std::fs::create_dir_all(dir);
    }
    let file = match OpenOptions::new().read(true).write(true).create(true).truncate(false).open(path) {
        Ok(file) => file,
        Err(e) => {
            diagnostics::note(&format!("{event}.unavailable"), &[("error", e.to_string().into())]);
            return FileLock { file: None };
        }
    };
    let deadline = Instant::now() + patience;
    loop {
        // SAFETY: flock on a descriptor this function owns.
        let result = unsafe { libc::flock(file.as_raw_fd(), libc::LOCK_EX | libc::LOCK_NB) };
        if result == 0 {
            return FileLock { file: Some(file) };
        }
        let err = std::io::Error::last_os_error();
        let again = matches!(err.raw_os_error(), Some(libc::EWOULDBLOCK) | Some(libc::EINTR));
        if !again || Instant::now() >= deadline {
            diagnostics::note(&format!("{event}.gave-up"), &[("error", err.to_string().into())]);
            return FileLock { file: None };
        }
        std::thread::sleep(Duration::from_millis(20));
    }
}

struct StateHold {
    _local: Option<MutexGuard<'static, ()>>,
    _file: Option<FileLock>,
}

/// Run `work` holding the state lock over `ClaudeGraft/state.lock`.
///
/// Holds nest on one thread: only the outermost takes the file, since flock
/// belongs to the open file and a second descriptor from a nested hold would
/// wait on the first one forever.
pub fn with_state_lock<T>(work: impl FnOnce() -> T) -> T {
    let depth = STATE_DEPTH.with(|d| d.get());
    let hold = if depth == 0 {
        let local = STATE_LOCAL.lock().unwrap_or_else(|e| e.into_inner());
        let file = take(&paths::state_lock_file(), STATE_LOCK_PATIENCE, "state-lock");
        StateHold { _local: Some(local), _file: Some(file) }
    } else {
        StateHold { _local: None, _file: None }
    };
    STATE_DEPTH.with(|d| d.set(depth + 1));
    struct Leave;
    impl Drop for Leave {
        fn drop(&mut self) {
            STATE_DEPTH.with(|d| d.set(d.get().saturating_sub(1)));
        }
    }
    let leave = Leave;
    let result = work();
    drop(leave);
    drop(hold);
    result
}

pub fn launch_lock_file() -> PathBuf {
    paths::graft_file("launch.lock")
}

/// Held from deciding that a profile is closed to having started Claude on
/// it, so two presses, or a press and a launcher, cannot both decide it is
/// closed and both start one. Never taken while the state lock is held, so
/// the two are never held in both orders.
pub fn with_launch_lock<T>(work: impl FnOnce() -> T) -> T {
    let hold = take(&launch_lock_file(), Duration::from_secs(60), "launch-lock");
    let result = work();
    drop(hold);
    result
}
