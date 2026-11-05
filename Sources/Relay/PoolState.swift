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
        var cacheLifetime: TimeInterval = PoolPolicy.shortCache
        var passedOver: [PoolPass] = []
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
    private var conversations: [String: Conversation] = [:]
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
            kept[member.id] = state
        }
        runtime = kept
        // An account nobody names any more is forgotten with its members, so
        // what is held stays the size of what Graft describes.
        let named = Set(new.members.map { $0.account ?? $0.id })
        limits = limits.filter { named.contains($0.key) }
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

    /// `bound` is a request carrying reasoning only the account that wrote it
    /// can read back (`PoolRequest.carriesBoundReasoning`).
    func plan(own: String, session: String?, pinned: Bool, bound: Bool = false,
              now: Date = Date()) -> [Attempt] {
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
            PoolPolicy.Account(id: member.id, limits: limits[member.account ?? member.id],
                               usable: member.id == own || canLend(member))
        }
        var prefer: [String] = []
        if let session {
            let held = conversations[session]
            // One account is one candidate whichever name it came under, so a
            // choice of a name the relay did not count is a choice of the one
            // it did.
            if let chosen = configuration.assignments?[session] {
                let account = configuration.members.first { $0.id == chosen }.map { $0.account ?? $0.id } ?? chosen
                if let counted = candidates.first(where: { ($0.account ?? $0.id) == account }) {
                    prefer.append(counted.id)
                }
            }
            if let last = held?.servedBy,
               bound || now.timeIntervalSince(held!.lastAt) < held!.cacheLifetime {
                // Reasoning outlives the cache: it is lost on any other account
                // however long the conversation sat. So it stays until its
                // account has no room, and then stays wherever it went, which
                // loses the reasoning once rather than on every move. A person
                // moving it from the dropdown is that one move, made on purpose.
                prefer.append(last)
            }
        }
        // Nothing says where a conversation the relay has not seen wrote its
        // reasoning — one from before the relay last started, or one with no
        // session — and the sender's own account is where it began.
        if bound, prefer.isEmpty { prefer.append(own) }
        return PoolPolicy.order(own: own, accounts: accounts, prefer: prefer, now: now).map { id in
            id == own ? Attempt(member: own, token: nil)
                : Attempt(member: id, token: candidates.first { $0.id == id }?.token)
        }
    }

    func observe(_ member: String, _ headers: [(String, String)], now: Date = Date()) {
        guard let parsed = PoolLimits.parse(headers, now: now) else { return }
        limits[accountKey(member)] = parsed
    }

    func apply(_ outcome: PoolPolicy.Outcome, to member: String, now: Date = Date()) {
        switch outcome {
        case .deliver, .refusedForAccount:
            break
        case .exhausted(let until):
            let key = accountKey(member)
            var known = limits[key]
                ?? PoolLimits(fiveHour: 0, fiveHourReset: nil, week: 0, weekReset: nil,
                              refusedUntil: nil, observed: now)
            known.refusedUntil = until
            limits[key] = known
        case .loginRefused:
            runtime[member, default: Runtime()].refusedToken = lendingDigest[member]
        }
    }

    func served(by member: String, from own: String, session: String?, status: Int,
                passedOver: [PoolPass], model: String?, cacheLifetime: TimeInterval,
                now: Date = Date()) {
        runtime[member, default: Runtime()].served += 1
        runtime[member, default: Runtime()].lastServed = now
        if let session {
            var held = conversations[session] ?? Conversation(from: own, lastAt: now)
            held.from = own
            held.lastAt = now
            held.requests += 1
            held.model = model ?? held.model
            held.passedOver = passedOver
            // A failed answer wrote no cache anywhere, so it moves nothing.
            if (200..<300).contains(status) {
                held.servedBy = member
                held.cacheLifetime = cacheLifetime
            }
            conversations[session] = held
            if conversations.count > 256 {
                conversations = conversations.filter {
                    now.timeIntervalSince($0.value.lastAt) < Self.conversationMemory
                }
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
                            cachedUntil: held.servedBy == nil ? nil
                                : held.lastAt.addingTimeInterval(held.cacheLifetime),
                            passedOver: held.passedOver)
            }
        return PoolStatus(binary: binary, pid: pid, started: started,
                          enabled: configuration?.enabled ?? false,
                          configured: configuration != nil, members: members,
                          recent: recent, sessions: Array(sessions), openExchanges: active)
    }
}
