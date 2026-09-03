import Foundation

/// Reads a plan's live limits from Anthropic's own endpoint, the one Claude
/// Code uses. Better than the figures a profile writes to disk in every way
/// that matters: exact reset times rather than times worked out from history,
/// and current values even for a profile that is not running.
enum UsageAPI {
    static let endpoint = URL(string: "https://api.anthropic.com/api/oauth/usage")!

    struct Reading: Equatable {
        var fiveHour: Int
        var week: Int
        var fiveHourReset: Date?
        var weekReset: Date?
        var plan: String?
        var fable: Int? = nil
        var fableReset: Date? = nil
    }

    enum Failure: LocalizedError {
        /// Carries `Retry-After` when the service sends one, so a caller can
        /// wait exactly as long as it was asked to.
        case http(Int, retryAfter: TimeInterval?)
        case unreadable

        var errorDescription: String? {
            switch self {
            case .http(401, _), .http(403, _):
                return L10n.text("That login was refused. Open the profile once so Claude can renew it.")
            case .http(429, _):
                return L10n.text("Anthropic is rate-limiting usage checks. The last figures stay on screen.")
            case .http(let code, _):
                return L10n.format("The usage service answered %ld.", code)
            case .unreadable:
                return L10n.text("The usage service sent something unexpected.")
            }
        }
    }

    /// Blocking; call it off the main thread.
    static func fetch(token: String, timeout: TimeInterval = 10) throws -> Reading {
        var request = URLRequest(url: endpoint, timeoutInterval: timeout)
        request.httpMethod = "GET"
        request.setValue("Bearer \(token.trimmingCharacters(in: .whitespacesAndNewlines))",
                         forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue("oauth-2025-04-20", forHTTPHeaderField: "anthropic-beta")

        var payload: Data?
        var response: URLResponse?
        var failure: Error?
        let finished = DispatchSemaphore(value: 0)
        URLSession.shared.dataTask(with: request) { data, urlResponse, error in
            payload = data
            response = urlResponse
            failure = error
            finished.signal()
        }.resume()
        _ = finished.wait(timeout: .now() + timeout + 5)

        if let failure { throw failure }
        let http = response as? HTTPURLResponse
        let code = http?.statusCode ?? 0
        guard code == 200 else {
            let header = http?.value(forHTTPHeaderField: "Retry-After")
            throw Failure.http(code, retryAfter: header.flatMap(TimeInterval.init))
        }
        guard let payload,
              let body = try? JSONSerialization.jsonObject(with: payload) as? [String: Any],
              let reading = reading(from: body)
        else { throw Failure.unreadable }
        return reading
    }

    /// Separated from the request so the shape of the answer can be tested.
    static func reading(from body: [String: Any]) -> Reading? {
        guard let session = window(body["five_hour"]) else { return nil }
        let weekly = window(body["seven_day"])
        let fable = fableWindow(body["limits"])
        return Reading(fiveHour: session.used,
                       week: weekly?.used ?? 0,
                       fiveHourReset: session.resets,
                       weekReset: weekly?.resets,
                       plan: (body["subscription_type"] as? String)?.capitalized,
                       fable: fable?.used,
                       fableReset: fable?.resets)
    }

    private static func window(_ value: Any?) -> (used: Int, resets: Date?)? {
        guard let object = value as? [String: Any],
              let used = percentage(object["utilization"])
        else { return nil }
        return (used, date(object["resets_at"]))
    }

    private static func fableWindow(_ value: Any?) -> (used: Int, resets: Date?)? {
        guard let limits = value as? [[String: Any]],
              let limit = limits.first(where: {
                  guard $0["kind"] as? String == "weekly_scoped",
                        let scope = $0["scope"] as? [String: Any],
                        let model = scope["model"] as? [String: Any],
                        let name = model["display_name"] as? String
                  else { return false }
                  return name.localizedCaseInsensitiveContains("fable")
              }),
              let used = percentage(limit["percent"])
        else { return nil }
        return (used, date(limit["resets_at"]))
    }

    private static func percentage(_ value: Any?) -> Int? {
        switch value {
        case let number as Int: return number
        case let number as Double: return Int(number.rounded())
        default: return nil
        }
    }

    /// `resets_at` comes back as an ISO timestamp, occasionally as epoch seconds.
    private static func date(_ value: Any?) -> Date? {
        if let text = value as? String {
            let withFraction = ISO8601DateFormatter()
            withFraction.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
            return withFraction.date(from: text) ?? ISO8601DateFormatter().date(from: text)
        }
        if let seconds = value as? Double { return Date(timeIntervalSince1970: seconds) }
        if let seconds = value as? Int { return Date(timeIntervalSince1970: Double(seconds)) }
        return nil
    }
}
