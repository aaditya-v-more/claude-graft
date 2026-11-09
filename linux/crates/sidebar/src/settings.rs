//! `claude_desktop_config.json` belongs to each profile.
//!
//! It holds account-specific permission opt-ins beside local MCP server
//! definitions. Linking it lets one profile replace the other's choices, and
//! relinking it on every launch discards changes Claude saved by renaming over
//! the link. So it is never shared: the servers a profile lacks are copied
//! into its own real file, and nothing else crosses.

use graft_core::{diagnostics, files, paths};
use serde_json::{Map, Value};
use std::fs;
use std::io::Write;
use std::os::unix::fs::{OpenOptionsExt, PermissionsExt};
use std::path::{Path, PathBuf};

pub const NAME: &str = "claude_desktop_config.json";

/// Where a profile's own copy of something replaced by a link is kept, the
/// same stash the chat sharing uses: `.<name>.graft-own` beside it.
pub fn stash_path(file: &Path) -> PathBuf {
    let name = file.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    file.with_file_name(format!(".{name}.graft-own"))
}

fn profile_name(profile: &Path) -> Value {
    profile.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default().into()
}

/// A settings file as an object: an empty one when there is no file, `None`
/// when there is one and it cannot be read.
fn read(file: &Path) -> Option<Map<String, Value>> {
    if !files::exists(file) {
        return Some(Map::new());
    }
    match files::read_json(file) {
        files::JsonRead::Value(Value::Object(map)) => Some(map),
        _ => None,
    }
}

fn sorted(value: &Value) -> Value {
    match value {
        Value::Object(map) => {
            let mut keys: Vec<&String> = map.keys().collect();
            keys.sort();
            Value::Object(keys.into_iter().map(|k| (k.clone(), sorted(&map[k]))).collect())
        }
        Value::Array(items) => Value::Array(items.iter().map(sorted).collect()),
        other => other.clone(),
    }
}

/// Add the MCP servers `source` defines and `profile` lacks to the
/// profile's own settings, keeping its own definitions and every other
/// preference. A legacy link to the source's file becomes a real file again:
/// from the profile's stashed original when there is one, and otherwise
/// holding only the source's server definitions, never its permission grants.
/// The source is never written.
pub fn copy_desktop_servers(source: &Path, profile: &Path) {
    if paths::same_path(source, profile) {
        return;
    }
    let destination = profile.join(NAME);
    let backup = stash_path(&destination);
    let linked = files::is_symlink(&destination);
    let local = if linked { &backup } else { &destination };

    // A backup caught mid-write is not permission to discard it. Keep the
    // old link as well until both settings files can be read safely.
    let own = read(local);
    let borrowed = read(&source.join(NAME));
    let servers_fit = |m: &Map<String, Value>| matches!(m.get("mcpServers"), None | Some(Value::Object(_)));
    let (Some(mut own), Some(borrowed)) = (own, borrowed) else {
        diagnostics::note("settings.unread", &[("profile", profile_name(profile))]);
        return;
    };
    if !servers_fit(&own) || !servers_fit(&borrowed) {
        diagnostics::note("settings.unread", &[("profile", profile_name(profile))]);
        return;
    }
    let mut servers = own.get("mcpServers").and_then(Value::as_object).cloned().unwrap_or_default();
    let mut added = false;
    if let Some(theirs) = borrowed.get("mcpServers").and_then(Value::as_object) {
        for (name, definition) in theirs {
            if !servers.contains_key(name) {
                servers.insert(name.clone(), definition.clone());
                added = true;
            }
        }
    }
    if !linked && !added {
        return;
    }
    if added {
        own.insert("mcpServers".into(), Value::Object(servers));
    }

    // Renaming a complete sibling over the link replaces the link itself; a
    // direct write through it would change the source's permissions.
    let staged = profile.join(format!(".graft-settings-{}.tmp", uuid::Uuid::new_v4()));
    let result = (|| -> std::io::Result<()> {
        let data = serde_json::to_vec_pretty(&sorted(&Value::Object(own)))?;
        let mut handle = fs::OpenOptions::new().write(true).create_new(true).mode(0o600).open(&staged)?;
        handle.write_all(&data)?;
        handle.sync_all()?;
        fs::set_permissions(&staged, fs::Permissions::from_mode(0o600))?;
        fs::rename(&staged, &destination)
    })();
    let _ = fs::remove_file(&staged);
    match result {
        Ok(()) => diagnostics::note(
            "settings.local",
            &[("profile", profile_name(profile)), ("replacedLink", linked.into())],
        ),
        Err(e) => diagnostics::note(
            "settings.failed",
            &[("profile", profile_name(profile)), ("error", e.to_string().into())],
        ),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use graft_core::testing::Sandbox;
    use serde_json::json;
    use std::os::unix::fs::MetadataExt;

    fn write_settings(value: &Value, file: &Path) {
        fs::write(file, files::sorted_json(value)).unwrap();
    }

    fn settings(file: &Path) -> Value {
        serde_json::from_slice(&fs::read(file).unwrap_or_default()).unwrap_or(json!({}))
    }

    fn relink(target: &Path, at: &Path) {
        let _ = fs::remove_file(at);
        std::os::unix::fs::symlink(target, at).unwrap();
    }

    #[test]
    fn each_profile_keeps_its_permission_choices() {
        let sandbox = Sandbox::new();
        let source = sandbox.config().join("Claude-Settings-Source");
        let profile = sandbox.config().join("Claude-Settings-Own");
        fs::create_dir_all(&source).unwrap();
        fs::create_dir_all(&profile).unwrap();
        let source_file = source.join(NAME);
        let own_file = profile.join(NAME);
        let source_settings = json!({"mcpServers": {"shared": {"command": "source-server"}},
            "preferences": {"bypassPermissionsOptInByAccount": {"AAAA": true}}});
        let own_settings = json!({"mcpServers": {"own": {"command": "own-server"}},
            "preferences": {"bypassPermissionsOptInByAccount": {"BBBB": false}}, "globalShortcut": "disabled"});
        write_settings(&source_settings, &source_file);
        write_settings(&own_settings, &own_file);
        let source_bytes = fs::read(&source_file).unwrap();

        copy_desktop_servers(&source, &profile);
        assert!(!files::is_symlink(&own_file), "sharing chats leaves a writable local settings file");
        assert_eq!(settings(&own_file)["preferences"], own_settings["preferences"],
            "a source's permission opt-in never replaces the borrowing profile's choices");
        assert_eq!(settings(&own_file)["mcpServers"].as_object().unwrap().len(), 2,
            "missing local MCP servers are copied without removing the profile's own servers");

        let mut edited = own_settings.clone();
        edited["preferences"] = json!({"bypassPermissionsOptInByAccount": {"BBBB": true}});
        edited["mcpServers"] = json!({"shared": {"command": "my-override"}});
        // Claude saves settings by replacing the file; the next launch must
        // not replace that permission choice with another link to the source.
        fs::remove_file(&own_file).unwrap();
        write_settings(&edited, &own_file);
        copy_desktop_servers(&source, &profile);
        assert_eq!(settings(&own_file)["preferences"], edited["preferences"],
            "a permission choice made after sharing survives the next launch");
        assert_eq!(settings(&own_file)["mcpServers"]["shared"], json!({"command": "my-override"}),
            "an existing MCP server keeps this profile's configuration");
        assert_eq!(fs::read(&source_file).unwrap(), source_bytes,
            "changing the borrowing profile's settings never writes to the source");

        fs::remove_file(&own_file).unwrap();
        write_settings(&own_settings, &stash_path(&own_file));
        relink(&source_file, &own_file);
        copy_desktop_servers(&source, &profile);
        assert!(!files::is_symlink(&own_file), "an older settings link migrates to a real file");
        assert_eq!(settings(&own_file)["preferences"], own_settings["preferences"],
            "migration restores this profile's stashed permission choices");
        assert_eq!(settings(&own_file)["globalShortcut"], json!("disabled"),
            "migration preserves unrelated local preferences");
        assert!(files::exists(&stash_path(&own_file)), "migration retains the original backup");
        fs::remove_file(stash_path(&own_file)).unwrap();

        relink(&source_file, &own_file);
        copy_desktop_servers(&source, &profile);
        assert_eq!(settings(&own_file).get("preferences"), None,
            "a legacy link without local settings never inherits permission grants");
        assert!(!files::is_symlink(&own_file), "a legacy link without a stash is writable too");
        assert_eq!(settings(&own_file)["mcpServers"].as_object().unwrap().len(), 1,
            "a legacy link retains access to the source's local MCP servers");
        let settled_bytes = fs::read(&own_file).unwrap();
        let settled_time = files::modification_time(&own_file);
        copy_desktop_servers(&source, &profile);
        assert!(fs::read(&own_file).unwrap() == settled_bytes && files::modification_time(&own_file) == settled_time,
            "a repeated launch leaves already copied settings untouched");
        assert_eq!(fs::metadata(&own_file).unwrap().mode() & 0o777, 0o600,
            "copied server configuration is readable only by the profile's owner");

        fs::write(&source_file, "{ incomplete").unwrap();
        copy_desktop_servers(&source, &profile);
        assert_eq!(fs::read(&own_file).unwrap(), settled_bytes, "a source caught mid-write cannot change local settings");
        fs::write(&source_file, &source_bytes).unwrap();

        fs::write(&own_file, "{ incomplete").unwrap();
        copy_desktop_servers(&source, &profile);
        assert_eq!(fs::read(&own_file).unwrap(), b"{ incomplete",
            "an unreadable local settings file is never treated as empty");
        fs::remove_file(&own_file).unwrap();
        fs::write(stash_path(&own_file), "{ incomplete").unwrap();
        relink(&source_file, &own_file);
        copy_desktop_servers(&source, &profile);
        assert!(files::is_symlink(&own_file), "migration leaves an unreadable backup recoverable");
        assert_eq!(fs::read(&source_file).unwrap(), source_bytes,
            "even a failed migration cannot overwrite the source's settings");
        write_settings(&own_settings, &stash_path(&own_file));
        fs::remove_file(&source_file).unwrap();
        copy_desktop_servers(&source, &profile);
        assert!(!files::is_symlink(&own_file) && settings(&own_file) == own_settings,
            "a broken legacy link can recover the profile's own settings from its backup");
        let names: Vec<String> = fs::read_dir(&profile).unwrap()
            .map(|e| e.unwrap().file_name().to_string_lossy().into_owned())
            .filter(|n| n.starts_with(".graft-settings-")).collect();
        assert!(names.is_empty(), "no staged copy is left in the profile");
    }

    #[test]
    fn a_profile_is_never_given_its_own_servers_again() {
        let sandbox = Sandbox::new();
        let profile = sandbox.config().join("Same");
        fs::create_dir_all(&profile).unwrap();
        fs::write(profile.join(NAME), "{\"mcpServers\":{}}").unwrap();
        copy_desktop_servers(&profile, &profile);
        assert_eq!(fs::read_to_string(profile.join(NAME)).unwrap(), "{\"mcpServers\":{}}");
    }
}
