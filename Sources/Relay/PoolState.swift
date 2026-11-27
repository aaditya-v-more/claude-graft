import Foundation

/// Everything the relay knows, which is only what Graft last told it and what
/// the service has said since. Nothing here is written to disk: tokens least
/// of all, and a restarted relay passes every request through untouched until
/// Graft has told it again.
actor PoolState {
    struct Attempt: Equatable {
        /// Nil for a request from nobody in the pool, which goes out as sent.
        var member: String?
        /// Nil means the token the request arrived with.
        var token: String?
        /// The sender asked only because nobody else is left: it had no room.
        var lastResort = false
    }

    private struct Runtime {
        var served = 0
        var lastServed: Date?
        var lastSeen: Date?
        /// Digest of the lent token the service refused. Lending stops until
        /// Graft hands over a different one, which is what a renewed login is.
        var refusedToken: String?
    }

    private struct Conversation {
        var from: String
        var servedBy: String?
        var lastAt: Date
        var requests = 0
        var model: String?
        var passedOver: [PoolPass] = []
        /// Set by the first answer to a request carrying a thread. From then
        /// on only such answers say where the conversation is: a title asked
        /// for beside it, answered by another account, holds none of it, and
        /// letting it move `servedBy` sent the next turn to an account that
        /// answered `thread_not_found` and named the wrong one in the dropdown.
        var threaded = false
        var startedAt: Date?
        var promptedAt: Date?
    }

    /// Long enough for the menu to go on naming a conversation somebody stepped
    /// away from for the afternoon, short enough that the list stays the day's.
    static let conversationMemory: TimeInterval = 24 * 60 * 60

    private var configuration: PoolConfiguration?
    private var memberByDigest: [String: String] = [:]
    private var lendingDigest: [String: String] = [:]
    private var runtime: [String: Runtime] = [:]
    /// By account rather than by member: two members on one account are one
    /// allowance, and what the service says about either is true of both.
    private var limits: [String: PoolLimits] = [:]
    /// Accounts that said no to one family of models, by account and family,
    /// until when: a week of it spent, or for an hour a plan without it.
    private var shut: [String: [String: Date]] = [:]
    private var conversations: [String: Conversation] = [:]
    /// The sessions each one was started from, by session; see `PoolLineage`.
    private var launchers: [String: [String]] = [:]
    private var recent: [PoolRouting] = []
    private var active = 0

    private func accountKey(_ id: String) -> String {
        configuration?.members.first { $0.id == id }?.account ?? id
    }

    func configure(_ new: PoolConfiguration, now: Date = Date()) {
        configuration = new
        memberByDigest = [:]
        lendingDigest = [:]
        var kept: [String: Runtime] = [:]
        for member in new.members {
            for digest in member.identify where memberByDigest[digest] == nil {
                memberByDigest[digest] = member.id
            }
            var state = runtime[member.id] ?? Runtime()
            if let token = member.token {
                let digest = UsagePool.digest(token)
                lendingDigest[member.id] = digest
                if memberByDigest[digest] == nil { memberByDigest[digest] = member.id }
                if state.refusedToken != digest { state.refusedToken = nil }
            }
            // The service's own headers are fresher than any poll while the
            // account is in use, and a poll is fresher once it has gone quiet.
            let key = member.account ?? member.id
            if let pushed = member.limits, pushed.observed > (limits[key]?.observed ?? .distantPast) {
                let refused = limits[key]?.refusedUntil
                limits[key] = pushed
                if let refused, refused > now { limits[key]?.refusedUntil = refused }
            }
            if let models = member.limits?.models { limits[key]?.models = models }
            kept[member.id] = state
        }
        runtime = kept
        // An account nobody names any more is forgotten with its members, so
        // what is held stays the size of what Graft describes.
        let named = Set(new.members.map { $0.account ?? $0.id })
        limits = limits.filter { named.contains($0.key) }
        shut = shut.filter { named.contains($0.key) }
    }

    /// Which member a token belongs to. Every request carrying one counts as
    /// that Claude being connected, whatever the request was for.
    func member(for bearer: String, now: Date = Date()) -> String? {
        guard let id = memberByDigest[UsagePool.digest(bearer)] else { return nil }
        runtime[id, default: Runtime()].lastSeen = now
        return id
    }

    /// The sender first, then the accounts it was given, each account once. A
    /// lender on the sender's own account is no lender at all: borrowing from
    /// it spends the very allowance the request was going to spend anyway.
    /// An account given under two names is counted under the first that can
    /// lend: a Claude's own login lapses while it stays closed, and the same
    /// account signed in through Graft lasts a year.
    static func candidates(for sender: PoolMember, in members: [PoolMember],
                           canLend: (PoolMember) -> Bool = { $0.token != nil }) -> [PoolMember] {
        var out = [sender]
        var place: [String: Int] = [sender.account ?? sender.id: 0]
        for id in sender.borrowsFrom ?? [] {
            guard id != sender.id, let lender = members.first(where: { $0.id == id }) else { continue }
            let account = lender.account ?? lender.id
            if let index = place[account] {
                if index > 0, !canLend(out[index]), canLend(lender) { out[index] = lender }
            } else {
                place[account] = out.count
                out.append(lender)
            }
        }
        return out
    }

    /// `bound` is a request only the account that last answered the
    /// conversation can take as it stands: one carrying reasoning that account
    /// wrote (`PoolRequest.carriesBoundReasoning`), or continuing a thread
    /// stored there (`PoolRequest.continuesThread`).
    func plan(own: String, session: String?, pinned: Bool, bound: Bool = false,
              family: String? = nil, now: Date = Date()) -> [Attempt] {
        let asSent = [Attempt(member: own, token: nil)]
        guard let configuration, configuration.enabled, !pinned,
              let sender = configuration.members.first(where: { $0.id == own })
        else { return asSent }
        func canLend(_ member: PoolMember) -> Bool {
            member.token != nil && runtime[member.id]?.refusedToken != lendingDigest[member.id]
        }
        let candidates = Self.candidates(for: sender, in: configuration.members, canLend: canLend)
        guard candidates.count > 1 else { return asSent }
        let accounts = candidates.map { member in
            let key = member.account ?? member.id
            return PoolPolicy.Account(id: member.id, limits: limits[key],
                                      usable: member.id == own || canLend(member),
                                      shut: family.flatMap { shut[key]?[$0] }.map { $0 > now } ?? false)
        }
        // One account is one candidate whichever name it came under, so a
        // name the relay did not count stands for the one it did: a choice
        // made in the dropdown, and equally the name that last answered, once
        // a Claude's own login lapsed and the same account signed in here took
        // its place.
        func counted(_ id: String) -> String? {
            let account = configuration.members.first { $0.id == id }.map { $0.account ?? $0.id } ?? id
            return candidates.first { ($0.account ?? $0.id) == account }?.id
        }
        var chosen: String?
        var holder: String?
        if let session {
            chosen = configuration.assignments?[session].flatMap(counted)
            // A conversation stays with the account that answered it until
            // that account has no room, then stays wherever it went: its
            // cache and its reasoning are lost once rather than on every
            // move. A relay started since is told where by Graft.
            holder = (conversations[session]?.servedBy ?? configuration.holders?[session]).flatMap(counted)
                // A side chat carries the conversation it was asked beside,
                // reasoning and cache with it, so it begins where that one
                // is answered.
                ?? launchers[session]?.compactMap { origin in
                    (self.conversations[origin]?.servedBy ?? configuration.holders?[origin]
                        ?? configuration.assignments?[origin]).flatMap(counted)
                }.first
        }
        // Nothing says where a conversation nobody remembers wrote its
        // reasoning, and the sender's own account is where it began.
        let prefer = PoolPolicy.preferred(assigned: chosen, holder: holder, bound: bound, own: own)
        var ids = PoolPolicy.order(own: own, accounts: accounts, prefer: prefer,
                                   first: (configuration.first ?? []).compactMap(counted),
                                   ownFirst: configuration.ownFirst ?? false,
                                   family: family, now: now)
        // The last word is always the sender's own account, asked again if it
        // was asked already: whatever the lent accounts answered last — a
        // refused login, a plan without the model, a limit with their own
        // reset times — is about a login Claude Code never sent.
        let roomy = accounts.contains {
            $0.id == own && !$0.shut && PoolPolicy.hasRoom($0.limits, family: family, now: now)
        }
        if ids.last != own { ids.append(own) }
        return ids.enumerated().map { index, id in
            id == own ? Attempt(member: own, token: nil, lastResort: !roomy && index == ids.count - 1)
                : Attempt(member: id, token: candidates.first { $0.id == id }?.token)
        }
    }

    /// True until something says where `session` is answered or what started
    /// it, which is as long as its transcript is worth a look.
    func wantsLaunchers(of session: String) -> Bool {
        conversations[session] == nil && launchers[session] == nil && configuration?.holders?[session] == nil
    }

    func learn(launchers ids: [String], of session: String) { launchers[session] = ids }

    func observe(_ member: String, _ headers: [(String, String)], now: Date = Date()) {
        guard var parsed = PoolLimits.parse(headers, now: now) else { return }
        // An answer's headers say nothing of the windows one model has.
        parsed.models = limits[accountKey(member)]?.models
        limits[accountKey(member)] = parsed
    }

    /// How long a plan found without a model is taken to be without it.
    static let refusalMemory: TimeInterval = 60 * 60

    /// `family` is the model the request asked for, where what the account
    /// said is about that model alone; nil leaves a refusal unremembered.
    func apply(_ outcome: PoolPolicy.Outcome, to member: String, family: String? = nil,
               token: String? = nil, now: Date = Date()) {
        let key = accountKey(member)
        switch outcome {
        case .deliver:
            break
        case .refusedForAccount:
            if let family { shut[key, default: [:]][family] = now.addingTimeInterval(Self.refusalMemory) }
        case .modelExhausted(let until):
            if let family { shut[key, default: [:]][family] = until }
        case .exhausted(let until):
            var known = limits[key]
                ?? PoolLimits(fiveHour: 0, fiveHourReset: nil, week: 0, weekReset: nil,
                              refusedUntil: nil, observed: now)
            known.refusedUntil = until
            limits[key] = known
        case .loginRefused:
            // The token that was sent, which Graft may have replaced since.
            runtime[member, default: Runtime()].refusedToken = token.map(UsagePool.digest) ?? lendingDigest[member]
        }
    }

    func served(by member: String, from own: String, session: String?, status: Int,
                passedOver: [PoolPass], model: String?, threaded: Bool = false,
                starts: Bool = false, prompted: Bool = false, moves: Bool = true,
                now: Date = Date()) {
        runtime[member, default: Runtime()].served += 1
        runtime[member, default: Runtime()].lastServed = now
        if (200..<300).contains(status), let family = PoolRequest.family(of: model) {
            shut[accountKey(member)]?[family] = nil
        }
        if let session {
            var held = conversations[session]
                ?? Conversation(from: own, lastAt: now, startedAt: starts ? now : nil)
            held.from = own
            held.requests += 1
            if prompted { held.promptedAt = now }
            if threaded || !held.threaded {
                held.lastAt = now
                held.model = model ?? held.model
                held.passedOver = passedOver
                // A failed answer wrote no cache anywhere, so it moves nothing,
                // and nor does one found elsewhere while its account was only
                // out of reach.
                if (200..<300).contains(status) {
                    if moves || held.servedBy == nil { held.servedBy = member }
                    held.threaded = held.threaded || threaded
                }
            }
            conversations[session] = held
            if conversations.count > 256 {
                conversations = conversations.filter {
                    now.timeIntervalSince($0.value.lastAt) < Self.conversationMemory
                }
                launchers = launchers.filter { conversations[$0.key] != nil }
            }
        }
        recent.append(PoolRouting(at: now, from: own, servedBy: member, status: status,
                                  passedOver: passedOver, model: model, session: session))
        if recent.count > 40 { recent.removeFirst(recent.count - 40) }
    }

    func began() { active += 1 }
    func ended() { active -= 1 }
    var inFlight: Int { active }

    func status(binary: String, pid: Int32, started: Date, now: Date = Date()) -> PoolStatus {
        let members = (configuration?.members ?? []).map { member in
            let state = runtime[member.id]
            return PoolMemberStatus(id: member.id, limits: limits[member.account ?? member.id],
                                    served: state?.served ?? 0, lastServed: state?.lastServed,
                                    lastSeen: state?.lastSeen,
                                    tokenRefused: state?.refusedToken != nil)
        }
        let sessions = conversations
            .filter { now.timeIntervalSince($0.value.lastAt) < Self.conversationMemory }
            .sorted { $0.value.lastAt > $1.value.lastAt }
            .prefix(64)
            .map { id, held in
                PoolSession(id: id, from: held.from, servedBy: held.servedBy, lastAt: held.lastAt,
                            requests: held.requests, model: held.model,
                            passedOver: held.passedOver, startedAt: held.startedAt,
                            promptedAt: held.promptedAt)
            }
        return PoolStatus(binary: binary, pid: pid, started: started,
                          enabled: configuration?.enabled ?? false,
                          configured: configuration != nil, members: members,
                          recent: recent, sessions: Array(sessions), openExchanges: active)
    }
}
