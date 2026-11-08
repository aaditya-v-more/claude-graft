import AppKit
import Foundation

enum ClaudeUpdateConfirmation {
    static func show(runningCount: Int) -> Bool {
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = L10n.text("Update Claude Desktop?")
        alert.informativeText = L10n.format("This will close all running Claude instances and stop their current workflows. Finish or save your work before continuing.\n\nCurrently running: %ld.\n\nYour profiles and manual update setting will be kept.", runningCount)
        alert.addButton(withTitle: L10n.text("Cancel"))
        alert.addButton(withTitle: L10n.text("Quit All Claude & Update"))
        NSApp.activate(ignoringOtherApps: true)
        return alert.runModal() == .alertSecondButtonReturn
    }
}

/// What a Graft started part way through an update does with the run it finds
/// in the journal, decided on a snapshot that could be read.
enum ClaudeUpdateResume: Equatable {
    /// Only an updater already running may be followed through a relaunch;
    /// restarting Graft is not another press of Quit All.
    case follow(ClaudeUpdateFlow.Phase)
    case finished
    case abandoned

    static func decide(_ sample: ClaudeUpdateFlow.Sample, target: String) -> ClaudeUpdateResume {
        if sample.helper != nil { return .follow(.downloading) }
        if sample.installerRunning { return .follow(.installing) }
        let installed = sample.installed.map { $0 == target || ClaudeUpdateFeed.isNewer($0, than: target) } ?? false
        return installed ? .finished : .abandoned
    }
}

/// Stopping Claude's updater used to send one quit and wait for ever. One quit
/// it missed left the update lock held, so every shortcut refused to open,
/// behind "Waiting for Claude's updater to close…" and a window kept hidden,
/// so nobody could close it by hand either. The lock stays held while it
/// runs: that much is right.
enum ClaudeUpdateStop {
    static let resendInterval: TimeInterval = 10
    static let hidingLimit: TimeInterval = 60

    static func resendsQuit(lastQuit: Date?, now: Date) -> Bool {
        lastQuit.map { now.timeIntervalSince($0) >= resendInterval } ?? true
    }

    static func keepsHiding(stoppingSince: Date?, now: Date) -> Bool {
        stoppingSince.map { now.timeIntervalSince($0) < hidingLimit } ?? true
    }
}

final class ClaudeDesktopUpdater: ObservableObject {
    @Published private(set) var installedVersion: String?
    @Published private(set) var availableVersion: String?
    @Published private(set) var checking = false
    @Published private(set) var isUpdating = false
    @Published private(set) var status: String?
    @Published private(set) var problem: String?

    private struct Run: Codable {
        var flow: ClaudeUpdateFlow
        var logOffset: UInt64
    }
    private let queue = DispatchQueue(label: "graft.claude-update", qos: .utility)
    private let session: URLSession
    private let version: () -> String?
    private var timer: Timer?
    private var checkTimer: Timer?
    private var observers: [NSObjectProtocol] = []
    private var concealed: NSRunningApplication?
    private var concealment: [NSKeyValueObservation] = []
    private var lastCheck: Date?
    private var run: Run?
    private var lease: ClaudeUpdateGate.Lease?
    private var stopping: String?
    private var stoppingSince: Date?
    private var lastQuit: Date?
    /// Read on the main thread only, beside `concealed`.
    private var showingUpdater = false
    /// A journal found at start whose fate waits on a snapshot that could be
    /// read. Its lock is already held.
    private var pendingResume: Run?
    private var confirming = false

    static var directory: URL { Graft.applicationSupport.appending(path: "ClaudeGraft/DesktopUpdate.noindex") }
    static var profile: URL { directory.appending(path: "Profile") }
    private static var journal: URL { directory.appending(path: "run.json") }
    private static var log: URL { Graft.fm.homeDirectoryForCurrentUser.appending(path: "Library/Logs/Claude/main.log") }

    init(session: URLSession? = nil, version: @escaping () -> String? = { ClaudeUpdateFeed.installedVersion() }) {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.httpShouldSetCookies = false
        configuration.timeoutIntervalForResource = 30
        self.session = session ?? URLSession(configuration: configuration)
        self.version = version
    }

    func start() {
        guard timer == nil else { return }
        queue.async {
            self.resume()
            DispatchQueue.main.async { self.check(force: false) }
        }
        let timer = Timer(timeInterval: 2, repeats: true) { [weak self] _ in
            guard let self else { return }
            self.queue.async { self.poll() }
        }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
        // Polling found the updater four seconds after it started, and its
        // window came back on its own five seconds later. A Claude that starts
        // during an update is checked the moment it is announced.
        let workspace = NSWorkspace.shared.notificationCenter
        for name in [NSWorkspace.didLaunchApplicationNotification, NSWorkspace.didActivateApplicationNotification] {
            observers.append(workspace.addObserver(forName: name, object: nil, queue: .main) { [weak self] note in
                guard let self, self.isUpdating,
                      let app = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication,
                      app.bundleIdentifier == "com.anthropic.claudefordesktop" else { return }
                if app == self.concealed { Self.keepHidden(app); return }
                let pid = app.processIdentifier
                self.queue.async {
                    if Graft.processes().contains(where: { $0.id == pid && Graft.carriesDataDir($0.command, Self.profile) }) {
                        DispatchQueue.main.async { self.conceal(app) }
                    }
                    self.poll()
                }
            })
        }
        let checkTimer = Timer(timeInterval: 60, repeats: true) { [weak self] _ in self?.check(force: false) }
        RunLoop.main.add(checkTimer, forMode: .common)
        self.checkTimer = checkTimer
    }

    func check(force: Bool = true) {
        guard !checking, !isUpdating,
              force || lastCheck.map({ Date().timeIntervalSince($0) >= 3600 }) ?? true else { return }
        checking = true
        lastCheck = Date()
        queue.async {
            self.fetch { result in
                DispatchQueue.main.async {
                    switch result {
                    case .success(let release):
                        self.installedVersion = release.installed
                        self.availableVersion = release.available
                        self.problem = nil
                    case .failure(let error): self.problem = error.localizedDescription
                    }
                    self.checking = false
                }
            }
        }
    }

    private func fetch(_ completion: @escaping (Result<(installed: String, available: String?), Error>) -> Void) {
        do {
            guard let installed = version() else {
                throw NSError(domain: "ClaudeUpdate", code: 1, userInfo: [NSLocalizedDescriptionKey: L10n.text("Install Claude Desktop to check for updates.")])
            }
            let id = try Self.deviceID()
            var request = URLRequest(url: ClaudeUpdateFeed.url(version: installed, deviceID: id))
            request.cachePolicy = .reloadIgnoringLocalCacheData
            request.timeoutInterval = 25
            session.dataTask(with: request) { data, response, error in
                do {
                    guard error == nil, let response = response as? HTTPURLResponse,
                          response.statusCode == 200 || response.statusCode == 204 else {
                        throw ClaudeUpdateFeed.Failure.unavailable
                    }
                    let available = response.statusCode == 204 ? nil
                        : try ClaudeUpdateFeed.available(in: data ?? Data(), installed: installed)
                    completion(.success((installed, available)))
                } catch { completion(.failure(error)) }
            }.resume()
        } catch { completion(.failure(error)) }
    }

    private func begin(_ target: String, approved: Set<ClaudeUpdateFlow.Instance>) {
        do {
            lease = try ClaudeUpdateGate.acquire()
            try ManualUpdates.checkManagedPolicy()
            let sample = try Self.snapshot()
            guard sample.instances.isSubset(of: approved) else {
                throw NSError(domain: "ClaudeUpdate", code: 5, userInfo: [NSLocalizedDescriptionKey: L10n.text("Another Claude opened after you confirmed. Review the running instances and try again.")])
            }
            guard sample.helper == nil else {
                throw NSError(domain: "ClaudeUpdate", code: 2, userInfo: [NSLocalizedDescriptionKey: L10n.text("A previous Claude update is still running. Close its Claude instance and try again.")])
            }
            run = Run(flow: ClaudeUpdateFlow(target: target, original: sample.instances, phaseStarted: Date()),
                      logOffset: Self.logSize())
            try save()
            publish()
            DispatchQueue.main.async { Shared.updateRecovery.forgetObservations() }
            for instance in sample.instances { Self.quit(instance) }
            Diagnostics.note("claude-update.requested", ["version": target, "instances": sample.instances.count])
            poll()
        } catch { finish(error.localizedDescription) }
    }

    private func installAfterChecking(_ result: Result<(installed: String, available: String?), Error>, approved: Set<ClaudeUpdateFlow.Instance>) {
        switch result {
        case .success(let release):
            guard let target = release.available else { finish(nil); return }
            begin(target, approved: approved)
        case .failure(let error): finish(error.localizedDescription)
        }
    }

    /// A timer can advertise a release, but only this decision can end work.
    func confirmInstall() {
        guard !confirming, !isUpdating, !checking, availableVersion != nil else { return }
        confirming = true
        queue.async {
            do {
                let approved = try Self.snapshot().instances
                DispatchQueue.main.async {
                    defer { self.confirming = false }
                    if ClaudeUpdateConfirmation.show(runningCount: approved.count) {
                        self.install(approved: approved)
                    }
                }
            } catch {
                DispatchQueue.main.async { self.confirming = false; self.problem = error.localizedDescription }
            }
        }
    }

    func install(approved: Set<ClaudeUpdateFlow.Instance> = []) {
        guard !isUpdating, !checking, availableVersion != nil else { return }
        isUpdating = true
        problem = nil
        status = L10n.text("Checking the latest Claude release before closing anything…")
        queue.async {
            self.fetch { result in self.queue.async { self.installAfterChecking(result, approved: approved) } }
        }
    }

    private static func deviceID() throws -> String {
        guard !Graft.isSymlink(directory), !Graft.isSymlink(profile) else { throw ManualUpdates.Failure.invalidProfile }
        try Graft.fm.createDirectory(at: profile, withIntermediateDirectories: true)
        let file = profile.appending(path: "ant-did")
        if Graft.exists(file) {
            guard !Graft.isSymlink(file), let encoded = try? Data(contentsOf: file),
                  let data = Data(base64Encoded: encoded), let id = String(data: data, encoding: .utf8),
                  UUID(uuidString: id) != nil else { throw ManualUpdates.Failure.unreadable(L10n.text("the update device identifier")) }
            return id
        }
        let id = UUID().uuidString.lowercased()
        try UpdaterProfile.writeDeviceID(id, into: profile)
        return id
    }

    private func save() throws {
        if let run { try JSONEncoder().encode(run).write(to: Self.journal, options: .atomic) }
    }

    /// The lock comes first and a snapshot that cannot be read decides nothing.
    /// A snapshot fails while any Claude has no launch date, which is exactly
    /// the updater starting or a Claude closing, and the old order sent that
    /// failure to `finish`: the journal deleted, the lock let go, shortcuts
    /// allowed to open, and Claude's installer left to swap the app under one
    /// of them once the updater nobody was watching any more quit.
    private func resume() {
        guard Graft.exists(Self.journal) else { return }
        do {
            let saved = try JSONDecoder().decode(Run.self, from: Data(contentsOf: Self.journal))
            lease = try ClaudeUpdateGate.acquire()
            pendingResume = saved
            publish(saved.flow.phase)
            settleResume()
        } catch { finish(error.localizedDescription) }
    }

    private func settleResume() {
        guard let saved = pendingResume else { return }
        let sample: ClaudeUpdateFlow.Sample
        do { sample = try Self.snapshot() } catch {
            DispatchQueue.main.async { self.problem = error.localizedDescription }
            return
        }
        pendingResume = nil
        run = saved
        switch ClaudeUpdateResume.decide(sample, target: saved.flow.target) {
        case .follow(let phase):
            run?.flow.phase = phase
            run?.flow.phaseStarted = Date()
            DispatchQueue.main.async { self.problem = nil }
            publish()
            poll()
        case .finished:
            finish(nil)
        case .abandoned:
            finish(L10n.text("The previous Claude update did not finish. Check for updates and try again."))
        }
    }

    private func poll() {
        if pendingResume != nil { settleResume(); return }
        guard var current = run else { return }
        do {
            let sample = try Self.snapshot()
            if let stopping {
                if sample.helper == nil && !sample.installerRunning { finish(stopping); return }
                let now = Date()
                if let helper = sample.helper, ClaudeUpdateStop.resendsQuit(lastQuit: lastQuit, now: now) {
                    Self.quit(helper)
                    lastQuit = now
                }
                if !ClaudeUpdateStop.keepsHiding(stoppingSince: stoppingSince, now: now) {
                    DispatchQueue.main.async {
                        guard !self.showingUpdater else { return }
                        // Hidden any longer, the one window that could be
                        // closed by hand is the one nobody can reach.
                        self.showingUpdater = true
                        self.reveal()
                        Diagnostics.note("claude-update.updater-shown", ["reason": "still running a minute after the quit"])
                    }
                }
                return
            }
            // `open -g` launches it behind, but Claude shows its window anyway,
            // and a signed-out window reads as a request to sign in.
            if let helper = sample.helper {
                DispatchQueue.main.async {
                    guard let app = NSRunningApplication(processIdentifier: helper.pid),
                          app.bundleIdentifier == "com.anthropic.claudefordesktop",
                          app.launchDate == helper.launched else { return }
                    self.conceal(app)
                }
            }
            let previousPhase = current.flow.phase
            let action = current.flow.advance(sample, at: Date())
            run = current
            if current.flow.phase != previousPhase { try save(); publish() }
            switch action {
            case .launch:
                // A dedicated, signed-out instance uses Claude's own signed
                // updater. Working profiles keep disableAutoUpdates throughout.
                if UpdaterProfile.isSignedIn(Self.profile) {
                    do {
                        try UpdaterProfile.reset(Self.profile, keeping: Self.deviceID())
                        Diagnostics.note("claude-update.profile-reset", [:])
                    } catch {
                        // Hidden, a signed-in updater still updates; refusing
                        // here would leave Claude out of date over a cleanup.
                        Diagnostics.note("claude-update.profile-reset", ["error": error.localizedDescription])
                    }
                }
                let arguments = ["-n", "-g", "-j", "-a", Graft.claudeApp.path,
                                 "--args", "--user-data-dir=\(Self.profile.path)"]
                guard Graft.runTool("/usr/bin/open", arguments) == 0 else {
                    stop(L10n.text("Claude's updater could not be opened."), sample: sample)
                    return
                }
            case .quitHelper:
                if let helper = sample.helper { Self.quit(helper) }
            case .complete:
                finish(nil)
            case .fail(let message):
                stop(message, sample: sample)
            case .wait:
                if current.flow.phase == .downloading, let failure = readProgress() {
                    stop(failure, sample: sample)
                }
            }
        } catch {
            // A failed process read cannot prove that the updater is gone.
            DispatchQueue.main.async { self.problem = error.localizedDescription }
        }
    }

    private func stop(_ message: String, sample: ClaudeUpdateFlow.Sample) {
        stopping = message
        stoppingSince = Date()
        lastQuit = Date()
        if let helper = sample.helper { Self.quit(helper) }
        if sample.helper == nil && !sample.installerRunning { finish(message) }
        else {
            DispatchQueue.main.async {
                self.problem = message
                self.status = L10n.text("Waiting for Claude's updater to close…")
            }
        }
    }

    private func finish(_ failure: String?) {
        let target = run?.flow.target
        if run != nil { try? Graft.fm.removeItem(at: Self.journal) }
        run = nil
        pendingResume = nil
        stopping = nil
        stoppingSince = nil
        lastQuit = nil
        lease = nil
        let installed = version()
        DispatchQueue.main.async {
            self.reveal()
            self.showingUpdater = false
            self.installedVersion = installed
            self.isUpdating = false
            self.problem = failure
            if failure == nil {
                self.availableVersion = nil
                self.status = L10n.format("Claude Desktop %@ is installed. Open your shortcuts when ready.", installed ?? target ?? "")
            } else { self.status = nil }
        }
        Diagnostics.note("claude-update.finished", ["version": installed ?? "", "error": failure ?? ""])
    }

    private func publish(_ shown: ClaudeUpdateFlow.Phase? = nil) {
        guard let phase = shown ?? run?.flow.phase else { return }
        let message: String
        switch phase {
        case .quitting: message = L10n.text("Closing all Claude instances…")
        case .starting: message = L10n.text("Starting Claude's updater…")
        case .downloading: message = L10n.text("Claude is checking and downloading its update…")
        case .installing: message = L10n.text("Installing the Claude update…")
        }
        DispatchQueue.main.async { self.isUpdating = true; self.status = message }
    }

    private static func snapshot() throws -> ClaudeUpdateFlow.Sample {
        let processes = Graft.processes()
        guard processes.contains(where: { $0.id == getpid() }) else {
            throw NSError(domain: "ClaudeUpdate", code: 3, userInfo: [NSLocalizedDescriptionKey: L10n.text("Could not read running Claude instances. The update will wait.")])
        }
        var instances = Set<ClaudeUpdateFlow.Instance>()
        var helper: ClaudeUpdateFlow.Instance?
        for process in processes where Graft.isClaudeProcess(process.command) {
            let app = NSRunningApplication(processIdentifier: process.id)
            if app?.bundleIdentifier != "com.anthropic.claudefordesktop",
               !process.command.hasPrefix(Graft.claudeApp.appending(path: "Contents/MacOS/Claude").path) { continue }
            guard app?.bundleIdentifier == "com.anthropic.claudefordesktop", let launched = app?.launchDate else {
                throw NSError(domain: "ClaudeUpdate", code: 4, userInfo: [NSLocalizedDescriptionKey: L10n.text("Claude is still opening or closing. The update will wait.")])
            }
            let instance = ClaudeUpdateFlow.Instance(pid: process.id, launched: launched)
            instances.insert(instance)
            if Graft.carriesDataDir(process.command, profile) { helper = instance }
        }
        return ClaudeUpdateFlow.Sample(instances: instances, helper: helper,
                                       installed: ClaudeUpdateFeed.installedVersion(), staged: stagedVersion(),
                                       installerRunning: processes.contains { Graft.isClaudeInstallerProcess($0.command) })
    }

    private static func stagedVersion() -> String? {
        let cache = Graft.fm.homeDirectoryForCurrentUser.appending(path: "Library/Caches/com.anthropic.claudefordesktop.ShipIt")
        guard let data = try? Data(contentsOf: cache.appending(path: "ShipItState.plist")),
              let object = (try? JSONSerialization.jsonObject(with: data) as? [String: Any])
                ?? (try? PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any]),
              object["bundleIdentifier"] as? String == "com.anthropic.claudefordesktop",
              let target = object["targetBundleURL"] as? String, let targetURL = URL(string: target), targetURL.isFileURL,
              Graft.samePath(targetURL, Graft.claudeApp),
              let staged = object["updateBundleURL"] as? String, let url = URL(string: staged), url.isFileURL,
              url.resolvingSymlinksInPath().path.hasPrefix(cache.resolvingSymlinksInPath().path + "/") else { return nil }
        return ClaudeUpdateFeed.installedVersion(at: url)
    }

    private static func quit(_ instance: ClaudeUpdateFlow.Instance) {
        DispatchQueue.main.async {
            guard let app = NSRunningApplication(processIdentifier: instance.pid),
                  app.bundleIdentifier == "com.anthropic.claudefordesktop", app.launchDate == instance.launched else { return }
            _ = app.terminate()
        }
    }

    /// Claude unhides itself whenever it shows a window, so hiding it once
    /// lasts only until then. Watching the flag puts it back as it changes,
    /// and watching the launch catches a hide that came before Claude could
    /// take it.
    private func conceal(_ app: NSRunningApplication) {
        guard !showingUpdater else { return }
        if app == concealed { Self.keepHidden(app); return }
        concealed = app
        concealment = [
            app.observe(\.isHidden, options: [.initial, .new]) { app, _ in Self.keepHidden(app) },
            app.observe(\.isFinishedLaunching, options: [.initial, .new]) { app, _ in Self.keepHidden(app) },
            app.observe(\.isActive, options: [.new]) { app, _ in Self.keepHidden(app) },
        ]
    }

    private func reveal() {
        concealment = []
        concealed = nil
    }

    private static func keepHidden(_ app: NSRunningApplication) {
        let hide = {
            guard !app.isTerminated, app.isFinishedLaunching, !app.isHidden else { return }
            // Logged because a hide nobody sees is otherwise unprovable: the
            // count and timing are how a run tells a flash from a clean pass.
            // `pid` is already the logging process's own.
            Diagnostics.note("claude-update.hid-updater", ["updater": Int(app.processIdentifier), "accepted": app.hide()])
        }
        if Thread.isMainThread { hide() } else { DispatchQueue.main.async(execute: hide) }
    }

    private static func logSize() -> UInt64 {
        (try? Graft.fm.attributesOfItem(atPath: log.path)[.size] as? NSNumber)?.uint64Value ?? 0
    }

    private func readProgress() -> String? {
        guard let offset = run?.logOffset, let handle = try? FileHandle(forReadingFrom: Self.log) else { return nil }
        defer { try? handle.close() }
        let end = Self.logSize()
        let start = min(offset, end)
        try? handle.seek(toOffset: max(start, end > 262144 ? end - 262144 : 0))
        guard let data = try? handle.readToEnd() else { return nil }
        run?.logOffset = end
        let text = String(decoding: data, as: UTF8.self)
        if text.contains("[updater] Auto-updates disabled by enterprise policy") {
            return L10n.text("Claude's policy prevents this update. Ask its administrator to allow updates.")
        }
        if text.contains("[updater] Auto-update error:") {
            return L10n.text("Claude reported an update error. Check your connection and try again.")
        }
        if text.contains("[updater] Found an update, downloading") {
            DispatchQueue.main.async { self.status = L10n.text("Downloading the Claude update…") }
        }
        return nil
    }
}
