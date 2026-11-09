//! Grafting, its stashes, and the settings it must not hand across.

use super::*;
use crate::fsx::{exists, is_directory, is_symlink, stash_path};
use crate::{graft, relink, ungraft};
use std::os::unix::fs::PermissionsExt;

#[test]
fn grafting_merges_two_histories_and_ungrafting_gives_back_what_was_owned() {
    let _world = World::new();
    let main = make_profile_with(
        "Claude",
        Some("AAAA"),
        Some("ORG-A"),
        &["shared"],
        &[("userThemeMode", "dark"), ("locale", "en-GB")],
    );
    write(&main.join("claude_desktop_config.json"), "{\"preferences\":{}}");

    // Different account: the pairing has to go one level deeper.
    let work = make_profile("Claude-Work", Some("BBBB"), Some("ORG-B"), &["mine"]);
    graft(&main, &work);
    assert_eq!(
        chats_visible(&work),
        vec!["local_mine.json", "local_shared.json"],
        "sharing a history merges the two rather than standing one in for the other"
    );
    assert_eq!(
        chats_visible(&main),
        vec!["local_mine.json", "local_shared.json"],
        "and the profile lent from ends up holding both of them too"
    );

    let own_folder = work.join("claude-code-sessions/BBBB/.ORG-B.graft-own");
    assert!(own_folder.join("local_mine.json").exists(), "its own chats are stashed, not destroyed");
    assert!(
        !work.join("claude-code-sessions/BBBB/ORG-B.graft-own").exists(),
        "the stash is hidden, so it cannot be read back as an organization"
    );

    let config = graft_core::files::config_json(&work);
    assert_eq!(config["oauth:tokenCache"], "token-for-Claude-Work", "the login is not overwritten by the source's");
    assert_eq!(config["userThemeMode"], "dark", "the theme is copied across");
    assert_eq!(config["locale"], "en-GB", "so is the locale");
    assert!(
        !is_symlink(&work.join("claude_desktop_config.json")),
        "permission settings are never linked to another profile"
    );
    assert!(!exists(&work.join("extensions-blocklist.json")), "the per-organization blocklist is never linked");

    graft(&main, &work);
    assert_eq!(chats_visible(&work), vec!["local_mine.json", "local_shared.json"], "grafting twice changes nothing");

    ungraft(&work);
    assert_eq!(chats_visible(&work), vec!["local_mine.json"], "ungrafting gives back its own chats");
    assert!(!is_symlink(&work.join("claude_desktop_config.json")), "and drops the links");

    // Same account: every organization it has is shared.
    let twin = make_profile("Claude-Twin", Some("AAAA"), Some("ORG-A"), &[]);
    graft(&main, &twin);
    assert!(
        !is_symlink(&twin.join("claude-code-sessions")) && is_directory(&twin.join("claude-code-sessions")),
        "the same account shares the whole store, as folders rather than a link"
    );
    assert_eq!(
        chats_visible(&twin),
        vec!["local_mine.json", "local_shared.json"],
        "and sees everything that profile holds, merges it was lent included"
    );
}

fn write_settings(value: &Value, path: &Path) {
    std::fs::write(path, graft_core::files::sorted_json(value)).unwrap();
}

fn settings(path: &Path) -> Value {
    match json_of(path) {
        Value::Null => json!({}),
        other => other,
    }
}

#[test]
fn each_profile_keeps_its_permission_choices() {
    let _world = World::new();
    let source = make_profile("Claude-Settings-Source", Some("AAAA"), Some("ORG"), &[]);
    let profile = make_profile("Claude-Settings-Own", Some("BBBB"), Some("ORG"), &[]);
    let name = "claude_desktop_config.json";
    let source_file = source.join(name);
    let own_file = profile.join(name);
    let source_settings = json!({
        "mcpServers": {"shared": {"command": "source-server"}},
        "preferences": {"bypassPermissionsOptInByAccount": {"AAAA": true}},
    });
    let own_settings = json!({
        "mcpServers": {"own": {"command": "own-server"}},
        "preferences": {"bypassPermissionsOptInByAccount": {"BBBB": false}},
        "globalShortcut": "disabled",
    });
    write_settings(&source_settings, &source_file);
    write_settings(&own_settings, &own_file);
    let source_bytes = std::fs::read(&source_file).unwrap();
    graft(&source, &profile);
    assert!(!is_symlink(&own_file), "sharing chats leaves a writable local settings file");
    assert_eq!(
        settings(&own_file)["preferences"],
        own_settings["preferences"],
        "a source's permission opt-in never replaces the borrowing profile's choices"
    );
    assert_eq!(
        settings(&own_file)["mcpServers"].as_object().map(|m| m.len()),
        Some(2),
        "missing local MCP servers are copied without removing the profile's own servers"
    );

    let mut edited = own_settings.clone();
    edited["preferences"] = json!({"bypassPermissionsOptInByAccount": {"BBBB": true}});
    edited["mcpServers"] = json!({"shared": {"command": "my-override"}});
    // Claude saves settings by replacing the file; the next launch must not
    // replace that permission choice with another link to the source.
    std::fs::remove_file(&own_file).unwrap();
    write_settings(&edited, &own_file);
    graft(&source, &profile);
    assert_eq!(
        settings(&own_file)["preferences"],
        edited["preferences"],
        "a permission choice made after sharing survives the next launch"
    );
    assert_eq!(
        settings(&own_file)["mcpServers"]["shared"],
        json!({"command": "my-override"}),
        "an existing MCP server keeps this profile's configuration"
    );
    assert_eq!(
        std::fs::read(&source_file).unwrap(),
        source_bytes,
        "changing the borrowing profile's settings never writes to the source"
    );
    ungraft(&profile);
    assert_eq!(
        settings(&own_file)["preferences"],
        edited["preferences"],
        "returning to separate chats keeps the latest permission choice"
    );

    std::fs::remove_file(&own_file).unwrap();
    write_settings(&own_settings, &own_file);
    relink(&source_file, &own_file);
    graft(&source, &profile);
    assert!(!is_symlink(&own_file), "an older settings link migrates to a real file");
    assert_eq!(
        settings(&own_file)["preferences"],
        own_settings["preferences"],
        "migration restores this profile's stashed permission choices"
    );
    assert_eq!(settings(&own_file)["globalShortcut"], "disabled", "migration preserves unrelated local preferences");
    assert!(exists(&stash_path(&own_file)), "migration retains the original backup");
    ungraft(&profile);

    std::fs::remove_file(&own_file).unwrap();
    relink(&source_file, &own_file);
    graft(&source, &profile);
    assert!(
        settings(&own_file).get("preferences").is_none(),
        "a legacy link without local settings never inherits permission grants"
    );
    assert!(!is_symlink(&own_file), "a legacy link without a stash is writable too");
    assert_eq!(
        settings(&own_file)["mcpServers"].as_object().map(|m| m.len()),
        Some(1),
        "a legacy link retains access to the source's local MCP servers"
    );
    let settled_bytes = std::fs::read(&own_file).unwrap();
    let settled_time = crate::fsx::modified(&own_file);
    graft(&source, &profile);
    assert!(
        std::fs::read(&own_file).unwrap() == settled_bytes && crate::fsx::modified(&own_file) == settled_time,
        "a repeated launch leaves already copied settings untouched"
    );
    assert_eq!(
        std::fs::metadata(&own_file).unwrap().permissions().mode() & 0o777,
        0o600,
        "copied server configuration is readable only by the profile's owner"
    );

    std::fs::write(&source_file, "{ incomplete").unwrap();
    graft(&source, &profile);
    assert_eq!(
        std::fs::read(&own_file).unwrap(),
        settled_bytes,
        "a source caught mid-write cannot change local settings"
    );
    std::fs::write(&source_file, &source_bytes).unwrap();

    std::fs::write(&own_file, "{ incomplete").unwrap();
    graft(&source, &profile);
    assert_eq!(
        read(&own_file).as_deref(),
        Some("{ incomplete"),
        "an unreadable local settings file is never treated as empty"
    );
    std::fs::remove_file(&own_file).unwrap();
    std::fs::write(stash_path(&own_file), "{ incomplete").unwrap();
    relink(&source_file, &own_file);
    graft(&source, &profile);
    assert!(is_symlink(&own_file), "migration leaves an unreadable backup recoverable");
    assert_eq!(
        std::fs::read(&source_file).unwrap(),
        source_bytes,
        "even a failed migration cannot overwrite the source's settings"
    );
    write_settings(&own_settings, &stash_path(&own_file));
    std::fs::remove_file(&source_file).unwrap();
    graft(&source, &profile);
    assert!(
        !is_symlink(&own_file) && settings(&own_file) == own_settings,
        "a broken legacy link can recover the profile's own settings from its backup"
    );
}

// Claude writes config.json, and recreates chat directories, by renaming over
// whatever is there. A rename leaves a real file where the symlink was, so the
// profile goes back to writing its own copy without anything noticing. These
// cover what has to happen the next time that profile is grafted.

#[test]
fn data_written_after_a_graft_is_what_comes_back() {
    let _world = World::new();
    let main = make_profile("Claude", Some("AAAA"), Some("ORG-A"), &["shared"]);
    write(&main.join("claude_desktop_config.json"), "{\"preferences\":{}}");

    let work = make_profile("Claude-Work", Some("BBBB"), Some("ORG-B"), &["mine"]);
    let settings = work.join("claude_desktop_config.json");
    write(&settings, "own settings");
    graft(&main, &work);

    // Claude renames a temporary over the link and carries on writing its own.
    let _ = std::fs::remove_file(&settings);
    write(&settings, "own settings, edited since");

    let own_chats = work.join("claude-code-sessions/BBBB/ORG-B");
    let _ = std::fs::remove_dir_all(&own_chats);
    write(&own_chats.join("local_written_since.json"), "{}");

    graft(&main, &work);
    ungraft(&work);

    assert_eq!(chats_visible(&work), vec!["local_mine.json"], "what the profile owned before the graft is what it gets back");
    assert!(
        exists(&main.join("claude-code-sessions/AAAA/ORG-A/local_written_since.json")),
        "and a chat it wrote while borrowing is handed over rather than lost"
    );
    assert_eq!(
        read(&settings).as_deref(),
        Some("own settings, edited since"),
        "an edit made after the graft is what comes back, not the copy it replaced"
    );
}

#[test]
fn an_orphaned_stash_is_folded_back_in() {
    let _world = World::new();
    let main = make_profile("Claude", Some("AAAA"), Some("ORG-A"), &["shared"]);
    let work = make_profile("Claude-Work", Some("BBBB"), Some("ORG-B"), &["mine"]);
    graft(&main, &work);

    let own_chats = work.join("claude-code-sessions/BBBB/ORG-B");
    let _ = std::fs::remove_dir_all(&own_chats);
    write(&own_chats.join("local_written_since.json"), "{}");

    ungraft(&work);
    assert!(
        !exists(&work.join("claude-code-sessions/BBBB/.ORG-B.graft-own")),
        "ungrafting folds the stash back in rather than abandoning it beside the copies"
    );
    assert_eq!(chats_visible(&work), vec!["local_mine.json"], "so the profile is handed back what it owned");
    assert!(
        exists(&main.join("claude-code-sessions/AAAA/ORG-A/local_written_since.json")),
        "and what it wrote while borrowing went to the profile it borrowed from"
    );
}

#[test]
fn a_config_caught_mid_write_decides_nothing() {
    let world = World::new();
    let main = make_profile_with(
        "Claude",
        Some("AAAA"),
        Some("ORG-A"),
        &["shared"],
        &[("userThemeMode", "dark"), ("locale", "en-GB")],
    );
    let work = make_profile("Claude-Work", Some("BBBB"), Some("ORG-B"), &["mine"]);

    // What a read landing part way through Claude's rename sees.
    let config = work.join("config.json");
    write(&config, "{\"lastKnownAccountUuid\":\"BBBB\",\"oauth:tokenCache\":\"the-login\"");

    graft(&main, &work);
    assert!(
        read(&config).map(|t| t.contains("the-login")).unwrap_or(false),
        "a config that will not parse is left as it is, login and all"
    );
    assert_eq!(chats_visible(&work), vec!["local_mine.json"], "and the profile keeps reading its own chats");
    assert!(
        !is_symlink(&work.join("claude-code-sessions")),
        "an account nothing can read is never taken for the source's own"
    );

    // A profile that has never been signed in has no config at all, and
    // sharing the whole store is what that case is for.
    let fresh = world.support().join("Claude-Fresh");
    std::fs::create_dir_all(&fresh).unwrap();
    graft(&main, &fresh);
    assert_eq!(chats_visible(&fresh), vec!["local_shared.json"], "a profile with no config yet still shares the whole store");
}

#[test]
fn a_profile_is_never_grafted_from_itself() {
    let _world = World::new();
    // A profile grafted from itself would stash every file it owns and leave
    // links pointing at their own empty names.
    let profile = make_profile("Claude-Selfie", Some("AAAA"), Some("ORG1"), &["1"]);
    write(&profile.join("window-state.json"), "kept");

    graft(&profile, &profile);

    assert!(!is_symlink(&profile.join("window-state.json")), "grafting a profile from itself changes nothing");
    assert_eq!(read(&profile.join("window-state.json")).as_deref(), Some("kept"), "and its own files are untouched");
    assert!(
        profile.join("claude-code-sessions/AAAA/ORG1/local_1.json").exists(),
        "and its chats are still there"
    );

    assert!(!relink(&profile, &profile), "relink refuses a link to itself");
    assert!(is_directory(&profile), "the folder survives that too");
}
