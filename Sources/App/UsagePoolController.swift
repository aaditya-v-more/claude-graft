import AppKit
import Foundation

/// Which Claudes draw on which accounts. Every Claude has its own list, kept
/// by the Claude's key — `main` for Claude's own profile, a shortcut's id for
/// the rest — and a list can name other Claudes or accounts signed in through
/// Graft with no Claude of their own.
struct UsageSharing: Codable, Equatable {
    var lenders: [String: [String]] = [:]
    /// Conversations moved to an account by hand, by session.
    var assignments: [String: Assignment] = [:]

    struct Assignment: Codable, Equatable {
        var to: String
        var at: Date
    }
}

/// An account signed in through Graft rather than through a Claude. Its token
/// is sealed with Claude's own safe-storage key, which Graft already holds for
/// reading Claude's logins, so keeping it costs no keychain item and no dialog
/// of its own.
struct PoolAccount: Codable, Equatable, Identifiable {
    var id: String
    var accountUuid: String?
    var email: String?
    var organizationName: String?
    var scopes: [String]
    var expires: Date
    var added: Date
    var sealed: String
}

/// Keeps the relay running while any Claude draws on another account, tells it
/// who is who, and puts the two lines that route a Claude's Claude Code through
/// it into that Claude's own Code environment.
///
/// The relay holds nothing of its own. Every pass reads each login afresh —
/// Claude renews them, and a lent token has to be the current one — and hands
/// the relay everything again, so a relay that was restarted, replaced or
/// killed is back in step within one pass.
final class UsagePoolController: ObservableObject {
    enum Lending: Equatable {
        case ready
        case signedOut
        case expired
        case unreadable
    }

    /// A conversation the menu bar shows.
    struct Chat: Identifiable, Equatable {
        var id: String
        var title: String?
        var claude: String
        var servedBy: String?
        var assigned: String?
        /// Every account it may be moved to, its own first.
        var choices: [String]
        var lastAt: Date
        /// The account it was meant for said no on its last request.
        var passedOver: Bool
    }

    static let mainKey = "main"
    static let accountPrefix = "account:"

    static func key(for shortcut: Shortcut) -> String { shortcut.id.uuidString }

    @Published private(set) var sharing: UsageSharing
    @Published private(set) var accounts: [PoolAccount]
    @Published private(set) var status: PoolStatus?
    @Published private(set) var problem: String?
    @Published private(set) var lending: [String: Lending] = [:]
    /// By Claude key.
    @Published private(set) var environment: [String: ClaudeCodeEnvironment.State] = [:]
    /// By session.
    @Published private(set) var titles: [String: String] = [:]
    /// Which account each Claude is signed into, by key. It comes out of each
    /// profile's `config.json`, so a pass reads it on `queue` and a view only
    /// ever reads this.
    @Published private(set) var claudeAccounts: [String: String] = [:]

    private weak var store: ShortcutStore?
    private weak var usage: UsageMonitor?
    private var timer: Timer?
    private var watchers = 0
    private var syncing = false
    private var syncAgain = false
    private var observers: [NSObjectProtocol] = []
    private let queue = DispatchQueue(label: "graft.usage-pool")
    /// Touched on `queue` alone.
    private var children: [pid_t: DispatchSourceProcess] = [:]
    private var lastSpawn = Date.distantPast
    private var titleCache: [String: (stamp: Date, session: String?, title: String?)] = [:]

    init() {
        sharing = (try? Data(contentsOf: UsagePool.settingsFile))
            .flatMap { try? JSONDecoder().decode(UsageSharing.self, from: $0) } ?? UsageSharing()
        accounts = (try? Data(contentsOf: UsagePool.accountsFile))
            .flatMap { try? JSONDecoder().decode([PoolAccount].self, from: $0) } ?? []
    }

    static var isSupported: Bool {
        if #available(macOS 15, *) { return true }
        return false
    }

    /// Enough to name and place what it lists, and nothing that runs: no relay,
    /// no timer, no file written. The layout suite draws the pane from this.
    func attach(store: ShortcutStore, usage: UsageMonitor) {
        self.store = store
        self.usage = usage
    }

    func start(store: ShortcutStore, usage: UsageMonitor) {
        attach(store: store, usage: usage)
        schedule()
        watchClaudes()
        sync()
    }

    // MARK: - Who lends to whom

    func lenders(of claude: String) -> [String] { sharing.lenders[claude] ?? [] }

    var isAnyoneBorrowing: Bool { sharing.lenders.values.contains { !$0.isEmpty } }

    func addLender(_ lender: String, to claude: String) {
        guard lender != claude, !lenders(of: claude).contains(lender) else { return }
        var next = sharing
        next.lenders[claude, default: []].append(lender)
        save(next)
    }

    /// An account signed in through Graft goes when the last Claude using it
    /// lets it go: nothing else would ever lend it, and its token is a year of
    /// access nobody is looking at.
    func removeLender(_ lender: String, from claude: String) {
        var next = sharing
        next.lenders[claude]?.removeAll { $0 == lender }
        if next.lenders[claude]?.isEmpty == true { next.lenders[claude] = nil }
        save(next)
        forgetUnusedAccounts()
    }

    func assign(_ session: String, to member: String?) {
        var next = sharing
        next.assignments[session] = member.map { UsageSharing.Assignment(to: $0, at: Date()) }
        persist(next)
        sync()
    }

    /// One account signed in twice is one entry, holding the newer login.
    func add(_ account: PoolAccount, to claude: String) {
        var stored = account
        if let uuid = account.accountUuid,
           let index = accounts.firstIndex(where: { $0.accountUuid == uuid }) {
            stored.id = accounts[index].id
            accounts[index] = stored
        } else {
            accounts.append(stored)
        }
        saveAccounts()
        addLender(stored.id, to: claude)
        sync()
    }

    private func forgetUnusedAccounts() {
        let used = Set(sharing.lenders.values.flatMap { $0 })
        let kept = accounts.filter { used.contains($0.id) }
        guard kept.count != accounts.count else { return }
        accounts = kept
        saveAccounts()
    }

    private func save(_ next: UsageSharing) {
        persist(next)
        Diagnostics.note("usage sharing changed", ["borrowers": next.lenders.count,
                                                   "lenders": next.lenders.values.map(\.count).reduce(0, +)])
        sync()
    }

    private func persist(_ next: UsageSharing) {
        sharing = next
        write(next, to: UsagePool.settingsFile)
    }

    private func saveAccounts() {
        write(accounts, to: UsagePool.accountsFile)
    }

    private func write<T: Encodable>(_ value: T, to url: URL) {
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(),
                                                 withIntermediateDirectories: true,
                                                 attributes: [.posixPermissions: 0o700])
        guard let data = try? JSONEncoder().encode(value) else { return }
        FileManager.default.createFile(atPath: url.path, contents: data,
                                       attributes: [.posixPermissions: 0o600])
    }

    /// A shortcut deleted, or a draft discarded, leaves its id behind here with
    /// nothing to stand for, as a borrower and as a lender alike.
    private func prune(against store: ShortcutStore, now: Date = Date()) {
        let known = Set([Self.mainKey] + store.shortcuts.map(Self.key(for:)) + accounts.map(\.id))
        var next = sharing
        for (claude, list) in next.lenders {
            let kept = known.contains(claude) ? list.filter(known.contains) : []
            next.lenders[claude] = kept.isEmpty ? nil : kept
        }
        // A choice made by hand outlives the cache it was protecting, but not
        // the day: by then the conversation is over or somebody has moved on.
        let active = Set((status?.sessions ?? []).filter { now.timeIntervalSince($0.lastAt) < 86_400 }.map(\.id))
        next.assignments = next.assignments.filter {
            active.contains($0.key) || now.timeIntervalSince($0.value.at) < 86_400
        }
        guard next != sharing else { return }
        persist(next)
        forgetUnusedAccounts()
    }

    // MARK: - Names

    func profile(of key: String) -> URL? {
        if key == Self.mainKey { return Graft.mainProfile }
        return UUID(uuidString: key).flatMap { store?.shortcut($0) }?.profileDir
    }

    func name(of key: String) -> String {
        if key == Self.mainKey { return "Claude" }
        if let account = accounts.first(where: { $0.id == key }) {
            return account.email ?? L10n.text("Signed-in account")
        }
        return UUID(uuidString: key).flatMap { store?.shortcut($0) }?.name ?? L10n.text("Removed shortcut")
    }

    func accountUuid(of key: String) -> String? {
        if let account = accounts.first(where: { $0.id == key }) { return account.accountUuid }
        return claudeAccounts[key]
    }

    /// Off the main thread: every one of these is a file read.
    static func readAccounts(_ claudes: [(String, URL)]) -> [String: String] {
        var found: [String: String] = [:]
        for (key, profile) in claudes { found[key] = Graft.account(of: profile) }
        return found
    }

    func learnAccounts(_ found: [String: String]) {
        if claudeAccounts != found { claudeAccounts = found }
    }

    /// Every Claude this machine has, by key, Claude's own first.
    var claudes: [String] {
        [Self.mainKey] + (store?.shortcuts ?? []).map(Self.key(for:))
    }

    /// The other Claude on the same account, which is worth saying beside an
    /// account somebody has just signed in: it is the same allowance twice.
    func sameAccount(as key: String, besides claude: String) -> String? {
        guard let uuid = accountUuid(of: key) else { return nil }
        return claudes.first { $0 != key && $0 != claude && accountUuid(of: $0) == uuid }
    }

    // MARK: - Conversations

    /// The ones worth a line in the menu: from a Claude that borrows, active in
    /// the last half hour, the Claude in front first and then the newest. The
    /// front one is `FrontmostClaude`'s, since matching a Claude to its profile
    /// takes a `ps` and the status item has already paid for it.
    func chats(front: URL?, now: Date = Date()) -> [Chat] {
        guard let status else { return [] }
        let front = front.map(PoolEnvironment.key(for:))
        let listed = status.sessions.filter { session in
            now.timeIntervalSince(session.lastAt) < 30 * 60 && !lenders(of: session.from).isEmpty
        }
        let inFront = { (session: PoolSession) -> Bool in
            front != nil && self.profile(of: session.from).map(PoolEnvironment.key(for:)) == front
        }
        return listed
            .sorted { a, b in
                let fa = inFront(a), fb = inFront(b)
                return fa != fb ? fa : a.lastAt > b.lastAt
            }
            .prefix(5)
            .map { session in
                let assigned = sharing.assignments[session.id]?.to
                return Chat(id: session.id, title: titles[session.id], claude: session.from,
                            servedBy: session.servedBy, assigned: assigned,
                            choices: oneEach([session.from] + lenders(of: session.from)),
                            lastAt: session.lastAt,
                            passedOver: assigned != nil && session.servedBy != assigned
                                && !session.passedOver.isEmpty)
            }
    }

    /// One name for each account, the one the relay counts: the first that
    /// can lend. Two names for one account would be a choice that changes
    /// nothing.
    private func oneEach(_ keys: [String]) -> [String] {
        let canLend = { (key: String) in
            self.lending[key] == .ready
                && self.status?.members.first(where: { $0.id == key })?.tokenRefused != true
        }
        var out: [String] = []
        var place: [String: Int] = [:]
        for key in keys {
            let account = accountUuid(of: key) ?? key
            if let index = place[account] {
                if index > 0, !canLend(out[index]), canLend(key) { out[index] = key }
            } else {
                place[account] = out.count
                out.append(key)
            }
        }
        return out
    }

    // MARK: - Passes

    /// The dropdown being open is somebody watching where conversations go,
    /// which is worth a pass every few seconds; otherwise half a minute keeps
    /// logins current, and that is all the relay needs from here.
    func watch() {
        watchers += 1
        schedule()
        sync()
    }

    func unwatch() {
        watchers = max(0, watchers - 1)
        schedule()
    }

    private func schedule() {
        timer?.invalidate()
        let interval: TimeInterval = watchers > 0 ? 3 : 30
        let timer = Timer(timeInterval: interval, repeats: true) { [weak self] _ in self?.sync() }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    /// A Claude quitting is the moment its environment can be written, and
    /// Claude's own profile has no launcher to do it on the way back in.
    private func watchClaudes() {
        observers.append(NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didTerminateApplicationNotification, object: nil, queue: .main
        ) { [weak self] note in
            guard let app = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication,
                  app.bundleURL.map({ Graft.samePath($0, Graft.claudeApp) }) == true else { return }
            self?.sync()
        })
    }

    func restartRelay() {
        queue.async {
            _ = try? PoolSocket.send(PoolCommand(quit: true))
            self.lastSpawn = .distantPast
            DispatchQueue.main.async { self.sync() }
        }
    }

    private struct Input {
        var key: String
        var name: String
        var profile: URL?
        var account: PoolAccount?
        var borrowsFrom: [String]
        var limits: PoolLimits?
    }

    /// Everything that reads a login, a file or a socket happens on `queue`;
    /// only the shortcut list and the usage figures, which belong to the main
    /// thread, are read here.
    func sync() {
        guard let store, let usage else { return }
        prune(against: store)
        if syncing {
            syncAgain = true
            return
        }
        syncing = true
        let sharing = self.sharing
        let accounts = self.accounts

        var involved: [String] = []
        for (claude, list) in sharing.lenders where !list.isEmpty {
            for key in [claude] + list where !involved.contains(key) { involved.append(key) }
        }
        var seen = Set<String>()
        // Two shortcuts can stand for one profile; lending it twice would count
        // one account's room twice.
        let inputs = involved.compactMap { key -> Input? in
            if let account = accounts.first(where: { $0.id == key }) {
                return Input(key: key, name: account.email ?? key, profile: nil, account: account,
                             borrowsFrom: [], limits: nil)
            }
            guard let profile = profile(of: key),
                  seen.insert(PoolEnvironment.key(for: profile)).inserted else { return nil }
            var limits: PoolLimits?
            if let entry = usage.entry(for: profile), let figures = entry.usage, let taken = entry.refreshed {
                limits = PoolLimits(fiveHourPercent: figures.fiveHour, fiveHourReset: figures.fiveHourReset,
                                    weekPercent: figures.week, weekReset: figures.weekReset, observed: taken)
            }
            return Input(key: key, name: name(of: key), profile: profile, account: nil,
                         borrowsFrom: sharing.lenders[key] ?? [], limits: limits)
        }
        // Every Claude Graft knows of, so one that stopped borrowing has its
        // lines taken out as well as one that started has them put in.
        let claudeProfiles = claudes.compactMap { key in profile(of: key).map { (key, $0) } }
        let assignments = sharing.assignments.mapValues(\.to)

        queue.async {
            var lending: [String: Lending] = [:]
            let signedInto = Self.readAccounts(claudeProfiles)
            let key = try? ClaudeCredentials.safeStorageKey(prompting: .no)
            let members = inputs.map { input -> PoolMember in
                if let account = input.account {
                    let token = key.flatMap { ClaudeCredentials.unseal(account.sealed, key: $0) }
                        .map { String(decoding: $0, as: UTF8.self) }
                    let current = token != nil && account.expires > Date().addingTimeInterval(120)
                    lending[input.key] = token == nil ? .unreadable : current ? .ready : .expired
                    return PoolMember(id: input.key, name: input.name, account: account.accountUuid,
                                      identify: token.map { [UsagePool.digest($0)] } ?? [],
                                      token: current ? token : nil, limits: nil, borrowsFrom: nil)
                }
                let login = Self.login(of: input.profile!)
                lending[input.key] = login.state
                return PoolMember(id: input.key, name: input.name,
                                  account: signedInto[input.key],
                                  identify: login.identify, token: login.token, limits: input.limits,
                                  borrowsFrom: input.borrowsFrom)
            }
            let configuration = PoolConfiguration(enabled: true, members: members, assignments: assignments)
            let journal = PoolEnvironment.readJournal()
            let wanted = !members.isEmpty || !journal.profiles.isEmpty
            var (status, problem) = self.deliver(configuration, wanted: wanted)

            var environment: [String: ClaudeCodeEnvironment.State] = [:]
            if let key, wanted {
                var wants: [URL: Bool] = [:]
                for (claude, profile) in claudeProfiles {
                    let borrows = !(sharing.lenders[claude] ?? []).isEmpty && Self.isSupported
                    if borrows || journal.profiles[PoolEnvironment.key(for: profile)] != nil {
                        wants[profile] = borrows
                    }
                }
                let states = ClaudeCodeEnvironment.settle(
                    wanted: wants, key: key, relay: Self.relayExecutable?.path, relayReady: status != nil,
                    running: { Graft.isRunning(profile: $0) })
                for (claude, profile) in claudeProfiles {
                    if let state = states[PoolEnvironment.key(for: profile)] { environment[claude] = state }
                }
                // A relay that was not needed until the lines went in.
                if status == nil, states.values.contains(where: \.holdsLines) {
                    (status, problem) = self.deliver(configuration, wanted: true)
                }
            } else if wanted, key == nil {
                problem = problem ?? L10n.text("Graft needs keychain access to set up Claude Code for sharing.")
            }

            let titles = self.lookUpTitles(status?.sessions ?? [], profiles: claudeProfiles)
            DispatchQueue.main.async {
                self.learnAccounts(signedInto)
                // Only what changed: every assignment redraws whatever is
                // watching, and most passes change nothing.
                if self.lending != lending { self.lending = lending }
                if self.status != status { self.status = status }
                if self.problem != problem { self.problem = problem }
                if self.environment != environment { self.environment = environment }
                if self.titles != titles { self.titles = titles }
                self.syncing = false
                if self.syncAgain {
                    self.syncAgain = false
                    self.sync()
                }
            }
        }
    }

    /// Never prompts. The usage monitor is what asks for keychain access, once,
    /// and the key it gets is the one every read in this process shares.
    private static func login(of profile: URL) -> (state: Lending, identify: [String], token: String?) {
        do {
            guard let all = try ClaudeCredentials.tokens(for: profile, prompting: .no) else {
                return (.signedOut, [], nil)
            }
            let current = all.filter(\.isCurrent)
            let lent = current.filter { $0.scopes.contains(UsagePool.lendingScope) }
                .max { $0.expires < $1.expires }
                ?? current.filter { $0.covers([ClaudeCredentials.inferenceScope]) }
                .max { $0.expires < $1.expires }
            return (lent == nil ? .expired : .ready, all.map { UsagePool.digest($0.value) }, lent?.value)
        } catch ClaudeCredentials.Failure.notSignedIn {
            return (.signedOut, [], nil)
        } catch {
            return (.unreadable, [], nil)
        }
    }

    // MARK: - Titles

    /// A conversation's title is what its record in the sidebar says, found by
    /// the session id every request carries, which is the record's
    /// `cliSessionId`. Records are read again only when they change.
    private func lookUpTitles(_ sessions: [PoolSession], profiles: [(String, URL)]) -> [String: String] {
        let recent = sessions.filter { Date().timeIntervalSince($0.lastAt) < 30 * 60 }
        guard !recent.isEmpty else { return [:] }
        let wanted = Set(recent.map(\.id))
        var found: [String: String] = [:]
        let fm = FileManager.default
        for (_, profile) in profiles {
            let store = profile.appending(path: "claude-code-sessions")
            for account in (try? fm.contentsOfDirectory(atPath: store.path)) ?? [] {
                let accountDir = store.appending(path: account)
                for org in (try? fm.contentsOfDirectory(atPath: accountDir.path)) ?? [] {
                    let orgDir = accountDir.appending(path: org)
                    for name in (try? fm.contentsOfDirectory(atPath: orgDir.path)) ?? []
                    where name.hasSuffix(".json") {
                        let url = orgDir.appending(path: name)
                        let stamp = (try? fm.attributesOfItem(atPath: url.path))?[.modificationDate] as? Date
                            ?? .distantPast
                        var entry = titleCache[url.path]
                        if entry?.stamp != stamp {
                            let record = (try? Data(contentsOf: url))
                                .flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] }
                            entry = (stamp, record?["cliSessionId"] as? String, record?["title"] as? String)
                            titleCache[url.path] = entry
                        }
                        if let session = entry?.session, wanted.contains(session),
                           let title = entry?.title, !title.isEmpty {
                            found[session] = title
                        }
                    }
                }
            }
        }
        for session in wanted where found[session] == nil {
            if let title = Self.transcriptTitle(session) { found[session] = title }
        }
        return found
    }

    /// A conversation that has no record yet still has its transcript, and
    /// the command line writes the title it settles on there as it goes.
    static func transcriptTitle(_ session: String) -> String? {
        let fm = FileManager.default
        let projects = Graft.claudeProjects
        for folder in (try? fm.contentsOfDirectory(atPath: projects.path)) ?? [] {
            let url = projects.appending(path: folder).appending(path: "\(session).jsonl")
            guard let handle = try? FileHandle(forReadingFrom: url) else { continue }
            defer { try? handle.close() }
            let size = (try? handle.seekToEnd()) ?? 0
            try? handle.seek(toOffset: size > 262_144 ? size - 262_144 : 0)
            let tail = String(decoding: handle.readDataToEndOfFile(), as: UTF8.self)
            for key in ["\"customTitle\":\"", "\"aiTitle\":\""] {
                guard let start = tail.range(of: key, options: .backwards)?.upperBound,
                      let end = tail[start...].firstIndex(of: "\"") else { continue }
                let title = String(tail[start..<end])
                if !title.isEmpty { return title }
            }
            return nil
        }
        return nil
    }

    // MARK: - The relay

    static var relayExecutable: URL? {
        Bundle.main.url(forResource: "graft-relay", withExtension: nil)
    }

    /// What a relay started from this build reports about itself. A relay left
    /// running by an earlier version answers with something else and is asked
    /// to step aside.
    private static let bundledDigest: String? = relayExecutable.flatMap { url in
        (try? Data(contentsOf: url, options: .alwaysMapped)).map(UsagePool.digest(data:))
    }

    /// On `queue`.
    private func deliver(_ configuration: PoolConfiguration, wanted: Bool) -> (PoolStatus?, String?) {
        guard wanted else {
            // Nobody left to serve and no Claude still pointed at it. A relay
            // still running is asked to step aside; one that is not is left
            // that way.
            if FileManager.default.fileExists(atPath: UsagePool.socketFile.path) {
                _ = try? PoolSocket.send(PoolCommand(quit: true))
            }
            return (nil, nil)
        }
        // The relay keeps its certificate's key in memory alone, and only
        // macOS 15 can import one that way rather than into the login keychain.
        guard Self.isSupported else {
            return (nil, L10n.text("Sharing usage needs macOS 15 or later."))
        }
        guard let executable = Self.relayExecutable, let expected = Self.bundledDigest else {
            return (nil, L10n.text("This copy of Claude Graft has no relay in it."))
        }
        if let status = try? PoolSocket.send(PoolCommand(configure: configuration)) {
            guard status.binary != expected else { return (status, nil) }
            Diagnostics.note("relay replaced", ["running": String(status.binary.prefix(12)),
                                                "bundled": String(expected.prefix(12))])
            // Answered once the old relay has let go of its port and its lock,
            // so the new one can start straight away.
            _ = try? PoolSocket.send(PoolCommand(quit: true))
        }
        // A relay that will not stay up must not be started again every three
        // seconds while somebody watches.
        guard Date().timeIntervalSince(lastSpawn) > 20 else {
            return (nil, L10n.text("The relay is not running. Diagnostics has the reason."))
        }
        lastSpawn = Date()
        guard let pid = PoolEnvironment.spawnRelay(executable.path) else {
            return (nil, L10n.text("The relay could not be started."))
        }
        // Graft's child until Graft quits, and a child nobody waits for stays
        // in the process table after it exits.
        let source = DispatchSource.makeProcessSource(identifier: pid, eventMask: .exit, queue: queue)
        source.setEventHandler { [weak self] in
            var status: Int32 = 0
            waitpid(pid, &status, WNOHANG)
            self?.children[pid]?.cancel()
            self?.children[pid] = nil
        }
        children[pid] = source
        source.resume()
        // The first start makes the certificate, which takes a second or two.
        for _ in 0..<60 {
            usleep(250_000)
            if let status = try? PoolSocket.send(PoolCommand(configure: configuration)) {
                return (status, nil)
            }
        }
        return (nil, L10n.text("The relay is not running. Diagnostics has the reason."))
    }
}
