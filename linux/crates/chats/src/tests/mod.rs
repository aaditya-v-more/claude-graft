//! The suite, run inside a throwaway home so nothing here can reach a real
//! profile, a real transcript or a real Claude.

use crate::{running, CHAT_STORES};
use graft_core::testing::Sandbox;
use graft_core::paths;
use serde_json::{json, Map, Value};
use std::path::{Path, PathBuf};

mod chats_elsewhere;
mod grafting;
mod mirroring;
mod records_filing;
mod records_reading;
mod stores;

/// One test's world: a sandboxed home, and a machine with no Claude running
/// unless the test says otherwise.
pub struct World {
    pub sandbox: Sandbox,
}

impl World {
    pub fn new() -> World {
        let sandbox = Sandbox::new();
        running::set_processes_override(None);
        std::fs::create_dir_all(paths::graft_folder()).unwrap();
        World { sandbox }
    }

    /// `~/.config`, where every profile lives.
    pub fn support(&self) -> PathBuf {
        paths::config_home()
    }

    pub fn home(&self) -> PathBuf {
        self.sandbox.home()
    }

    pub fn projects(&self) -> PathBuf {
        crate::records::claude_projects()
    }
}

impl Drop for World {
    fn drop(&mut self) {
        running::set_processes_override(None);
    }
}

/// A profile with a login, and chat records in both stores, the way the Mac
/// suite's `makeProfile` builds one.
pub fn make_profile(name: &str, account: Option<&str>, org: Option<&str>, chats: &[&str]) -> PathBuf {
    make_profile_with(name, account, org, chats, &[])
}

pub fn make_profile_with(
    name: &str,
    account: Option<&str>,
    org: Option<&str>,
    chats: &[&str],
    extras: &[(&str, &str)],
) -> PathBuf {
    let profile = paths::profile_dir(name);
    std::fs::create_dir_all(&profile).unwrap();
    if let Some(account) = account {
        let mut config = Map::new();
        config.insert("lastKnownAccountUuid".into(), json!(account));
        config.insert("oauth:tokenCache".into(), json!(format!("token-for-{name}")));
        for (k, v) in extras {
            config.insert((*k).into(), json!(v));
        }
        std::fs::write(profile.join("config.json"), serde_json::to_vec(&Value::Object(config)).unwrap()).unwrap();
        if let Some(org) = org {
            for store in CHAT_STORES {
                let dir = profile.join(store).join(account).join(org);
                std::fs::create_dir_all(&dir).unwrap();
                for chat in chats {
                    std::fs::write(dir.join(format!("local_{chat}.json")), "{}").unwrap();
                }
            }
        }
    }
    profile
}

/// The shape every released Mac version left on disk when it shared chats: the
/// profile's own set moved to the hidden sibling, and a link where they were.
/// Built by hand because nothing here makes one any more, and a profile
/// carried over from a Mac still has one and has to be able to migrate off it.
pub fn link_the_old_way(folder: &Path, target: &Path) {
    let stash = crate::fsx::stash_path(folder);
    if crate::fsx::exists(folder) {
        std::fs::rename(folder, &stash).unwrap();
    }
    std::os::unix::fs::symlink(target, folder).unwrap();
}

/// Every record a profile's Claude Code store shows, across its accounts and
/// organizations, hidden folders left out as a sidebar leaves them out.
pub fn chats_visible(profile: &Path) -> Vec<String> {
    let store = profile.join("claude-code-sessions");
    let mut found = Vec::new();
    for account in crate::fsx::names_or_empty(&store) {
        if account.starts_with('.') {
            continue;
        }
        let account_dir = store.join(&account);
        let Some(orgs) = crate::fsx::names(&account_dir) else { continue };
        for org in orgs.into_iter().filter(|o| !o.starts_with('.')) {
            for file in crate::fsx::names_or_empty(&account_dir.join(&org)) {
                if file.starts_with("local_") {
                    found.push(file);
                }
            }
        }
    }
    found.sort();
    found
}

pub fn write(path: &Path, text: &str) {
    if let Some(dir) = path.parent() {
        std::fs::create_dir_all(dir).unwrap();
    }
    std::fs::write(path, text).unwrap();
}

pub fn read(path: &Path) -> Option<String> {
    std::fs::read_to_string(path).ok()
}

pub fn json_of(path: &Path) -> Value {
    std::fs::read(path).ok().and_then(|b| serde_json::from_slice(&b).ok()).unwrap_or(Value::Null)
}
