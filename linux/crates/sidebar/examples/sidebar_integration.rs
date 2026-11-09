//! Sidebar sync against real Chromium storage, in disposable profiles.
//!
//! Run by `linux/test-sidebar.sh` on the host, never in the suite: it copies
//! the installed Claude's Electron runtime and runs that copy, headless, over
//! profiles it makes inside the folder it is given. Nothing here reads or
//! writes a real profile, and nothing starts, shows or quits a Claude.

use graft_core::{files, lock, paths};
use graft_sidebar::{helper, storage, synchronize_before_opening, Snapshot};
use serde_json::{json, Value};
use std::collections::{BTreeSet, HashMap};
use std::fs;
use std::os::unix::fs::{MetadataExt, PermissionsExt};
use std::path::{Path, PathBuf};

const SEED_SCRIPT: &str = r#"
const {app, WebContentsView, session} = require('electron');
const fs = require('node:fs');
const request = JSON.parse(fs.readFileSync(process.argv[process.argv.length - 1], 'utf8'));
app.setPath('userData', request.scratch + '/runtime');
app.commandLine.appendSwitch('host-resolver-rules', 'MAP * ~NOTFOUND');
setTimeout(() => app.exit(2), 15000).unref();
app.whenReady().then(async () => {
  const ses = session.fromPath(request.profile);
  ses.protocol.handle('https', () => new Response('<!doctype html>', {headers: {'Content-Type': 'text/html'}}));
  const view = new WebContentsView({webPreferences: {session: ses, sandbox: true}});
  await view.webContents.loadURL('https://claude.ai/');
  const result = await view.webContents.executeJavaScript(`(async () => {
    const request = ${JSON.stringify(request)};
    const db = await new Promise((resolve, reject) => {
      const open = indexedDB.open('keyval-store', 1);
      open.onupgradeneeded = () => open.result.createObjectStore('keyval');
      open.onsuccess = () => resolve(open.result); open.onerror = reject;
    });
    if (request.verify) {
      const unrelated = await new Promise(resolve => {
        const get = db.transaction('keyval').objectStore('keyval').get('unrelated-account-data');
        get.onsuccess = () => resolve(get.result);
      });
      const frame = JSON.parse(localStorage.getItem('dframe-store'));
      db.close();
      return unrelated === 'keep exactly' && frame.state.sidebarWidth === 333
        && frame.state.sortByByMode.cowork === 'created'
        && localStorage.getItem('ccd-sync-pending:ccd/dframe-store') === frame.state.lastSidebarScopeKey
        && localStorage.getItem('unrelated-login-marker') === 'own-profile';
    }
    await new Promise((resolve, reject) => {
      const tx = db.transaction('keyval', 'readwrite');
      if (!request.noPinRecord) tx.objectStore('keyval').put(JSON.stringify({state: {starredIds: request.pins},
        version: 0, updatedAt: 1}), 'store:pin-state:dframe-starred-code');
      tx.objectStore('keyval').put('keep exactly', 'unrelated-account-data');
      tx.oncomplete = resolve; tx.onerror = reject;
    });
    const pinnedOrder = request.pins.map(id => 'code:' + id);
    localStorage.setItem('dframe-store', JSON.stringify({version: 1, state: {pinnedOrder,
      sortByByMode: {code: request.sort, cowork: 'created'}, sidebarWidth: 333, lastSidebarScopeKey: request.scope}}));
    localStorage.setItem('LSS-persisted.starred-local-code-sessions',
      JSON.stringify({value: request.pins, tabId: 'test', timestamp: request.time}));
    localStorage.setItem('LSS-persisted.dframe-local-slice',
      JSON.stringify({value: {pinnedOrder, homeProjectsPinnedOrder: []}, tabId: 'test', timestamp: request.time}));
    localStorage.setItem('unrelated-login-marker', 'own-profile');
    localStorage.setItem('ccd-sync-owner', request.scope.split('/')[0]);
    localStorage.setItem('ccd-sync-active', '1');
    db.close();
    return true;
  })()`);
  fs.writeFileSync(request.output, JSON.stringify(result));
  await ses.flushStorageData();
  view.webContents.close();
  app.quit();
}).catch(error => { console.error(error); app.exit(1); });
"#;

struct Checks(usize);

impl Checks {
    fn check(&mut self, condition: bool, message: &str) {
        if !condition {
            eprintln!("FAILED {message}");
            std::process::exit(1);
        }
        self.0 += 1;
        println!("ok {message}");
    }
}

/// A second runtime beside the helper, with the seeding script in place of
/// the helper's. The binary is linked rather than copied again, since both
/// sit in the folder this run was given.
fn seeder_from(helper_dir: &Path, seeder: &Path) -> std::io::Result<()> {
    fs::create_dir_all(seeder.join("resources"))?;
    for entry in fs::read_dir(helper_dir)? {
        let entry = entry?;
        let name = entry.file_name();
        let target = seeder.join(&name);
        let meta = fs::symlink_metadata(entry.path())?;
        if meta.file_type().is_symlink() {
            std::os::unix::fs::symlink(fs::read_link(entry.path())?, target)?;
        } else if name == helper::EXECUTABLE && fs::hard_link(entry.path(), &target).is_err() {
            fs::copy(entry.path(), &target)?;
        }
    }
    let archive = helper::asar(&[("package.json", br#"{"name":"graft-seed","main":"main.js"}"#),
        ("main.js", SEED_SCRIPT.as_bytes())]);
    fs::write(seeder.join("resources/app.asar"), archive)
}

fn seed(seeder: &Path, root: &Path, request: Value) -> Value {
    let scratch = root.join(format!("seed-{}", uuid::Uuid::new_v4()));
    fs::create_dir_all(&scratch).unwrap();
    let mut request = request;
    request["scratch"] = paths::text(&scratch).into();
    request["output"] = paths::text(&scratch.join("result.json")).into();
    let reply = helper::run(seeder, &request, &scratch).unwrap_or(Value::Null);
    let _ = fs::remove_dir_all(&scratch);
    reply
}

fn sync(profile: &Path) {
    lock::with_launch_lock(|| synchronize_before_opening(profile));
}

fn main() {
    let root = PathBuf::from(std::env::args().nth(1).expect("a folder to work in"));
    fs::create_dir_all(&root).unwrap();
    let root = paths::normalize(&root);
    let real_home = paths::home();
    let stray = real_home.join(".config/graft-sidebar");
    let stray_before = stray.exists();
    let config = root.join("config");
    fs::create_dir_all(&config).unwrap();
    paths::set_home_override(Some(root.join("home")));
    paths::set_config_home_override(Some(config.clone()));
    paths::set_data_home_override(Some(root.join("data")));
    let mut checks = Checks(0);

    let started = std::time::Instant::now();
    let helper_dir = helper::prepare_helper().expect("the helper could be built from the installed Claude");
    checks.check(helper_dir.starts_with(&config), "the helper is built inside this run's own folder");
    println!("   built in {:.1}s", started.elapsed().as_secs_f64());
    let started = std::time::Instant::now();
    checks.check(helper::prepare_helper().ok() == Some(helper_dir.clone()), "a built helper is reused");
    println!("   reused in {:.2}s", started.elapsed().as_secs_f64());
    let seeder = root.join("seed-runtime");
    seeder_from(&helper_dir, &seeder).unwrap();

    let a = config.join("A");
    let b = config.join("B");
    let c = config.join("C");
    let shared: BTreeSet<String> = ["local_a", "local_b", "local_c"].iter().map(|s| s.to_string()).collect();
    for (profile, account, org, pins, time) in [
        (&a, "account-a", "org-a", vec!["local_c", "local_a"], 10),
        (&b, "account-b", "org-b", vec!["local_b"], 20),
        (&c, "account-c", "org-c", vec![], 30),
    ] {
        let store = profile.join(format!("claude-code-sessions/{account}/{org}"));
        fs::create_dir_all(&store).unwrap();
        files::write_json(&profile.join("config.json"), &json!({"lastKnownAccountUuid": account})).unwrap();
        let pin_order: Vec<String> = pins.iter().map(|id| format!("code:{id}")).collect();
        let settings = json!({"mcpServers": {"own": {"command": "keep"}}, "preferences": {"permissionChoice": "keep",
            "epitaxyPrefs": {"starred-local-code-sessions": pins, "dframe-local-slice":
                {"pinnedOrder": pin_order, "homeProjectsPinnedOrder": []}}}});
        let settings_file = profile.join("claude_desktop_config.json");
        if profile == &a {
            // Laid out the way a person might have left it, so the pass can
            // be seen to keep the layout and the mode it was given.
            let text = graft_sidebar::records::preferences_text(Some("{\n    \"x\": 1\n}\n"), &settings);
            fs::write(&settings_file, text).unwrap();
            fs::set_permissions(&settings_file, fs::Permissions::from_mode(0o640)).unwrap();
        } else {
            fs::write(&settings_file, settings.to_string()).unwrap();
        }
        for id in &shared {
            files::write_json(&store.join(format!("{id}.json")),
                &json!({"sessionId": id, "title": "Unchanged", "isArchived": false})).unwrap();
        }
        let reply = seed(&seeder, &root, json!({"profile": paths::text(profile), "pins": pins, "sort": "recency",
            "scope": format!("{account}/{org}"), "time": time, "noPinRecord": profile == &c}));
        checks.check(reply == json!(true),
            &format!("{} has actual Chromium sidebar storage", profile.file_name().unwrap().to_string_lossy()));
    }
    let store_a = a.join("claude-code-sessions/account-a/org-a");
    let store_b = b.join("claude-code-sessions/account-b/org-b");
    let key = format!("{}\0{}", paths::text(&store_a), paths::text(&store_b));
    files::write_json(&paths::graft_file("mirrored-chats.json"), &json!({"pairs": {key: {}}})).unwrap();

    let read = |profiles: &[&PathBuf]| -> HashMap<String, Snapshot> {
        let list: Vec<PathBuf> = profiles.iter().map(|p| (*p).clone()).collect();
        storage::storage(&list, None, &HashMap::new()).expect("the storage could be read")
    };
    let text = |p: &Path| paths::text(p);

    let started = std::time::Instant::now();
    sync(&a);
    println!("   first sync in {:.1}s", started.elapsed().as_secs_f64());
    let snapshots = read(&[&a, &b]);
    let order: Vec<&str> = snapshots[&text(&a)].order.iter().map(String::as_str).collect();
    checks.check(order == ["local_b", "local_c", "local_a"] && snapshots[&text(&a)].same_choices(&snapshots[&text(&b)]),
        "two real databases agree on merged pins after reopening the storage engine");
    let record: Value = serde_json::from_slice(&fs::read(store_b.join("local_c.json")).unwrap()).unwrap();
    checks.check(record["isStarred"] == json!(true) && record["title"] == json!("Unchanged"),
        "the session records say the same as the sidebar and keep their titles");

    let mut change = snapshots[&text(&a)].clone();
    change.pins = vec!["local_b".into(), "local_c".into()];
    change.order = vec!["local_c".into(), "local_b".into()];
    change.sort = "alpha".into();
    let written = storage::storage(std::slice::from_ref(&a), Some(&HashMap::from([(text(&a), change.clone())])),
        &HashMap::from([(text(&a), shared.clone())]));
    checks.check(written.is_ok(), "a change is written into one profile through Chromium");
    sync(&b);
    let snapshots = read(&[&a, &b]);
    let theirs = &snapshots[&text(&b)];
    checks.check(theirs.order == change.order && theirs.sort == "alpha" && theirs.pins == change.pins,
        "an unpin, reorder and sort change survive another process and reach the source");

    let mut cleared = theirs.clone();
    cleared.pins.clear();
    cleared.order.clear();
    storage::storage(std::slice::from_ref(&b), Some(&HashMap::from([(text(&b), cleared)])),
        &HashMap::from([(text(&b), shared.clone())])).expect("the pins could be cleared");
    sync(&a);
    let snapshots = read(&[&a, &b]);
    checks.check(snapshots.values().all(|s| s.pins.is_empty() && s.order.is_empty()),
        "clearing the final pins survives restarting both stores");

    // Claude writes no pin record until something is pinned, which is how
    // every profile that has never pinned a chat looks on disk.
    let mut unpinned = read(&[&c])[&text(&c)].clone();
    checks.check(unpinned.pins.is_empty() && unpinned.order.is_empty(), "a profile with no pin record reads as having no pins");
    unpinned.pins = vec!["local_a".into()];
    unpinned.order = vec!["local_a".into()];
    storage::storage(std::slice::from_ref(&c), Some(&HashMap::from([(text(&c), unpinned)])),
        &HashMap::from([(text(&c), shared.clone())])).expect("a pin could be written");
    checks.check(read(&[&c])[&text(&c)].pins == vec!["local_a".to_string()],
        "a pin written into a profile with no pin record survives another process");

    for profile in [&a, &b] {
        let name = profile.file_name().unwrap().to_string_lossy().into_owned();
        let file = profile.join("claude_desktop_config.json");
        let settings: Value = serde_json::from_slice(&fs::read(&file).unwrap()).unwrap();
        checks.check(settings["preferences"]["permissionChoice"] == json!("keep") && settings["mcpServers"].is_object(),
            &format!("{name} retains its own desktop settings"));
        checks.check(settings["preferences"]["epitaxyPrefs"]["starred-local-code-sessions"] == json!([]),
            &format!("{name}'s fallback pins agree with its sidebar"));
        let reply = seed(&seeder, &root, json!({"profile": paths::text(profile), "verify": true}));
        checks.check(reply == json!(true), &format!("{name} retains unrelated browser records and appearance"));
    }
    let a_settings = fs::read_to_string(a.join("claude_desktop_config.json")).unwrap();
    checks.check(a_settings.contains("\n    \"mcpServers\"") && a_settings.ends_with("}\n"),
        "a settings file written forward keeps its indent and final newline");
    checks.check(fs::metadata(a.join("claude_desktop_config.json")).unwrap().mode() & 0o777 == 0o640,
        "and its permissions");

    let cache: Vec<String> = fs::read_dir(helper::cache_dir()).unwrap()
        .map(|e| e.unwrap().file_name().to_string_lossy().into_owned()).collect();
    checks.check(cache.iter().filter(|n| n.len() == 20).count() == 1 && cache.contains(&"CACHEDIR.TAG".to_string()),
        "the helper cache holds one helper and says it is a cache");
    let leftovers: Vec<String> = fs::read_dir(paths::graft_folder()).unwrap()
        .map(|e| e.unwrap().file_name().to_string_lossy().into_owned())
        .filter(|n| n.starts_with(".sidebar-")).collect();
    checks.check(leftovers.is_empty(), "every pass removes its scratch folder");
    let backups = files::listing(&helper::backups_dir()).unwrap_or_default();
    checks.check(!backups.is_empty(), "a writing pass leaves a backup of what it replaced");
    checks.check(stray.exists() == stray_before, "the helper writes nothing into the real home folder");
    let listing = std::process::Command::new("ps").args(["-eo", "args="]).output().unwrap();
    let running = String::from_utf8_lossy(&listing.stdout).lines()
        .any(|line| line.contains(&paths::text(&root)) && line.contains(helper::EXECUTABLE));
    checks.check(!running, "no helper process outlives its pass");

    println!("{}/{} sidebar storage checks passed", checks.0, checks.0);
}
