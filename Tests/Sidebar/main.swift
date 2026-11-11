import Foundation

let fm = FileManager.default
let root = URL(fileURLWithPath: "/private/tmp/graft-sidebar-integration-\(UUID().uuidString).noindex")
let support = root.appending(path: "Support")
Graft.applicationSupportOverride = support
Graft.runningClaudesOverride = { [] }
try fm.createDirectory(at: support, withIntermediateDirectories: true)
defer { if ProcessInfo.processInfo.environment["GRAFT_KEEP_SIDEBAR_FIXTURES"] == nil { try? fm.removeItem(at: root) } }
var checks = 0
func check(_ condition: Bool, _ message: String) {
    guard condition else { fatalError(message) }
    checks += 1
    print("ok \(message)")
}

let helper = try SidebarSync.prepareHelper()
let seeder = root.appending(path: "Seed.app")
check(Graft.runTool("/bin/cp", ["-cR", helper.path, seeder.path]) == 0, "a disposable fixture builder is copied")
let seedScript = #"""
const {app, BrowserWindow, session} = require('electron');
const fs = require('node:fs');
const request = JSON.parse(fs.readFileSync(process.argv[1], 'utf8'));
app.setPath('userData', request.scratch);
app.commandLine.appendSwitch('disable-gpu');
app.commandLine.appendSwitch('host-resolver-rules', 'MAP * ~NOTFOUND');
app.whenReady().then(async () => {
  const ses = session.fromPath(request.profile);
  ses.protocol.handle('https', () => new Response('<!doctype html>'));
  const window = new BrowserWindow({show:false,webPreferences:{session:ses,sandbox:true}});
  await window.loadURL('https://claude.ai/');
  const result = await window.webContents.executeJavaScript(`(async () => {
    const request = ${JSON.stringify(request)};
    const db = await new Promise((resolve,reject) => {
      const open = indexedDB.open('keyval-store',1);
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
    await new Promise((resolve,reject) => {
      const tx = db.transaction('keyval','readwrite');
      if (!request.noPinRecord) tx.objectStore('keyval').put(JSON.stringify({state:{starredIds:request.pins},version:0,updatedAt:1}),
        'store:pin-state:dframe-starred-code');
      tx.objectStore('keyval').put('keep exactly','unrelated-account-data');
      tx.oncomplete=resolve;tx.onerror=reject;
    });
    const pinnedOrder = request.pins.map(id=>'code:'+id);
    localStorage.setItem('dframe-store',JSON.stringify({version:1,state:{pinnedOrder,sortByByMode:{code:request.sort,cowork:'created'},
      sidebarWidth:333,lastSidebarScopeKey:request.scope}}));
    localStorage.setItem('LSS-persisted.starred-local-code-sessions',JSON.stringify({value:request.pins,tabId:'test',timestamp:request.time}));
    localStorage.setItem('LSS-persisted.dframe-local-slice',JSON.stringify({value:{pinnedOrder,homeProjectsPinnedOrder:[]},tabId:'test',timestamp:request.time}));
    localStorage.setItem('unrelated-login-marker','own-profile');
    localStorage.setItem('ccd-sync-owner',request.scope.split('/')[0]);
    localStorage.setItem('ccd-sync-active','1');
    db.close();return true;
  })()`);
  fs.writeFileSync(request.output,JSON.stringify(result));
  ses.flushStorageData();window.destroy();app.quit();
}).catch(e=>{console.error(e);app.exit(1)});
setTimeout(()=>app.exit(2),15000).unref();
"""#
let archive = try SidebarSync.asar([("package.json", Data(#"{"main":"main.js"}"#.utf8)), ("main.js", Data(seedScript.utf8))])
let contents = seeder.appending(path: "Contents")
try archive.data.write(to: contents.appending(path: "Resources/app.asar"))
let infoFile = contents.appending(path: "Info.plist")
var info = try PropertyListSerialization.propertyList(from: Data(contentsOf: infoFile), format: nil) as! [String:Any]
info["ElectronAsarIntegrity"] = ["Resources/app.asar": ["algorithm":"SHA256", "hash":archive.hash]]
try PropertyListSerialization.data(fromPropertyList: info, format: .xml, options: 0).write(to: infoFile)
try SidebarSync.sealIntegrity(of: seeder, headerHash: archive.hash)
check(Graft.runTool("/usr/bin/codesign", ["--force","--sign","-",seeder.path]) == 0, "the fixture builder is signed")
let a = support.appending(path: "A"), b = support.appending(path: "B")
let shared: Set<String> = ["local_a","local_b","local_c"]
let c = support.appending(path: "C")
for (profile, account, org, pins, time) in [(a,"account-a","org-a",["local_c","local_a"],10),
                                          (b,"account-b","org-b",["local_b"],20),
                                          (c,"account-c","org-c",[],30)] {
    let store = profile.appending(path: "claude-code-sessions/\(account)/\(org)")
    try fm.createDirectory(at: store, withIntermediateDirectories: true)
    try JSONSerialization.data(withJSONObject: ["lastKnownAccountUuid":account]).write(to: profile.appending(path: "config.json"))
    try JSONSerialization.data(withJSONObject: ["mcpServers":["own":["command":"keep"]],
        "preferences":["permissionChoice":"keep","epitaxyPrefs":["starred-local-code-sessions":pins,
            "dframe-local-slice":["pinnedOrder":pins.map { "code:" + $0 },"homeProjectsPinnedOrder":[]]]]])
        .write(to: profile.appending(path: "claude_desktop_config.json"))
    for id in shared {
        try JSONSerialization.data(withJSONObject: ["sessionId":id,"title":"Unchanged","isArchived":false])
            .write(to: store.appending(path: id + ".json"))
    }
    let request: [String:Any] = ["scratch":root.appending(path: "seed-scratch").path,"profile":profile.path,
        "pins":pins,"sort":"recency","scope":account + "/" + org,"time":time,
        "output":root.appending(path: "seed-result.json").path, "noPinRecord":profile == c]
    let file = root.appending(path: "seed-request.json")
    try JSONSerialization.data(withJSONObject: request).write(to: file)
    check(Graft.runTool(contents.appending(path: "MacOS/sidebar-storage").path,[file.path]) == 0,
          "\(profile.lastPathComponent) has actual Chromium sidebar storage")
}
let storeA = a.appending(path: "claude-code-sessions/account-a/org-a")
let storeB = b.appending(path: "claude-code-sessions/account-b/org-b")
Graft.saveMirrorState(Graft.MirrorState(pairs: [Graft.pairKey(storeA,storeB):[:]]))
SidebarSync.withLaunchLock { SidebarSync.synchronize(beforeOpening: a) }
var snapshots = try SidebarSync.storage([a,b], changes:nil, shared:[:])
check(snapshots[a.path]!.order == ["local_b","local_c","local_a"] && snapshots[a.path]!.sameChoices(as: snapshots[b.path]!),
      "two real databases agree on merged pins after reopening the storage engine")
var change = snapshots[a.path]!
change.pins = ["local_b","local_c"]; change.order = ["local_c","local_b"]; change.sort = "alpha"
_ = try SidebarSync.storage([a],changes:[a.path:change],shared:[a.path:shared])
SidebarSync.withLaunchLock { SidebarSync.synchronize(beforeOpening: b) }
snapshots = try SidebarSync.storage([a,b], changes:nil, shared:[:])
check(snapshots[b.path]!.order == change.order && snapshots[b.path]!.sort == "alpha"
      && snapshots[b.path]!.pins == change.pins, "an unpin, reorder and sort change survive another process and reach the source")
change = snapshots[b.path]!; change.pins = []; change.order = []
_ = try SidebarSync.storage([b],changes:[b.path:change],shared:[b.path:shared])
SidebarSync.withLaunchLock { SidebarSync.synchronize(beforeOpening: a) }
snapshots = try SidebarSync.storage([a,b], changes:nil, shared:[:])
check(snapshots.values.allSatisfy { $0.pins.isEmpty && $0.order.isEmpty }, "clearing the final pins survives restarting both stores")
// Claude writes no pin record until something is pinned, which is how every
// profile that has never pinned a chat looks on disk.
var unpinned = try SidebarSync.storage([c], changes: nil, shared: [:])[c.path]!
check(unpinned.pins.isEmpty && unpinned.order.isEmpty, "a profile with no pin record reads as having no pins")
unpinned.pins = ["local_a"]; unpinned.order = ["local_a"]
_ = try SidebarSync.storage([c], changes: [c.path: unpinned], shared: [c.path: shared])
check(try SidebarSync.storage([c], changes: nil, shared: [:])[c.path]!.pins == ["local_a"],
      "a pin written into a profile with no pin record survives another process")
for profile in [a,b] {
    let prefs = try JSONSerialization.jsonObject(with: Data(contentsOf: profile.appending(path: "claude_desktop_config.json"))) as! [String:Any]
    check((prefs["preferences"] as? [String:Any])?["permissionChoice"] as? String == "keep" && prefs["mcpServers"] != nil,
          "\(profile.lastPathComponent) retains its own desktop settings")
    let file = root.appending(path: "verify-request.json")
    let result = root.appending(path: "verify-result.json")
    try JSONSerialization.data(withJSONObject:["scratch":root.appending(path:"verify-scratch").path,
        "profile":profile.path,"verify":true,"output":result.path]).write(to:file)
    check(Graft.runTool(contents.appending(path:"MacOS/sidebar-storage").path,[file.path]) == 0
          && (try? String(contentsOf:result)) == "true", "\(profile.lastPathComponent) retains unrelated browser records and appearance")
}
// Project order: read from clones, written into the real database, newest wins.
func projectValue(_ order: [String], _ time: Int) -> String {
    String(decoding: try! JSONSerialization.data(withJSONObject: ["value": order, "tabId": "test", "timestamp": time]), as: UTF8.self)
}
let orderKeyA = SidebarSync.projectOrderPrefix + "account-a", orderKeyB = SidebarSync.projectOrderPrefix + "account-b"
_ = try SidebarSync.projectStorage(action: "projects-write", profiles: [
    ["path": a.path, "keys": [orderKeyA], "values": [orderKeyA: projectValue(["/x", "/y", "/z"], 500)]],
    ["path": b.path, "keys": [orderKeyB], "values": [orderKeyB: projectValue(["/z", "/x"], 100)]]])
SidebarSync.withLaunchLock { SidebarSync.synchronizeProjectOrder(beforeOpening: b) }
var orders = try SidebarSync.projectStorage(action: "projects-read", profiles: [["path": a.path, "keys": [orderKeyA]],
                                                                              ["path": b.path, "keys": [orderKeyB]]])
func decoded(_ text: String?) -> [String: Any] {
    text.flatMap { try? JSONSerialization.jsonObject(with: Data($0.utf8)) as? [String: Any] } ?? [:]
}
check(decoded(orders[b.path]?[orderKeyB])["value"] as? [String] == ["/x", "/y", "/z"]
      && (decoded(orders[b.path]?[orderKeyB])["timestamp"] as? NSNumber)?.intValue == 500,
      "the newer project order reaches the other account's real database")
check(decoded(orders[a.path]?[orderKeyA])["value"] as? [String] == ["/x", "/y", "/z"],
      "the account it came from keeps its own order")
check(SidebarSync.desktopProjectOrder(b, account: "account-b") == ["/x", "/y", "/z"],
      "the other account's preferences file, which Claude displays from, receives the order")
let bPrefs = try JSONSerialization.jsonObject(with: Data(contentsOf: b.appending(path: "claude_desktop_config.json"))) as! [String: Any]
check((bPrefs["preferences"] as? [String: Any])?["permissionChoice"] as? String == "keep" && bPrefs["mcpServers"] != nil,
      "writing the order keeps that account's other desktop settings")
_ = try SidebarSync.projectStorage(action: "projects-write", profiles: [
    ["path": b.path, "keys": [orderKeyB], "values": [orderKeyB: projectValue(["/y", "/x", "/z"], 900)]]])
// Claude saves a reorder in both places: the timed Local Storage copy and the preferences file.
do {
    let file = b.appending(path: "claude_desktop_config.json")
    var root = try JSONSerialization.jsonObject(with: Data(contentsOf: file)) as! [String: Any]
    var prefs = root["preferences"] as! [String: Any], epitaxy = prefs["epitaxyPrefs"] as! [String: Any]
    epitaxy["code-projects-order.account-b"] = ["/y", "/x", "/z"]
    prefs["epitaxyPrefs"] = epitaxy; root["preferences"] = prefs
    try JSONSerialization.data(withJSONObject: root).write(to: file)
}
let aPrefsFile = a.appending(path: "claude_desktop_config.json")
try ("{\n    \"zLast\": 1,\n    \"preferences\": " + String(decoding: try JSONSerialization.data(withJSONObject:
    (try JSONSerialization.jsonObject(with: Data(contentsOf: aPrefsFile)) as! [String: Any])["preferences"]!), as: UTF8.self)
    + ",\n    \"mcpServers\": {}\n}\n").write(to: aPrefsFile, atomically: true, encoding: .utf8)
try Graft.fm.setAttributes([.posixPermissions: 0o640], ofItemAtPath: aPrefsFile.path)
SidebarSync.withLaunchLock { SidebarSync.synchronizeProjectOrder(beforeOpening: a) }
orders = try SidebarSync.projectStorage(action: "projects-read", profiles: [["path": a.path, "keys": [orderKeyA]]])
check(decoded(orders[a.path]?[orderKeyA])["value"] as? [String] == ["/y", "/x", "/z"]
      && SidebarSync.desktopProjectOrder(a, account: "account-a") == ["/y", "/x", "/z"],
      "a later reorder on the other account travels back, into what Claude displays too")
let aPrefsText = try String(contentsOf: aPrefsFile, encoding: .utf8)
check(aPrefsText.hasPrefix("{\n    \"zLast\": 1,\n    \"preferences\": {") && aPrefsText.hasSuffix("}\n")
      && ((try? Graft.fm.attributesOfItem(atPath: aPrefsFile.path))?[.posixPermissions] as? Int) == 0o640,
      "the order is written into a desktop settings file in its own key order, indent and permissions")
let before = try Data(contentsOf: a.appending(path: "Local Storage/leveldb/CURRENT"))
SidebarSync.withLaunchLock { SidebarSync.synchronizeProjectOrder(beforeOpening: a) }
let after = try Data(contentsOf: a.appending(path: "Local Storage/leveldb/CURRENT"))
orders = try SidebarSync.projectStorage(action: "projects-read", profiles: [["path": a.path, "keys": [orderKeyA]]])
check(after == before && decoded(orders[a.path]?[orderKeyA])["value"] as? [String] == ["/y", "/x", "/z"],
      "a pass with nothing to carry leaves both databases as they were")
print("\(checks)/\(checks) native sidebar checks passed")
if ProcessInfo.processInfo.environment["GRAFT_KEEP_SIDEBAR_FIXTURES"] != nil { print(root.path) }
