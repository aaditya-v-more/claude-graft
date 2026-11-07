import Foundation

/// An account Graft has been signed in to on claude.ai, for its artifacts.
///
/// The sign-in itself is not here and never passes through Graft: it lives in
/// the web view store named by `id`, and only pages from claude.ai, loaded into
/// that store, ever send it. What is kept on disk is what the account said
/// about itself once signed in, so the list can name it before it is asked.
struct ArtifactAccount: Codable, Equatable, Identifiable {
    var id: UUID
    var email: String?
    var accountUuid: String?
    var org: String?
}

/// People to share with in one go. claude.ai has no such thing for a personal
/// account, so the group is Graft's, and sharing with it invites each address.
struct ArtifactGroup: Codable, Equatable, Identifiable {
    var id: UUID
    var name: String
    var emails: [String]
}

struct Artifact: Equatable, Identifiable {
    /// The artifact's UUID, which every request is keyed by. The page address
    /// uses a shorter id of its own, which is why `openURL` is kept as given.
    var id: String
    /// The signed-in account that listed it.
    var account: UUID
    var org: String
    var title: String
    var kind: String
    var openURL: URL?
    var audience: String
    /// `mine`, or `shared` for one someone else owns.
    var rel: String
    var sortTime: Date?
    var ownerAccount: String?

    var isOwn: Bool { rel == "mine" }
    var access: ArtifactAccess { ArtifactAccess(audience: audience) }
}

/// Who can open an artifact, as the share panel on claude.ai says it.
enum ArtifactAccess: Equatable {
    case onlyYou
    case invited
    case organization
    case anyoneWithLink

    init(audience: String) {
        switch audience {
        case "public", "link": self = .anyoneWithLink
        case "org", "space": self = .organization
        case "users": self = .invited
        default: self = .onlyYou
        }
    }

    var label: String {
        switch self {
        case .onlyYou: return L10n.text("Only you")
        case .invited: return L10n.text("Only people invited")
        case .organization: return L10n.text("Your organization")
        case .anyoneWithLink: return L10n.text("Anyone with the link")
        }
    }

    var symbol: String {
        switch self {
        case .onlyYou: return "lock"
        case .invited: return "person.2"
        case .organization: return "building.2"
        case .anyoneWithLink: return "globe"
        }
    }
}

/// The requests claude.ai's own artifact pages make, measured against its
/// web client in November 2026. None of them is documented, so each is kept to
/// exactly the shape that page sends.
enum ArtifactAPI {
    static let origin = URL(string: "https://claude.ai")!
    /// The page a signed-in store is asked from. It answers with the account
    /// whether or not anything else is open, and it is claude.ai's own, so a
    /// request made from it carries the sign-in as the site's pages do.
    static let accountPath = "/api/account"
    static let signInURL = URL(string: "https://claude.ai/login")!

    /// Without the first of these the artifact service answers 404 to a
    /// request that is otherwise identical to the page's, as though the
    /// artifact did not exist.
    static let headers = ["X-Frame-CP": "go", "X-Frame-Platform": "web"]

    /// A page of 48 is what claude.ai asks for; five of them is more than a
    /// window could show, and stops a list that never ends from being followed.
    static let pageSize = 48
    static let maximumPages = 5

    static func listPath(rel: String, org: String, after token: String? = nil) -> String {
        var query = [("rel", rel), ("empty", "exclude"), ("limit", String(pageSize)),
                     ("org", org), ("sort", "last_viewed")]
        if let token { query.append(("page_token", token)) }
        return "/api/frame/artifacts?" + encode(query)
    }

    static func permPath(_ artifact: String, org: String) -> String {
        "/api/frame/perm/\(component(artifact))?" + encode([("org", org)])
    }

    static func invitePath(_ artifact: String, org: String) -> String {
        "/api/frame/invite/\(component(artifact))?" + encode([("org", org)])
    }

    /// The role claude.ai offers first, and the only one that cannot change
    /// the artifact.
    static func inviteBody(email: String) -> Data {
        (try? JSONSerialization.data(withJSONObject: ["email": email, "role": "reader"],
                                     options: [.sortedKeys])) ?? Data()
    }

    /// What the share panel sends to change who can open an artifact. Going
    /// back to private keeps whoever was invited: "users" with them, "owner"
    /// when there is nobody, which is what the panel itself does.
    static func readModeBody(public isPublic: Bool, invited: [String]) -> Data {
        let read: [String: Any] = isPublic ? ["mode": "public"]
            : invited.isEmpty ? ["mode": "owner"] : ["mode": "users", "users": invited]
        return (try? JSONSerialization.data(withJSONObject: ["read": read], options: [.sortedKeys])) ?? Data()
    }

    struct Identity: Equatable {
        var accountUuid: String
        var email: String
        var org: String
    }

    /// The organization artifacts live under is the one that can chat; an
    /// account in a team as well as on its own plan belongs to both, and the
    /// first membership listed is not reliably either.
    static func identity(from data: Data) -> Identity? {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let uuid = object["uuid"] as? String,
              let email = object["email_address"] as? String,
              let memberships = object["memberships"] as? [[String: Any]] else { return nil }
        let organizations = memberships.compactMap { $0["organization"] as? [String: Any] }
        let chat = organizations.first {
            (($0["capabilities"] as? [String]) ?? []).contains("chat")
        } ?? organizations.first
        guard let org = chat?["uuid"] as? String else { return nil }
        return Identity(accountUuid: uuid, email: email, org: org)
    }

    static func page(from data: Data, account: UUID, org: String) -> (artifacts: [Artifact], next: String?)? {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let rows = object["artifacts"] as? [[String: Any]] else { return nil }
        let artifacts = rows.compactMap { row -> Artifact? in
            guard let id = row["id"] as? String else { return nil }
            let title = (row["title"] as? String).flatMap { $0.isEmpty ? nil : $0 }
            return Artifact(id: id, account: account, org: org,
                            title: title ?? L10n.text("Untitled"),
                            kind: row["kind"] as? String ?? "page",
                            openURL: (row["open_url"] as? String).flatMap(URL.init(string:)),
                            audience: row["audience"] as? String ?? "owner",
                            rel: row["rel"] as? String ?? "mine",
                            sortTime: (row["sort_time"] as? String).flatMap(date)
                                ?? (row["updated_at"] as? String).flatMap(date),
                            ownerAccount: row["owner_account"] as? String)
        }
        let token = (object["next_page_token"] as? String).flatMap { $0.isEmpty ? nil : $0 }
        return (artifacts, token)
    }

    struct Permissions: Equatable {
        var version: String?
        var invited: [String]
    }

    static func permissions(from data: Data) -> Permissions? {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        let read = object["read"] as? [String: Any]
        return Permissions(version: object["version"] as? String ?? (object["version"] as? Int).map(String.init),
                           invited: read?["users"] as? [String] ?? [])
    }

    /// One artifact may come back from two accounts: one that owns it, and one
    /// it was shared with. It is listed once, under the owner.
    static func merge(_ lists: [[Artifact]]) -> [Artifact] {
        var seen: [String: Int] = [:]
        var merged: [Artifact] = []
        for artifact in lists.joined() {
            if let index = seen[artifact.id] {
                if artifact.isOwn && !merged[index].isOwn { merged[index] = artifact }
                continue
            }
            seen[artifact.id] = merged.count
            merged.append(artifact)
        }
        return merged.sorted { ($0.sortTime ?? .distantPast) > ($1.sortTime ?? .distantPast) }
    }

    private static func date(_ text: String) -> Date? {
        let fractional = ISO8601DateFormatter()
        fractional.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return fractional.date(from: text) ?? ISO8601DateFormatter().date(from: text)
    }

    private static func component(_ text: String) -> String {
        text.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed.subtracting(["/"])) ?? text
    }

    private static func encode(_ pairs: [(String, String)]) -> String {
        var allowed = CharacterSet.urlQueryAllowed
        allowed.remove(charactersIn: "&=+?")
        return pairs.map { "\($0)=\($1.addingPercentEncoding(withAllowedCharacters: allowed) ?? $1)" }
            .joined(separator: "&")
    }
}

/// What Graft keeps about artifacts, beside the rest of its own state.
enum ArtifactFiles {
    static var directory: URL { Graft.applicationSupport.appending(path: "ClaudeGraft") }
    static var accounts: URL { directory.appending(path: "artifact-accounts.json") }
    static var groups: URL { directory.appending(path: "artifact-groups.json") }

    static func load<T: Decodable>(_ type: T.Type, from url: URL) -> T? {
        guard let data = try? Data(contentsOf: url) else { return nil }
        return try? JSONDecoder().decode(type, from: data)
    }

    @discardableResult
    static func save<T: Encodable>(_ value: T, to url: URL) -> Bool {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        guard let data = try? encoder.encode(value) else { return false }
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(),
                                                 withIntermediateDirectories: true)
        return (try? data.write(to: url, options: .atomic)) != nil
    }
}

enum GroupAddresses {
    /// Addresses as people paste them: one per line, or a list out of a mail
    /// header with commas, semicolons and names in angle brackets. Each one
    /// once, in the order given, and anything that is not an address dropped.
    static func parse(_ text: String) -> [String] {
        var seen = Set<String>()
        var addresses: [String] = []
        let separators = CharacterSet(charactersIn: ",;").union(.whitespacesAndNewlines)
        for piece in text.components(separatedBy: separators) {
            let trimmed = piece.trimmingCharacters(in: CharacterSet(charactersIn: "<>\"'()"))
                .lowercased()
            guard isAddress(trimmed), seen.insert(trimmed).inserted else { continue }
            addresses.append(trimmed)
        }
        return addresses
    }

    static func isAddress(_ text: String) -> Bool {
        let parts = text.split(separator: "@", omittingEmptySubsequences: false)
        guard parts.count == 2, !parts[0].isEmpty else { return false }
        let domain = parts[1]
        return domain.contains(".") && !domain.hasPrefix(".") && !domain.hasSuffix(".")
    }
}
