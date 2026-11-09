//! A throwaway world for the suite, so nothing a test does can reach a real
//! profile. Every path in `paths` is redirected into a temporary folder for as
//! long as the guard lives, and tests holding one run one at a time, since the
//! redirection is process-wide.

use crate::paths;
use std::path::PathBuf;
use std::sync::{Mutex, MutexGuard};

static SERIAL: Mutex<()> = Mutex::new(());

pub struct Sandbox {
    pub root: tempfile_lite::TempDir,
    _serial: MutexGuard<'static, ()>,
}

impl Sandbox {
    pub fn new() -> Sandbox {
        let serial = SERIAL.lock().unwrap_or_else(|e| e.into_inner());
        let root = tempfile_lite::TempDir::new();
        let home = root.path().join("home");
        std::fs::create_dir_all(home.join(".config")).unwrap();
        std::fs::create_dir_all(home.join(".local/share")).unwrap();
        paths::set_home_override(Some(home.clone()));
        paths::set_config_home_override(Some(home.join(".config")));
        paths::set_data_home_override(Some(home.join(".local/share")));
        Sandbox { root, _serial: serial }
    }

    pub fn home(&self) -> PathBuf {
        self.root.path().join("home")
    }

    pub fn config(&self) -> PathBuf {
        self.home().join(".config")
    }
}

impl Default for Sandbox {
    fn default() -> Self {
        Self::new()
    }
}

impl Drop for Sandbox {
    fn drop(&mut self) {
        paths::set_home_override(None);
        paths::set_config_home_override(None);
        paths::set_data_home_override(None);
    }
}

/// A temporary folder removed when dropped, kept here so the core crate needs
/// no test-only dependency in its public API.
pub mod tempfile_lite {
    use std::path::{Path, PathBuf};

    pub struct TempDir(PathBuf);

    impl TempDir {
        pub fn new() -> TempDir {
            let base = std::env::temp_dir();
            for n in 0..10_000u32 {
                let path = base.join(format!("graft-test-{}-{n}", std::process::id()));
                if std::fs::create_dir(&path).is_ok() {
                    return TempDir(path);
                }
            }
            panic!("no temporary folder could be made");
        }

        pub fn path(&self) -> &Path {
            &self.0
        }
    }

    impl Default for TempDir {
        fn default() -> Self {
            Self::new()
        }
    }

    impl Drop for TempDir {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
}
