//! The suite, run inside a throwaway home so nothing here can reach a real
//! profile, a real transcript or a real Claude.

#![allow(dead_code)]

use crate::{running, CHAT_STORES};
use graft_core::testing::Sandbox;
use graft_core::paths;
use serde_json::{json, Map, Value};
use std::path::{Path, PathBuf};

mod chats_elsewhere;
mod grafting;
mod linux_places;
mod mirroring;
mod records_filing;
mod records_reading;
mod reporting;
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

// A profile will not write a record into a folder that resolves outside
// itself, so a session started from a grafted profile closes without a record
// and vanishes from every sidebar while its transcript sits whole on disk. The
// sweep writes the missing record; everything in it comes out of the
// transcript.

pub const OWNER_ACCOUNT: &str = "0a0a0a0a-0a0a-4a0a-8a0a-0a0a0a0a0a0a";
pub const OWNER_ORG: &str = "0b0b0b0b-0b0b-4b0b-8b0b-0b0b0b0b0b0b";
pub const BORROWER_ACCOUNT: &str = "0c0c0c0c-0c0c-4c0c-8c0c-0c0c0c0c0c0c";
pub const BORROWER_ORG: &str = "0d0d0d0d-0d0d-4d0d-8d0d-0d0d0d0d0d0d";

/// The folder the suite's sessions worked in. The Mac suite used
/// `/Users/Shared/work`, which a Linux machine never has, so a session there
/// reads as one from another machine; `/tmp` is on every Linux machine.
pub const WORK_CWD: &str = "/tmp/shared/work";

/// One transcript, laid out the way the command line writes them: a bridge
/// line naming the owner, a prompt, an answer, and a title line.
#[derive(Clone)]
pub struct Transcript {
    pub session: String,
    pub bridge: Option<String>,
    pub owner: String,
    pub org: String,
    pub title: Option<String>,
    pub spoke: bool,
    pub first: String,
    pub last: String,
    pub said: Option<String>,
    pub extra: Vec<String>,
    pub cwd: String,
}

impl Transcript {
    pub fn new(session: &str) -> Transcript {
        Transcript {
            session: session.to_string(),
            bridge: Some("cse_01BridgeSession0000".into()),
            owner: OWNER_ACCOUNT.into(),
            org: OWNER_ORG.into(),
            title: None,
            spoke: true,
            first: "2026-08-29T17:00:00.000Z".into(),
            last: "2026-08-29T17:00:10.000Z".into(),
            said: None,
            extra: Vec::new(),
            cwd: WORK_CWD.into(),
        }
    }

    pub fn title(mut self, title: &str) -> Self {
        self.title = Some(title.into());
        self
    }

    pub fn owner(mut self, owner: &str, org: &str) -> Self {
        self.owner = owner.into();
        self.org = org.into();
        self
    }

    pub fn no_bridge(mut self) -> Self {
        self.bridge = None;
        self
    }

    pub fn bridge(mut self, bridge: &str) -> Self {
        self.bridge = Some(bridge.into());
        self
    }

    pub fn silent(mut self) -> Self {
        self.spoke = false;
        self
    }

    pub fn times(mut self, first: &str, last: &str) -> Self {
        self.first = first.into();
        self.last = last.into();
        self
    }

    pub fn said(mut self, said: &str) -> Self {
        self.said = Some(said.into());
        self
    }

    pub fn extra(mut self, lines: &[&str]) -> Self {
        self.extra.extend(lines.iter().map(|l| l.to_string()));
        self
    }

    pub fn cwd(mut self, cwd: &str) -> Self {
        self.cwd = cwd.into();
        self
    }

    pub fn lines(&self) -> Vec<String> {
        let mut lines = Vec::new();
        let (session, cwd) = (&self.session, &self.cwd);
        if let Some(bridge) = &self.bridge {
            lines.push(format!(
                "{{\"type\":\"bridge-session\",\"sessionId\":\"{session}\",\"bridgeSessionId\":\"{bridge}\",\
                 \"lastSequenceNum\":0,\"ownerAccountUuid\":\"{}\",\"ownerOrganizationUuid\":\"{}\"}}",
                self.owner, self.org
            ));
        }
        if self.spoke {
            let said = self.said.clone().unwrap_or_else(|| format!("hello from {session}"));
            lines.push(format!(
                "{{\"parentUuid\":null,\"isSidechain\":false,\"promptId\":\"prompt-one\",\"cwd\":\"{cwd}\",\
                 \"permissionMode\":\"auto\",\"gitBranch\":\"main\",\"type\":\"user\",\"message\":{{\"role\":\"user\",\
                 \"content\":\"{said}\"}},\"uuid\":\"u-1\",\"timestamp\":\"{}\"}}",
                self.first
            ));
            lines.push(format!(
                "{{\"parentUuid\":\"u-1\",\"isSidechain\":false,\"cwd\":\"{cwd}\",\"permissionMode\":\"auto\",\
                 \"gitBranch\":\"main\",\"type\":\"assistant\",\"message\":{{\"id\":\"m-1\",\"model\":\"claude-opus-5\",\
                 \"content\":[{{\"text\":\"hi\",\"type\":\"text\"}}]}},\"uuid\":\"a-1\",\"timestamp\":\"{}\",\
                 \"effort\":\"max\"}}",
                self.last
            ));
        } else {
            // A session opened and closed with nothing said in it still leaves
            // a transcript: a bridge line, a stamp, and no conversation at all.
            lines.push(format!(
                "{{\"cwd\":\"{cwd}\",\"permissionMode\":\"auto\",\"type\":\"file-history-summary\",\"timestamp\":\"{}\"}}",
                self.last
            ));
        }
        if let Some(title) = &self.title {
            lines.push(format!("{{\"type\":\"custom-title\",\"customTitle\":\"{title}\",\"sessionId\":\"{session}\"}}"));
        }
        lines.extend(self.extra.iter().cloned());
        lines
    }

    /// Writes it into `~/.claude/projects/-Users-test-work/<session>.jsonl`.
    pub fn write(&self) -> PathBuf {
        let dir = crate::records::claude_projects().join("-Users-test-work");
        std::fs::create_dir_all(&dir).unwrap();
        let file = dir.join(format!("{}.jsonl", self.session));
        std::fs::write(&file, self.lines().join("\n")).unwrap();
        file
    }
}

/// A modification time set by hand, for the checks that turn on how long a
/// transcript has been quiet.
pub fn set_modified(path: &Path, when: std::time::SystemTime) {
    let file = std::fs::OpenOptions::new().write(true).open(path).unwrap();
    file.set_modified(when).unwrap();
}
