import Foundation

/// Polls each profile's plan usage.
///
/// Two sources, in order of preference. The live one is Anthropic's usage
/// endpoint, reached with the token that profile already holds: exact reset
/// times, and current figures even when that Claude is not running. The
/// fallback is `plan-usage-history.json`, which a profile writes while it runs
/// — good enough to show something before the keychain prompt is answered, or
/// if it is declined.
///
/// The fallback is never taken in silence. When the keychain turns out to be
/// shut, one pass raises the dialog and the dropdown says so until it is
/// answered; see `mayPromptUnasked` for the one case that waits.
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

        /// A live figure was taken just now, so only one read off disk can be
        /// old enough to be worth warning about.
        var isDimmed: Bool {
            guard let usage else { return false }
            return !isLive && usage.isStale
        }
    }

    @Published private(set) var entries: [Entry] = []
    /// Set when live usage is unavailable for a reason worth showing once.
    @Published private(set) var liveProblem: String?
    /// True once a background pass has found the keychain shut to it, so the
    /// dropdown can point at Refresh Usage rather than silently showing the
    /// weaker figures from disk.
    @Published private(set) var needsKeychainAccess = false

    /// Whether a pass nobody asked for may raise the one keychain dialog.
    ///
    /// False only while the app is coming up into the menu bar with no window,
    /// which is what a login item does: a dialog thrown over whatever someone
    /// is doing at login is an ambush, and the answer to it is worthless. It
    /// turns on the moment the window or the dropdown is opened, which is when
    /// the figures are being looked at anyway.
    var mayPromptUnasked = true

    private var timer: Timer?
    /// Serial, which is what lets `invalidate` be ordered against the pass that
    /// follows it rather than racing the same dictionary from two threads.
    private let queue = DispatchQueue(label: "graft.usage", qos: .utility)
    private var inFlight = false

    /// A press that arrived mid-pass, run as soon as that pass is out of the
    /// way. Only ever one: two presses waiting on the same answer are one
    /// press.
    private struct Waiting {
        var prompting: ClaudeCredentials.Prompting
        var freshness: Freshness
        weak var store: ShortcutStore?
    }
    private var waiting: Waiting?

    /// True while a pass someone asked for is running, so a button can say so.
    /// Timer ticks leave it alone — a spinner every thirty seconds is noise.
    @Published private(set) var isRefreshing = false

    /// The API is polled far less often than the disk, and only per profile.
    private var liveCache: [String: (fetched: Date, reading: UsageAPI.Reading)] = [:]
    static let liveInterval: TimeInterval = 5 * 60

    /// How current the figures need to be for whoever is asking.
    enum Freshness {
        /// A tick nobody asked for. Five minutes old will do.
        case cached
        /// Someone is looking at the numbers right now.
        case recent
        /// Someone pressed something and is waiting for the answer.
        case now
    }

    /// The five-minute cache exists to keep a thirty-second timer off the
    /// endpoint, not to make a button do nothing. Pressing Refresh Usage used
    /// to return whatever was already in hand, so the figure could sit five
    /// minutes out of date with no way to hurry it — which reads as the app
    /// having stopped updating. Someone looking gets a shorter cache; the floor
    /// is still there, because a button can be pressed faster than any service
    /// wants to answer.
    static let recentInterval: TimeInterval = 60

    /// Opening the dropdown is someone looking; pressing Refresh Usage is
    /// someone asking. Those were the same thing, and the minute that made the
    /// first one cheap made the second one do nothing at all — press it and the
    /// figure it handed back could be fifty-nine seconds old, with the button
    /// giving no sign either way. The floor left here is only wide enough to
    /// swallow a double-click.
    static let nowInterval: TimeInterval = 2

    static func mayUseCache(age: TimeInterval, freshness: Freshness) -> Bool {
        switch freshness {
        case .cached: return age < liveInterval
        case .recent: return age < recentInterval
        case .now: return age < nowInterval
        }
    }

    /// What becomes of a request that arrives while a pass is already running.
    ///
    /// A timer tick is worth dropping: the pass in flight is about to answer
    /// the same question. A press is not. Opening the dropdown starts a pass of
    /// its own, so pressing Refresh Usage a moment later — the obvious thing to
    /// do when the figure looks stale — used to land squarely inside it and be
    /// discarded without a trace.
    enum Arrival { case start, queue, drop }

    static func arrival(passInFlight: Bool, interactive: Bool) -> Arrival {
        guard passInFlight else { return .start }
        return interactive ? .queue : .drop
    }

    /// A failed call must not simply be retried on the next thirty-second tick:
    /// that turns one refused request into a hundred and twenty an hour, which
    /// is exactly how a client earns a rate limit. Each failure pushes the next
    /// attempt further out, and a success clears it.
    private var backoff: [String: (until: Date, failures: Int)] = [:]

    static let backoffSteps: [TimeInterval] = [60, 120, 300, 900, 1800]

    /// How long to wait after `failures` consecutive failures. A `Retry-After`
    /// from the service wins over our own schedule — it knows better.
    static func retryDelay(afterFailures failures: Int, retryAfter: TimeInterval?) -> TimeInterval {
        if let retryAfter, retryAfter > 0 { return max(retryAfter, backoffSteps[0]) }
        let index = min(max(failures, 1) - 1, backoffSteps.count - 1)
        return backoffSteps[index]
    }

    /// `Retry-After` is the one wait a user's own refresh cannot shorten.
    static func mayAttempt(now: Date, until: Date?, interactive: Bool, serverAsked: Bool) -> Bool {
        guard let until else { return true }
        if now >= until { return true }
        return interactive && !serverAsked
    }

    /// The account the number in the menu bar is about.
    ///
    /// Whatever is open, since that is the window being spent. An account left
    /// sitting at its limit should not shout over the one actually in use.
    /// With nothing open there is no such answer, so it falls back to whichever
    /// is closest to its limit.
    var headlineEntry: Entry? {
        let usable = entries.filter { entry in
            guard let usage = entry.usage else { return false }
            return entry.isLive || !usage.isStale
        }
        let running = usable.filter(\.isRunning)
        let pool = running.isEmpty ? usable : running
        return pool.max { ($0.usage?.fiveHour ?? 0) < ($1.usage?.fiveHour ?? 0) }
    }

    var headline: Int? { headlineEntry?.usage?.fiveHour }

    /// What the last pass found for one profile, for a view that shows a single
    /// account rather than all of them.
    ///
    /// The window used to read `plan-usage-history.json` for itself while the
    /// menu bar showed what the endpoint had just said, so one account was
    /// reported from two sources that do not agree. The file is only written
    /// while that Claude runs, so a profile closed since Tuesday had Tuesday's
    /// figures in the window and today's in the bar — and Tuesday's were from
    /// before a weekly reset, which is how the window came to show 92% of a
    /// week that was actually 30% spent.
    func entry(for profile: URL) -> Entry? {
        entries.first { $0.profile.path == profile.path }
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

    /// Used by the tests to drive the menu bar figure without a filesystem.
    func setEntriesForTesting(_ replacement: [Entry]) { entries = replacement }

    /// Reads the store on the calling thread — it must be the main one — then
    /// does the filesystem, keychain and network work away from it.
    ///
    /// `interactive` says a person pressed something, which is what allows a
    /// pass to skip its own backoff. Whether the keychain dialog may be raised
    /// is a separate question with a separate answer, because opening the
    /// dropdown should be allowed to ask for access without also being allowed
    /// to hammer the endpoint on every open.
    func refresh(_ store: ShortcutStore,
                 interactive: Bool = false,
                 prompting: ClaudeCredentials.Prompting? = nil,
                 freshness: Freshness? = nil) {
        var targets: [(name: String, profile: URL, shortcut: UUID?)] = [
            (name: "Claude", profile: Graft.mainProfile, shortcut: nil)
        ]
        for shortcut in store.shortcuts where shortcut.installedName != nil {
            targets.append((name: shortcut.name, profile: shortcut.profileDir, shortcut: shortcut.id))
        }

        let asking = prompting ?? (interactive ? .yes : (mayPromptUnasked ? .onceIfShut : .no))
        // Opening the dropdown wants a current figure but must not skip the
        // backoff: a failing endpoint would then be asked again on every open.
        let wanted = freshness ?? (interactive ? .now : .cached)

        if interactive { isRefreshing = true }

        switch Self.arrival(passInFlight: inFlight, interactive: interactive) {
        case .drop:
            return
        case .queue:
            waiting = Waiting(prompting: asking, freshness: wanted, store: store)
            return
        case .start:
            break
        }
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
                                        freshness: wanted,
                                        prompting: asking,
                                        problem: &problem,
                                        locked: &locked) {
                    entry.usage = Graft.Usage(fiveHour: live.fiveHour,
                                              week: live.week,
                                              organization: onDisk?.organization,
                                              sampled: Date(),
                                              fiveHourReset: live.fiveHourReset,
                                              weekReset: live.weekReset,
                                              fable: live.fable,
                                              fableReset: live.fableReset)
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

                if let queued = self.waiting, let store = queued.store {
                    self.waiting = nil
                    self.refresh(store, interactive: true,
                                 prompting: queued.prompting, freshness: queued.freshness)
                } else {
                    self.waiting = nil
                    self.isRefreshing = false
                }
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
                if let fable = usage.fable { row["fable"] = fable }
                if let reset = usage.fableReset { row["fableReset"] = stamp.string(from: reset) }
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

    /// Drops the stored reading for one profile.
    ///
    /// For the caller that has just changed the number itself. Starting a
    /// session opens a five-hour window, so the figure taken before it is known
    /// to be wrong — and it was still inside every cache window there is, which
    /// is why pressing Start Session moved nothing on screen. Ordered ahead of
    /// the refresh that follows by the queue both go through.
    func invalidate(_ profile: URL) {
        queue.async { [weak self] in self?.liveCache[profile.path] = nil }
    }

    /// Cached hard: one call per profile per interval, and never a prompt on a
    /// background pass.
    private func live(for profile: URL,
                      interactive: Bool,
                      freshness: Freshness,
                      prompting: ClaudeCredentials.Prompting,
                      problem: inout String?,
                      locked: inout Bool) -> UsageAPI.Reading? {
        let now = Date()
        if let cached = liveCache[profile.path],
           Self.mayUseCache(age: now.timeIntervalSince(cached.fetched), freshness: freshness) {
            return cached.reading
        }

        let waiting = backoff[profile.path]
        guard Self.mayAttempt(now: now,
                              until: waiting?.until,
                              interactive: interactive,
                              serverAsked: serverAskedToWait.contains(profile.path))
        else {
            if interactive { problem = waitingMessage(until: waiting?.until) }
            return liveCache[profile.path]?.reading
        }

        do {
            guard let token = try ClaudeCredentials.token(for: profile, prompting: prompting)
            else { return nil }
            let reading = try UsageAPI.fetch(token: token.value)
            liveCache[profile.path] = (Date(), reading)
            backoff[profile.path] = nil
            serverAskedToWait.remove(profile.path)
            return reading
        } catch {
            // Falling back to the on-disk figures is the normal outcome here,
            // so only an explicit request reports why. The keychain being shut
            // is worth surfacing quietly either way, since it is one click to
            // fix and everything live depends on it.
            switch error {
            case ClaudeCredentials.Failure.noKeychainAccess,
                 ClaudeCredentials.Failure.keychainDeclined:
                locked = true
            default:
                break
            }
            if interactive { problem = (error as? LocalizedError)?.errorDescription }

            var retryAfter: TimeInterval?
            if case UsageAPI.Failure.http(_, let asked) = error, let asked {
                retryAfter = asked
                serverAskedToWait.insert(profile.path)
            }
            let failures = (backoff[profile.path]?.failures ?? 0) + 1
            let delay = Self.retryDelay(afterFailures: failures, retryAfter: retryAfter)
            backoff[profile.path] = (Date().addingTimeInterval(delay), failures)
            return liveCache[profile.path]?.reading
        }
    }

    /// Paths the service itself asked us to leave alone for a while.
    private var serverAskedToWait: Set<String> = []

    private func waitingMessage(until: Date?) -> String {
        guard let until, let remaining = Graft.countdown(to: until) else {
            return L10n.text("Waiting before asking for usage again.")
        }
        return L10n.format("Waiting %@ before asking for usage again.", remaining)
    }
}
