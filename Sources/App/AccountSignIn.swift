import AppKit
import CryptoKit
import Foundation

/// Signs an account in through the browser so it can lend its usage without a
/// Claude of its own: the same sign-in Claude Code's long-lived login uses, a
/// year-long token for model requests and nothing else.
///
/// The browser comes back to a listener on loopback with a one-time code, and
/// the code is exchanged for the token at Anthropic's sign-in service. That
/// exchange is the one request here that does not go to `api.anthropic.com`,
/// and what it sends is the code, never a token. The refresh token the service
/// returns beside it is dropped unread: a year is the whole of what is wanted,
/// and holding one would be holding a login that renews itself.
final class AccountSignIn: ObservableObject {
    enum Phase: Equatable {
        case idle
        /// For the Claude the account is being added to.
        case waiting(String)
        case failed(String, message: String)
    }

    static let clientID = "9d1c250a-e61b-44d9-88ed-5944d1962f5e"
    static let authorize = URL(string: "https://claude.com/cai/oauth/authorize")!
    static let exchange = URL(string: "https://platform.claude.com/v1/oauth/token")!
    static let scope = "user:inference"
    static let lifetime = 365 * 24 * 60 * 60
    /// Long enough to sign in from scratch, two-step verification included.
    static let patience: TimeInterval = 10 * 60

    @Published private(set) var phase: Phase = .idle

    private let pool: UsagePoolController
    private var cancelled = false
    private let lock = NSLock()

    init(pool: UsagePoolController) {
        self.pool = pool
    }

    /// Only ever from a button: sealing the login may need the keychain, and
    /// it is a person waiting on the answer who is asked.
    func start(for claude: String, interactive: Bool) {
        if case .waiting = phase { return }
        phase = .waiting(claude)
        setCancelled(false)
        let known = pool.accountUuid(of: claude)
        let profile = pool.profile(of: claude)
        DispatchQueue.global(qos: .userInitiated).async {
            // Before the first pass has read it, the profile is asked directly.
            let ownAccount = known ?? profile.flatMap(Graft.account(of:))
            let result = self.run(excluding: ownAccount, interactive: interactive)
            DispatchQueue.main.async {
                guard case .waiting(claude) = self.phase else { return }
                switch result {
                case .success(let account):
                    self.pool.add(account, to: claude)
                    self.phase = .idle
                case .failure(.cancelled):
                    self.phase = .idle
                case .failure(let failure):
                    self.phase = .failed(claude, message: failure.message)
                }
            }
        }
    }

    func cancel() {
        setCancelled(true)
        phase = .idle
    }

    func dismissFailure() {
        if case .failed = phase { phase = .idle }
    }

    private func setCancelled(_ value: Bool) {
        lock.lock()
        cancelled = value
        lock.unlock()
    }

    private var isCancelled: Bool {
        lock.lock()
        defer { lock.unlock() }
        return cancelled
    }

    enum Failure: Error, Equatable {
        case cancelled
        case timedOut
        case listener
        case declined(String?)
        case refused(Int, String?)
        case unreadable
        case ownAccount
        case keychain

        var message: String {
            switch self {
            case .cancelled:
                return ""
            case .timedOut:
                return L10n.text("Nothing came back from the browser, so the sign-in was abandoned.")
            case .listener:
                return L10n.text("Graft could not listen for the browser's answer on this Mac.")
            case .declined(let reason):
                return reason.map { L10n.format("The sign-in was not completed: %@", $0) }
                    ?? L10n.text("The sign-in was not completed.")
            case .refused(let status, let reason):
                return reason.map { L10n.format("Anthropic did not accept the sign-in: %@", $0) }
                    ?? L10n.format("Anthropic did not accept the sign-in (%ld).", status)
            case .unreadable:
                return L10n.text("Anthropic's answer to the sign-in could not be read.")
            case .ownAccount:
                return L10n.text("""
                    That is the account this Claude already uses, so it has nothing to add. \
                    Switch accounts in the browser, then try again.
                    """)
            case .keychain:
                return L10n.text("Graft needs keychain access to keep this login. Try again and allow it.")
            }
        }
    }

    // MARK: - The flow

    private func run(excluding ownAccount: String?, interactive: Bool) -> Result<PoolAccount, Failure> {
        guard let listener = Loopback() else { return .failure(.listener) }
        defer { listener.close() }
        let verifier = Self.randomToken()
        let state = Self.randomToken()
        let redirect = "http://localhost:\(listener.port)/callback"
        NSWorkspace.shared.open(Self.authorizeURL(redirect: redirect, verifier: verifier, state: state))

        let deadline = Date().addingTimeInterval(Self.patience)
        while true {
            guard !isCancelled else { return .failure(.cancelled) }
            guard Date() < deadline else { return .failure(.timedOut) }
            guard let connection = listener.accept(within: 0.25) else { continue }
            defer { Darwin.close(connection) }
            guard let query = Loopback.callbackQuery(connection) else {
                Loopback.respond(connection, status: 404, page: nil)
                continue
            }
            // Anything without the state this sign-in opened with came from
            // somewhere else: a stale tab, or a page poking at the port.
            guard query["state"] == state else {
                Loopback.respond(connection, status: 400,
                                 page: Self.page(L10n.text("This sign-in link is out of date. Start again from Claude Graft.")))
                continue
            }
            guard let code = query["code"] else {
                let failure = Failure.declined(query["error_description"] ?? query["error"])
                Loopback.respond(connection, status: 200, page: Self.page(failure.message))
                return .failure(failure)
            }
            let result = finish(code: code, redirect: redirect, verifier: verifier, state: state,
                                excluding: ownAccount, interactive: interactive)
            switch result {
            case .success(let account):
                Loopback.respond(connection, status: 200, page: Self.page(
                    L10n.format("%@ is signed in. You can close this tab and go back to Claude Graft.",
                                account.email ?? L10n.text("The account"))))
            case .failure(let failure):
                Loopback.respond(connection, status: 200, page: Self.page(failure.message))
            }
            return result
        }
    }

    private func finish(code: String, redirect: String, verifier: String, state: String,
                        excluding ownAccount: String?, interactive: Bool) -> Result<PoolAccount, Failure> {
        let body: [String: Any] = [
            "grant_type": "authorization_code",
            "code": code,
            "redirect_uri": redirect,
            "client_id": Self.clientID,
            "code_verifier": verifier,
            "state": state,
            "expires_in": Self.lifetime,
        ]
        var request = URLRequest(url: Self.exchange, timeoutInterval: 30)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.httpBody = try? JSONSerialization.data(withJSONObject: body)

        var payload: Data?
        var response: URLResponse?
        let done = DispatchSemaphore(value: 0)
        let session = URLSession(configuration: .ephemeral)
        session.dataTask(with: request) { data, answer, _ in
            payload = data
            response = answer
            done.signal()
        }.resume()
        _ = done.wait(timeout: .now() + 35)
        session.invalidateAndCancel()

        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        let object = payload.flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] }
        guard status == 200 else {
            let reason = object?["error_description"] as? String
                ?? (object?["error"] as? [String: Any])?["message"] as? String
            return .failure(.refused(status, reason))
        }
        guard let object, let token = object["access_token"] as? String, !token.isEmpty
        else { return .failure(.unreadable) }
        let account = object["account"] as? [String: Any]
        let organization = object["organization"] as? [String: Any]
        let uuid = account?["uuid"] as? String
        if let uuid, uuid == ownAccount { return .failure(.ownAccount) }

        guard let key = try? ClaudeCredentials.safeStorageKey(prompting: interactive ? .yes : .no),
              let sealed = ClaudeCredentials.seal(Data(token.utf8), key: key)
        else { return .failure(.keychain) }
        let lifetime = (object["expires_in"] as? Double) ?? Double(Self.lifetime)
        let scopes = (object["scope"] as? String)?.split(separator: " ").map(String.init) ?? [Self.scope]
        let id = UsagePoolController.accountPrefix + (uuid ?? UUID().uuidString.lowercased())
        return .success(PoolAccount(id: id, accountUuid: uuid,
                                    email: account?["email_address"] as? String,
                                    organizationName: organization?["name"] as? String,
                                    scopes: scopes, expires: Date().addingTimeInterval(lifetime),
                                    added: Date(), sealed: sealed))
    }

    static func authorizeURL(redirect: String, verifier: String, state: String) -> URL {
        let challenge = Data(SHA256.hash(data: Data(verifier.utf8))).base64URL
        var components = URLComponents(url: authorize, resolvingAgainstBaseURL: false)!
        components.queryItems = [
            URLQueryItem(name: "code", value: "true"),
            URLQueryItem(name: "client_id", value: clientID),
            URLQueryItem(name: "response_type", value: "code"),
            URLQueryItem(name: "redirect_uri", value: redirect),
            URLQueryItem(name: "scope", value: scope),
            URLQueryItem(name: "code_challenge", value: challenge),
            URLQueryItem(name: "code_challenge_method", value: "S256"),
            URLQueryItem(name: "state", value: state),
        ]
        // `+` would be read as a space, and a colon or slash left bare in a
        // value is a thing some servers parse and others do not.
        var allowed = CharacterSet.urlQueryAllowed
        allowed.remove(charactersIn: "+&=:/?")
        components.percentEncodedQuery = components.queryItems!.map {
            "\($0.name)=\($0.value!.addingPercentEncoding(withAllowedCharacters: allowed)!)"
        }.joined(separator: "&")
        return components.url!
    }

    static func randomToken() -> String {
        var bytes = [UInt8](repeating: 0, count: 32)
        _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        return Data(bytes).base64URL
    }

    static func page(_ message: String) -> String {
        let escaped = message
            .replacingOccurrences(of: "&", with: "&amp;")
            .replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;")
        return """
            <!doctype html><html><head><meta charset="utf-8"><title>Claude Graft</title>
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <style>
            :root { color-scheme: light dark; }
            body { font: 15px -apple-system, system-ui, sans-serif; margin: 0; min-height: 100vh;
                   display: grid; place-items: center; background: Canvas; color: CanvasText; }
            p { max-width: 28em; padding: 0 24px; line-height: 1.45; text-align: center; }
            </style></head><body><p>\(escaped)</p></body></html>
            """
    }
}

private extension Data {
    var base64URL: String {
        base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }
}

/// Listens on both loopback addresses at one port. The redirect names
/// `localhost`, and which of the two a browser tries first is the browser's
/// business.
private final class Loopback {
    let port: UInt16
    private var sockets: [Int32] = []

    init?() {
        let first = Self.listen(family: AF_INET, port: 0)
        guard let first else { return nil }
        sockets.append(first.fd)
        port = first.port
        if let second = Self.listen(family: AF_INET6, port: port) { sockets.append(second.fd) }
    }

    func close() {
        sockets.forEach { Darwin.close($0) }
        sockets = []
    }

    func accept(within seconds: TimeInterval) -> Int32? {
        var fds = sockets.map { pollfd(fd: $0, events: Int16(POLLIN), revents: 0) }
        guard poll(&fds, nfds_t(fds.count), Int32(seconds * 1000)) > 0 else { return nil }
        for entry in fds where entry.revents & Int16(POLLIN) != 0 {
            let connection = Darwin.accept(entry.fd, nil, nil)
            if connection >= 0 {
                var one: Int32 = 1
                setsockopt(connection, SOL_SOCKET, SO_NOSIGPIPE, &one, socklen_t(MemoryLayout<Int32>.size))
                var wait = timeval(tv_sec: 5, tv_usec: 0)
                setsockopt(connection, SOL_SOCKET, SO_RCVTIMEO, &wait, socklen_t(MemoryLayout<timeval>.size))
                return connection
            }
        }
        return nil
    }

    private static func listen(family: Int32, port: UInt16) -> (fd: Int32, port: UInt16)? {
        let fd = socket(family, SOCK_STREAM, 0)
        guard fd >= 0 else { return nil }
        var one: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &one, socklen_t(MemoryLayout<Int32>.size))
        var bound: Int32
        if family == AF_INET {
            var address = sockaddr_in()
            address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
            address.sin_family = sa_family_t(AF_INET)
            address.sin_port = port.bigEndian
            address.sin_addr.s_addr = inet_addr("127.0.0.1")
            bound = withUnsafePointer(to: &address) {
                $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
                }
            }
        } else {
            setsockopt(fd, IPPROTO_IPV6, IPV6_V6ONLY, &one, socklen_t(MemoryLayout<Int32>.size))
            var address = sockaddr_in6()
            address.sin6_len = UInt8(MemoryLayout<sockaddr_in6>.size)
            address.sin6_family = sa_family_t(AF_INET6)
            address.sin6_port = port.bigEndian
            address.sin6_addr = in6addr_loopback
            bound = withUnsafePointer(to: &address) {
                $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in6>.size))
                }
            }
        }
        guard bound == 0, Darwin.listen(fd, 8) == 0 else {
            Darwin.close(fd)
            return nil
        }
        var address = sockaddr_storage()
        var length = socklen_t(MemoryLayout<sockaddr_storage>.size)
        _ = withUnsafeMutablePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { getsockname(fd, $0, &length) }
        }
        let assigned: UInt16 = withUnsafePointer(to: &address) { pointer in
            family == AF_INET
                ? pointer.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { UInt16(bigEndian: $0.pointee.sin_port) }
                : pointer.withMemoryRebound(to: sockaddr_in6.self, capacity: 1) { UInt16(bigEndian: $0.pointee.sin6_port) }
        }
        return (fd, assigned)
    }

    /// The query of a `GET /callback`, or nil for any other request — a
    /// browser asks for a favicon on the same port.
    static func callbackQuery(_ connection: Int32) -> [String: String]? {
        var collected = Data()
        var buffer = [UInt8](repeating: 0, count: 4096)
        while collected.count < 16_384 {
            let count = read(connection, &buffer, buffer.count)
            if count <= 0 { break }
            collected.append(buffer, count: count)
            if collected.range(of: Data("\r\n\r\n".utf8)) != nil { break }
        }
        let head = String(decoding: collected, as: UTF8.self)
        guard let line = head.split(separator: "\r\n", maxSplits: 1).first else { return nil }
        let parts = line.split(separator: " ")
        guard parts.count >= 2, parts[0] == "GET",
              let components = URLComponents(string: "http://localhost" + parts[1]),
              components.path == "/callback"
        else { return nil }
        var query: [String: String] = [:]
        for item in components.queryItems ?? [] where query[item.name] == nil {
            query[item.name] = item.value
        }
        return query
    }

    static func respond(_ connection: Int32, status: Int, page: String?) {
        let body = Data((page ?? "").utf8)
        let reason = status == 200 ? "OK" : status == 400 ? "Bad Request" : "Not Found"
        let head = """
            HTTP/1.1 \(status) \(reason)\r
            Content-Type: text/html; charset=utf-8\r
            Content-Length: \(body.count)\r
            Cache-Control: no-store\r
            Connection: close\r
            \r

            """
        _ = PoolSocket.writeAll(connection, Data(head.utf8) + body)
    }
}
