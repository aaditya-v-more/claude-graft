import AppKit
import Foundation

/// ShipIt reopens the bundle without its profile argument. The marker belongs
/// to Claude: reading it must leave its navigation and window state intact for
/// the profile's eventual startup.
struct UpdateRelaunchMarker: Decodable, Equatable {
    let ts: Double
    var windowVisible: Bool?
    var navOnly: Bool?

    static let lifetime: TimeInterval = 5 * 60

    var date: Date { Date(timeIntervalSince1970: ts / 1000) }

    func isFresh(at now: Date) -> Bool {
        let age = now.timeIntervalSince(date)
        return ts.isFinite && ts > 0 && age >= 0 && age < Self.lifetime
            && windowVisible != nil && navOnly != true
    }

    static func read(from profile: URL) -> Self? {
        guard let data = try? Data(contentsOf: profile.appending(path: "stealth-relaunch"))
        else { return nil }
        return try? JSONDecoder().decode(Self.self, from: data)
    }
}

/// Driven by complete process snapshots, so a failed ps cannot turn every
/// running profile into one that just quit. Kept separate from the timer so
/// tests can cover the gaps between quitting, installation and relaunch.
struct UpdateRecovery {
    struct Sample {
        var name: String
        var profile: URL
        var pids: Set<pid_t>
        var marker: UpdateRelaunchMarker?
        var openNeighbours: [String: String] = [:]
        var defaultPIDs: Set<pid_t> = []
    }

    struct Request: Identifiable {
        var id: String { profile.path }
        var name: String
        var profile: URL
        var marker: UpdateRelaunchMarker
        var previousNeighbours: Set<String>
        var previousDefaultPIDs: Set<pid_t>
    }

    enum Event {
        case ready(Request)
        case restored(Request)
        case failed(Request)
        case cancelled(String)
    }

    private struct Observation {
        var firstSeen: Date
        var pids: Set<pid_t>
        var initialMarker: Double?
        var neighbours: Set<String>
        var defaultPIDs: Set<pid_t>
        var stoppedAt: Date?
        var request: Request?
        var startedAt: Date?
    }

    // Claude's argv-preserving restart gets the first chance. Installation
    // itself is also checked: a bundle present on disk may still be the old one.
    static let grace: TimeInterval = 15
    static let launchTimeout: TimeInterval = 45
    private var observations: [String: Observation] = [:]
    private var handled: [String: Double] = [:]

    mutating func observe(_ samples: [Sample], at now: Date,
                          installerRunning: Bool = false) -> [Event] {
        var events: [Event] = []
        let paths = Set(samples.map { $0.profile.path })
        for path in observations.keys where !paths.contains(path) {
            observations[path] = nil
            events.append(.cancelled(path))
        }
        handled = handled.filter { paths.contains($0.key) }

        for sample in samples {
            let path = sample.profile.path
            var observed = observations[path]
            if !sample.pids.isEmpty {
                if let request = observed?.request {
                    events.append(observed?.startedAt == nil ? .cancelled(path) : .restored(request))
                }
                if observed == nil || observed?.pids != sample.pids || observed?.stoppedAt != nil {
                    observed = Observation(firstSeen: now, pids: sample.pids,
                                           initialMarker: sample.marker?.ts,
                                           neighbours: Set(sample.openNeighbours.keys),
                                           defaultPIDs: sample.defaultPIDs)
                } else if sample.marker?.isFresh(at: now) != true {
                    observed?.neighbours = Set(sample.openNeighbours.keys)
                    observed?.defaultPIDs = sample.defaultPIDs
                }
                observations[path] = observed
                continue
            }
            guard var observed else { continue }
            if observed.stoppedAt == nil { observed.stoppedAt = now }

            if let started = observed.startedAt, let request = observed.request {
                if now.timeIntervalSince(started) >= Self.launchTimeout {
                    observations[path] = nil
                    events.append(.failed(request))
                } else {
                    observations[path] = observed
                }
                continue
            }

            guard let marker = sample.marker, marker.isFresh(at: now),
                  marker.ts != observed.initialMarker,
                  marker.date >= observed.firstSeen,
                  marker.date <= observed.stoppedAt!,
                  observed.request != nil || marker.ts != handled[path]
            else {
                if observed.request != nil { events.append(.cancelled(path)) }
                // A marker written just before exit can be caught mid-write.
                // This short window never admits one written after the quit.
                observations[path] = now.timeIntervalSince(observed.stoppedAt!) < Self.grace
                    ? observed : nil
                continue
            }

            if observed.request == nil,
               now.timeIntervalSince(observed.stoppedAt!) >= Self.grace,
               !installerRunning {
                let request = Request(name: sample.name, profile: sample.profile, marker: marker,
                                      previousNeighbours: observed.neighbours,
                                      previousDefaultPIDs: observed.defaultPIDs)
                observed.request = request
                handled[path] = marker.ts
                events.append(.ready(request))
            }
            observations[path] = observed
        }
        return events
    }

    mutating func started(_ request: Request, at now: Date) -> Bool {
        guard observations[request.id]?.request?.marker.ts == request.marker.ts,
              observations[request.id]?.startedAt == nil else { return false }
        observations[request.id]?.startedAt = now
        return true
    }

    mutating func dismiss(_ request: Request) {
        guard observations[request.id]?.request?.marker.ts == request.marker.ts else { return }
        observations[request.id] = nil
    }

    static func newSharers(for request: Request, in sample: Sample) -> [String] {
        sample.openNeighbours.filter { !request.previousNeighbours.contains($0.key) }
            .map(\.value).sorted()
    }
}

final class UpdateRecoveryMonitor: ObservableObject {
    struct ExtraInstance {
        var pid: pid_t
        var launched: Date
    }

    struct Notice: Identifiable {
        var id: String { request.id }
        var request: UpdateRecovery.Request
        var message: String
        var needsDecision = false
        var extras: [ExtraInstance] = []
    }

    @Published private(set) var notices: [Notice] = []
    private let queue = DispatchQueue(label: "graft.update-recovery", qos: .utility)
    private var recovery = UpdateRecovery()
    private var timer: Timer?
    private var inFlight = false
    private weak var store: ShortcutStore?
    private let lifecycle = NSLock()
    private var active = false

    private var isActive: Bool { lifecycle.withLock { active } }

    private struct Target {
        var name: String
        var profile: URL
        var neighbours: [(name: String, profile: URL)]
    }

    private func targets() -> [Target] {
        guard let store else { return [] }
        return store.shortcuts.filter {
            $0.installedName != nil && Graft.validateFolder($0.folder) == nil
        }.map {
            Target(name: $0.name, profile: $0.profileDir,
                   neighbours: store.chatStoreNeighbours(of: $0))
        }
    }

    func start(watching store: ShortcutStore, every seconds: TimeInterval = 5) {
        guard timer == nil else { return }
        self.store = store
        lifecycle.withLock { active = true }
        refresh()
        let timer = Timer(timeInterval: seconds, repeats: true) { [weak self] _ in self?.refresh() }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    func stop() {
        lifecycle.withLock { active = false }
        timer?.invalidate()
        timer = nil
        store = nil
    }

    private static func snapshot(_ targets: [Target]) -> (samples: [UpdateRecovery.Sample], installing: Bool)? {
        let processes = Graft.processes()
        guard processes.contains(where: { $0.id == getpid() }) else { return nil }
        let claudes = processes.filter { Graft.isClaudeProcess($0.command) }
        let defaults = Set(claudes.filter { Graft.isDefaultInstance($0.command) }.map(\.id))
        func pids(_ profile: URL) -> Set<pid_t> {
            if Graft.samePath(profile, Graft.mainProfile) { return defaults }
            return Set(claudes.filter { Graft.carriesDataDir($0.command, profile) }.map(\.id))
        }
        let samples = targets.filter { Graft.isDirectory($0.profile) }.map { target in
            UpdateRecovery.Sample(name: target.name, profile: target.profile,
                                  pids: pids(target.profile),
                                  marker: UpdateRelaunchMarker.read(from: target.profile),
                                  openNeighbours: Dictionary(target.neighbours.filter {
                                      !pids($0.profile).isEmpty
                                  }.map { ($0.profile.path, $0.name) }, uniquingKeysWith: { first, _ in first }),
                                  defaultPIDs: defaults)
        }
        let installing = processes.contains { Graft.isClaudeInstallerProcess($0.command) }
            || !Graft.fm.isExecutableFile(atPath: Graft.claudeApp.appending(path: "Contents/MacOS/Claude").path)
        return (samples, installing)
    }

    func refresh() {
        guard !inFlight, store != nil else { return }
        inFlight = true
        let targets = targets()
        queue.async { [weak self] in
            guard let self else { return }
            do { try ClaudeUpdateGate.requireLaunchAllowed() }
            catch {
                self.recovery = UpdateRecovery()
                DispatchQueue.main.async { self.inFlight = false }
                return
            }
            if let snapshot = Self.snapshot(targets) {
                for event in self.recovery.observe(snapshot.samples, at: Date(),
                                                   installerRunning: snapshot.installing) {
                    switch event {
                    case .ready(let request):
                        self.offerOrLaunch(request, targets: targets, approved: false)
                    case .restored(let request):
                        self.publish(request, message: L10n.format("Claude restarted itself to update. %@ was reopened.", request.name),
                                     samples: snapshot.samples)
                        Diagnostics.note("update-recovery.restored", ["profile": request.profile.lastPathComponent])
                    case .failed(let request):
                        self.publish(request, message: L10n.format("Claude restarted itself to update, but %@ could not be reopened. Open its shortcut to try again.", request.name), samples: snapshot.samples)
                        Diagnostics.note("update-recovery.failed", ["profile": request.profile.lastPathComponent])
                    case .cancelled(let path):
                        DispatchQueue.main.async { self.notices.removeAll { $0.id == path && $0.needsDecision } }
                    }
                }
            }
            DispatchQueue.main.async { self.inFlight = false }
        }
    }

    func forgetObservations() {
        queue.async { self.recovery = UpdateRecovery() }
        notices.removeAll { $0.needsDecision }
    }

    private func offerOrLaunch(_ request: UpdateRecovery.Request, targets: [Target], approved: Bool) {
        guard isActive, let snapshot = Self.snapshot(targets),
              let sample = snapshot.samples.first(where: { $0.profile.path == request.id }),
              sample.pids.isEmpty,
              sample.marker == request.marker, request.marker.isFresh(at: Date())
        else { return }
        let sharers = UpdateRecovery.newSharers(for: request, in: sample)
        if !approved && !sharers.isEmpty {
            publish(request, message: L10n.format("Claude restarted itself to update and closed %@. %@",
                    request.name, ChatConflict.message(sharers: sharers)), needsDecision: true, samples: snapshot.samples)
            Diagnostics.note("update-recovery.conflict", ["profile": request.profile.lastPathComponent,
                                                          "sharers": sharers])
            return
        }
        guard !snapshot.installing else {
            publish(request, message: L10n.format("Claude is still installing its update. Reopen %@ when it finishes.", request.name),
                    needsDecision: true, samples: snapshot.samples)
            return
        }
        guard isActive, recovery.started(request, at: Date()) else { return }
        Diagnostics.note("update-recovery.launch", ["profile": request.profile.lastPathComponent,
                                                    "marker": request.marker.date])
        if Graft.open(profile: request.profile, inBackground: true) {
            publish(request, message: L10n.format("Claude restarted itself to update. Reopening %@…", request.name), samples: snapshot.samples)
        } else {
            recovery.dismiss(request)
            publish(request, message: L10n.format("Claude restarted itself to update, but %@ could not be reopened. Open its shortcut to try again.", request.name), samples: snapshot.samples)
            Diagnostics.note("update-recovery.failed", ["profile": request.profile.lastPathComponent])
        }
    }

    private func publish(_ request: UpdateRecovery.Request, message: String,
                         needsDecision: Bool = false, samples: [UpdateRecovery.Sample]) {
        let extraPIDs = (samples.first { $0.profile.path == request.id }?.defaultPIDs ?? [])
            .subtracting(request.previousDefaultPIDs)
        DispatchQueue.main.async {
            guard self.store != nil else { return }
            let extras = extraPIDs.compactMap { pid -> ExtraInstance? in
                guard let launched = NSRunningApplication(processIdentifier: pid)?.launchDate else { return nil }
                return ExtraInstance(pid: pid, launched: launched)
            }
            self.notices.removeAll { $0.id == request.id }
            self.notices.append(Notice(request: request, message: message,
                                       needsDecision: needsDecision, extras: extras))
        }
    }

    func reopen(_ notice: Notice) {
        let targets = targets()
        queue.async { self.offerOrLaunch(notice.request, targets: targets, approved: true) }
    }

    func dismiss(_ notice: Notice) {
        notices.removeAll { $0.id == notice.id }
        if notice.needsDecision { queue.async { self.recovery.dismiss(notice.request) } }
    }

    func closeExtras(_ notice: Notice) {
        let alert = NSAlert()
        alert.messageText = L10n.text("Close the extra Claude?")
        alert.informativeText = L10n.text("A default Claude instance opened during this restart. Closing it ends any sessions running in that instance.")
        alert.addButton(withTitle: L10n.text("Keep Open"))
        alert.addButton(withTitle: L10n.text("Close Extra Claude"))
        NSApp.activate(ignoringOtherApps: true)
        guard alert.runModal() == .alertSecondButtonReturn else { return }
        queue.async {
            let defaults = Set(Graft.processes().filter { Graft.isDefaultInstance($0.command) }.map(\.id))
            DispatchQueue.main.async {
                for extra in notice.extras where defaults.contains(extra.pid) {
                    // A delayed click must never quit a process that reused the pid.
                    guard let app = NSRunningApplication(processIdentifier: extra.pid),
                          app.launchDate == extra.launched else { continue }
                    _ = app.terminate()
                }
            }
        }
    }
}
