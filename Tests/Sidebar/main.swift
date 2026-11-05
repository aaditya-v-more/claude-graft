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
      tx.objectStore('keyval').put(JSON.stringify({state:{starredIds:request.pins},version:0,updatedAt:1}),
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
check(Graft.runTool("/usr/bin/codesign", ["--force","--sign","-",seeder.path]) == 0, "the fixture builder is signed")
let a = support.appending(path: "A"), b = support.appending(path: "B")
let shared: Set<String> = ["local_a","local_b","local_c"]
for (profile, account, org, pins, time) in [(a,"account-a","org-a",["local_c","local_a"],10),
                                          (b,"account-b","org-b",["local_b"],20)] {
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
        "output":root.appending(path: "seed-result.json").path]
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
print("\(checks)/\(checks) native sidebar checks passed")
if ProcessInfo.processInfo.environment["GRAFT_KEEP_SIDEBAR_FIXTURES"] != nil { print(root.path) }
