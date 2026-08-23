import Foundation

/// Polls each profile's plan usage.
///
/// Two sources, in order of preference. The live one is Anthropic's usage
/// endpoint, reached with the token that profile already holds: exact reset
/// times, and current figures even when that Claude is not running. The
/// fallback is `plan-usage-history.json`, which a profile writes while it runs
/// — good enough to show something before the keychain prompt is answered, or
/// if it is declined.
final class UsageMonitor: ObservableObject {
    struct Entry: Identifiable, Equatable {
        var id: String { profile.path }
        var name: String
        var profile: URL
        var usage: Graft.Usage?
        var isRunning: Bool
        /// Nil for the main profile, which has no shortcut behind it.
        var shortcut: UUID?
        /// True when the figures came from the API rather than from disk.
        var isLive = false
        var plan: String?
    }

    @Published private(set) var entries: [Entry] = []
    /// Set when live usage is unavailable for a reason worth showing once.
    @Published private(set) var liveProblem: String?
    /// True once a background pass has found the keychain shut to it, so the
    /// dropdown can point at Refresh Usage rather than silently showing the
    /// weaker figures from disk.
    @Published private(set) var needsKeychainAccess = false

    private var timer: Timer?
    private let queue = DispatchQueue(label: "graft.usage", qos: .utility)
    private var inFlight = false

    /// The API is polled far less often than the disk, and only per profile.
    private var liveCache: [String: (fetched: Date, reading: UsageAPI.Reading)] = [:]
    private static let liveInterval: TimeInterval = 5 * 60

    /// The number worth putting in the menu bar: the closest five-hour window
    /// to its limit among everything still meaningful.
    var headline: Int? {
        entries
            .compactMap { entry -> Int? in
                guard let usage = entry.usage else { return nil }
                guard entry.isLive || !usage.isStale else { return nil }
                return usage.fiveHour
            }
            .max()
    }

    /// Idempotent: the window and the menu bar item both ask for it, and only
    /// one of the two may ever appear.
    func start(watching store: ShortcutStore, every seconds: TimeInterval = 30) {
        guard timer == nil else { refresh(store); return }
        refresh(store)
        let timer = Timer(timeInterval: seconds, repeats: true) { [weak self, weak store] _ in
            guard let store else { return }
            self?.refresh(store)
        }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    func stop() {
        timer?.invalidate()
        timer = nil
    }

    /// Reads the store on the calling thread — it must be the main one — then
    /// does the filesystem, keychain and network work away from it.
    ///
    /// `interactive` allows the one keychain prompt macOS shows before a
    /// profile's token can be read; background polls never ask.
    func refresh(_ store: ShortcutStore, interactive: Bool = false) {
        var targets: [(name: String, profile: URL, shortcut: UUID?)] = [
            (name: "Claude", profile: Graft.mainProfile, shortcut: nil)
        ]
        for shortcut in store.shortcuts where shortcut.installedName != nil {
            targets.append((name: shortcut.name, profile: shortcut.profileDir, shortcut: shortcut.id))
        }

        guard !inFlight else { return }
        inFlight = true
        queue.async { [weak self] in
            guard let self else { return }
            var problem: String?
            var locked = false
            let fresh = targets.map { target -> Entry in
                let onDisk = Graft.usage(of: target.profile)
                var entry = Entry(name: target.name,
                                  profile: target.profile,
                                  usage: onDisk,
                                  isRunning: Graft.isRunning(profile: target.profile),
                                  shortcut: target.shortcut)
                if let live = self.live(for: target.profile,
                                        interactive: interactive,
                                        problem: &problem,
                                        locked: &locked) {
                    entry.usage = Graft.Usage(fiveHour: live.fiveHour,
                                              week: live.week,
                                              organization: onDisk?.organization,
                                              sampled: Date(),
                                              fiveHourReset: live.fiveHourReset,
                                              weekReset: live.weekReset)
                    entry.isLive = true
                    entry.plan = live.plan
                }
                return entry
            }
            self.record(fresh, locked: locked, problem: problem)
            DispatchQueue.main.async {
                self.inFlight = false
                if self.entries != fresh { self.entries = fresh }
                if self.liveProblem != problem { self.liveProblem = problem }
                if self.needsKeychainAccess != locked { self.needsKeychainAccess = locked }
            }
        }
    }

    /// A plain record of what the last pass found, so the state of live usage
    /// can be checked without reading it off the screen. Figures only — no
    /// token, and nothing that is not already on disk elsewhere.
    static var statusFile: URL {
        Graft.applicationSupport
            .appending(path: "ClaudeGraft")
            .appending(path: "usage-status.json")
    }

    private func record(_ entries: [Entry], locked: Bool, problem: String?) {
        let stamp = ISO8601DateFormatter()
        var report: [String: Any] = [
            "checkedAt": stamp.string(from: Date()),
            "keychainLocked": locked,
        ]
        if let problem { report["problem"] = problem }
        report["profiles"] = entries.map { entry -> [String: Any] in
            var row: [String: Any] = ["name": entry.name, "live": entry.isLive]
            if let usage = entry.usage {
                row["fiveHour"] = usage.fiveHour
                row["week"] = usage.week
                if let reset = usage.fiveHourReset { row["fiveHourReset"] = stamp.string(from: reset) }
                if let reset = usage.weekReset { row["weekReset"] = stamp.string(from: reset) }
            }
            if let plan = entry.plan { row["plan"] = plan }
            return row
        }
        guard let data = try? JSONSerialization.data(withJSONObject: report,
                                                     options: [.prettyPrinted, .sortedKeys])
        else { return }
        try? FileManager.default.createDirectory(at: Self.statusFile.deletingLastPathComponent(),
                                                 withIntermediateDirectories: true)
        try? data.write(to: Self.statusFile)
    }

    /// Cached hard: one call per profile per interval, and never a prompt on a
    /// background pass.
    private func live(for profile: URL,
                      interactive: Bool,
                      problem: inout String?,
                      locked: inout Bool) -> UsageAPI.Reading? {
        if let cached = liveCache[profile.path],
           Date().timeIntervalSince(cached.fetched) < Self.liveInterval {
            return cached.reading
        }
        do {
            guard let token = try ClaudeCredentials.token(for: profile, allowInteraction: interactive)
            else { return nil }
            let reading = try UsageAPI.fetch(token: token.value)
            liveCache[profile.path] = (Date(), reading)
            return reading
        } catch {
            // Falling back to the on-disk figures is the normal outcome here,
            // so only an explicit request reports why. The keychain being shut
            // is worth surfacing quietly either way, since it is one click to
            // fix and everything live depends on it.
            if case ClaudeCredentials.Failure.noKeychainAccess = error { locked = true }
            if interactive { problem = (error as? LocalizedError)?.errorDescription }
            return liveCache[profile.path]?.reading
        }
    }
}
