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
/// Nothing here may run except because somebody pressed the button. The three
/// callers are the three Start Session buttons and the suite checks that no
/// fourth appears: a view refreshes on appearing and again every thirty
/// seconds, so a session wired into one of those would start a five-hour
/// window on every account, over and over, without anybody asking for one.
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
                return """
                    No login is stored for this profile yet. Open it once and sign in, \
                    then a session can be started from here.
                    """
            case .inFlight:
                return "A session is already being started for this account."
            case .credentials(let detail):
                return detail
            case .http(401, _), .http(403, _):
                return "That profile's login was refused. Open it once so Claude can renew it."
            case .http(429, _):
                return "That account is rate-limited right now."
            case .http(let code, let detail):
                return detail.isEmpty
                    ? "Anthropic answered \(code)."
                    : "Anthropic answered \(code): \(detail)"
            case .unreachable(let detail):
                return "Could not reach Anthropic: \(detail)"
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
        guard claim(profile) else { return .inFlight }
        defer { release(profile) }

        let token: ClaudeCredentials.Token?
        do {
            token = try ClaudeCredentials.token(for: profile,
                                                prompting: interactive ? .yes : .no,
                                                requiring: [ClaudeCredentials.inferenceScope])
        } catch {
            return .credentials((error as? LocalizedError)?.errorDescription
                ?? error.localizedDescription)
        }
        guard let token else { return .noLogin }

        var request = URLRequest(url: endpoint, timeoutInterval: 30)
        request.httpMethod = "POST"
        request.setValue("Bearer \(token.value)", forHTTPHeaderField: "Authorization")
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
        URLSession.shared.dataTask(with: request) { data, urlResponse, error in
            payload = data
            response = urlResponse
            transportFailure = error
            finished.signal()
        }.resume()
        _ = finished.wait(timeout: .now() + 35)

        if let transportFailure { return .unreachable(transportFailure.localizedDescription) }
        let code = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard code == 200 else { return .http(code, message(in: payload)) }
        return nil
    }

    // MARK: - One at a time, per account

    /// The window and the dropdown each carry a Start Session button for the
    /// same account, and each only knows about its own press. A second one
    /// landing while the first is still open sends a second message to start a
    /// window that is already starting.
    private static let claims = NSLock()
    private static var claimed: Set<String> = []

    /// Not private: the suite presses the same account twice.
    static func claim(_ profile: URL) -> Bool {
        claims.lock()
        defer { claims.unlock() }
        return claimed.insert(profile.standardizedFileURL.path).inserted
    }

    static func release(_ profile: URL) {
        claims.lock()
        claimed.remove(profile.standardizedFileURL.path)
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
