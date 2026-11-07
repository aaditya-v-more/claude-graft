import Foundation
import Network

/// Where requests for the service go. Tests point this at a plain listener on
/// loopback; nothing else can, so the relay never speaks to the real service
/// without TLS.
enum Upstream {
    case service
    case loopback(UInt16)

    func connect() async throws -> Wire {
        let connection: NWConnection
        switch self {
        case .service:
            let tls = NWProtocolTLS.Options()
            sec_protocol_options_add_tls_application_protocol(tls.securityProtocolOptions, "http/1.1")
            connection = NWConnection(host: NWEndpoint.Host(UsagePool.host), port: 443,
                                      using: NWParameters(tls: tls, tcp: NWProtocolTCP.Options()))
        case .loopback(let port):
            connection = NWConnection(host: .ipv4(.loopback), port: NWEndpoint.Port(rawValue: port)!,
                                      using: .tcp)
        }
        let wire = Wire(connection, label: "relay.upstream")
        try await wire.open(timeout: 20)
        return wire
    }
}

/// One TLS connection from Claude Code, and every request that comes down it.
///
/// A model request is read whole before anything is sent, because it may have
/// to go out more than once; anything else goes as it arrives. The answer is
/// never held: once an attempt is the one being delivered, its bytes go back
/// as they arrive, which is what keeps a streamed reply streaming.
final class Exchange {
    static let headLimit = 64 * 1024
    /// Far beyond any request with a long history and a few images in it; past
    /// this a body is something other than a conversation.
    static let bodyLimit = 256 * 1024 * 1024

    private let client: Wire
    private let state: PoolState
    private let upstream: Upstream
    private var connection: Wire?
    /// The error type of the last refusal read and found not to be a missing
    /// thread, for the diagnostics line that follows it.
    private var refusal: String?

    init(client: Wire, state: PoolState, upstream: Upstream) {
        self.client = client
        self.state = state
        self.upstream = upstream
    }

    func run() async {
        do {
            try await client.open(timeout: 20)
            while try await serveOne() {}
        } catch {}
        connection?.close()
        client.close()
    }

    /// False when the connection should close after this request.
    private func serveOne() async throws -> Bool {
        guard let (head, raw) = try await client.readHead(limit: Self.headLimit) else { return false }
        guard head.line.count == 3, head.method != "CONNECT", let framing = head.requestBody else {
            try? await client.send(HTTPMessage.failure(400, "Bad Request", "The relay could not read that request."))
            return false
        }
        await state.began()
        defer { Task { await self.state.ended() } }

        if head.tokens("expect").contains("100-continue") {
            try await client.send(Data("HTTP/1.1 100 Continue\r\n\r\n".utf8))
        }

        var own: String?
        if let bearer = PoolRequest.bearer(in: head.pairs) {
            own = await state.member(for: bearer)
        }
        // Only a model request from a Claude the pool knows can change
        // accounts, so only that one is ever taken apart. Remote Control's own
        // session, artifacts, a socket the connection is upgraded into: all of
        // it belongs to the account that sent it, and rebuilding a request
        // drops `Upgrade` as a hop's field, which is all it takes to stop a
        // socket opening.
        guard PoolRequest.isInference(method: head.method, target: head.target), let own,
              !head.asksToUpgrade else {
            return try await passThrough(head, raw: raw, framing: framing)
        }
        let body: Data
        do {
            body = try await client.readBody(framing, limit: Self.bodyLimit)
        } catch RelayError.tooLarge {
            try? await client.send(HTTPMessage.failure(413, "Payload Too Large", "That request is too large for the relay."))
            return false
        }

        let session = head.value("x-claude-code-session-id")
        let pinned = PoolRequest.mustStayWithSender(body: body,
                                                    contentEncoding: head.value("content-encoding"))
        let continuing = PoolRequest.continuesThread(body: body)
        let attempts = await state.plan(own: own, session: session, pinned: pinned,
                                        bound: continuing || PoolRequest.carriesBoundReasoning(body: body))

        var passed: [PoolPass] = []
        var index = 0
        while index < attempts.count {
            let attempt = attempts[index]
            let last = index == attempts.count - 1
            let borrowed = attempt.token != nil
            // A moved thread's refusal is read to tell `thread_not_found` from
            // a plan that lacks something, and a compressed one cannot be.
            let request = HTTPMessage.request(head, body: body,
                                              authorization: attempt.token.map { "Bearer \($0)" },
                                              plainAnswer: continuing && borrowed)
            let answer: (head: HTTPHead, raw: Data)
            do {
                answer = try await send(request)
            } catch {
                connection?.close()
                connection = nil
                if last {
                    try? await client.send(HTTPMessage.failure(
                        502, "Bad Gateway", "The relay could not reach \(UsagePool.host): \(error.localizedDescription)"))
                    return false
                }
                if let member = attempt.member { passed.append(PoolPass(id: member, reason: .unreachable)) }
                index += 1
                continue
            }

            let status = answer.head.status ?? 502
            if let member = attempt.member {
                await state.observe(member, answer.head.pairs)
                let outcome = PoolPolicy.outcome(status: status, headers: answer.head.pairs,
                                                 borrowed: borrowed, now: Date())
                if outcome == .refusedForAccount, status == 404, continuing, !last,
                   let missing = try await threadMissing(answer, method: head.method) {
                    // The thread is stored on the account the conversation
                    // left, and sending the request there would undo the move.
                    // Claude Code answers this by sending everything as a new
                    // thread, which this account can take.
                    Diagnostics.note("relay thread restarted", ["member": member])
                    await state.served(by: member, from: own, session: session, status: status,
                                       passedOver: passed, model: PoolRequest.model(in: body),
                                       cacheLifetime: PoolPolicy.cacheLifetime(of: body), threaded: true)
                    return try await deliver(answer, body: missing, method: head.method, keepAlive: head.keepsAlive)
                }
                if outcome != .deliver, !last {
                    await state.apply(outcome, to: member)
                    // Its answer is a few hundred bytes nobody will read, and
                    // dropping the connection is cheaper than draining it.
                    connection?.close()
                    connection = nil
                    switch outcome {
                    case .exhausted:
                        passed.append(PoolPass(id: member, reason: .full))
                        index += 1
                    case .loginRefused:
                        passed.append(PoolPass(id: member, reason: .login))
                        index += 1
                    default:
                        // What one plan lacks the next may lack too; the
                        // account Claude chose the request for is the one sure
                        // to take it.
                        passed.append(PoolPass(id: member, reason: .refused))
                        index = attempts.count - 1
                    }
                    var noted: [String: Any] = ["member": member, "status": status,
                                                "reason": passed.last!.reason.rawValue]
                    if continuing { noted["thread"] = "continue" }
                    if let refusal { noted["error"] = refusal }
                    refusal = nil
                    Diagnostics.note("relay passed over", noted)
                    continue
                }
                await state.served(by: member, from: own, session: session, status: status,
                                   passedOver: passed, model: PoolRequest.model(in: body),
                                   cacheLifetime: PoolPolicy.cacheLifetime(of: body),
                                   threaded: PoolRequest.carriesThread(body: body))
            }
            return try await deliver(answer, method: head.method, keepAlive: head.keepsAlive)
        }
        return false
    }

    /// A request that leaves under its sender's login whatever happens goes as
    /// it arrived. One with a stated length is read first, so a connection kept
    /// from the last request can be tried and, if the service had dropped it,
    /// tried again. A chunked body may still be coming when its answer starts —
    /// a stream that ends only when its sender does, waiting on what comes back
    /// — and an upgraded connection carries no more HTTP at all, so neither is
    /// held: each gets a connection of its own and its body is forwarded
    /// alongside the answer.
    private func passThrough(_ head: HTTPHead, raw: Data, framing: HTTPBody) async throws -> Bool {
        let upgrade = head.asksToUpgrade
        let answer: (head: HTTPHead, raw: Data)
        var upload: Task<Void, Error>?
        if !upgrade, framing != .chunked {
            let body: Data
            do {
                body = try await client.readBody(framing, limit: Self.bodyLimit)
            } catch RelayError.tooLarge {
                try? await client.send(HTTPMessage.failure(413, "Payload Too Large", "That request is too large for the relay."))
                return false
            }
            do {
                answer = try await send(raw + body)
            } catch {
                connection?.close()
                connection = nil
                try? await client.send(HTTPMessage.failure(
                    502, "Bad Gateway", "The relay could not reach \(UsagePool.host): \(error.localizedDescription)"))
                return false
            }
        } else {
            connection?.close()
            connection = nil
            let fresh: Wire
            do {
                fresh = try await upstream.connect()
            } catch {
                try? await client.send(HTTPMessage.failure(
                    502, "Bad Gateway", "The relay could not reach \(UsagePool.host): \(error.localizedDescription)"))
                return false
            }
            connection = fresh
            try await fresh.send(raw)
            upload = Task { try await self.forward(framing, to: fresh) }
            guard let first = try await finalHead(fresh) else { throw RelayError.closed }
            answer = first
        }

        if upgrade, answer.head.status == 101, let up = connection {
            try await upload?.value
            try await client.send(answer.raw)
            connection = nil
            await splice(client, up)
            return false
        }
        let more = try await deliver(answer, method: head.method, keepAlive: head.keepsAlive)
        try await upload?.value
        return more
    }

    /// The rest of a request's body, as it comes, to the connection its head
    /// went down.
    private func forward(_ framing: HTTPBody, to up: Wire) async throws {
        switch framing {
        case .none, .untilClose:
            return
        case .length(let total):
            var remaining = total
            while remaining > 0 {
                if client.buffer.isEmpty { guard try await client.fill() else { throw RelayError.truncated } }
                let piece = client.take(min(remaining, client.buffer.count))
                remaining -= piece.count
                try await up.send(piece)
            }
        case .chunked:
            var framing = ChunkedFraming()
            var nothing: Data?
            while true {
                if !client.buffer.isEmpty {
                    let taken = try framing.consume(client.buffer, collecting: &nothing)
                    try await up.send(client.take(taken))
                }
                if framing.isDone { return }
                guard try await client.fill() else { throw RelayError.truncated }
            }
        }
    }

    /// A connection kept from the last request may have been closed by the
    /// far end while it sat idle, which shows up as a dead connection before a
    /// byte of answer. That one is tried again on a fresh connection; anything
    /// that failed after an answer had started is not.
    private func send(_ request: Data) async throws -> (head: HTTPHead, raw: Data) {
        if let reused = connection {
            let before = reused.received
            do {
                try await reused.send(request)
                if let answer = try await finalHead(reused) { return answer }
            } catch where reused.received == before {
            }
            guard reused.received == before else { throw RelayError.truncated }
            reused.close()
            connection = nil
        }
        let fresh = try await upstream.connect()
        connection = fresh
        try await fresh.send(request)
        guard let answer = try await finalHead(fresh) else { throw RelayError.closed }
        return answer
    }

    /// Skips interim answers. The relay has already said `100 Continue` to
    /// Claude Code itself, and never asks the service for one.
    private func finalHead(_ wire: Wire) async throws -> (head: HTTPHead, raw: Data)? {
        while true {
            guard let answer = try await wire.readHead(limit: Self.headLimit) else { return nil }
            if let status = answer.head.status, (100..<200).contains(status), status != 101 { continue }
            return answer
        }
    }

    /// The body of a refusal, when it is the service saying it holds no such
    /// thread. Read rather than dropped only here: an error is a few hundred
    /// bytes, and which error it is decides whether it goes back or on.
    private func threadMissing(_ answer: (head: HTTPHead, raw: Data), method: String) async throws -> Data? {
        guard let up = connection else { return nil }
        let framing = answer.head.responseBody(to: method)
        guard framing != .untilClose else { return nil }
        if let encoding = answer.head.value("content-encoding"), !encoding.isEmpty,
           encoding.lowercased() != "identity" { return nil }
        let body = try await up.readBody(framing, limit: 64 * 1024)
        if body.range(of: PoolRequest.threadMissing) != nil { return body }
        // The last live run could only guess why a moved thread was refused;
        // the service's own error type settles it next time.
        let error = (try? JSONSerialization.jsonObject(with: body)) as? [String: Any]
        refusal = ((error?["error"] as? [String: Any])?["type"] as? String) ?? "unreadable"
        return nil
    }

    /// An answer whose body was read to decide on it. A chunked one goes back
    /// as a single chunk, which is the same body however it was cut.
    private func deliver(_ answer: (head: HTTPHead, raw: Data), body: Data, method: String,
                         keepAlive: Bool) async throws -> Bool {
        try await client.send(answer.raw)
        if answer.head.responseBody(to: method) == .chunked {
            if !body.isEmpty {
                try await client.send(Data(String(body.count, radix: 16).utf8) + Data("\r\n".utf8)
                                      + body + Data("\r\n".utf8))
            }
            try await client.send(Data("0\r\n\r\n".utf8))
        } else {
            try await client.send(body)
        }
        if !answer.head.keepsAlive || connection?.buffer.isEmpty == false {
            connection?.close()
            connection = nil
        }
        return keepAlive && answer.head.keepsAlive
    }

    private func deliver(_ answer: (head: HTTPHead, raw: Data), method: String, keepAlive: Bool) async throws -> Bool {
        guard let up = connection else { return false }
        try await client.send(answer.raw)
        switch answer.head.responseBody(to: method) {
        case .none:
            break
        case .length(let total):
            var remaining = total
            while remaining > 0 {
                if up.buffer.isEmpty { guard try await up.fill() else { throw RelayError.truncated } }
                let piece = up.take(min(remaining, up.buffer.count))
                remaining -= piece.count
                try await client.send(piece)
            }
        case .chunked:
            var framing = ChunkedFraming()
            var nothing: Data?
            while true {
                if !up.buffer.isEmpty {
                    let taken = try framing.consume(up.buffer, collecting: &nothing)
                    try await client.send(up.take(taken))
                }
                if framing.isDone { break }
                guard try await up.fill() else { throw RelayError.truncated }
            }
        case .untilClose:
            try await client.send(up.takeAll())
            while try await up.fill() { try await client.send(up.takeAll()) }
            up.close()
            connection = nil
            return false
        }
        if !answer.head.keepsAlive || !up.buffer.isEmpty {
            up.close()
            connection = nil
        }
        // A `Connection: close` from the service went back to Claude Code with
        // the rest of the head, so it is closing its end too.
        return keepAlive && answer.head.keepsAlive
    }
}
