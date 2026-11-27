import Foundation

/// Starts the next five-hour window on the accounts somebody switched this on
/// for, each time the last one runs out or at the times they gave it. The one
/// place a session starts without a press; notes/usage-and-credentials.md
/// says what holds it to that.
final class AutoSessions: ObservableObject {
    struct Attempt: Codable, Equatable {
        var at: Date
        /// Nothing is sent to the account before this.
        var notBefore: Date
        /// What went wrong, or nil when the window opened.
        var problem: String?
    }

    private struct Saved: Codable {
        var accounts: [String] = []
        var attempts: [String: Attempt] = [:]
        // Optional: a file written before there were times has neither.
        var times: [String: [Int]]?
        var timesSet: [String: Date]?
    }

    /// By the pool's keys: `main`, a shortcut's id, a signed-in account's id.
    @Published private(set) var accounts: Set<String>
    @Published private(set) var attempts: [String: Attempt]
    /// Minutes after midnight at which an account's window is opened, in
    /// place of whenever the last one closes. An account with none has the latter.
    @Published private(set) var times: [String: [Int]]
    /// When an account's times last began to count: none before it is acted on.
    private var timesSet: [String: Date]

    private weak var store: ShortcutStore?
    private weak var usage: UsageMonitor?
    private weak var pool: UsagePoolController?
    private var timer: Timer?
    private var sending = false
    /// When each account that can be sent to was first found with no figures.
    private var unknownSince: [String: Date] = [:]
    private let queue = DispatchQueue(label: "graft.auto-sessions", qos: .utility)

    static var file: URL { Graft.applicationSupport.appending(path: "ClaudeGraft/automatic-sessions.json") }

    /// A window closes on the minute, and a minute late is soon enough.
    static let interval: TimeInterval = 60
    static let window: TimeInterval = 5 * 60 * 60
    /// How long an account's figures may stay unknown before it is asked.
    static let patience: TimeInterval = 2 * 60
    static let retry: TimeInterval = 15 * 60
    /// A login the service turned away stays turned away until a person
    /// renews it, and asking with it again is a request spent on a no.
    static let retryRefused: TimeInterval = 60 * 60
    /// How late a time may be acted on, for a Mac that slept through it.
    static let grace: TimeInterval = 30 * 60

    init() {
        let saved = (try? Data(contentsOf: Self.file))
            .flatMap { try? JSONDecoder().decode(Saved.self, from: $0) } ?? Saved()
        accounts = Set(saved.accounts)
        attempts = saved.attempts
        times = saved.times ?? [:]
        timesSet = saved.timesSet ?? [:]
    }

    /// Enough to draw its switches from, and nothing that runs.
    func attach(store: ShortcutStore, usage: UsageMonitor, pool: UsagePoolController) {
        self.store = store
        self.usage = usage
        self.pool = pool
    }

    func start(store: ShortcutStore, usage: UsageMonitor, pool: UsagePoolController) {
        attach(store: store, usage: usage, pool: pool)
        guard timer == nil else { return }
        let timer = Timer(timeInterval: Self.interval, repeats: true) { [weak self] _ in self?.consider() }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    func isOn(_ key: String) -> Bool { accounts.contains(key) }

    func set(_ key: String, on: Bool) {
        guard on != accounts.contains(key) else { return }
        if on { accounts.insert(key) } else { accounts.remove(key) }
        // Switched on is asked now, whatever an earlier try left behind.
        attempts[key] = nil
        if on { timesSet[key] = Date() }
        save()
        if on { consider() }
    }

    func times(of key: String) -> [Int] { times[key] ?? [] }

    /// A time given is a wish for sessions, so it turns the account on.
    func add(_ minute: Int, to key: String, now: Date = Date()) {
        let minute = min(max(minute, 0), 24 * 60 - 1)
        guard !times(of: key).contains(minute) else { return }
        times[key] = (times(of: key) + [minute]).sorted()
        timesSet[key] = now
        if accounts.insert(key).inserted { attempts[key] = nil }
        save()
    }

    func remove(_ minute: Int, from key: String) {
        let kept = times(of: key).filter { $0 != minute }
        guard kept.count != times(of: key).count else { return }
        times[key] = kept.isEmpty ? nil : kept
        save()
    }

    // MARK: - Deciding

    /// Whether to send now. Nothing known of an account is a reason to: its
    /// first answer says what it has.
    static func due(_ usage: Graft.Usage?, last: Attempt?, unknownSince: Date? = nil, now: Date) -> Bool {
        if let last, now < last.notBefore { return false }
        // Figures land a moment after an account can first be sent to.
        guard let usage else { return unknownSince.map { now.timeIntervalSince($0) >= patience } ?? false }
        guard usage.fiveHour != nil else { return false }
        // A week used up refuses the message that would open the window.
        if usage.week >= 100, usage.weekReset.map({ $0 > now }) ?? true { return false }
        if let reset = usage.fiveHourReset { return reset <= now }
        // A figure above zero is an open window whose start the history does
        // not reach, unless the figure is itself older than a window.
        return (usage.fiveHour ?? 0) == 0 || now.timeIntervalSince(usage.sampled) >= window
    }

    private static func moment(_ minute: Int, on day: Date, _ calendar: Calendar) -> Date? {
        calendar.date(bySettingHour: minute / 60, minute: minute % 60, second: 0, of: day)
    }

    /// The last of an account's times to have come round, today's or yesterday's.
    static func latest(_ times: [Int], by now: Date, calendar: Calendar = .current) -> Date? {
        let today = calendar.startOfDay(for: now)
        let days = [today, calendar.date(byAdding: .day, value: -1, to: today)].compactMap { $0 }
        return days.flatMap { day in times.compactMap { moment($0, on: day, calendar) } }
            .filter { $0 <= now }.max()
    }

    /// The first of them still to come.
    static func following(_ times: [Int], after now: Date, calendar: Calendar = .current) -> Date? {
        let today = calendar.startOfDay(for: now)
        let days = [today, calendar.date(byAdding: .day, value: 1, to: today)].compactMap { $0 }
        return days.flatMap { day in times.compactMap { moment($0, on: day, calendar) } }
            .filter { $0 > now }.min()
    }

    /// Whether one of an account's times is asking for a window now: it has
    /// come round since `since`, and not so long ago that it has lapsed.
    static func called(_ times: [Int], since: Date, now: Date, calendar: Calendar = .current) -> Bool {
        guard let moment = latest(times, by: now, calendar: calendar) else { return false }
        return moment > since && now.timeIntervalSince(moment) <= grace
    }

    /// Before this none of an account's times counts: when they were given,
    /// or the window the last of them opened.
    private func timesCount(from key: String) -> Date {
        let opened = attempts[key].flatMap { $0.problem == nil ? $0.at : nil }
        return max(timesSet[key] ?? .distantPast, opened ?? .distantPast)
    }

    /// When an account may next be sent to: once its new window closes, or
    /// after long enough for whatever refused it to have passed.
    static func next(after failure: SessionStarter.Failure?, limits: PoolLimits?, now: Date) -> Date {
        switch failure {
        case nil:
            return max(limits?.fiveHourReset ?? now.addingTimeInterval(window), now.addingTimeInterval(interval))
        case .inFlight?:
            return now.addingTimeInterval(interval)
        case .http(429, _)?:
            return max(limits?.refusedUntil ?? limits?.fiveHourReset ?? .distantPast, now.addingTimeInterval(retry))
        case .http(401, _)?, .http(403, _)?, .refused?:
            return now.addingTimeInterval(retryRefused)
        default:
            return now.addingTimeInterval(retry)
        }
    }

    /// What its switch says beside it while it is on.
    func state(of key: String, now: Date = Date()) -> String? {
        guard accounts.contains(key) else { return nil }
        let figures = figures(of: key)
        if let figures, figures.fiveHour == nil { return L10n.text("No 5-hour limit on this account") }
        let times = times(of: key)
        if times.isEmpty, let reset = figures?.fiveHourReset, reset > now {
            return L10n.format("Next session at %@", reset.formatted(date: .omitted, time: .shortened))
        }
        if let last = attempts[key], let problem = last.problem, last.notBefore > now {
            return L10n.format("Could not start · trying again at %1$@ · %2$@",
                               last.notBefore.formatted(date: .omitted, time: .shortened), problem)
        }
        guard !times.isEmpty else { return L10n.text("Starts within a minute") }
        let open = figures?.fiveHourReset.flatMap { $0 > now ? $0 : nil }
        if open == nil, Self.called(times, since: timesCount(from: key), now: now) {
            return L10n.text("Starts within a minute")
        }
        // A time that comes round while a window is open opens nothing.
        guard let next = Self.following(times, after: open ?? now) else { return nil }
        return L10n.format("Next session at %@", next.formatted(date: .omitted, time: .shortened))
    }

    func failed(_ key: String, now: Date = Date()) -> Bool {
        guard accounts.contains(key), let last = attempts[key] else { return false }
        return last.problem != nil && last.notBefore > now
    }

    private func figures(of key: String) -> Graft.Usage? {
        guard let pool else { return nil }
        if key.hasPrefix(UsagePoolController.accountPrefix) { return pool.entry(forAccount: key)?.usage }
        return pool.profile(of: key).flatMap { usage?.entry(for: $0) }?.usage
    }

    private func target(of key: String) -> SessionStarter.Target? {
        guard let pool else { return nil }
        if key.hasPrefix(UsagePoolController.accountPrefix) {
            guard pool.lending[key] == .ready else { return nil }
            return pool.accounts.first { $0.id == key }.map(SessionStarter.Target.account)
        }
        // A shortcut not created yet has no Claude to sign in to.
        guard let profile = pool.profile(of: key), usage?.entry(for: profile) != nil else { return nil }
        return .profile(profile)
    }

    // MARK: - Sending

    /// Run by this object's timer, and at the moment a switch is turned on.
    private func consider(now: Date = Date()) {
        guard !sending, !accounts.isEmpty, let store, let usage, let pool,
              !usage.entries.isEmpty else { return }
        forget(besides: Set(pool.claudes + pool.accounts.map(\.id)))

        // One message to an account, however many names it has here.
        var reached: Set<String> = []
        var due: [(key: String, target: SessionStarter.Target)] = []
        for key in accounts.sorted() {
            guard let target = target(of: key) else { continue }
            let known = figures(of: key)
            if known != nil { unknownSince[key] = nil } else if unknownSince[key] == nil { unknownSince[key] = now }
            let times = times(of: key)
            guard Self.due(known, last: attempts[key], unknownSince: unknownSince[key], now: now),
                  times.isEmpty || Self.called(times, since: timesCount(from: key), now: now),
                  reached.insert(pool.accountUuid(of: key) ?? key).inserted else { continue }
            due.append((key, target))
        }
        guard !due.isEmpty else { return }

        sending = true
        queue.async {
            let results = due.map { ($0.key, $0.target, SessionStarter.startUnasked($0.target)) }
            DispatchQueue.main.async {
                self.sending = false
                let now = Date()
                for (key, target, outcome) in results {
                    let attempt = Attempt(at: now,
                                          notBefore: Self.next(after: outcome.failure, limits: outcome.limits, now: now),
                                          problem: outcome.failure?.errorDescription)
                    let account = pool.accountUuid(of: key)
                    for name in self.accounts where name == key || (account != nil && pool.accountUuid(of: name) == account) {
                        self.attempts[name] = attempt
                    }
                    let named = key.hasPrefix(UsagePoolController.accountPrefix)
                        ? key.dropFirst(UsagePoolController.accountPrefix.count) : Substring(key)
                    Diagnostics.note("automatic session", ["opened": outcome.failure == nil,
                                                           "account": String(named.prefix(8)),
                                                           "timed": !self.times(of: key).isEmpty])
                    switch target {
                    case .profile(let profile):
                        usage.invalidate(profile)
                        usage.refresh(store, prompting: .no, freshness: .recent, focus: profile)
                    case .account:
                        pool.heardFromSession(account: key, limits: outcome.limits)
                    }
                }
                self.save()
            }
        }
    }

    /// A Claude deleted or an account removed takes its switch with it.
    private func forget(besides known: Set<String>) {
        let kept = accounts.intersection(known)
        guard kept != accounts else { return }
        accounts = kept
        attempts = attempts.filter { kept.contains($0.key) }
        times = times.filter { known.contains($0.key) }
        timesSet = timesSet.filter { known.contains($0.key) }
        save()
    }

    private func save() {
        let url = Self.file
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(),
                                                 withIntermediateDirectories: true)
        guard let data = try? JSONEncoder().encode(Saved(accounts: accounts.sorted(), attempts: attempts,
                                                         times: times, timesSet: timesSet))
        else { return }
        try? data.write(to: url, options: .atomic)
    }
}
