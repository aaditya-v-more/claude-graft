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
    private var lastCheck: Date?
    private var run: Run?
    private var lease: ClaudeUpdateGate.Lease?
    private var stopping: String?
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
        try Data(id.utf8).base64EncodedData().write(to: file, options: .atomic)
        try Graft.fm.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path)
        return id
    }

    private func save() throws {
        if let run { try JSONEncoder().encode(run).write(to: Self.journal, options: .atomic) }
    }

    private func resume() {
        guard Graft.exists(Self.journal) else { return }
        do {
            let saved = try JSONDecoder().decode(Run.self, from: Data(contentsOf: Self.journal))
            let sample = try Self.snapshot()
            lease = try ClaudeUpdateGate.acquire()
            run = saved
            // Restarting Graft is not another press of Quit All. Only an
            // updater already running may be followed through a relaunch.
            if sample.helper != nil || sample.installerRunning {
                run?.flow.phase = sample.helper != nil ? .downloading : .installing
                run?.flow.phaseStarted = Date()
                publish()
                poll()
            } else {
                let installed = sample.installed.map { $0 == saved.flow.target || ClaudeUpdateFeed.isNewer($0, than: saved.flow.target) } ?? false
                finish(installed ? nil : L10n.text("The previous Claude update did not finish. Check for updates and try again."))
            }
        } catch { finish(error.localizedDescription) }
    }

    private func poll() {
        guard var current = run else { return }
        do {
            let sample = try Self.snapshot()
            if let stopping {
                if sample.helper == nil && !sample.installerRunning { finish(stopping) }
                return
            }
            let previousPhase = current.flow.phase
            let action = current.flow.advance(sample, at: Date())
            run = current
            if current.flow.phase != previousPhase { try save(); publish() }
            switch action {
            case .launch:
                // A dedicated, signed-out instance uses Claude's own signed
                // updater. Working profiles keep disableAutoUpdates throughout.
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
        stopping = nil
        lease = nil
        let installed = version()
        DispatchQueue.main.async {
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

    private func publish() {
        guard let phase = run?.flow.phase else { return }
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
