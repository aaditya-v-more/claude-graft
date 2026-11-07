import AppKit
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
        /// When the figures shown were actually taken: the moment the service
        /// answered, or the moment the profile wrote its own record. Never the
        /// moment a pass happened to run, which is what every reused reading
        /// used to be stamped with — a figure five minutes old presented as one
        /// taken just now, and an hour-old one after a failure presented the
        /// same way.
        var refreshed: Date?
        /// Why these are not a live figure from this pass, in a sentence, or
        /// nil when they are. The fallback is never taken in silence.
        var note: String?

        /// When the figure was taken and where from, for the line under it.
        func freshness(now: Date = Date()) -> String {
            guard let refreshed else { return L10n.text("Not refreshed yet") }
            let when = now.timeIntervalSince(refreshed) < 45
                ? L10n.text("just now")
                : Entry.relative.localizedString(for: refreshed, relativeTo: now)
            return isLive
                ? L10n.format("Updated %@ from Anthropic", when)
                : L10n.format("Updated %@ from this profile's own record", when)
        }

        private static let relative: RelativeDateTimeFormatter = {
            let formatter = RelativeDateTimeFormatter()
            formatter.unitsStyle = .short
            return formatter
        }()

        /// A live figure was taken just now, so only one read off disk can be
        /// old enough to be worth warning about.
        var isDimmed: Bool {
            guard let usage else { return false }
            return !isLive && usage.isStale
        }

        /// The figures, when they still say something about the window now. A
        /// figure read off disk hours ago does not, and the bar leaves it out.
        var currentUsage: Graft.Usage? {
            guard let usage, isLive || !usage.isStale else { return nil }
            return usage
        }

        /// Start Session opens a five-hour window, and an account without one
        /// has nothing for it to open. Before any figure has come in, whether
        /// it has one is not known yet, and the button stays.
        var mayStartSession: Bool {
            guard let usage else { return true }
            return usage.fiveHour != nil
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
        /// Kept rather than assumed. A queued request used to be replayed as a
        /// press whatever it had been, so merely opening the dropdown mid-pass
        /// skipped the backoff and put a spinner up.
        var interactive: Bool
        var prompting: ClaudeCredentials.Prompting
        var freshness: Freshness
        var focus: URL?
        weak var store: ShortcutStore?
    }
    private var waiting: Waiting?

    /// True while a pass someone asked for is running, so a button can say so.
    /// Timer ticks leave it alone — a spinner every thirty seconds is noise.
    @Published private(set) var isRefreshing = false
    /// The one profile that pass is about, or nil when it is about all of
    /// them, so only that row's button spins.
    @Published private(set) var refreshingProfile: String?

    /// The API is polled far less often than the disk, and only per profile.
    private var liveCache: [String: (fetched: Date, reading: UsageAPI.Reading)] = [:]
    /// What went wrong the last time a profile's figures were asked for, kept
    /// until an answer comes back so every pass in between can say so.
    private var lastFailure: [String: String] = [:]
    static let liveInterval: TimeInterval = 5 * 60
    /// A Claude that is open is a Claude being used, and its figures move
    /// while nobody looks. Five minutes behind an account in use is what made
    /// the bar read like it had stopped; one closed moves only when something
    /// else spends it, and keeps the longer interval.
    static let activeInterval: TimeInterval = 2 * 60

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

    static func mayUseCache(age: TimeInterval, freshness: Freshness, running: Bool = false) -> Bool {
        switch freshness {
        case .cached: return age < (running ? activeInterval : liveInterval)
        case .recent: return age < recentInterval
        case .now: return age < nowInterval
        }
    }

    /// Which of two requests asks for more. Two waiting on one pass are one
    /// request for the stronger of them.
    static func stronger(_ a: Freshness, _ b: Freshness) -> Freshness {
        func rank(_ f: Freshness) -> Int { f == .now ? 2 : f == .recent ? 1 : 0 }
        return rank(a) >= rank(b) ? a : b
    }

    /// A profile writes its own record while it runs. When that is newer than
    /// the last answer from the service, it is the better figure — a reading
    /// held in the cache used to win regardless, so the bar stayed behind an
    /// account the profile itself had just reported on.
    static func prefersLocal(liveFetched: Date, localSampled: Date?) -> Bool {
        guard let localSampled else { return false }
        return localSampled > liveFetched
    }

    /// Which profile a Claude process belongs to, from its command line. The
    /// main profile is the one launched with no `--user-data-dir` at all.
    static func profile(launchedBy command: String, among profiles: [URL]) -> URL? {
        guard Graft.isClaudeProcess(command) else { return nil }
        if Graft.isDefaultInstance(command) {
            return profiles.first { Graft.samePath($0, Graft.mainProfile) }
        }
        return profiles.first { Graft.carriesDataDir(command, $0) }
    }

    /// What becomes of a request that arrives while a pass is already running.
    ///
    /// A timer tick is worth dropping: the pass in flight is about to answer
    /// the same question. A press is not. Opening the dropdown starts a pass of
    /// its own, so pressing Refresh Usage a moment later — the obvious thing to
    /// do when the figure looks stale — used to land squarely inside it and be
    /// discarded without a trace.
    enum Arrival { case start, queue, drop }

    /// Opening the dropdown is not a press, and was dropped like a tick — so
    /// the figure someone opened it to see was whatever the timer's pass in
    /// flight happened to reuse, up to five minutes old. Anything asking for
    /// more than a tick does now waits its turn.
    static func arrival(passInFlight: Bool, interactive: Bool, freshness: Freshness = .cached) -> Arrival {
        guard passInFlight else { return .start }
        return interactive || freshness != .cached ? .queue : .drop
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
    ///
    /// Each is weighed by the figure the bar would show for it. Weighed by the
    /// five-hour window alone, an account without one counted as zero and
    /// lost to every other account, however little of its week was left.
    var headlineEntry: Entry? {
        let usable = entries.filter { $0.currentUsage != nil }
        let running = usable.filter(\.isRunning)
        let pool = running.isEmpty ? usable : running
        return pool.max { ($0.usage?.headline.percent ?? 0) < ($1.usage?.headline.percent ?? 0) }
    }

    var headline: Graft.Usage.Headline? { headlineEntry?.usage?.headline }

    /// The account the menu bar is about, and whether it is named there.
    ///
    /// Named, it is the Claude in front whatever its figures say. Unnamed, the
    /// tightest window open is the right figure to show; with a name beside
    /// it, another account's figure would be read as that account's own.
    func statusEntry(front: URL?) -> (entry: Entry, named: Bool)? {
        if let front, let named = entry(for: front) { return (named, true) }
        return headlineEntry.map { ($0, false) }
    }

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

    /// Bringing a Claude to the front is switching to that account, which is
    /// the moment its figure matters. Only that profile is asked, and only as
    /// often as someone looking may ask, so flicking between windows costs at
    /// most one request a minute. `FrontmostClaude` reports the switch, since
    /// the name in the bar needs the same lookup.
    func claudeCameForward(_ profile: URL, store: ShortcutStore) {
        refresh(store, freshness: .recent, focus: profile)
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
    ///
    /// `focus` narrows the request to one profile: it gets the freshness asked
    /// for, and every other profile is read as a tick would read it.
    func refresh(_ store: ShortcutStore,
                 interactive: Bool = false,
                 prompting: ClaudeCredentials.Prompting? = nil,
                 freshness: Freshness? = nil,
                 focus: URL? = nil) {
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
        // A press about one account may ask the keychain for that account; the
        // others are read the way a tick would read them.
        let elsewhere: ClaudeCredentials.Prompting = mayPromptUnasked ? .onceIfShut : .no

        if interactive {
            isRefreshing = true
            refreshingProfile = focus?.path
        }

        switch Self.arrival(passInFlight: inFlight, interactive: interactive, freshness: wanted) {
        case .drop:
            return
        case .queue:
            if let earlier = waiting {
                waiting = Waiting(interactive: earlier.interactive || interactive,
                                  prompting: interactive ? asking : earlier.prompting,
                                  freshness: Self.stronger(earlier.freshness, wanted),
                                  focus: earlier.focus.flatMap { a in focus.flatMap { Graft.samePath(a, $0) ? a : nil } },
                                  store: store)
            } else {
                waiting = Waiting(interactive: interactive, prompting: asking,
                                  freshness: wanted, focus: focus, store: store)
            }
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
                let running = Graft.isRunning(profile: target.profile)
                let focused = focus.map { Graft.samePath($0, target.profile) } ?? true
                var entry = Entry(name: target.name,
                                  profile: target.profile,
                                  usage: onDisk,
                                  isRunning: running,
                                  shortcut: target.shortcut)
                entry.refreshed = onDisk?.sampled
                let outcome = self.live(for: target.profile,
                                        interactive: interactive && focused,
                                        freshness: focused ? wanted : .cached,
                                        running: running,
                                        prompting: focused ? asking : elsewhere,
                                        problem: &problem,
                                        locked: &locked)
                if let live = outcome.reading, let fetched = outcome.fetched,
                   !Self.prefersLocal(liveFetched: fetched, localSampled: onDisk?.sampled) {
                    entry.usage = Graft.Usage(fiveHour: live.fiveHour,
                                              week: live.week,
                                              organization: onDisk?.organization,
                                              sampled: fetched,
                                              fiveHourReset: live.fiveHourReset,
                                              weekReset: live.weekReset,
                                              fable: live.fable,
                                              fableReset: live.fableReset)
                    entry.isLive = true
                    entry.plan = live.plan
                    entry.refreshed = fetched
                    // One line, and the time the figure is from: a fallback
                    // read as a figure taken now is what this note is for.
                    entry.note = outcome.failure.map {
                        L10n.format("%@ · figure from %@", $0, Self.clock(fetched))
                    }
                } else if let onDisk {
                    entry.note = outcome.failure.map {
                        L10n.format("%@ · own record from %@", $0, Self.clock(onDisk.sampled))
                    }
                } else {
                    entry.note = outcome.failure
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
                    self.refresh(store, interactive: queued.interactive,
                                 prompting: queued.prompting, freshness: queued.freshness,
                                 focus: queued.focus)
                } else {
                    self.waiting = nil
                    self.isRefreshing = false
                    self.refreshingProfile = nil
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
            if let refreshed = entry.refreshed { row["refreshedAt"] = stamp.string(from: refreshed) }
            if let note = entry.note { row["note"] = note }
            if let usage = entry.usage {
                if let fiveHour = usage.fiveHour {
                    row["fiveHour"] = fiveHour
                } else {
                    row["noFiveHourLimit"] = true
                }
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

    /// What asking for one profile's live figures came to: the reading to
    /// show, if there is one, when it was really taken, and — when this pass
    /// did not get a current answer — why not.
    private struct LiveOutcome {
        var reading: UsageAPI.Reading?
        var fetched: Date?
        var failure: String?
    }

    /// Cached hard: one call per profile per interval, and never a prompt on a
    /// background pass.
    private func live(for profile: URL,
                      interactive: Bool,
                      freshness: Freshness,
                      running: Bool,
                      prompting: ClaudeCredentials.Prompting,
                      problem: inout String?,
                      locked: inout Bool) -> LiveOutcome {
        let now = Date()
        let cached = liveCache[profile.path]
        if let cached,
           Self.mayUseCache(age: now.timeIntervalSince(cached.fetched), freshness: freshness, running: running) {
            return LiveOutcome(reading: cached.reading, fetched: cached.fetched)
        }

        let waiting = backoff[profile.path]
        guard Self.mayAttempt(now: now,
                              until: waiting?.until,
                              interactive: interactive,
                              serverAsked: serverAskedToWait.contains(profile.path))
        else {
            if interactive { problem = waitingMessage(until: waiting?.until) }
            let why = [lastFailure[profile.path], waitingMessage(until: waiting?.until, inLine: true)]
                .compactMap { $0 }.joined(separator: " · ")
            return LiveOutcome(reading: cached?.reading, fetched: cached?.fetched, failure: why)
        }

        do {
            guard let token = try ClaudeCredentials.token(for: profile, prompting: prompting)
            else {
                return LiveOutcome(failure: L10n.text("No saved login to ask Anthropic with"))
            }
            let reading = try UsageAPI.fetch(token: token.value)
            let fetched = Date()
            liveCache[profile.path] = (fetched, reading)
            backoff[profile.path] = nil
            lastFailure[profile.path] = nil
            serverAskedToWait.remove(profile.path)
            return LiveOutcome(reading: reading, fetched: fetched)
        } catch {
            // Falling back to the on-disk figures is the normal outcome here,
            // so only an explicit request raises it to the dropdown's problem
            // line. The row itself always says, quietly. The keychain being
            // shut is worth surfacing either way, since it is one click to fix
            // and everything live depends on it.
            let why: String
            switch error {
            case ClaudeCredentials.Failure.noKeychainAccess,
                 ClaudeCredentials.Failure.keychainDeclined:
                locked = true
                why = L10n.text("Live usage needs keychain access")
            default:
                why = L10n.text("Anthropic didn't answer")
            }
            lastFailure[profile.path] = why
            if interactive { problem = (error as? LocalizedError)?.errorDescription }

            var retryAfter: TimeInterval?
            if case UsageAPI.Failure.http(_, let asked) = error, let asked {
                retryAfter = asked
                serverAskedToWait.insert(profile.path)
            }
            let failures = (backoff[profile.path]?.failures ?? 0) + 1
            let delay = Self.retryDelay(afterFailures: failures, retryAfter: retryAfter)
            backoff[profile.path] = (Date().addingTimeInterval(delay), failures)
            return LiveOutcome(reading: cached?.reading, fetched: cached?.fetched, failure: why)
        }
    }

    /// Paths the service itself asked us to leave alone for a while.
    private var serverAskedToWait: Set<String> = []

    private func waitingMessage(until: Date?, inLine: Bool = false) -> String {
        guard let until, let remaining = Graft.countdown(to: until) else {
            return L10n.text(inLine ? "next try soon" : "Next try soon")
        }
        return L10n.format(inLine ? "next try in %@" : "Next try in %@", remaining)
    }

    /// The time alone for today, with the day for anything older.
    static func clock(_ date: Date, now: Date = Date()) -> String {
        Calendar.current.isDate(date, inSameDayAs: now)
            ? date.formatted(date: .omitted, time: .shortened)
            : date.formatted(date: .abbreviated, time: .shortened)
    }
}
