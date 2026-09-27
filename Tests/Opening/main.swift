import AppKit

let fm = FileManager.default
let temporary = fm.temporaryDirectory.appending(path: "graft-opening-\(UUID().uuidString).noindex")
let support = temporary.appending(path: "Application Support")
try fm.createDirectory(at: support, withIntermediateDirectories: true)
Graft.applicationSupportOverride = support
Graft.claudeProjectsOverride = temporary.appending(path: "empty-projects")
Graft.claudeConfigFileOverride = temporary.appending(path: "empty-login.json")
Installer.installDirectoryOverride = temporary.appending(path: "Applications")
Installer.registersWithLaunchServices = false
let app = NSApplication.shared
app.setActivationPolicy(.accessory)
app.finishLaunching()
var checks = 0
var failures = 0
func check(_ condition: Bool, _ message: String) {
    checks += 1
    if !condition { failures += 1 }
    print("\(condition ? "ok" : "FAIL")  \(message)")
    fflush(stdout)
}
func settle(_ seconds: TimeInterval = 0.2) { RunLoop.main.run(until: Date().addingTimeInterval(seconds)) }
@discardableResult
func until(_ condition: () -> Bool) -> Bool {
    let deadline = Date().addingTimeInterval(20)
    repeat {
        if condition() { return true }
        settle()
    } while Date() < deadline
    return condition()
}
func openProfile(_ shortcut: Shortcut) {
    var completed = false
    var failure: Error?
    ProfileOpener.open(profile: shortcut.profileDir, shortcut: shortcut) { error in
        failure = error
        completed = true
    }
    check(until { completed }, "the app's Open action completes")
    check(failure == nil, "the selected shortcut opens without an error")
}
func pids(_ profile: URL) -> [pid_t] {
    Graft.processes().filter { Graft.isClaudeProcess($0.command) && Graft.carriesDataDir($0.command, profile) }.map(\.id)
}
func launches(_ profile: URL) -> Int {
    let log = (try? String(contentsOf: Diagnostics.file, encoding: .utf8)) ?? ""
    return log.split(separator: "\n").filter { line in
        guard let data = String(line).data(using: .utf8),
              let event = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        else { return false }
        return event["event"] as? String == "launcher.run" && event["profile"] as? String == profile.lastPathComponent
    }.count
}
func stop(_ profile: URL) {
    for pid in pids(profile) { NSRunningApplication(processIdentifier: pid)?.terminate() }
    check(until { pids(profile).isEmpty }, "the disposable profile exits cleanly")
}

var first = Shortcut(name: "Opening First", folder: "Claude-Opening-First", source: .own)
var second = Shortcut(name: "Opening Second", folder: "Claude-Opening-Second", source: .own)
first.installedName = first.name
second.installedName = second.name
let store = ShortcutStore()
store.shortcuts = [first, second]
let existing = Graft.processes().filter { Graft.isClaudeProcess($0.command) }.map(\.id)
defer { try? fm.removeItem(at: temporary) }
_ = try Installer.install(first, sourceDir: nil)
let original = try Installer.install(second, sourceDir: nil)
let renamed = original.deletingLastPathComponent().appending(path: "STORE Claude.app")
try fm.moveItem(at: original, to: renamed)
check(Installer.installedBundle(for: second) == nil, "the renamed bundle reproduces the old failed lookup")

openProfile(first)
check(until { pids(first.profileDir).count == 1 }, "the first selected profile launches its own Claude")
openProfile(second)
check(until { pids(second.profileDir).count == 1 }, "the renamed second shortcut launches while the first is running")
check(launches(second.profileDir) == 1, "the selected shortcut actually reaches its launcher")
let secondPID = pids(second.profileDir).first
check(until { secondPID != nil && NSWorkspace.shared.frontmostApplication?.processIdentifier == secondPID },
      "the second profile's window is brought forward")
openProfile(first)
check(until { pids(first.profileDir).first.map { NSWorkspace.shared.frontmostApplication?.processIdentifier == $0 } == true },
      "Open can bring the first profile forward again")
openProfile(second)
check(until { launches(second.profileDir) >= 2 }, "a repeated click still runs the shortcut's profile check")
check(until { secondPID != nil && NSWorkspace.shared.frontmostApplication?.processIdentifier == secondPID },
      "reopening the second profile activates its existing window")
settle(1)
check(pids(second.profileDir) == secondPID.map { [$0] }, "repeated Open never creates a second Claude on the same profile")

let hidden = temporary.appending(path: "Missing shortcut.app")
try fm.moveItem(at: renamed, to: hidden)
var missingFinished = false
var missingError: Error?
ProfileOpener.open(profile: second.profileDir, shortcut: second) { error in
    missingError = error
    missingFinished = true
}
check(until { missingFinished }, "a missing shortcut completes with an error")
check(missingError as? ProfileOpener.Failure == .missingShortcut,
      "the missing shortcut is reported instead of falling back to another profile")
check(pids(second.profileDir) == secondPID.map { [$0] }, "the missing shortcut does not switch or duplicate the selected profile")
try fm.moveItem(at: hidden, to: renamed)
var sessionFinished = false
var sessionFailure: SessionStarter.Failure?
DispatchQueue.global(qos: .userInitiated).async {
    let failure = SessionStarter.start(profile: second.profileDir, interactive: true)
    DispatchQueue.main.async {
        sessionFailure = failure
        sessionFinished = true
    }
}
check(until { sessionFinished }, "Start Session completes for the selected disposable profile")
if case .noLogin? = sessionFailure {
    check(true, "Start Session reports the selected profile's missing login")
} else { check(false, "Start Session reports the selected profile's missing login") }
check(launches(second.profileDir) == 2, "Start Session does not launch a desktop window")
stop(second.profileDir)
openProfile(second)
check(until { pids(second.profileDir).count == 1 }, "the renamed shortcut can open again after quitting")
check(launches(second.profileDir) == 3, "opening after quitting reaches the shortcut launcher again")
let remaining = Graft.processes().filter {
    Graft.isClaudeProcess($0.command)
        && !Graft.carriesDataDir($0.command, first.profileDir)
        && !Graft.carriesDataDir($0.command, second.profileDir)
}.map(\.id)
check(remaining == existing, "the checks neither launch nor close any real profile")
stop(first.profileDir)
stop(second.profileDir)
try? fm.removeItem(at: temporary)
print("\(checks - failures)/\(checks) opening checks passed")
if failures != 0 { exit(1) }
