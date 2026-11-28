import AppKit
import CoreServices
import Foundation

/// Which Claudes draw on which accounts. Every Claude has its own list, kept
/// by the Claude's key — `main` for Claude's own profile, a shortcut's id for
/// the rest — and a list can name other Claudes or accounts signed in through
/// Graft with no Claude of their own.
struct UsageSharing: Codable, Equatable {
    var lenders: [String: [String]] = [:]
    /// Conversations moved to an account by hand, by session.
    var assignments: [String: Assignment] = [:]
    /// Where each conversation was last answered, for a relay that starts
    /// again. Optional, so a file from before it was kept still reads.
    var holders: [String: Assignment]? = nil
    /// The accounts to use up first, first one first; nil goes by the resets.
    var first: [String]? = nil

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
    /// Nil on an account signed in before organizations were told apart.
    var organizationUuid: String? = nil
    var scopes: [String]
    var expires: Date
    var added: Date
    var sealed: String
    /// Its colour in Graft, given when it was added. One that is also a
    /// Claude here shows that Claude's colour instead: see `preset(of:)`.
    var iconPreset: Shortcut.IconPreset? = nil
    /// What a person called it; nil goes by its address.
    var name: String? = nil

    var allowance: Allowance? {
        accountUuid.map { Allowance(account: $0, organization: organizationUuid) }
    }

    /// Two organizations of one login would read as the same line, so each
    /// then says which it is.
    func title(among others: [PoolAccount]) -> String? {
        guard name == nil, let email, let organizationName,
              others.contains(where: { $0.id != id && $0.name == nil && $0.email == email })
        else { return name ?? email }
        return email + " · " + organizationName
    }
}

/// A login and the organization it was made in. One login can sit in two
/// organizations, and each has an allowance of its own.
struct Allowance: Equatable {
    var account: String
    var organization: String?

    /// An organization nobody has seen is taken for the other's.
    func isSame(as other: Allowance) -> Bool {
        account == other.account
            && (organization == nil || other.organization == nil || organization == other.organization)
    }

    /// What each goes by wherever accounts are counted: the login alone,
    /// until two organizations are seen under it.
    static func names(_ known: [String: Allowance]) -> [String: String] {
        var seen: [String: Set<String>] = [:]
        for one in known.values {
            if let organization = one.organization { seen[one.account, default: []].insert(organization) }
        }
        return known.mapValues { one in
            guard let organization = one.organization, (seen[one.account]?.count ?? 0) > 1
            else { return one.account }
            return one.account + "/" + organization
        }
    }
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
        /// Set for one the relay saw begin; see `PoolSession.startedAt`.
        var startedAt: Date? = nil
        /// When its Claude last showed it, from the record Claude keeps.
        var focusedAt: Date? = nil
        /// When a person last typed into it; see `PoolSession.promptedAt`.
        var promptedAt: Date? = nil
        /// False for one named from its record alone, which the relay has not
        /// carried since it began: no account has answered it, and none will
        /// until it is written in again.
        var carried = true

        /// The latest sign of someone looking at it. Claude writes
        /// `lastFocusedAt` the moment its window switches to a conversation,
        /// which is the one thing that says which is on screen; a Claude that
        /// reads another's chats writes no records, and there the last thing
        /// typed is the next best word.
        var attention: Date? { [focusedAt, promptedAt, startedAt].compactMap { $0 }.max() }
    }

    /// What the record of a conversation says about it. A conversation
    /// archived, or deleted from the sidebar, is one the person has put away,
    /// and the menu bar stops naming it.
    struct Filed: Equatable {
        var title: String?
        var archived = false
        var gone = false
        var focusedAt: Date?
    }

    /// The conversation Claude last brought on screen in one profile, read
    /// from the `lastFocusedAt` Claude writes into the records in its store.
    struct Focus: Equatable {
        var session: String
        var at: Date
        /// What the record says of it, which is all there is to name a
        /// conversation by when the relay has not carried it.
        var title: String? = nil
        /// Archived, or its record deleted.
        var putAway = false
    }

    /// A focus as one store held it, which is how the store that wrote it is
    /// told from the ones the mirror copied it into.
    private struct Stamp: Hashable {
        var session: String
        var at: Date
    }

    /// The conversation on screen in the Claude in front, which the menu bar
    /// names for as long as it is there.
    ///
    /// Claude's own word decides: the conversation its window focused last.
    /// One the relay has not carried, such as an old chat opened to read, is
    /// among `chats` by its record alone (`uncarried`). A focus matching none
    /// of them, because that conversation was put away, names nothing rather
    /// than the conversation before it, which is the one thing certain not to
    /// be on screen. A Claude whose records say nothing of focus falls back on
    /// the conversation typed into last, and only when nothing says even
    /// that, on the one that last heard back.
    static func current(_ chats: [Chat], focused: Focus? = nil, now: Date = Date()) -> Chat? {
        if let focused {
            if let shown = chats.first(where: { $0.id == focused.session }) { return shown }
            if chats.allSatisfy({ ($0.attention ?? .distantPast) < focused.at }) { return nil }
        }
        return chats
            .filter { now.timeIntervalSince($0.lastAt) < 30 * 60 }
            .max { a, b in
                let x = a.attention ?? .distantPast, y = b.attention ?? .distantPast
                return x != y ? x < y : a.lastAt < b.lastAt
            }
    }

    /// The account answering a conversation now: the one it was moved to,
    /// until that one has no room and another answers in its place, or else
    /// the one that answered last. Right after a move nothing has answered
    /// from there yet, and naming the account that answered before it read as
    /// though the choice had not taken.
    static func answeringKey(_ chat: Chat) -> String? {
        guard let assigned = chat.assigned, !chat.passedOver else { return chat.servedBy ?? chat.assigned }
        return assigned
    }

    /// "Automatic" alone did not say whether a conversation answered by Claude 2
    /// was there because it was chosen or because the ranking put it there, so
    /// the choice names the account it has settled on.
    func automaticTitle(_ chat: Chat) -> String {
        guard chat.assigned == nil, let served = chat.servedBy else { return L10n.text("Automatic") }
        return L10n.format("Automatic · now %@", name(of: served))
    }

    func current(front: URL?, now: Date = Date()) -> Chat? {
        guard let front, let status else { return nil }
        let key = PoolEnvironment.key(for: front)
        let mine = status.sessions.filter { session in
            isShown(session) && profile(of: session.from).map(PoolEnvironment.key(for:)) == key
        }
        let claude = claudes.first { profile(of: $0).map(PoolEnvironment.key(for:)) == key }
        let focus = claude.flatMap { focused[$0] }
        var chats = mine.map(chat(for:))
        if let claude, let focus, !lenders(of: claude).isEmpty,
           let alone = Self.uncarried(focus, in: claude, carried: status.sessions,
                                      choices: oneEach([claude] + lenders(of: claude)),
                                      assigned: sharing.assignments[focus.session]?.to) {
            chats.append(alone)
        }
        return Self.current(chats, focused: focus, now: now)
    }

    /// The conversation on screen when the relay has never carried it, or has
    /// forgotten it after a day: a chat from before sharing began, opened to
    /// read. Its record is the only thing that knows its name, and no account
    /// has answered it, so the bar says the name alone. One the relay did
    /// carry is left to its own row, which an archived or deleted one has
    /// already left; named from its record, it would be back on the bar.
    static func uncarried(_ focus: Focus, in claude: String, carried: [PoolSession],
                          choices: [String], assigned: String?) -> Chat? {
        guard !focus.putAway, !carried.contains(where: { $0.id == focus.session }) else { return nil }
        return Chat(id: focus.session, title: focus.title, claude: claude, servedBy: nil, assigned: assigned,
                    choices: choices, lastAt: focus.at, passedOver: false, focusedAt: focus.at, carried: false)
    }

    static let mainKey = "main"
    static let accountPrefix = "account:"

    static func key(for shortcut: Shortcut) -> String { shortcut.id.uuidString }

    @Published private(set) var sharing: UsageSharing
    @Published private(set) var accounts: [PoolAccount] {
        didSet {
            usage?.signedIn = accounts
            nameAllowances()
        }
    }
    /// What the service said of an account on the answer to its Start
    /// Session, by account: the one figure for a login nobody borrows from
    /// and the usage endpoint will not answer.
    @Published private(set) var heard: [String: PoolLimits] = [:]
    /// What an account's claude.ai sign-in said of it, by account: the
    /// figures of a login the usage endpoint will not answer. See WebUsage.
    @Published private(set) var seenOnWeb: [String: WebUsage.Held] = [:]
    /// The address each account signs in with, by account id, as a Claude's
    /// own login or a claude.ai sign-in told this Mac since launch.
    @Published private(set) var addresses: [String: String] = [:]
    @Published private(set) var status: PoolStatus?
    @Published private(set) var problem: String?
    /// Something is lent and this Graft cannot read the key the logins are
    /// sealed with, so the relay is left with what it was last told.
    @Published private(set) var lendingPaused = false
    @Published private(set) var lending: [String: Lending] = [:]
    /// By Claude key.
    @Published private(set) var environment: [String: ClaudeCodeEnvironment.State] = [:]
    /// By session.
    @Published private(set) var filed: [String: Filed] = [:]
    /// By Claude key.
    @Published private(set) var focused: [String: Focus] = [:]
    /// Which account each Claude is signed into, by key, as `Allowance.names`
    /// has it. A pass reads the logins on `queue` and a view only ever reads
    /// this.
    @Published private(set) var claudeAccounts: [String: String] = [:]
    private var claudeLogins: [String: Allowance] = [:]
    /// The same for the accounts signed in here.
    private var accountNames: [String: String] = [:]

    private weak var store: ShortcutStore?
    private weak var usage: UsageMonitor?
    private var timer: Timer?
    private var peekTimer: Timer?
    private var peeking = false
    private var peekAgain = false
    private lazy var storeWatch = StoreWatch { [weak self] in self?.peek() }
    private var watchers = 0
    private var syncing = false
    private var syncAgain = false
    private var observers: [NSObjectProtocol] = []
    // Drained after every block: the record walk runs here every two seconds
    // and on each change in a store, and with the default frequency the
    // Foundation objects it reads go on piling up until the thread retires,
    // which a queue this busy never lets it do.
    private let queue = DispatchQueue(label: "graft.usage-pool", autoreleaseFrequency: .workItem)
    /// Touched on `queue` alone.
    private var children: [pid_t: DispatchSourceProcess] = [:]
    /// Touched on `queue` alone: each Claude's last login read whole, by key.
    private var lastLogins: [String: (login: Login, at: Date)] = [:]
    private var lastSpawn = Date.distantPast
    /// `beside` names the record of the conversation a side chat was asked in.
    private var recordCache: [String: (stamp: Date, session: String?, filed: Filed, beside: String?)] = [:]
    /// Sessions a record was seen for, in their own Claude's store and in
    /// another's. One of them with no record any more was deleted from its
    /// sidebar; one never seen is too new to have one.
    private var everOwn: Set<String> = []
    private var everElsewhere: Set<String> = []
    private var missingBefore: Set<String> = []
    /// By Claude key.
    private var lastFocus: [String: Focus] = [:]
    /// Every focus a store holds, against the stores it was first seen in.
    private var focusOrigins: [Stamp: Set<String>] = [:]
    /// Claudes whose last focus had no record on the previous pass.
    private var focusMissingBefore: Set<String> = []
    private var transcriptTitles: [String: (stamp: Date, title: String?)] = [:]
    private var transcriptURLs: [String: URL] = [:]
    private var transcriptMisses: [String: Date] = [:]

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
        usage.signedIn = accounts
        usage.learned = { [weak self] in self?.learn(addresses: [$0: $1]) }
    }

    func start(store: ShortcutStore, usage: UsageMonitor) {
        attach(store: store, usage: usage)
        schedule()
        watchClaudes()
        sync()
        let peek = Timer(timeInterval: 2, repeats: true) { [weak self] _ in
            self?.followStores()
            self?.peek()
        }
        RunLoop.main.add(peek, forMode: .common)
        peekTimer = peek
    }

    /// The menu bar follows the conversation on screen, and Claude says which
    /// that is only in its records. Claude writes a record about a second
    /// after its window switches conversation, and with the records read every
    /// two seconds the bar trailed a switch by up to three; so the stores are
    /// watched as well, and a write there is read a tenth of a second later.
    /// The two-second look stays for the relay's news, which no file carries.
    /// A full pass reads logins as well, which is why it waits half a minute;
    /// this asks the relay what it carried and reads again only the records
    /// whose files changed.
    private func peek() {
        guard status != nil, sharing.lenders.values.contains(where: { !$0.isEmpty }) else { return }
        // A write that lands while records are being read may be the one that
        // matters, so it is read once the current look is done rather than
        // waiting out the next tick.
        guard !syncing, !peeking else { peekAgain = true; return }
        peeking = true
        let claudeProfiles = claudes.compactMap { key in profile(of: key).map { (key, $0) } }
        queue.async {
            let status = try? PoolSocket.send(PoolCommand())
            let records = status.map { self.lookUpRecords($0.sessions, profiles: claudeProfiles) }
            DispatchQueue.main.async {
                self.peeking = false
                // A relay that stopped answering is a full pass's business.
                if let status, let records, !self.syncing {
                    if self.status != status { self.status = status }
                    if self.filed != records.filed { self.filed = records.filed }
                    if self.focused != records.focused { self.focused = records.focused }
                }
                self.peekAgainIfAsked()
            }
        }
    }

    /// Asked again on every tick, so a Claude added or a store made since the
    /// last one is watched from the next, and here rather than in `peek`, which
    /// the watch itself calls and so must not tear the watch down.
    private func followStores() {
        let sharing = self.sharing.lenders.values.contains(where: { !$0.isEmpty })
        storeWatch.watch(sharing ? claudes.compactMap(profile(of:)).map(Self.recordStore(of:)) : [])
    }

    private func peekAgainIfAsked() {
        guard peekAgain else { return }
        peekAgain = false
        peek()
    }

    /// Where a Claude keeps its conversations' records, as FSEvents names it:
    /// the stream reports real paths, so a link on the way is resolved first.
    static func recordStore(of profile: URL) -> String {
        profile.appending(path: "claude-code-sessions").resolvingSymlinksInPath().path
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
        let login = account.allowance
        if let login, let index = accounts.firstIndex(where: { $0.allowance?.isSame(as: login) == true }) {
            stored.id = accounts[index].id
            stored.iconPreset = accounts[index].iconPreset
            stored.name = accounts[index].name
            accounts[index] = stored
        } else {
            // The login's other organization is already here under its id.
            if accounts.contains(where: { $0.id == stored.id }), let organization = account.organizationUuid {
                stored.id += "/" + organization
            }
            // One that is a Claude here takes that Claude's colour, and
            // keeps it should that Claude go.
            stored.iconPreset = login
                .flatMap { login in claudes.first { claudeLogins[$0]?.isSame(as: login) == true } }
                .map(preset(of:)) ?? Shortcut.IconPreset.next(besides: coloursInUse)
            accounts.append(stored)
        }
        saveAccounts()
        addLender(stored.id, to: claude)
        sync()
        askUsage(of: stored.id)
    }

    /// A new login in the old one's place: its name, its colour and the
    /// Claudes drawing on it stay as they were.
    func renew(_ id: String, with account: PoolAccount) {
        guard let index = accounts.firstIndex(where: { $0.id == id }) else { return }
        var stored = account
        stored.id = id
        stored.iconPreset = accounts[index].iconPreset
        stored.name = accounts[index].name
        stored.added = accounts[index].added
        accounts[index] = stored
        saveAccounts()
        sync()
        askUsage(of: id)
    }

    /// An empty name, or its own address, goes back to the address.
    func rename(_ id: String, to name: String) {
        guard let index = accounts.firstIndex(where: { $0.id == id }) else { return }
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        let wanted = trimmed.isEmpty || trimmed == accounts[index].email ? nil : trimmed
        guard accounts[index].name != wanted else { return }
        accounts[index].name = wanted
        saveAccounts()
    }

    func recolour(_ id: String, to preset: Shortcut.IconPreset) {
        guard let index = accounts.firstIndex(where: { $0.id == id }),
              accounts[index].iconPreset != preset else { return }
        accounts[index].iconPreset = preset
        saveAccounts()
    }

    /// Out of every Claude's list at once, which is what lets its login go.
    func remove(account id: String) {
        var next = sharing
        for claude in Array(next.lenders.keys) {
            next.lenders[claude]?.removeAll { $0 == id }
            if next.lenders[claude]?.isEmpty == true { next.lenders[claude] = nil }
        }
        save(next)
        forgetUnusedAccounts()
    }

    /// The Claudes drawing on an account.
    func borrowers(of lender: String) -> [String] {
        claudes.filter { lenders(of: $0).contains(lender) }
    }

    private func forgetUnusedAccounts() {
        let used = Set(sharing.lenders.values.flatMap { $0 })
        let kept = accounts.filter { used.contains($0.id) }
        guard kept.count != accounts.count else { return }
        accounts = kept
        heard = heard.filter { used.contains($0.key) }
        seenOnWeb = seenOnWeb.filter { used.contains($0.key) }
        saveAccounts()
    }

    /// Only an account's own login is renewed from here; a Claude's is
    /// renewed by opening that Claude.
    func needsSignIn(_ key: String) -> Bool {
        key.hasPrefix(Self.accountPrefix)
            && (status?.members.first { $0.id == key }?.tokenRefused == true || lending[key] == .expired)
    }

    /// Whether a lender can answer now, and if not what stands in the way.
    func condition(of key: String, now: Date = Date()) -> String {
        let isAccount = key.hasPrefix(Self.accountPrefix)
        let reported = status?.members.first { $0.id == key }
        if reported?.tokenRefused == true {
            return isAccount ? L10n.text("Login refused · sign in again")
                             : L10n.text("Login refused · open that Claude to renew it")
        }
        switch lending[key] {
        case nil:
            return L10n.text("Checking its login…")
        case .signedOut:
            return L10n.text("Not signed in")
        case .expired:
            return isAccount ? L10n.text("Login expired · sign in again")
                             : L10n.text("Login expired · open that Claude to renew it")
        case .unreadable:
            return L10n.text("Login locked · open the keychain from the menu bar")
        case .ready:
            break
        }
        if let limits = reported?.limits, !PoolPolicy.hasRoom(limits, now: now) {
            if let until = PoolPolicy.fullUntil(limits, now: now) {
                return L10n.format("Full until %@", UsageMonitor.clock(until, now: now))
            }
            return L10n.text("Full")
        }
        if let served = reported?.served, served > 0 {
            return L10n.format("Ready · answered %ld requests", served)
        }
        return L10n.text("Ready")
    }

    func needsAttention(_ key: String) -> Bool {
        status?.members.first { $0.id == key }?.tokenRefused == true
            || (lending[key] != nil && lending[key] != .ready)
    }

    // MARK: - A signed-in account's figures

    private func askUsage(of id: String, pressed: Bool = false) {
        guard let store else { return }
        usage?.invalidate(account: id)
        // A press has already asked the keychain once; this asks nobody.
        usage?.refresh(store, interactive: pressed, prompting: pressed ? .no : nil,
                       freshness: pressed ? .now : .recent, account: id)
    }

    /// The service's headers name a five-hour reset on every answer for an
    /// account that has the window, so one without a reset has no window.
    static func usage(from limits: PoolLimits, keeping prior: Graft.Usage? = nil) -> Graft.Usage {
        let fiveHour = limits.fiveHourReset == nil && limits.fiveHour == 0
            ? nil : Int((limits.fiveHour * 100).rounded())
        return Graft.Usage(fiveHour: fiveHour, week: Int((limits.week * 100).rounded()),
                           organization: nil, sampled: limits.observed,
                           fiveHourReset: limits.fiveHourReset, weekReset: limits.weekReset,
                           fable: prior?.fable, fableReset: prior?.fableReset)
    }

    /// One figure for an account signed in here, the newest of what the usage
    /// endpoint said, what a Claude on the same account shows and what the
    /// service said on that account's own answers.
    func entry(forAccount id: String) -> UsageMonitor.Entry? {
        guard accounts.contains(where: { $0.id == id }) else { return nil }
        var entry = usage?.entry(forAccount: id)
            ?? UsageMonitor.Entry(name: "", profile: UsageMonitor.Entry.noProfile, usage: nil,
                                  isRunning: false, account: id)
        entry.name = name(of: id)
        if let its = claude(sameAccountAs: id).flatMap(profile(of:)).flatMap({ usage?.entry(for: $0) }),
           its.usage != nil, (its.refreshed ?? .distantPast) > (entry.refreshed ?? .distantPast) {
            entry.usage = its.usage
            entry.isLive = its.isLive
            entry.plan = its.plan ?? entry.plan
            entry.refreshed = its.refreshed
        }
        if let web = seenOnWeb[id], web.fetched > (entry.refreshed ?? .distantPast) {
            let live = web.reading
            entry.usage = Graft.Usage(fiveHour: live.fiveHour, week: live.week, organization: nil,
                                      sampled: web.fetched, fiveHourReset: live.fiveHourReset,
                                      weekReset: live.weekReset, fable: live.fable, fableReset: live.fableReset)
            entry.isLive = true
            entry.plan = live.plan ?? entry.plan
            entry.refreshed = web.fetched
        }
        let answers = [status?.members.first { $0.id == id }?.limits, heard[id]].compactMap { $0 }
        for limits in answers where limits.observed > (entry.refreshed ?? .distantPast) {
            entry.usage = Self.usage(from: limits, keeping: entry.usage)
            entry.isLive = false
            entry.refreshed = limits.observed
        }
        return entry
    }

    func learn(web held: WebUsage.Held, for id: String) {
        guard accounts.contains(where: { $0.id == id }) else { return }
        seenOnWeb[id] = held
    }

    func learn(addresses found: [String: String]) {
        let next = addresses.merging(found) { $1 }
        if next != addresses { addresses = next }
    }

    /// The address an account signs in with, where this Mac has been told.
    func address(of key: String) -> String? {
        if let account = accounts.first(where: { $0.id == key }) {
            return account.email ?? account.accountUuid.flatMap { addresses[$0] }
        }
        guard let uuid = claudeLogins[key]?.account else { return nil }
        return addresses[uuid] ?? accounts.first { $0.accountUuid == uuid }?.email
    }

    /// The accounts a Claude draws on besides its own, in the order it asks.
    func drawnOn(by claude: String) -> [String] {
        Array(members(of: claude).dropFirst())
    }

    /// The accounts no Claude in a list draws on, which a list by Claude
    /// would otherwise leave out.
    func accountsAlone(besides claudes: [String]) -> [PoolAccount] {
        let shown = Set(claudes.flatMap(drawnOn(by:)).map { accountUuid(of: $0) ?? $0 })
        return accountsAlone.filter { !shown.contains(accountUuid(of: $0.id) ?? $0.id) }
    }

    /// One Auto-start switch to an account, wherever it is listed: an
    /// account that is also a Claude here goes by that Claude.
    func autoStartKey(of key: String) -> String {
        key.hasPrefix(Self.accountPrefix) ? claude(sameAccountAs: key) ?? key : key
    }

    /// Accounts with no Claude of their own here, which the list of Claudes
    /// does not already show.
    var accountsAlone: [PoolAccount] {
        accounts.filter { claude(sameAccountAs: $0.id) == nil }
    }

    /// The figures of whatever a key names: a Claude's own, or those of an
    /// account signed in here.
    func entry(of key: String) -> UsageMonitor.Entry? {
        key.hasPrefix(Self.accountPrefix) ? entry(forAccount: key)
            : profile(of: key).flatMap { usage?.entry(for: $0) }
    }

    /// Which of a Claude's accounts answers a conversation, by the name that
    /// Claude lists it under: the relay may know it by another of its names.
    static func answering(_ chat: Chat?, among members: [String], account: (String) -> String?) -> String? {
        guard let key = chat.flatMap(answeringKey) else { return nil }
        let wanted = account(key)
        return members.first { $0 == key || (wanted != nil && account($0) == wanted) }
    }

    /// The accounts being spent now: each one answering a conversation heard
    /// from in the last half hour, in a Claude that is open.
    static func spent(_ chats: [Chat], open: Set<String>, account: (String) -> String?,
                      now: Date = Date()) -> Set<String> {
        Set(chats.filter { open.contains($0.claude) && now.timeIntervalSince($0.lastAt) < 30 * 60 }
            .compactMap(answeringKey).map { account($0) ?? $0 })
    }

    /// Accounts with no Claude here, as the bar weighs them beside the Claudes.
    func accountsWeighed(now: Date = Date()) -> [UsageMonitor.Entry] {
        let open = Set((usage?.entries ?? []).filter(\.isRunning).map(\.key))
        let spent = Self.spent((status?.sessions ?? []).filter(isShown).map(chat(for:)), open: open,
                               account: accountUuid(of:), now: now)
        return accountsAlone.compactMap { account in
            guard var entry = entry(forAccount: account.id) else { return nil }
            entry.isRunning = spent.contains(accountUuid(of: account.id) ?? account.id)
            return entry
        }
    }

    /// What Graft itself has learnt of an account, for a relay that has sent
    /// it nothing yet.
    private func knownLimits(of id: String) -> PoolLimits? {
        let asked = usage?.entry(forAccount: id).flatMap { entry -> PoolLimits? in
            guard entry.isLive, let figures = entry.usage, let taken = entry.refreshed else { return nil }
            var limits = PoolLimits(fiveHourPercent: figures.fiveHour, fiveHourReset: figures.fiveHourReset,
                                    weekPercent: figures.week, weekReset: figures.weekReset, observed: taken)
            limits.models = Self.modelWindows(figures)
            return limits
        }
        // What was heard on an answer says nothing of one model's week, so
        // the last reading of that goes with it.
        var newest = [asked, heard[id]].compactMap { $0 }.max { $0.observed < $1.observed }
        if newest?.models == nil { newest?.models = asked?.models }
        return newest
    }

    /// The weeks that count one family of models alone, as the usage
    /// endpoint reports them, for the relay to pass an account over by.
    static func modelWindows(_ figures: Graft.Usage) -> [String: PoolLimits.Window]? {
        figures.fable.map { ["fable": PoolLimits.Window(used: Double($0) / 100, reset: figures.fableReset)] }
    }

    /// The order accounts are used up in, as somebody set it; empty goes by
    /// the weekly resets.
    func setOrder(_ keys: [String]) {
        var next = sharing
        next.first = keys.isEmpty ? nil : keys
        save(next)
    }

    var orderIsSet: Bool { sharing.first != nil }

    /// Claudes that exist, then the accounts that are none of them.
    var everyone: [String] {
        [Self.mainKey] + (store?.shortcuts ?? []).filter { $0.installedName != nil }.map(Self.key(for:))
            + accountsAlone.map(\.id)
    }

    func figures(of key: String) -> Graft.Usage? {
        key.hasPrefix(Self.accountPrefix) ? entry(forAccount: key)?.usage
            : profile(of: key).flatMap { usage?.entry(for: $0) }?.usage
    }

    /// The order in effect, which is the relay's: see `PoolPolicy.order`.
    func order(now: Date = Date()) -> [String] {
        Self.order(everyone, first: sharing.first ?? [], account: accountUuid(of:),
                   reset: { self.figures(of: $0)?.weekReset }, now: now)
    }

    static func order(_ keys: [String], first: [String], account: (String) -> String?,
                      reset: (String) -> Date?, now: Date) -> [String] {
        let named = first.map { account($0) ?? $0 }
        let ranked = keys.enumerated().map { index, key in
            (key: key, index: index, place: named.firstIndex(of: account(key) ?? key),
             reset: PoolPolicy.nextWeekReset(reset(key), now: now))
        }
        return ranked.sorted { a, b in
            if a.place != b.place { return (a.place ?? Int.max) < (b.place ?? Int.max) }
            if a.reset != b.reset { return (a.reset ?? .distantFuture) < (b.reset ?? .distantFuture) }
            return a.index < b.index
        }.map(\.key)
    }

    /// Puts one account at a place, which makes the whole order somebody's.
    func move(_ key: String, to place: Int) {
        setOrder(Self.moved(key, to: place, in: order()))
    }

    static func moved(_ key: String, to place: Int, in keys: [String]) -> [String] {
        var keys = keys
        guard let from = keys.firstIndex(of: key), keys.indices.contains(place) else { return keys }
        keys.insert(keys.remove(at: from), at: place)
        return keys
    }

    /// Only ever from a Start Session button, as `SessionStarter` asks. The
    /// answer is nil when the window is open, or what went wrong.
    func startSession(account id: String, done: @escaping (String?) -> Void) {
        guard let account = accounts.first(where: { $0.id == id }) else {
            return done(L10n.text("That account's login could not be read on this Mac."))
        }
        DispatchQueue.global(qos: .userInitiated).async {
            let outcome = SessionStarter.start(account: account, interactive: true)
            DispatchQueue.main.async {
                self.heardFromSession(account: id, limits: outcome.limits, pressed: true)
                done(outcome.failure?.errorDescription)
            }
        }
    }

    /// What a session's answer said of an account, pressed for or not.
    func heardFromSession(account id: String, limits: PoolLimits?, pressed: Bool = false) {
        if let limits { heard[id] = limits }
        askUsage(of: id, pressed: pressed)
        sync()
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
        // Not before the relay has been heard from: with nothing to go by,
        // every choice older than a day looked like one nobody was using.
        if let status {
            let active = Set(status.sessions.filter { now.timeIntervalSince($0.lastAt) < 86_400 }.map(\.id))
            next.assignments = next.assignments.filter {
                active.contains($0.key) || now.timeIntervalSince($0.value.at) < 86_400
            }
        }
        if let first = next.first {
            let kept = first.filter(known.contains)
            next.first = kept.isEmpty ? nil : kept
        }
        let held = Self.holders(next.holders ?? [:], hearing: status?.sessions ?? [], known: known)
        next.holders = held.isEmpty ? nil : held
        guard next != sharing else { return }
        persist(next)
        forgetUnusedAccounts()
    }

    static let holdersKept = 256

    /// What the relay says of each conversation it carried, kept for the next
    /// relay. A date moves only once it is an hour out, so a conversation in
    /// use is not a file written on every pass.
    static func holders(_ kept: [String: UsageSharing.Assignment], hearing sessions: [PoolSession],
                        known: Set<String>) -> [String: UsageSharing.Assignment] {
        var next = kept.filter { known.contains($0.value.to) }
        for session in sessions {
            guard let account = session.servedBy, known.contains(account) else { continue }
            if let had = next[session.id], had.to == account, session.lastAt.timeIntervalSince(had.at) < 3600 { continue }
            next[session.id] = UsageSharing.Assignment(to: account, at: session.lastAt)
        }
        guard next.count > holdersKept else { return next }
        let newest = next.sorted { $0.value.at != $1.value.at ? $0.value.at > $1.value.at : $0.key < $1.key }
        return Dictionary(uniqueKeysWithValues: newest.prefix(holdersKept).map { ($0.key, $0.value) })
    }

    // MARK: - Names

    func profile(of key: String) -> URL? {
        if key == Self.mainKey { return Graft.mainProfile }
        return UUID(uuidString: key).flatMap { store?.shortcut($0) }?.profileDir
    }

    func name(of key: String) -> String {
        if key == Self.mainKey { return "Claude" }
        if let account = accounts.first(where: { $0.id == key }) {
            return account.title(among: accounts) ?? L10n.text("Signed-in account")
        }
        return UUID(uuidString: key).flatMap { store?.shortcut($0) }?.name ?? L10n.text("Removed shortcut")
    }

    // MARK: - Colours

    /// The colour a Claude or an account is drawn in. An account that is also
    /// a Claude on this machine is that Claude, so it wears that Claude's
    /// colour and follows it when the colour changes.
    func preset(of key: String) -> Shortcut.IconPreset {
        if key == Self.mainKey { return .original }
        if let id = UUID(uuidString: key) { return store?.shortcut(id)?.iconPreset ?? .original }
        if let claude = claude(sameAccountAs: key) { return preset(of: claude) }
        return accounts.first { $0.id == key }?.iconPreset ?? .graphite
    }

    func claude(sameAccountAs key: String) -> String? {
        guard let uuid = accountUuid(of: key) else { return nil }
        return claudes.first { claudeAccounts[$0] == uuid }
    }

    /// Every colour something here is already drawn in, Claude's own first.
    var coloursInUse: [Shortcut.IconPreset] {
        [.original] + (store?.shortcuts ?? []).map(\.iconPreset)
            + accounts.filter { claude(sameAccountAs: $0.id) == nil }.compactMap(\.iconPreset)
    }

    /// Accounts added before accounts had colours are given one, once, in the
    /// order they were added.
    private func colourAccounts() {
        guard store != nil, accounts.contains(where: { $0.iconPreset == nil }) else { return }
        for index in accounts.indices.sorted(by: { accounts[$0].added < accounts[$1].added })
        where accounts[index].iconPreset == nil {
            accounts[index].iconPreset = claude(sameAccountAs: accounts[index].id).map(preset(of:))
                ?? Shortcut.IconPreset.next(besides: coloursInUse)
        }
        saveAccounts()
    }

    /// The Claude a profile folder belongs to, by its key here.
    func claudeKey(for profile: URL?) -> String? {
        guard let profile else { return nil }
        let wanted = PoolEnvironment.key(for: profile)
        return claudes.first { self.profile(of: $0).map(PoolEnvironment.key(for:)) == wanted }
    }

    /// The allowance a Claude or an account draws on, by the name it is
    /// counted under: two keys with one answer are one allowance.
    func accountUuid(of key: String) -> String? {
        if let account = accounts.first(where: { $0.id == key }) {
            return accountNames[key] ?? account.accountUuid
        }
        return claudeAccounts[key]
    }

    func allowance(of key: String) -> Allowance? {
        accounts.first { $0.id == key }?.allowance ?? claudeLogins[key]
    }

    private func nameAllowances() {
        var known = claudeLogins
        for account in accounts { known[account.id] = account.allowance }
        let names = Allowance.names(known)
        let ours = names.filter { claudeLogins[$0.key] != nil }
        if claudeAccounts != ours { claudeAccounts = ours }
        accountNames = names.filter { claudeLogins[$0.key] == nil }
    }

    /// Off the main thread: every one of these is a file read.
    static func readAccounts(_ claudes: [(String, URL)]) -> [String: String] {
        var found: [String: String] = [:]
        for (key, profile) in claudes { found[key] = Graft.account(of: profile) }
        return found
    }

    func learnAccounts(_ found: [String: Allowance]) {
        if claudeLogins != found {
            claudeLogins = found
            nameAllowances()
        }
        // After the Claudes' accounts are known, so an account that is one of
        // them takes its colour rather than using up a free one.
        colourAccounts()
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
            now.timeIntervalSince(session.lastAt) < 30 * 60 && isShown(session)
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
            .map(chat(for:))
    }

    /// A conversation archived or deleted in its sidebar has been put away,
    /// and the menu bar stops naming it; so has one in a Claude that no
    /// longer draws on anybody.
    private func isShown(_ session: PoolSession) -> Bool {
        !lenders(of: session.from).isEmpty && filed[session.id].map { !$0.archived && !$0.gone } != false
    }

    private func chat(for session: PoolSession) -> Chat {
        let choice = sharing.assignments[session.id]
        let assigned = choice?.to
        return Chat(id: session.id, title: filed[session.id]?.title, claude: session.from,
                    servedBy: session.servedBy, assigned: assigned,
                    choices: oneEach([session.from] + lenders(of: session.from)),
                    lastAt: session.lastAt,
                    passedOver: Self.passedOver(session, choice: choice, account: accountUuid(of:)),
                    startedAt: session.startedAt,
                    focusedAt: filed[session.id]?.focusedAt,
                    promptedAt: session.promptedAt)
    }

    /// Whether the account chosen for a conversation is not the one answering
    /// it. An account with no room is left out before it is asked, so nothing
    /// is passed over then, and only an answer newer than the choice says so.
    static func passedOver(_ session: PoolSession, choice: UsageSharing.Assignment?,
                           account: (String) -> String? = { _ in nil }) -> Bool {
        guard let choice, let answered = session.servedBy,
              (account(answered) ?? answered) != (account(choice.to) ?? choice.to) else { return false }
        return !session.passedOver.isEmpty || session.lastAt > choice.at
    }

    /// Everyone a Claude draws on, itself first, one name to an account.
    func members(of claude: String) -> [String] {
        lenders(of: claude).isEmpty ? [] : oneEach([claude] + lenders(of: claude))
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

    /// The dropdown's button while lending is paused. Pressing it is a person
    /// asking, which is the one thing that may put the keychain dialog up for
    /// a Graft whose new signature the item has never seen; a pass of its own
    /// only ever reads silently. The figures follow without asking again.
    func openKeychain(interactive: Bool) {
        let asking: ClaudeCredentials.Prompting = interactive ? .yes : .no
        queue.async {
            _ = try? ClaudeCredentials.safeStorageKey(prompting: asking)
            DispatchQueue.main.async {
                self.sync()
                if let store = self.store { self.usage?.refresh(store, prompting: .no, freshness: .recent) }
            }
        }
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
        // Sorted, because two shortcuts on one profile keep the first seen,
        // and a dictionary's order changes from one launch to the next.
        for (claude, list) in sharing.lenders.sorted(by: { $0.key < $1.key }) where !list.isEmpty {
            for key in [claude] + list where !involved.contains(key) { involved.append(key) }
        }
        var seen = Set<String>()
        // Two shortcuts can stand for one profile; lending it twice would count
        // one account's room twice.
        let inputs = involved.compactMap { key -> Input? in
            if let account = accounts.first(where: { $0.id == key }) {
                return Input(key: key, name: account.title(among: accounts) ?? key, profile: nil, account: account,
                             borrowsFrom: [], limits: knownLimits(of: key))
            }
            guard let profile = profile(of: key),
                  seen.insert(PoolEnvironment.key(for: profile)).inserted else { return nil }
            var limits: PoolLimits?
            if let entry = usage.entry(for: profile), let figures = entry.usage, let taken = entry.refreshed {
                limits = PoolLimits(fiveHourPercent: figures.fiveHour, fiveHourReset: figures.fiveHourReset,
                                    weekPercent: figures.week, weekReset: figures.weekReset, observed: taken)
                limits?.models = Self.modelWindows(figures)
            }
            return Input(key: key, name: name(of: key), profile: profile, account: nil,
                         borrowsFrom: sharing.lenders[key] ?? [], limits: limits)
        }
        // Every Claude Graft knows of, so one that stopped borrowing has its
        // lines taken out as well as one that started has them put in.
        let claudeProfiles = claudes.compactMap { key in profile(of: key).map { (key, $0) } }
        // The organization each Claude last ran under, which no login says.
        var inUse: [String: String] = [:]
        for (key, profile) in claudeProfiles {
            inUse[key] = usage.entry(for: profile)?.usage?.organization?.lowercased()
        }
        let assignments = sharing.assignments.mapValues(\.to)
        let holders = sharing.holders?.mapValues(\.to)
        let first = sharing.first

        queue.async {
            var lending: [String: Lending] = [:]
            let signedInto = Self.readAccounts(claudeProfiles)
            // Shut is not missing: no item at all is a Mac with no logins to
            // lend, while a shut one has them and this build may not read them
            // yet, which every update does to an ad-hoc signed app.
            var key: Data?
            var keyShut = false
            do { key = try ClaudeCredentials.safeStorageKey(prompting: .no) } catch { keyShut = true }
            var logins: [String: Login] = [:]
            for input in inputs {
                guard let profile = input.profile else { continue }
                var login = Self.login(of: profile, inUse: inUse[input.key])
                if !keyShut {
                    if login.state != .unreadable { self.lastLogins[input.key] = (login, Date()) }
                    login = Self.steady(login, kept: self.lastLogins[input.key])
                }
                logins[input.key] = login
            }
            var claudeLogins: [String: Allowance] = [:]
            for (claude, account) in signedInto {
                claudeLogins[claude] = Allowance(account: account,
                                                 organization: logins[claude]?.organization ?? inUse[claude])
            }
            var known = claudeLogins
            for account in accounts { known[account.id] = account.allowance }
            let allowances = Allowance.names(known)
            let members = inputs.map { input -> PoolMember in
                if let account = input.account {
                    let token = key.flatMap { ClaudeCredentials.unseal(account.sealed, key: $0) }
                        .map { String(decoding: $0, as: UTF8.self) }
                    let current = token != nil && account.expires > Date().addingTimeInterval(120)
                    lending[input.key] = token == nil ? .unreadable : current ? .ready : .expired
                    return PoolMember(id: input.key, name: input.name, account: allowances[input.key],
                                      identify: token.map { [UsagePool.digest($0)] } ?? [],
                                      token: current ? token : nil, limits: input.limits, borrowsFrom: nil)
                }
                let login = logins[input.key] ?? (.unreadable, [], nil, nil)
                lending[input.key] = login.state
                return PoolMember(id: input.key, name: input.name,
                                  account: allowances[input.key],
                                  identify: login.identify, token: login.token, limits: input.limits,
                                  borrowsFrom: input.borrowsFrom)
            }
            let configuration = PoolConfiguration(enabled: true, members: members, assignments: assignments,
                                                  holders: holders, first: first)
            let journal = PoolEnvironment.readJournal()
            let wanted = !members.isEmpty || !journal.profiles.isEmpty
            let paused = keyShut && !members.isEmpty
            var (status, problem) = self.deliver(configuration, wanted: wanted, complete: !keyShut)

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
                    (status, problem) = self.deliver(configuration, wanted: true, complete: true)
                }
            } else if wanted, key == nil {
                problem = problem ?? (paused
                    ? L10n.text("Lending is paused until the keychain is opened")
                    : L10n.text("Graft needs keychain access to set up Claude Code for sharing."))
            }

            let records = self.lookUpRecords(status?.sessions ?? [], profiles: claudeProfiles)
            DispatchQueue.main.async {
                self.learnAccounts(claudeLogins)
                // Only what changed: every assignment redraws whatever is
                // watching, and most passes change nothing.
                if self.lending != lending { self.lending = lending }
                if self.lendingPaused != paused { self.lendingPaused = paused }
                if self.status != status { self.status = status }
                if self.problem != problem { self.problem = problem }
                if self.environment != environment { self.environment = environment }
                if self.filed != records.filed { self.filed = records.filed }
                if self.focused != records.focused { self.focused = records.focused }
                self.syncing = false
                if self.syncAgain {
                    self.syncAgain = false
                    self.sync()
                }
                self.peekAgainIfAsked()
            }
        }
    }

    typealias Login = (state: Lending, identify: [String], token: String?, organization: String?)

    /// A login that could not be read this pass, with the key in hand, is a
    /// `config.json` caught as Claude renames a new one over it. Sent as it
    /// stands, it took the member's identity out of the relay for a pass, so
    /// the last one read stands in for it — for ten minutes, after which a
    /// file still unreadable is not a rename and the token in hand may be one
    /// Claude has replaced.
    static func steady(_ login: Login, kept: (login: Login, at: Date)?, now: Date = Date()) -> Login {
        guard login.state == .unreadable, let kept, now.timeIntervalSince(kept.at) < 10 * 60 else { return login }
        return kept.login
    }

    /// Never prompts. The usage monitor is what asks for keychain access, once,
    /// and the key it gets is the one every read in this process shares.
    private static func login(of profile: URL, inUse: String?) -> Login {
        do {
            guard let all = try ClaudeCredentials.tokens(for: profile, prompting: .no) else {
                return (.signedOut, [], nil, nil)
            }
            let lent = lent(from: all, inUse: inUse)
            return (lent == nil ? .expired : .ready, all.map { UsagePool.digest($0.value) }, lent?.value,
                    lent?.organization)
        } catch ClaudeCredentials.Failure.notSignedIn {
            return (.signedOut, [], nil, nil)
        } catch {
            return (.unreadable, [], nil, nil)
        }
    }

    /// A profile that has been in two organizations holds a login for each,
    /// and the one lent is the one its Claude Code is using.
    static func lent(from all: [ClaudeCredentials.Token], inUse: String?) -> ClaudeCredentials.Token? {
        let candidates = ClaudeCredentials.inUse(all.filter(\.isCurrent), organization: inUse)
        return candidates.filter { $0.scopes.contains(UsagePool.lendingScope) }
            .max { $0.expires < $1.expires }
            ?? candidates.filter { $0.covers([ClaudeCredentials.inferenceScope]) }
            .max { $0.expires < $1.expires }
    }

    // MARK: - Records

    /// What each conversation's record in a sidebar says, found by the session
    /// id every request carries, which is the record's `cliSessionId`. Records
    /// are read again only when their files change. On `queue`.
    func lookUpRecords(_ sessions: [PoolSession], profiles: [(String, URL)], now: Date = Date())
        -> (filed: [String: Filed], focused: [String: Focus]) {
        // Every conversation the relay remembers, not only the last half
        // hour's: the one on screen may have been quiet for longer.
        let from = Dictionary(sessions.map { ($0.id, $0.from) }, uniquingKeysWith: { first, _ in first })
        // Every record each store holds, by session: the one with the newest
        // focus where a session has a record under two accounts.
        var held: [String: [String: Filed]] = [:]
        // A conversation is put away in the Claude it is in; the same session
        // listed by another profile that shares its chats says nothing of that.
        var own: [String: [Filed]] = [:]
        var elsewhere: [String: [Filed]] = [:]
        var markers: Set<String> = []
        var walked: Set<String> = []
        // By Claude key: each record's session, and every side chat's focus.
        var named: [String: [String: String]] = [:]
        var asides: [String: [(session: String, beside: String, at: Date)]] = [:]
        // A store that would not open is not an empty one, and what it holds
        // is not gone; nothing is judged missing on such a pass.
        var unreadable = false
        let fm = FileManager.default
        func list(_ url: URL) -> [String] {
            do {
                return try fm.contentsOfDirectory(atPath: url.path)
            } catch let error as CocoaError where error.code == .fileReadNoSuchFile {
                return []
            } catch {
                unreadable = true
                return []
            }
        }
        for (key, profile) in profiles {
            let store = profile.appending(path: "claude-code-sessions")
            // Walked as the sweep walks it: a stash holds what a profile had
            // before it was grafted, and a deleted conversation's old record
            // there would keep it on the bar.
            for account in list(store) where !account.hasPrefix(".") && !account.hasSuffix(Graft.stashSuffix)
                && !Graft.nonAccountStoreItems.contains(account) {
                let accountDir = store.appending(path: account)
                guard Graft.isDirectory(accountDir) else { continue }
                for org in list(accountDir) where !org.hasPrefix(".") && !org.hasSuffix(Graft.stashSuffix) {
                    let orgDir = accountDir.appending(path: org)
                    guard Graft.isDirectory(orgDir) else { continue }
                    // A path and one lstat for each record, since this runs
                    // over every record of every Claude every two seconds:
                    // seven hundred on the development machine.
                    let orgPath = orgDir.path
                    for name in list(orgDir) { autoreleasepool {
                        if name.hasPrefix("deleted_") { markers.insert(String(name.dropFirst(8))); return }
                        guard name.hasPrefix("local_"), name.hasSuffix(".json") else { return }
                        let path = orgPath + "/" + name
                        walked.insert(path)
                        let stamp = Graft.modificationDate(atPath: path) ?? .distantPast
                        var entry = recordCache[path]
                        if entry?.stamp != stamp {
                            // The first look reads every record of every Claude whole.
                            let fresh = autoreleasepool { () -> (stamp: Date, session: String?, filed: Filed,
                                                                 beside: String?) in
                                let record = (try? Data(contentsOf: URL(fileURLWithPath: path)))
                                    .flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] }
                                let focused = (record?["lastFocusedAt"] as? NSNumber)
                                    .map { Date(timeIntervalSince1970: $0.doubleValue / 1000) }
                                let spawned = record?["spawnedFrom"] as? [String: Any]
                                return (stamp, record?["cliSessionId"] as? String,
                                        Filed(title: (record?["title"] as? String).flatMap { $0.isEmpty ? nil : $0 },
                                              archived: record?["isArchived"] as? Bool ?? false,
                                              focusedAt: focused),
                                        spawned?["btw"] as? Bool == true ? spawned?["sessionId"] as? String : nil)
                            }
                            entry = fresh
                            recordCache[path] = fresh
                        }
                        guard let entry, let session = entry.session else { return }
                        var filed = entry.filed
                        named[key, default: [:]][String(name.dropLast(5))] = session
                        if let beside = entry.beside, let at = filed.focusedAt {
                            asides[key, default: []].append((session, beside, at))
                            filed.focusedAt = nil
                        }
                        if filed.focusedAt ?? .distantPast >= held[key]?[session]?.focusedAt ?? .distantPast {
                            held[key, default: [:]][session] = filed
                        }
                        guard let owner = from[session] else { return }
                        if owner == key {
                            own[session, default: []].append(filed)
                            everOwn.insert(session)
                        } else {
                            elsewhere[session, default: []].append(filed)
                            everElsewhere.insert(session)
                        }
                    } }
                }
            }
        }
        recordCache = recordCache.filter { walked.contains($0.key) }

        // A side chat is a pane of the conversation it was asked beside, and
        // Claude focuses the two together in either order, so its focus is
        // that conversation's. With that one deleted it stands for itself.
        for (key, sides) in asides {
            for side in sides {
                if let main = named[key]?[side.beside], let before = held[key]?[main] {
                    if side.at > before.focusedAt ?? .distantPast { held[key]?[main]?.focusedAt = side.at }
                } else {
                    held[key]?[side.session]?.focusedAt = side.at
                }
            }
        }

        // A focus belongs to the store it turned up in first. The mirror
        // carries a record across with its focus, and read as this store's
        // own, the copy named the conversation another Claude had shown as
        // the one on screen here. One that several stores held when it was
        // first seen, as everything is on the first pass, counts for each.
        var origins: [Stamp: Set<String>] = [:]
        for (key, records) in held {
            for (session, record) in records {
                guard let at = record.focusedAt else { continue }
                let stamp = Stamp(session: session, at: at)
                if let first = focusOrigins[stamp] {
                    origins[stamp] = first
                } else {
                    origins[stamp, default: []].insert(key)
                }
            }
        }
        focusOrigins = origins
        var focused: [String: Focus] = [:]
        for (key, records) in held {
            for (session, record) in records {
                // Two records with the same focus, as a mirrored pair carries,
                // must pick the same one every pass. Left to the order the
                // dictionary happens to iterate in, the bar swapped between
                // them every two seconds and drew itself again each time.
                guard let at = record.focusedAt, origins[Stamp(session: session, at: at)]?.contains(key) == true
                else { continue }
                if let current = focused[key],
                   at < current.at || (at == current.at && session >= current.session) { continue }
                focused[key] = Focus(session: session, at: at, title: record.title, putAway: record.archived)
            }
        }

        // Deleting the conversation on screen takes its record, and with it
        // the latest focus in the store; the one focused before it is not
        // what Claude shows now, so the focus that went stays the word until
        // Claude writes a newer one. So does a focus the mirror wrote over
        // with another Claude's, since this Claude still shows that
        // conversation. A store that would not open keeps its last focus too.
        var focusMissing: Set<String> = []
        for (key, last) in lastFocus where profiles.contains(where: { $0.0 == key })
            && focused[key].map({ $0.at < last.at }) ?? true {
            var kept = last
            if let record = held[key]?[last.session] {
                kept.title = record.title ?? kept.title
                kept.putAway = record.archived
            } else if !unreadable {
                // Deleted, but only once it is missing on two passes running:
                // a pass woken by Claude's write can land before the record is
                // moved into place.
                focusMissing.insert(key)
                if focusMissingBefore.contains(key) { kept.putAway = true }
            }
            focused[key] = kept
        }
        if !unreadable { focusMissingBefore = focusMissing }
        for (key, focus) in focused where focus.title == nil && !focus.putAway {
            focused[key]?.title = transcriptTitle(focus.session, now: now)
        }
        lastFocus = focused

        var found: [String: Filed] = [:]
        var missing: Set<String> = []
        for session in from.keys {
            // Its own Claude's record decides. A copy in a store that shares
            // its chats lingers until the next pass that mirrors them, and
            // read first it kept a deleted conversation on the bar.
            let mine = own[session] ?? []
            let records = mine.isEmpty ? elsewhere[session] ?? [] : mine
            var filed = Filed(title: records.lazy.compactMap(\.title).first,
                              archived: !records.isEmpty && records.allSatisfy(\.archived),
                              focusedAt: records.compactMap(\.focusedAt).max())
            // A marker names a session only for a record Graft filed; one
            // Claude named itself is known gone by having been seen before
            // and missing on two passes running, since a file rewritten in
            // place can be caught between the two.
            let vanished = !unreadable && (mine.isEmpty && everOwn.contains(session)
                || records.isEmpty && everElsewhere.contains(session))
            if vanished { missing.insert(session) }
            filed.gone = markers.contains(session) || vanished && missingBefore.contains(session)
            if filed.title == nil { filed.title = transcriptTitle(session, now: now) }
            found[session] = filed
        }
        if !unreadable { missingBefore = missing }
        return (found, focused)
    }

    /// Read again only when the transcript has grown, since a conversation
    /// on screen that has no record would otherwise have its last quarter
    /// megabyte read every two seconds; and looked for again only once a
    /// minute when there is none, since the relay remembers a day of them.
    /// On `queue`.
    private func transcriptTitle(_ session: String, now: Date) -> String? {
        let fm = FileManager.default
        let url: URL
        if let known = transcriptURLs[session], fm.fileExists(atPath: known.path) {
            url = known
        } else {
            if let missed = transcriptMisses[session], now.timeIntervalSince(missed) < 60 { return nil }
            guard let found = Self.transcript(of: session) else {
                transcriptMisses[session] = now
                return nil
            }
            transcriptMisses[session] = nil
            transcriptURLs[session] = found
            url = found
        }
        let stamp = (try? fm.attributesOfItem(atPath: url.path))?[.modificationDate] as? Date ?? .distantPast
        if let known = transcriptTitles[session], known.stamp == stamp { return known.title }
        let title = Self.transcriptTitle(at: url)
        transcriptTitles[session] = (stamp, title)
        return title
    }

    static func transcript(of session: String) -> URL? {
        let fm = FileManager.default
        let projects = Graft.claudeProjects
        return ((try? fm.contentsOfDirectory(atPath: projects.path)) ?? [])
            .lazy
            .map { projects.appending(path: $0).appending(path: "\(session).jsonl") }
            .first { fm.fileExists(atPath: $0.path) }
    }

    /// A conversation that has no record yet still has its transcript, and
    /// the command line writes the title it settles on there as it goes.
    static func transcriptTitle(_ session: String) -> String? {
        transcript(of: session).flatMap(transcriptTitle(at:))
    }

    static func transcriptTitle(at url: URL) -> String? {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }
        let size = (try? handle.seekToEnd()) ?? 0
        try? handle.seek(toOffset: size > 262_144 ? size - 262_144 : 0)
        let tail = String(decoding: handle.readDataToEndOfFile(), as: UTF8.self)
        for key in ["\"customTitle\":\"", "\"aiTitle\":\""] {
            guard let open = tail.range(of: key, options: .backwards) else { continue }
            // Up to the first quote no backslash escapes, and read as JSON: a
            // title with a quote in it was cut off at the quote, and one with
            // a dash came out as its escape.
            var escaped = false
            guard let close = tail[open.upperBound...].indices.first(where: { index in
                defer { escaped = !escaped && tail[index] == "\\" }
                return !escaped && tail[index] == "\""
            }) else { continue }
            let literal = Data(tail[tail.index(before: open.upperBound)...close].utf8)
            if let title = try? JSONSerialization.jsonObject(with: literal, options: .fragmentsAllowed) as? String,
               !title.isEmpty { return title }
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

    /// What answered on the relay's socket.
    enum RelayReply: Equatable {
        case running(binary: String)
        /// Something answered in a shape this build cannot read: a relay from
        /// a release whose status this one does not know.
        case unreadable
        case nobody
    }

    /// What becomes of the relay that is there before a pass's configuration
    /// goes to it.
    enum RelayStep: Equatable { case keep, replace, start }

    /// A relay keeps the logins it was last given for as long as it runs, and
    /// a new one knows none until it is told. So one from another build is
    /// replaced only by a Graft that can tell its successor everything: after
    /// an update this Graft is often shut out of the keychain, and replacing
    /// the old relay then swapped lending that worked for a relay that knew
    /// nobody. One whose reply cannot be read is a relay all the same; it used
    /// to be left running while another was started every twenty seconds and
    /// exited on its lock.
    static func relayStep(_ reply: RelayReply, bundled: String, canConfigure: Bool) -> RelayStep {
        switch reply {
        case .nobody:
            return .start
        case .running(let binary) where binary == bundled:
            return .keep
        case .running, .unreadable:
            return canConfigure ? .replace : .keep
        }
    }

    /// Blocking; not on the main thread.
    static func ask(_ command: PoolCommand,
                    at path: String = UsagePool.socketFile.path) -> (status: PoolStatus?, reply: RelayReply) {
        do {
            let status = try PoolSocket.send(command, to: path)
            return (status, .running(binary: status.binary))
        } catch PoolSocket.Failure.garbled {
            return (nil, .unreadable)
        } catch {
            return (nil, .nobody)
        }
    }

    /// On `queue`. `complete` is false while the key cannot be read, when
    /// every login in `configuration` reads as missing: such a configuration
    /// is never sent, since a relay told it forgets every login it was
    /// lending, and the relay that is there is left with what it has. With no
    /// relay at all one is still started, because a Claude pointed at a relay
    /// that is not there has no Claude Code; it passes everything through as
    /// it came until a pass can tell it more.
    private func deliver(_ configuration: PoolConfiguration, wanted: Bool,
                         complete: Bool) -> (PoolStatus?, String?) {
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
        let command = complete ? PoolCommand(configure: configuration) : PoolCommand()
        let (status, reply) = Self.ask(command)
        switch Self.relayStep(reply, bundled: expected, canConfigure: complete) {
        case .keep:
            return (status, nil)
        case .replace:
            Diagnostics.note("relay replaced", ["running": status.map { String($0.binary.prefix(12)) } ?? "unreadable",
                                                "bundled": String(expected.prefix(12))])
            // Bare, because every relay since 2.0.0 reads a quit whatever else
            // has changed. Answered once the old relay has let go of its port
            // and its lock, so the new one can start straight away.
            _ = try? PoolSocket.send(PoolCommand(quit: true))
        case .start:
            break
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
            if let status = try? PoolSocket.send(command) {
                return (status, nil)
            }
        }
        return (nil, L10n.text("The relay is not running. Diagnostics has the reason."))
    }
}

/// Says when anything in the given folders changes, a tenth of a second after
/// it does, so a write is read when it lands rather than on the next tick.
/// FSEvents gathers a burst of writes into one call, Claude's write and its
/// rename alike. Graft's own writes are left out: the sweep files records and
/// the mirror copies them, and neither says which conversation is on screen.
final class StoreWatch {
    private var stream: FSEventStreamRef?
    private var watched: [String] = []
    private let changed: () -> Void

    init(changed: @escaping () -> Void) {
        self.changed = changed
    }

    /// Main thread. A folder that does not exist yet is left out until it
    /// does, which the next call finds.
    func watch(_ folders: [String]) {
        // Two Claudes can keep their records in one place.
        var seen = Set<String>()
        let wanted = folders.filter { seen.insert($0).inserted && Graft.isDirectory(URL(fileURLWithPath: $0)) }
        guard wanted != watched else { return }
        stop()
        watched = wanted
        guard !wanted.isEmpty else { return }
        var context = FSEventStreamContext(version: 0, info: Unmanaged.passUnretained(self).toOpaque(),
                                           retain: nil, release: nil, copyDescription: nil)
        let callback: FSEventStreamCallback = { _, info, _, _, _, _ in
            guard let info else { return }
            Unmanaged<StoreWatch>.fromOpaque(info).takeUnretainedValue().changed()
        }
        guard let stream = FSEventStreamCreate(kCFAllocatorDefault, callback, &context, wanted as CFArray,
                                               FSEventStreamEventId(kFSEventStreamEventIdSinceNow), 0.1,
                                               FSEventStreamCreateFlags(kFSEventStreamCreateFlagIgnoreSelf))
        else {
            watched = []
            return
        }
        // On the main queue, where the watch is started and stopped and where
        // the pass it wakes is asked for.
        FSEventStreamSetDispatchQueue(stream, .main)
        FSEventStreamStart(stream)
        self.stream = stream
    }

    private func stop() {
        guard let stream else { return }
        FSEventStreamStop(stream)
        FSEventStreamInvalidate(stream)
        FSEventStreamRelease(stream)
        self.stream = nil
        watched = []
    }

    deinit {
        stop()
    }
}
