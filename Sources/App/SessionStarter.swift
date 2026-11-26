import Foundation

/// Opens a five-hour window on one account by sending it a single short
/// message. Nothing appears on screen and no permission beyond the keychain
/// prompt is involved.
///
/// It uses that profile's own login, borrowed the same way the usage figures
/// are: access token only, nothing written back, and the request goes to
/// Anthropic and nowhere else. That is what makes it per account — the command
/// line could only ever have started a window on whichever single account it
/// happens to be signed into.
///
/// `start` runs because somebody pressed the button: its callers are the three
/// Start Session buttons and the pool, for a signed-in account's button.
/// `startUnasked` is for `AutoSessions` alone, on an account somebody switched
/// that on for. The suite checks that neither has another caller: a view
/// refreshes on appearing and again every thirty seconds, so a session wired
/// into one of those would start a five-hour window on every account, over
/// and over, without anybody asking for one.
enum SessionStarter {
    static let endpoint = URL(string: "https://api.anthropic.com/v1/messages")!
    static let model = "claude-haiku-4-5-20251001"

    enum Failure: LocalizedError {
        case noLogin
        case inFlight
        case credentials(String)
        case http(Int, String)
        case unreachable(String)

        var errorDescription: String? {
            switch self {
            case .noLogin:
                return L10n.text("""
                    No login is stored for this profile yet. Open it once and sign in, \
                    then a session can be started from here.
                    """)
            case .inFlight:
                return L10n.text("A session is already being started for this account.")
            case .credentials(let detail):
                return detail
            case .http(401, _), .http(403, _):
                return L10n.text("That profile's login was refused. Open it once so Claude can renew it.")
            case .http(429, _):
                return L10n.text("That account is rate-limited right now.")
            case .http(let code, let detail):
                return detail.isEmpty
                    ? L10n.format("Anthropic answered %ld.", code)
                    : L10n.format("Anthropic answered %ld: %@", code, detail)
            case .unreachable(let detail):
                return L10n.format("Could not reach Anthropic: %@", detail)
            }
        }
    }

    /// Blocking; call it off the main thread. Nil means the window is open.
    ///
    /// `interactive` allows the one keychain prompt macOS shows before a
    /// profile's token can be read. It is off unless a caller says otherwise,
    /// so a call arriving from somewhere nobody audited fails quietly rather
    /// than putting a system prompt in front of someone who asked for nothing.
    static func start(profile: URL, interactive: Bool = false, prompt: String = "hi") -> Failure? {
        open(profile: profile, interactive: interactive, prompt: prompt).failure
    }

    enum Target: Equatable {
        case profile(URL)
        case account(PoolAccount)
    }

    /// Never asks the keychain, having no way to be told it may: an account
    /// whose login is shut away is left until a person opens it.
    static func startUnasked(_ target: Target) -> (failure: Failure?, limits: PoolLimits?) {
        switch target {
        case .profile(let profile): return open(profile: profile, interactive: false, prompt: "hi")
        case .account(let account): return start(account: account, interactive: false)
        }
    }

    private static func open(profile: URL, interactive: Bool,
                             prompt: String) -> (failure: Failure?, limits: PoolLimits?) {
        guard claim(profile) else { return (.inFlight, nil) }
        defer { release(profile) }

        let token: ClaudeCredentials.Token?
        do {
            token = try ClaudeCredentials.token(for: profile,
                                                prompting: interactive ? .yes : .no,
                                                requiring: [ClaudeCredentials.inferenceScope])
        } catch {
            return (.credentials((error as? LocalizedError)?.errorDescription
                ?? error.localizedDescription), nil)
        }
        guard let token else { return (.noLogin, nil) }
        return send(token: token.value, prompt: prompt)
    }

    /// The same for an account signed in through Graft, with its own login.
    /// The answer carries the account's limits, which for a login the usage
    /// endpoint will not answer is the only figure there is.
    static func start(account: PoolAccount, interactive: Bool = false,
                      prompt: String = "hi") -> (failure: Failure?, limits: PoolLimits?) {
        guard claim(key: account.id) else { return (.inFlight, nil) }
        defer { release(key: account.id) }

        let token: String?
        do {
            token = try UsageMonitor.token(of: account, prompting: interactive ? .yes : .no)
        } catch ClaudeCredentials.Failure.expired {
            return (.credentials(L10n.text("That account's login has expired. Sign in to it again.")), nil)
        } catch {
            return (.credentials((error as? LocalizedError)?.errorDescription
                ?? error.localizedDescription), nil)
        }
        guard let token else {
            return (.credentials(L10n.text("That account's login could not be read on this Mac.")), nil)
        }
        let outcome = send(token: token, prompt: prompt)
        if case .http(let code, _)? = outcome.failure, code == 401 || code == 403 {
            return (.credentials(L10n.text("That account's login was refused. Sign in to it again.")), outcome.limits)
        }
        return outcome
    }

    private static func send(token: String, prompt: String) -> (failure: Failure?, limits: PoolLimits?) {
        var request = URLRequest(url: endpoint, timeoutInterval: 30)
        request.httpMethod = "POST"
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue("2023-06-01", forHTTPHeaderField: "anthropic-version")
        request.setValue("oauth-2025-04-20", forHTTPHeaderField: "anthropic-beta")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")

        // These tokens are minted for Claude Code, and the service expects to
        // see its system prompt on requests made with them.
        let body: [String: Any] = [
            "model": model,
            "max_tokens": 4,
            "system": [["type": "text", "text": "You are Claude Code, Anthropic's official CLI for Claude."]],
            "messages": [["role": "user", "content": prompt]],
        ]
        request.httpBody = try? JSONSerialization.data(withJSONObject: body)

        var payload: Data?
        var response: URLResponse?
        var transportFailure: Error?
        let finished = DispatchSemaphore(value: 0)
        UsageAPI.session.dataTask(with: request) { data, urlResponse, error in
            payload = data
            response = urlResponse
            transportFailure = error
            finished.signal()
        }.resume()
        _ = finished.wait(timeout: .now() + 35)

        if let transportFailure { return (.unreachable(transportFailure.localizedDescription), nil) }
        let http = response as? HTTPURLResponse
        let headers = (http?.allHeaderFields ?? [:]).compactMap { name, value in
            (name as? String).map { ($0, "\(value)") }
        }
        let limits = PoolLimits.parse(headers)
        let code = http?.statusCode ?? 0
        guard code == 200 else { return (.http(code, message(in: payload)), limits) }
        return (nil, limits)
    }

    // MARK: - One at a time, per account

    /// The window and the dropdown each carry a Start Session button for the
    /// same account, and each only knows about its own press. A second one
    /// landing while the first is still open sends a second message to start a
    /// window that is already starting.
    private static let claims = NSLock()
    private static var claimed: Set<String> = []

    /// Not private: the suite presses the same account twice.
    static func claim(_ profile: URL) -> Bool { claim(key: profile.standardizedFileURL.path) }

    static func release(_ profile: URL) { release(key: profile.standardizedFileURL.path) }

    static func claim(key: String) -> Bool {
        claims.lock()
        defer { claims.unlock() }
        return claimed.insert(key).inserted
    }

    static func release(key: String) {
        claims.lock()
        claimed.remove(key)
        claims.unlock()
    }

    /// Anthropic returns `{ "error": { "message": … } }` on a refusal.
    private static func message(in payload: Data?) -> String {
        guard let payload,
              let body = try? JSONSerialization.jsonObject(with: payload) as? [String: Any],
              let error = body["error"] as? [String: Any],
              let text = error["message"] as? String
        else { return "" }
        return text
    }
}
