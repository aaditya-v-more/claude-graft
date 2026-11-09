//! What is true of the chat stores now, written out in full each pass.
//!
//! The log says what happened; this says the current shape of things, which is
//! the question asked first after an incident. It is built as a string so the
//! suite can read what a given arrangement of folders produces.

use crate::fsx::{self, exists, is_directory, is_symlink, names_or_empty, resolved_text, stash_path};
use crate::mirror::load_mirror_state;
use crate::records::load_session_record_state;
use crate::{running, CHAT_STORES, NON_ACCOUNT_STORE_ITEMS};
use base64::Engine;
use graft_core::{config, diagnostics, files, paths};
use serde_json::{json, Value};
use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};

/// The account every bridged session on this machine is stamped with,
/// whichever profile's window it was typed into.
///
/// This is the single most confusing fact about running more than one
/// profile, and nothing in the interface says it: a profile pays for its own
/// inference with its own token, but the conversation is filed under whoever
/// the command line is logged in as. So a chat started in one profile is owned
/// by another, appears in that other one's sidebar and its cloud history, and
/// looks for all the world like the two profiles are syncing to each other.
/// They are not. They share one login.
pub fn command_line_login() -> Option<(String, String)> {
    let files::JsonRead::Value(root) = files::read_json(&paths::claude_code_config_file()) else {
        return None;
    };
    let oauth = root.get("oauthAccount")?;
    let account = oauth.get("accountUuid")?.as_str()?.to_string();
    let organization = oauth.get("organizationUuid")?.as_str()?.to_string();
    Some((account, organization))
}

/// The device this profile registered as. A profile is a whole browser profile
/// of its own, so it has its own device identity, and chats made under a
/// previous one are shown as coming from another device however firmly they
/// belong to the account reading them.
pub fn device_identifier(profile: &Path) -> Option<String> {
    let encoded = std::fs::read_to_string(profile.join("ant-did")).ok()?;
    let data = base64::engine::general_purpose::STANDARD.decode(encoded.trim()).ok()?;
    String::from_utf8(data).ok()
}

/// One organization folder as it sits on disk, against what this app believes
/// about it.
#[derive(Debug, Clone, PartialEq)]
pub struct FolderReport {
    pub store: String,
    pub pair: String,
    pub path: String,
    pub records: usize,
    pub linked: bool,
    /// Records in the hidden sibling, or `None` where there is no sibling. A
    /// stash is the profile's own chats waiting to come back, so a big one
    /// beside a nearly empty folder is a graft that was undone halfway.
    pub stashed: Option<usize>,
    /// Records this app remembers filing here. Far more than are there is how
    /// the sweep sees a sidebar somebody emptied, and it is also how it sees a
    /// folder that was moved out from under it.
    pub remembered: usize,
}

#[derive(Debug, Clone, PartialEq)]
pub struct ProfileReport {
    pub name: String,
    pub account: Option<String>,
    pub config_readable: bool,
    pub device: Option<String>,
    pub is_shortcut: bool,
    pub running: bool,
    pub folders: Vec<FolderReport>,
}

fn record_count(dir: &Path) -> usize {
    names_or_empty(dir).iter().filter(|n| n.starts_with("local_") && n.ends_with(".json")).count()
}

/// Everything worth knowing about the chat stores on this machine, read in one
/// pass and in one place.
pub fn state_report(checking_running: bool) -> Vec<ProfileReport> {
    let mut counted: HashMap<String, usize> = HashMap::new();
    for store in load_session_record_state().records.values() {
        *counted.entry(store.clone()).or_insert(0) += 1;
    }
    let shortcuts: HashSet<PathBuf> = config::shortcut_profiles().into_iter().collect();
    let listing = if checking_running { running::processes() } else { Vec::new() };

    let mut profiles = config::session_store_profiles();
    profiles.sort();
    profiles
        .into_iter()
        .map(|profile| {
            let mut folders = Vec::new();
            for store in CHAT_STORES {
                let store_dir = profile.join(store);
                for account in names_or_empty(&store_dir) {
                    if account.starts_with('.') || NON_ACCOUNT_STORE_ITEMS.contains(&account.as_str()) {
                        continue;
                    }
                    let account_dir = store_dir.join(&account);
                    if !is_directory(&account_dir) {
                        continue;
                    }
                    for org in names_or_empty(&account_dir) {
                        if org.starts_with('.') {
                            continue;
                        }
                        let org_dir = account_dir.join(&org);
                        if !is_directory(&org_dir) {
                            continue;
                        }
                        let stash = stash_path(&org_dir);
                        folders.push(FolderReport {
                            store: store.to_string(),
                            pair: format!("{account}/{org}"),
                            path: paths::text(&org_dir),
                            records: record_count(&org_dir),
                            linked: is_symlink(&org_dir),
                            stashed: exists(&stash).then(|| record_count(&stash)),
                            remembered: counted.get(&resolved_text(&org_dir)).copied().unwrap_or(0),
                        });
                    }
                }
            }
            let config = files::readable_config_json(&profile);
            ProfileReport {
                name: fsx::file_name(&profile),
                account: config
                    .as_ref()
                    .and_then(|c| c.get("lastKnownAccountUuid"))
                    .and_then(Value::as_str)
                    .map(str::to_string),
                config_readable: config.is_some(),
                device: device_identifier(&profile),
                is_shortcut: shortcuts.contains(&profile),
                running: checking_running && graft_core::claude::is_running_in(&listing, &profile),
                folders,
            }
        })
        .collect()
}

fn short(uuid: &str) -> String {
    uuid.chars().take(8).collect()
}

fn plural(n: usize) -> &'static str {
    if n == 1 {
        ""
    } else {
        "s"
    }
}

/// What is wrong right now, in the words the symptom arrives in.
///
/// The point of naming these is that every one of them has been reported as
/// something else, chats that unarchive themselves, profiles that sync to each
/// other, a sidebar that emptied overnight, and the gap between the symptom and
/// the cause has been the expensive part every time.
pub fn state_findings(report: &[ProfileReport], login: Option<&(String, String)>) -> Vec<String> {
    let mut findings = Vec::new();

    if let Some((login_account, _)) = login {
        let holder = report.iter().find(|p| p.account.as_deref() == Some(login_account.as_str())).map(|p| &p.name);
        for profile in report {
            let Some(account) = &profile.account else { continue };
            if account == login_account {
                continue;
            }
            let held = holder.map(|h| format!(" ({h})")).unwrap_or_default();
            let elsewhere = holder.cloned().unwrap_or_else(|| "the other profile".into());
            findings.push(format!(
                "{name} is signed into {acct}, but the command line is logged in as {login}{held}. \
                 Chats started in {name} are stamped with that account and filed there, so they \
                 appear in {elsewhere} rather than in {name}. This is one shared login, not two \
                 profiles syncing.",
                name = profile.name,
                acct = short(account),
                login = short(login_account),
            ));
        }
    } else {
        findings.push(
            "No command line login found, so no session on this machine has an owner and none can be filed."
                .into(),
        );
    }

    for profile in report {
        if !profile.config_readable {
            findings.push(format!(
                "{} has a config.json that will not parse. Nothing may decide where its chats go until it can be read.",
                profile.name
            ));
        }
        for folder in &profile.folders {
            if let Some(stashed) = folder.stashed {
                if stashed > folder.records {
                    findings.push(format!(
                        "{} {} {} holds {} record{} with {} put aside beside it. The stash is what the \
                         profile owned before a graft, so a graft was undone without handing them back \
                         and the sidebar is short by {}.",
                        profile.name,
                        folder.store,
                        folder.pair,
                        folder.records,
                        plural(folder.records),
                        stashed,
                        stashed
                    ));
                }
            }
            if folder.remembered > folder.records + 1 && folder.stashed.is_none() && !folder.linked {
                findings.push(format!(
                    "{} {} {} holds {} record{} where {} were filed. Records go missing when a sidebar \
                     is cleared by hand and when a folder is moved out from under this app, and only \
                     the second is a fault.",
                    profile.name,
                    folder.store,
                    folder.pair,
                    folder.records,
                    plural(folder.records),
                    folder.remembered
                ));
            }
        }
    }
    findings
}

/// The report as something a person reads.
pub fn state_report_text(checking_running: bool) -> String {
    let report = state_report(checking_running);
    let login = command_line_login();
    let findings = state_findings(&report, login.as_ref());
    let mut lines: Vec<String> = Vec::new();

    lines.push("Claude Graft - state of the chat stores".into());
    lines.push(format!("read {} by {}", diagnostics::now_stamp(), diagnostics::who()));
    lines.push(String::new());

    if let Some((account, organization)) = &login {
        let holder = report
            .iter()
            .find(|p| p.account.as_deref() == Some(account.as_str()))
            .map(|p| p.name.clone())
            .unwrap_or_else(|| "no profile here".into());
        lines.push("Command line login (~/.claude.json)".into());
        lines.push(format!("  account       {account}  - held by {holder}"));
        lines.push(format!("  organization  {organization}"));
        lines.push("  Every bridged session started from any profile is stamped with this".into());
        lines.push("  account and filed into the profile holding it, whichever window it".into());
        lines.push("  was typed into.".into());
    } else {
        lines.push("Command line login (~/.claude.json): none".into());
    }
    lines.push(String::new());

    for profile in &report {
        let mut marks = vec![if profile.is_shortcut { "shortcut" } else { "not a shortcut" }];
        if profile.running {
            marks.push("running");
        }
        if !profile.config_readable {
            marks.push("config.json unreadable");
        }
        lines.push(format!("{}  [{}]", profile.name, marks.join(", ")));
        lines.push(format!("  account  {}", profile.account.as_deref().unwrap_or("none")));
        lines.push(format!("  device   {}", profile.device.as_deref().unwrap_or("none")));
        for folder in &profile.folders {
            let mut note = format!("{} record{}", folder.records, plural(folder.records));
            if folder.linked {
                note.push_str(", linked");
            }
            if let Some(stashed) = folder.stashed {
                note.push_str(&format!(", {stashed} stashed beside it"));
            }
            if folder.remembered > 0 {
                note.push_str(&format!(", {} filed here", folder.remembered));
            }
            lines.push(format!("  {}/{}", folder.store, folder.pair));
            lines.push(format!("      {note}"));
        }
        lines.push(String::new());
    }

    let mirrors = load_mirror_state().pairs;
    lines.push(format!("Mirrored folder pairs: {}", mirrors.len()));
    for key in mirrors.keys() {
        lines.push(format!("  {}", key.replace('\0', "\n    against ")));
    }
    lines.push(String::new());

    lines.push(if findings.is_empty() { "Nothing to report.".into() } else { "Findings".into() });
    for finding in &findings {
        let joined: Vec<&str> = finding.split('\n').map(str::trim).collect();
        lines.push(format!("  - {}", joined.join(" ")));
    }

    let profiles: Vec<Value> = report
        .iter()
        .map(|p| {
            json!({
                "name": p.name,
                "account": p.account.clone().unwrap_or_default(),
                "running": p.running,
                "folders": p.folders.iter().map(|f| json!({
                    "pair": f.pair, "store": f.store, "records": f.records,
                    "linked": f.linked, "stashed": f.stashed.map(|s| s as i64).unwrap_or(-1),
                    "remembered": f.remembered,
                })).collect::<Vec<_>>(),
            })
        })
        .collect();
    diagnostics::note(
        "state",
        &[
            ("profiles", json!(profiles)),
            ("login", json!(login.as_ref().map(|l| l.0.clone()).unwrap_or_default())),
            ("findings", json!(findings)),
        ],
    );
    lines.join("\n") + "\n"
}

/// The report, rewritten in full.
pub fn write_state_report(checking_running: bool) {
    diagnostics::write_report(&state_report_text(checking_running));
}
