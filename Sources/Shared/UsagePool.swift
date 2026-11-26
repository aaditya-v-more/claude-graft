import CryptoKit
import Foundation

/// One Claude goes on being itself — its chats, artifacts, connectors and
/// Claude in Chrome all stay with the account it is signed into — while the
/// model requests its Claude Code makes are answered by whichever of the
/// accounts it was given has room.
///
/// The route in is the one setting Claude passes through to Claude Code
/// untouched: a proxy. Claude replaces the base URL and the token every time it
/// starts a session, so neither can be pointed anywhere, but `HTTPS_PROXY` and
/// `NODE_EXTRA_CA_CERTS` survive, and both live in Claude's own Code
/// environment, where Graft puts them. The relay answers that proxy, opens only
/// the traffic meant for `api.anthropic.com`, and changes exactly one thing in
/// it: the `Authorization` line of `POST /v1/messages`. Every other request —
/// artifacts, settings, telemetry, sessions — leaves with the token it arrived
/// with, which is why the Claude doing the work keeps its own identity
/// everywhere else.
enum UsagePool {
    /// Fixed, because it is written into a Claude's environment while that
    /// Claude is closed, and a session already running goes on using whatever
    /// it read when it started.
    static let port: UInt16 = 47823
    static let host = "api.anthropic.com"

    /// Short on purpose: the control socket lives here, and a Unix socket path
    /// longer than 103 bytes cannot be bound at all.
    static var directory: URL { Graft.applicationSupport.appending(path: "ClaudeGraft/pool") }
    static var certificateFile: URL { directory.appending(path: "relay-ca.pem") }
    static var leafFile: URL { directory.appending(path: "relay-leaf.pem") }
    static var identityFile: URL { directory.appending(path: "relay-identity.p12") }
    static var socketFile: URL { directory.appending(path: "relay.sock") }
    /// Which Claude borrows from which, and the conversations moved by hand.
    static var settingsFile: URL { Graft.applicationSupport.appending(path: "ClaudeGraft/usage-sharing.json") }
    /// Accounts signed in through Graft, their tokens sealed.
    static var accountsFile: URL { directory.appending(path: "accounts.json") }

    /// The identity file sits in a folder only this user can open, so this
    /// protects nothing; the import API refuses an empty one.
    static let identityPassphrase = "claude-graft-relay"

    /// The scope the token Claude hands Claude Code carries, which makes it the
    /// one to lend: the service has already accepted it for exactly this.
    static let lendingScope = "user:sessions:claude_code"

    /// Tokens are recognised by digest, so the relay can say which member a
    /// request came from without keeping a list of anybody's tokens it was not
    /// handed to lend.
    static func digest(_ token: String) -> String {
        digest(data: Data(token.utf8))
    }

    static func digest(data: Data) -> String {
        SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }
}

// MARK: - Limits

/// What an account has left, as the service last reported it. Utilisation is a
/// fraction, the way the response headers carry it, not a percentage.
struct PoolLimits: Codable, Equatable {
    var fiveHour: Double
    var fiveHourReset: Date?
    var week: Double
    var weekReset: Date?
    /// Set when the service said no outright, until the time it named.
    var refusedUntil: Date?
    var observed: Date
    /// Weekly windows that count one family of models alone, by family. Only
    /// the usage endpoint reports them, so only Graft's readings carry them.
    var models: [String: Window]? = nil

    struct Window: Codable, Equatable {
        var used: Double
        var reset: Date?
    }

    /// Every answer from `/v1/messages` carries the account's unified limits,
    /// so the relay learns an account's state from the very requests it sends
    /// there. Nil when the answer carried none, which is any request that
    /// never reached the model.
    static func parse(_ headers: [(String, String)], now: Date = Date()) -> PoolLimits? {
        func value(_ name: String) -> String? {
            headers.first { $0.0.caseInsensitiveCompare(name) == .orderedSame }?.1
        }
        func date(_ name: String) -> Date? {
            value(name).flatMap(Double.init).map { Date(timeIntervalSince1970: $0) }
        }
        let prefix = "anthropic-ratelimit-unified-"
        let five = value(prefix + "5h-utilization").flatMap(Double.init)
        let week = value(prefix + "7d-utilization").flatMap(Double.init)
        let status = value(prefix + "status")
        guard five != nil || week != nil || status != nil else { return nil }
        var limits = PoolLimits(fiveHour: five ?? 0,
                                fiveHourReset: date(prefix + "5h-reset"),
                                week: week ?? 0,
                                weekReset: date(prefix + "7d-reset"),
                                refusedUntil: nil,
                                observed: now)
        // A week of one model spent leaves the account open to every other.
        if status == "rejected", !PoolPolicy.countsOneModel(value(prefix + "representative-claim")) {
            limits.refusedUntil = date(prefix + "reset") ?? now.addingTimeInterval(60)
        }
        return limits
    }

    /// A reading from the usage endpoint, which reports percentages. An
    /// account with no five-hour window can never be full on one, so it counts
    /// as nothing spent there.
    init(fiveHourPercent: Int?, fiveHourReset: Date?, weekPercent: Int, weekReset: Date?, observed: Date) {
        self.init(fiveHour: Double(fiveHourPercent ?? 0) / 100, fiveHourReset: fiveHourReset,
                  week: Double(weekPercent) / 100, weekReset: weekReset,
                  refusedUntil: nil, observed: observed)
    }

    init(fiveHour: Double, fiveHourReset: Date?, week: Double, weekReset: Date?,
         refusedUntil: Date?, observed: Date, models: [String: Window]? = nil) {
        self.fiveHour = fiveHour
        self.fiveHourReset = fiveHourReset
        self.week = week
        self.weekReset = weekReset
        self.refusedUntil = refusedUntil
        self.observed = observed
        self.models = models
    }
}

// MARK: - Choosing an account

/// Which account answers a request, and in what order to fall back.
///
/// The account whose week resets soonest goes first, because what it has left
/// is the usage that expires soonest. An account is passed over once its
/// five-hour window or its week is all but spent, and stays passed over until
/// that window resets: utilisation only ever rises inside a window, so there
/// is no point at which it comes back early. The account a request came from is
/// always the last resort — when nobody has room, the request goes out exactly
/// as Claude sent it and the service decides.
enum PoolPolicy {
    /// Short of a hundred because a request is checked against the limit as it
    /// starts and not as it runs, so the last one in at 99% goes through and
    /// the one after is refused. A refusal is caught and sent on to the next
    /// account anyway; the margin only saves the round trip.
    static let fiveHourCeiling = 0.98
    static let weekCeiling = 0.99

    struct Account: Equatable {
        var id: String
        var limits: PoolLimits?
        /// Holds a token that has not been refused.
        var usable: Bool
        /// Said no to the model being asked for, and may yet take another:
        /// its week of that model spent, or a plan without it.
        var shut = false
    }

    /// The limits that count one family of models alone, as the service
    /// names them when it refuses: `seven_day_opus`, `seven_day_sonnet`, and
    /// `seven_day_overage_included` for the models with a week of their own.
    /// Read from Claude Code 2.1.330, which matches them to models this way.
    static func countsOneModel(_ claim: String?) -> Bool {
        claim?.hasPrefix("seven_day_") == true
    }

    /// A window that reset since the reading was taken starts again from
    /// nothing, and the week after it resets on the same weekday.
    static func nextWeekReset(_ reset: Date?, now: Date) -> Date? {
        guard var reset else { return nil }
        while reset <= now { reset += 7 * 24 * 60 * 60 }
        return reset
    }

    static func hasRoom(_ limits: PoolLimits?, family: String? = nil, now: Date) -> Bool {
        guard let limits else { return true }
        if let until = limits.refusedUntil, until > now { return false }
        if limits.fiveHour >= fiveHourCeiling, (limits.fiveHourReset ?? .distantFuture) > now { return false }
        if limits.week >= weekCeiling, (limits.weekReset ?? .distantFuture) > now { return false }
        if let family, let window = limits.models?[family], window.used >= weekCeiling,
           (window.reset ?? .distantFuture) > now { return false }
        return true
    }

    /// When a full account has room again: the last of the windows shut on
    /// it, and nil when one of them did not say.
    static func fullUntil(_ limits: PoolLimits, now: Date) -> Date? {
        var shut: [Date?] = []
        if let until = limits.refusedUntil, until > now { shut.append(until) }
        if limits.fiveHour >= fiveHourCeiling, (limits.fiveHourReset ?? .distantFuture) > now {
            shut.append(limits.fiveHourReset)
        }
        if limits.week >= weekCeiling, (limits.weekReset ?? .distantFuture) > now { shut.append(limits.weekReset) }
        return shut.contains(nil) ? nil : shut.compactMap { $0 }.max()
    }

    /// The accounts to try, best first. The one the request came from is in
    /// there wherever it ranked, or last when it has no room either, since then
    /// the request goes out as Claude sent it and the service decides. An
    /// account nobody has heard from yet goes after every account with a known
    /// reset: all that is known about it is that it has not said no.
    ///
    /// `prefer` goes in front of the ranking, first one first, for as long as
    /// each still has room: see `preferred`. `first` is the order somebody
    /// asked for, which comes before the resets for the accounts it names.
    /// Room is room for `family`, the model being asked for: an account out
    /// of one model, or without it, is passed over for that model alone.
    /// `ownFirst` puts the sender ahead of the ranking while it has room, so
    /// a conversation begins in the Claude it was opened in.
    static func order(own: String, accounts: [Account], prefer: [String] = [], first: [String] = [],
                      ownFirst: Bool = false, family: String? = nil, now: Date) -> [String] {
        let open = accounts.filter { $0.usable && !$0.shut && hasRoom($0.limits, family: family, now: now) }
        var ids = open.sorted { a, b in
            let pa = first.firstIndex(of: a.id) ?? Int.max, pb = first.firstIndex(of: b.id) ?? Int.max
            if pa != pb { return pa < pb }
            let ra = nextWeekReset(a.limits?.weekReset, now: now)
            let rb = nextWeekReset(b.limits?.weekReset, now: now)
            switch (ra, rb) {
            case let (x?, y?) where x != y: return x < y
            case (_?, nil): return true
            case (nil, _?): return false
            default:
                // Equal, or both unknown: staying on the account the request
                // came from costs nothing, so it wins the tie.
                if (a.id == own) != (b.id == own) { return a.id == own }
                return a.id < b.id
            }
        }.map(\.id)
        for id in (ownFirst ? [own] : []) + prefer.reversed() {
            if let index = ids.firstIndex(of: id) { ids.insert(ids.remove(at: index), at: 0) }
        }
        if !ids.contains(own) { ids.append(own) }
        return ids
    }

    /// Who goes in front of the ranking for a conversation: the account
    /// somebody chose for it, then the one that answered it last, whatever
    /// its model and however long it sat. A request only its writer can read,
    /// with nobody known to have answered, stays with its sender.
    static func preferred(assigned: String?, holder: String?, bound: Bool, own: String) -> [String] {
        var prefer = [assigned, holder].compactMap { $0 }
        if bound, holder == nil { prefer.append(own) }
        return prefer
    }

    /// What to make of an answer, decided from its status line and headers
    /// alone, before a byte of it reaches Claude Code.
    enum Outcome: Equatable {
        case deliver
        /// The account has no room. Try the next one.
        case exhausted(until: Date)
        /// It has none for this model, and may for another.
        case modelExhausted(until: Date)
        /// A borrowed token was refused. It is not offered again until Graft
        /// hands over a different one.
        case loginRefused
        /// The borrowed account cannot serve this particular request — a model
        /// or feature its plan lacks. It goes back to the account it came from.
        case refusedForAccount
    }

    static func outcome(status: Int, headers: [(String, String)], borrowed: Bool, now: Date) -> Outcome {
        switch status {
        case 429:
            func value(_ name: String) -> String? {
                headers.first { $0.0.caseInsensitiveCompare(name) == .orderedSame }?.1
            }
            let prefix = "anthropic-ratelimit-unified-"
            let named = value(prefix + "reset").flatMap(Double.init).map { Date(timeIntervalSince1970: $0) }
            let until = value(prefix + "status") == "rejected"
                ? named ?? now.addingTimeInterval(60)
                : now.addingTimeInterval(value("retry-after").flatMap(Double.init) ?? 60)
            return countsOneModel(value(prefix + "representative-claim"))
                ? .modelExhausted(until: until) : .exhausted(until: until)
        case 401:
            return borrowed ? .loginRefused : .deliver
        // One `where` would bind to the last pattern alone and send a 400 on
        // the account the request came from round again.
        case 400, 403, 404:
            return borrowed ? .refusedForAccount : .deliver
        default:
            return .deliver
        }
    }
}

// MARK: - Requests

enum PoolRequest {
    /// The one request worth moving. `count_tokens` and everything under
    /// `/api/` stay with the account that sent them.
    static func isInference(method: String, target: String) -> Bool {
        method == "POST" && target.split(separator: "?", maxSplits: 1).first == "/v1/messages"
    }

    /// File and server-side MCP references belong to their original account.
    /// Containers are checked at the API envelope: artifact tools use the same
    /// argument name inside conversation history without binding inference.
    static let accountBoundMarks = [
        "\"file_id\":\"",
        "\"mcp_servers\":[{",
    ]

    static func mustStayWithSender(body: Data, contentEncoding: String?) -> Bool {
        if let contentEncoding, !contentEncoding.isEmpty,
           contentEncoding.lowercased() != "identity" { return true }
        guard let request = (try? JSONSerialization.jsonObject(with: body)) as? [String: Any] else { return true }
        // Artifact tools also call their argument "container"; only the API's
        // top-level container belongs to the account handling inference.
        if let container = request["container"] as? String, !container.isEmpty { return true }
        if let container = request["container"] as? [String: Any], container["id"] is String { return true }
        return accountBoundMarks.contains { body.range(of: Data($0.utf8)) != nil }
    }

    /// Reasoning the model wrote earlier in a conversation is carried back in
    /// every later request, and for some models the service honours it only
    /// from the account that wrote it: from any other it is dropped without a
    /// word, the answer still a 200. A request with none of it loses nothing
    /// anywhere — a new conversation, or one just compacted — so only one that
    /// carries some is held. Matched with quotes and colon like the keys above,
    /// so the same words quoted in a prompt do not count.
    static let reasoningKeys = ["\"type\":\"thinking\"", "\"type\":\"redacted_thinking\""]

    /// The models whose reasoning was measured surviving a move between
    /// accounts in different organizations. Any other is taken to be bound,
    /// since holding one that was not costs a little sharing and moving one
    /// that was costs its reasoning on every move.
    static let reasoningTravels = ["claude-opus-5-5"]

    static func carriesBoundReasoning(body: Data) -> Bool {
        guard reasoningKeys.contains(where: { body.range(of: Data($0.utf8)) != nil }) else { return false }
        guard let model = model(in: body) else { return true }
        return !reasoningTravels.contains { model.hasPrefix($0) }
    }

    /// Claude Code keeps a conversation as a thread on the service and, once
    /// one exists, sends only the turns since the message it names: the rest
    /// is stored with the account that answered it. Any other account answers
    /// `thread_not_found`, and Claude Code then sends the whole conversation
    /// again as a new thread. Seen on this machine with Claude Code 2.1.317,
    /// where every moved continuation came back 404 from the account it was
    /// moved to. `JSON.stringify` writes `type` first, as the object is built.
    static let threadContinuation = "\"thread\":{\"type\":\"continue\""
    static let threadKey = "\"thread\":{"

    static func continuesThread(body: Data) -> Bool {
        body.range(of: Data(threadContinuation.utf8)) != nil
    }

    /// A conversation's first message carries no answer yet. Every later
    /// request, and every short one Claude Code asks beside an existing
    /// conversation, carries the turns so far.
    static func startsConversation(body: Data) -> Bool {
        body.range(of: Data("\"role\":\"assistant\"".utf8)) == nil
    }

    /// A turn a person typed: one on the conversation's thread whose last user
    /// message holds no tool's answer. Claude Code carries on after every tool
    /// with a user message that is nothing but `tool_result`s, and the short
    /// requests it makes beside a conversation carry no thread; a lone message
    /// with no thread is as likely a title asked for as a first message, and a
    /// first message is marked as begun anyway. A message typed while a tool
    /// was running rides along with that tool's answer and is missed; the next
    /// one typed is not.
    static func startsTurn(body: Data) -> Bool {
        guard carriesThread(body: body),
              let last = body.range(of: Data("\"role\":\"user\"".utf8), options: .backwards)
        else { return false }
        return body[last.upperBound...].range(of: Data("\"type\":\"tool_result\"".utf8)) == nil
    }

    /// The conversation's own turns carry a thread; the short requests Claude
    /// Code makes beside them — a title, a summary — share its session id and
    /// carry none.
    static func carriesThread(body: Data) -> Bool {
        body.range(of: Data(threadKey.utf8)) != nil
    }

    /// The service's word for a thread it does not hold, which Claude Code
    /// answers by starting the thread again with everything in it.
    static let threadMissing = Data("thread_not_found".utf8)

    /// A plan that sells the model asked for apart answers 429 and names no
    /// limit, as a busy service does. Matched as Claude Code 2.1.331 matches it.
    static func wantsCredits(_ refusal: Data) -> Bool {
        guard let answer = (try? JSONSerialization.jsonObject(with: refusal)) as? [String: Any],
              let error = answer["error"] as? [String: Any] else { return false }
        if (error["details"] as? [String: Any])?["error_code"] as? String == "credits_required" { return true }
        let message = (error["message"] as? String)?.lowercased() ?? ""
        return message.contains("usage credits are required") || message.contains("extra usage is required")
    }

    /// For the record of what went where. The first `"model":` is the request's
    /// own, Claude Code writing it ahead of the messages; a wrong guess here
    /// mislabels one line on screen and decides nothing.
    static func model(in body: Data) -> String? {
        let key = Data("\"model\":\"".utf8)
        guard let start = body.range(of: key)?.upperBound else { return nil }
        let window = body[start..<min(start + 120, body.endIndex)]
        guard let end = window.firstIndex(of: UInt8(ascii: "\"")) else { return nil }
        return String(decoding: body[start..<end], as: UTF8.self)
    }

    /// What a weekly window of its own counts: `claude-opus-5-5` is `opus`,
    /// whatever version or suffix follows.
    static func family(of model: String?) -> String? {
        guard let model = model?.lowercased() else { return nil }
        let named = model.split(separator: "-").first { $0 != "claude" && $0.first?.isLetter == true }
        return named.map(String.init) ?? model
    }

    static func bearer(in headers: [(String, String)]) -> String? {
        guard let value = headers.first(where: { $0.0.caseInsensitiveCompare("authorization") == .orderedSame })?.1,
              value.count > 7, value.prefix(7).lowercased() == "bearer "
        else { return nil }
        return String(value.dropFirst(7)).trimmingCharacters(in: .whitespaces)
    }
}

// MARK: - Talking to the relay

/// One account as the relay is told about it: a Claude, or an account signed in
/// through Graft with no Claude of its own. Tokens travel only over the relay's
/// own socket, in a folder only this user can open, and live only in its
/// memory: a relay that restarts knows nothing until Graft tells it again, and
/// until then it passes every request through untouched.
struct PoolMember: Codable, Equatable {
    var id: String
    var name: String
    /// The account behind it. Two members on one account are one allowance,
    /// so a Claude never borrows from its own account under another name, and
    /// what the service says about one is true of both.
    var account: String?
    /// Digests of every current token the profile holds. Claude hands Claude
    /// Code one of these, which is how a request is traced to its profile.
    var identify: [String]
    /// The token lent to other members' requests. Nil when the login could not
    /// be read, which leaves a Claude able to borrow but not to lend.
    var token: String?
    /// What Graft last read from the usage endpoint, so an account the relay
    /// has not sent anything to yet is not a blank.
    var limits: PoolLimits?
    /// The members whose accounts may answer this one's Claude Code. Each
    /// Claude has its own list; nobody borrows from an account it was not given.
    var borrowsFrom: [String]?
}

struct PoolConfiguration: Codable, Equatable {
    var enabled: Bool
    var members: [PoolMember]
    /// Conversations somebody moved by hand, by session, to the member that
    /// should answer them. Kept by Graft and handed over with everything else,
    /// so a relay that restarts still knows.
    var assignments: [String: String]?
    /// Where each conversation was last answered, as the relay itself said,
    /// for a relay that has started since and holds no record of its own.
    var holders: [String: String]?
    /// The accounts somebody wants used up first, first one first. Any not
    /// named follow in the order of their weekly resets.
    var first: [String]?
    /// A new conversation begins on its sender's account while that has room.
    var ownFirst: Bool?
}

struct PoolCommand: Codable {
    var configure: PoolConfiguration?
    /// Asks a relay built from a different binary to step aside: it stops
    /// listening at once and leaves when its last exchange finishes.
    var quit: Bool?
}

struct PoolPass: Codable, Equatable {
    enum Reason: String, Codable {
        /// Out of room in the five-hour window or the week.
        case full
        /// Its login was refused.
        case login
        /// It could not serve this request: a model or feature its plan lacks.
        case refused
        /// It could not be reached at all.
        case unreachable
    }
    var id: String
    var reason: Reason
}

struct PoolRouting: Codable, Equatable {
    var at: Date
    var from: String
    var servedBy: String
    var status: Int
    /// Members tried first that said no, in order.
    var passedOver: [PoolPass]
    var model: String?
    var session: String?
}

/// A conversation the relay has carried, by Claude Code's session id.
struct PoolSession: Codable, Equatable {
    var id: String
    /// The member whose Claude the conversation is in.
    var from: String
    /// The account that answered it last. Nil until one has.
    var servedBy: String?
    var lastAt: Date
    var requests: Int
    var model: String?
    /// Members passed over on its last request, so a conversation answered by
    /// somebody other than the account chosen for it can say why.
    var passedOver: [PoolPass]
    /// When it began, for one the relay saw from its first message. Nil for
    /// one already under way when the relay first saw it — a relay restarted
    /// mid-conversation sees every conversation for the first time, and only
    /// one with no answer in it yet is new to the person too.
    var startedAt: Date?
    /// The last time a person typed into it, as against Claude Code carrying
    /// on with a tool's answer or asking for a title beside it. The menu bar
    /// follows the conversation typed into last, because a window shows only
    /// one and the relay cannot see which; nil from a relay that predates it.
    var promptedAt: Date? = nil
}

struct PoolMemberStatus: Codable, Equatable {
    var id: String
    var limits: PoolLimits?
    var served: Int
    var lastServed: Date?
    /// The last time a request arrived carrying this member's own token: the
    /// only way to know a Claude is actually connected.
    var lastSeen: Date?
    var tokenRefused: Bool
}

struct PoolStatus: Codable, Equatable {
    /// Digest of the executable that is running, so Graft can tell a relay left
    /// over from an earlier version.
    var binary: String
    var pid: Int32
    var started: Date
    var enabled: Bool
    var configured: Bool
    var members: [PoolMemberStatus]
    var recent: [PoolRouting]
    /// Most recent first, and only the last day's.
    var sessions: [PoolSession]
    var openExchanges: Int
}

enum PoolSocket {
    enum Failure: Error { case notRunning, garbled, tooLong }

    /// Blocking, so never on the main thread. The socket is the relay's door,
    /// and a relay that does not answer it inside a few seconds is treated as
    /// absent rather than waited on.
    static func send(_ command: PoolCommand, to path: String = UsagePool.socketFile.path,
                     timeout: TimeInterval = 4) throws -> PoolStatus {
        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { throw Failure.notRunning }
        defer { close(fd) }
        var one: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &one, socklen_t(MemoryLayout<Int32>.size))
        var wait = timeval(tv_sec: Int(timeout), tv_usec: 0)
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &wait, socklen_t(MemoryLayout<timeval>.size))
        setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &wait, socklen_t(MemoryLayout<timeval>.size))
        guard var address = unixAddress(path) else { throw Failure.tooLong }
        let connected = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                connect(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size))
            }
        }
        guard connected == 0 else { throw Failure.notRunning }
        var payload = try JSONEncoder().encode(command)
        payload.append(0x0A)
        guard writeAll(fd, payload) else { throw Failure.notRunning }
        guard let line = readLine(fd, limit: 8 << 20),
              let status = try? JSONDecoder().decode(PoolStatus.self, from: line)
        else { throw Failure.garbled }
        return status
    }

    static func unixAddress(_ path: String) -> sockaddr_un? {
        var address = sockaddr_un()
        address.sun_family = sa_family_t(AF_UNIX)
        let bytes = Array(path.utf8)
        let capacity = MemoryLayout.size(ofValue: address.sun_path)
        guard bytes.count < capacity else { return nil }
        withUnsafeMutableBytes(of: &address.sun_path) { raw in
            raw.copyBytes(from: bytes)
            raw[bytes.count] = 0
        }
        return address
    }

    static func writeAll(_ fd: Int32, _ data: Data) -> Bool {
        data.withUnsafeBytes { raw in
            var offset = 0
            while offset < raw.count {
                let written = write(fd, raw.baseAddress! + offset, raw.count - offset)
                if written <= 0 { return false }
                offset += written
            }
            return true
        }
    }

    static func readLine(_ fd: Int32, limit: Int) -> Data? {
        var collected = Data()
        var buffer = [UInt8](repeating: 0, count: 65536)
        while collected.count < limit {
            let count = read(fd, &buffer, buffer.count)
            if count <= 0 { break }
            collected.append(buffer, count: count)
            if let end = collected.firstIndex(of: 0x0A) { return collected.prefix(upTo: end) }
        }
        return collected.isEmpty ? nil : collected
    }
}

// MARK: - The certificate Claude Code is told to trust

/// The relay opens `api.anthropic.com` traffic, so Claude Code has to accept a
/// certificate for that name that Graft made. It is told to through
/// `NODE_EXTRA_CA_CERTS`, which adds to what it trusts and replaces nothing,
/// and the authority it names can vouch for that one host only: the name
/// constraint says so, and its private key is thrown away the moment the one
/// certificate has been signed, so nothing else can ever be signed by it.
enum PoolCertificates {
    enum Failure: LocalizedError {
        case openssl(String)
        var errorDescription: String? {
            switch self {
            case .openssl(let detail): return L10n.format("The relay's certificate could not be made: %@", detail)
            }
        }
    }

    /// Regenerated a couple of months before the leaf runs out, and only by
    /// the relay holding `relay.lock`, as it starts. A session started before
    /// then goes on trusting the old authority and cannot reach the service
    /// through the new relay until it starts again. A relay starts at a login
    /// or after an update, and Graft's updates install themselves at any
    /// hour, so this can land mid-afternoon; it is once in some two years.
    static let renewal: TimeInterval = 60 * 24 * 60 * 60

    static var isPresent: Bool {
        [UsagePool.certificateFile, UsagePool.identityFile, UsagePool.leafFile]
            .allSatisfy { FileManager.default.fileExists(atPath: $0.path) }
    }

    /// Serialised against the app and the relay doing it at once with a lock
    /// file, so the authority Claude Code is told to trust is always the one
    /// that signed the certificate the relay presents.
    static func ensure() throws {
        let fm = FileManager.default
        let directory = UsagePool.directory
        try fm.createDirectory(at: directory, withIntermediateDirectories: true,
                               attributes: [.posixPermissions: 0o700])
        chmod(directory.path, 0o700)
        let lock = open(directory.appending(path: ".lock").path, O_CREAT | O_RDWR, 0o600)
        guard lock >= 0 else { throw Failure.openssl("lock") }
        defer { flock(lock, LOCK_UN); close(lock) }
        flock(lock, LOCK_EX)

        if isPresent, (try? openssl(["x509", "-checkend", String(Int(renewal)), "-noout",
                                     "-in", UsagePool.leafFile.path])) != nil {
            return
        }

        let work = directory.appending(path: ".new-\(UUID().uuidString)")
        try fm.createDirectory(at: work, withIntermediateDirectories: false,
                               attributes: [.posixPermissions: 0o700])
        defer { try? fm.removeItem(at: work) }
        func at(_ name: String) -> String { work.appending(path: name).path }

        try Data("""
            [req]
            distinguished_name = dn
            x509_extensions = v3_ca
            prompt = no
            [dn]
            CN = Claude Graft relay for api.anthropic.com
            [v3_ca]
            basicConstraints = critical, CA:TRUE, pathlen:0
            keyUsage = critical, keyCertSign, cRLSign
            subjectKeyIdentifier = hash
            nameConstraints = critical, permitted;DNS:\(UsagePool.host)

            """.utf8).write(to: URL(fileURLWithPath: at("ca.cnf")))
        try Data("""
            [req]
            distinguished_name = dn
            prompt = no
            [dn]
            CN = \(UsagePool.host)
            [v3_leaf]
            basicConstraints = critical, CA:FALSE
            keyUsage = critical, digitalSignature, keyEncipherment
            extendedKeyUsage = serverAuth
            subjectAltName = DNS:\(UsagePool.host)
            authorityKeyIdentifier = keyid

            """.utf8).write(to: URL(fileURLWithPath: at("leaf.cnf")))

        let serial = "0x" + UUID().uuidString.replacingOccurrences(of: "-", with: "").prefix(16)
        try openssl(["req", "-x509", "-newkey", "rsa:2048", "-nodes", "-keyout", at("ca.key"),
                     "-out", at("ca.pem"), "-days", "3650", "-config", at("ca.cnf")])
        try openssl(["req", "-newkey", "rsa:2048", "-nodes", "-keyout", at("leaf.key"),
                     "-out", at("leaf.csr"), "-config", at("leaf.cnf")])
        // 800 days keeps under the 825 Apple's own TLS stack enforces, in case
        // anything on the far side ever asks it rather than OpenSSL.
        try openssl(["x509", "-req", "-in", at("leaf.csr"), "-CA", at("ca.pem"), "-CAkey", at("ca.key"),
                     "-set_serial", String(serial), "-out", at("leaf.pem"), "-days", "800",
                     "-extfile", at("leaf.cnf"), "-extensions", "v3_leaf"])
        try openssl(["pkcs12", "-export", "-inkey", at("leaf.key"), "-in", at("leaf.pem"),
                     "-certfile", at("ca.pem"), "-name", "Claude Graft relay",
                     "-keypbe", "PBE-SHA1-3DES", "-certpbe", "PBE-SHA1-3DES", "-macalg", "sha1",
                     "-passout", "pass:\(UsagePool.identityPassphrase)", "-out", at("identity.p12")])
        chmod(at("identity.p12"), 0o600)

        // The authority last: until it lands, a reader sees the old pair or
        // none, never a new authority beside an old certificate.
        for (name, target) in [("identity.p12", UsagePool.identityFile),
                               ("leaf.pem", UsagePool.leafFile),
                               ("ca.pem", UsagePool.certificateFile)] {
            guard rename(at(name), target.path) == 0 else { throw Failure.openssl("rename \(name)") }
        }
    }

    @discardableResult
    static func openssl(_ arguments: [String]) throws -> String {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/openssl")
        process.arguments = arguments
        let output = Pipe()
        process.standardOutput = output
        process.standardError = output
        process.standardInput = FileHandle.nullDevice
        try process.run()
        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        let text = String(decoding: data, as: UTF8.self)
        guard process.terminationStatus == 0 else {
            throw Failure.openssl(text.split(separator: "\n").last.map(String.init) ?? arguments[0])
        }
        return text
    }
}

/// Who is at the other end of a connection the relay accepted on loopback.
/// Claude Code is told to trust the relay's authority; a program it starts
/// inherits the proxy without that trust, and refuses an opened connection.
enum PoolPeer {
    /// Every process holding a TCP socket from `local` to `remote`, both
    /// ports on this machine. Another user's processes cannot be asked.
    static func holders(local: UInt16, remote: UInt16) -> [pid_t] {
        var pids = [pid_t](repeating: 0, count: 8192)
        let count = proc_listallpids(&pids, Int32(pids.count * MemoryLayout<pid_t>.stride))
        guard count > 0 else { return [] }
        let whole = Int32(MemoryLayout<socket_fdinfo>.size)
        var found: [pid_t] = []
        for pid in pids.prefix(Int(count)) where pid > 0 {
            let size = proc_pidinfo(pid, PROC_PIDLISTFDS, 0, nil, 0)
            guard size > 0 else { continue }
            var open = [proc_fdinfo](repeating: proc_fdinfo(), count: Int(size) / MemoryLayout<proc_fdinfo>.stride)
            let used = proc_pidinfo(pid, PROC_PIDLISTFDS, 0, &open, size)
            guard used > 0 else { continue }
            for descriptor in open.prefix(Int(used) / MemoryLayout<proc_fdinfo>.stride)
            where descriptor.proc_fdtype == UInt32(PROX_FDTYPE_SOCKET) {
                var socket = socket_fdinfo()
                guard proc_pidfdinfo(pid, descriptor.proc_fd, PROC_PIDFDSOCKETINFO, &socket, whole) == whole,
                      socket.psi.soi_kind == SOCKINFO_TCP else { continue }
                // Ports are kept in network order in the low half of each.
                let ends = socket.psi.soi_proto.pri_tcp.tcpsi_ini
                if UInt16(bigEndian: UInt16(truncatingIfNeeded: ends.insi_lport)) == local,
                   UInt16(bigEndian: UInt16(truncatingIfNeeded: ends.insi_fport)) == remote {
                    found.append(pid)
                    break
                }
            }
        }
        return found
    }

    /// A process's executable and each parent's above it, nearest first.
    static func lineage(of pid: pid_t) -> [String] {
        var chain: [String] = []
        var next = pid
        while next > 1, chain.count < 32 {
            var path = [CChar](repeating: 0, count: 4 * Int(MAXPATHLEN))
            guard proc_pidpath(next, &path, UInt32(path.count)) > 0 else { break }
            chain.append(String(cString: path))
            var facts = proc_bsdinfo()
            let size = Int32(MemoryLayout<proc_bsdinfo>.size)
            guard proc_pidinfo(next, PROC_PIDTBSDINFO, 0, &facts, size) == size else { break }
            next = pid_t(facts.pbi_ppid)
        }
        return chain
    }

    /// Whether a program's connection to the service is opened. What Claude
    /// starts is Claude Code; what that starts is somebody else's program,
    /// unless it is Claude Code again. Anything not traced to Claude is
    /// opened as before, since being wrong there only costs what it cost.
    static func isOpened(_ lineage: [String], claude: (String) -> Bool) -> Bool {
        guard let app = lineage.firstIndex(where: claude), app > 1 else { return true }
        return lineage[0] == lineage[app - 1]
    }
}
