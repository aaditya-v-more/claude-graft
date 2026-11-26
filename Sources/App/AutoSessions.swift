import Foundation

/// Starts the next five-hour window on the accounts somebody switched this on
/// for, each time the last one runs out. The one place a session starts
/// without a press; notes/usage-and-credentials.md says what holds it to that.
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
    }

    /// By the pool's keys: `main`, a shortcut's id, a signed-in account's id.
    @Published private(set) var accounts: Set<String>
    @Published private(set) var attempts: [String: Attempt]

    private weak var store: ShortcutStore?
    private weak var usage: UsageMonitor?
    private weak var pool: UsagePoolController?
    private var timer: Timer?
    private var sending = false
    private let queue = DispatchQueue(label: "graft.auto-sessions", qos: .utility)

    static var file: URL { Graft.applicationSupport.appending(path: "ClaudeGraft/automatic-sessions.json") }

    /// A window closes on the minute, and a minute late is soon enough.
    static let interval: TimeInterval = 60
    static let window: TimeInterval = 5 * 60 * 60
    static let retry: TimeInterval = 15 * 60
    /// A login the service turned away stays turned away until a person
    /// renews it, and asking with it again is a request spent on a no.
    static let retryRefused: TimeInterval = 60 * 60

    init() {
        let saved = (try? Data(contentsOf: Self.file))
            .flatMap { try? JSONDecoder().decode(Saved.self, from: $0) } ?? Saved()
        accounts = Set(saved.accounts)
        attempts = saved.attempts
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
        save()
        if on { consider() }
    }

    // MARK: - Deciding

    /// Whether to send now. Nothing known of an account is a reason to: its
    /// first answer says what it has.
    static func due(_ usage: Graft.Usage?, last: Attempt?, now: Date) -> Bool {
        if let last, now < last.notBefore { return false }
        guard let usage else { return true }
        guard usage.fiveHour != nil else { return false }
        // A week used up refuses the message that would open the window.
        if usage.week >= 100, usage.weekReset.map({ $0 > now }) ?? true { return false }
        return usage.fiveHourReset.map { $0 <= now } ?? true
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
        if let reset = figures?.fiveHourReset, reset > now {
            return L10n.format("Next session at %@", reset.formatted(date: .omitted, time: .shortened))
        }
        if let last = attempts[key], let problem = last.problem, last.notBefore > now {
            return L10n.format("Could not start · trying again at %1$@ · %2$@",
                               last.notBefore.formatted(date: .omitted, time: .shortened), problem)
        }
        return L10n.text("Starts within a minute")
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
            guard let target = target(of: key),
                  Self.due(figures(of: key), last: attempts[key], now: now),
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
                    Diagnostics.note("automatic session", ["opened": outcome.failure == nil])
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
        save()
    }

    private func save() {
        let url = Self.file
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(),
                                                 withIntermediateDirectories: true)
        guard let data = try? JSONEncoder().encode(Saved(accounts: accounts.sorted(), attempts: attempts))
        else { return }
        try? data.write(to: url, options: .atomic)
    }
}
