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

/// What an invited person may do with an artifact, by the names claude.ai's
/// invitations use.
enum ArtifactRole: String, Codable, CaseIterable, Identifiable {
    case reader
    case commenter
    case editor

    var id: String { rawValue }

    var label: String {
        switch self {
        case .reader: return L10n.text("Can View")
        case .commenter: return L10n.text("Can Comment")
        case .editor: return L10n.text("Can Edit")
        }
    }

    /// The most an artifact will take of what was asked, never more: one
    /// that has no editors makes an editor a commenter if it can, a viewer
    /// otherwise.
    func offered(by permissions: ArtifactAPI.Permissions) -> ArtifactRole {
        switch self {
        case .editor where permissions.editors: return .editor
        case .editor, .commenter: return permissions.commenters ? .commenter : .reader
        case .reader: return .reader
        }
    }
}

/// People to share with in one go. claude.ai has no such thing for a personal
/// account, so the group is Graft's, and sharing with it invites each address.
///
/// Groups written before roles existed hold `emails` alone, and everyone in
/// them was invited as a viewer, which is what an address missing from
/// `roles` still means. So an old file reads as it always did, and a Graft
/// from before roles reads a new one as viewers only: less than was asked,
/// never more.
struct ArtifactGroup: Codable, Equatable, Identifiable {
    var id: UUID
    var name: String
    var emails: [String]
    var roles: [String: ArtifactRole] = [:]

    init(id: UUID, name: String, emails: [String], roles: [String: ArtifactRole] = [:]) {
        self.id = id
        self.name = name
        self.emails = emails
        self.roles = roles
    }

    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        id = try values.decode(UUID.self, forKey: .id)
        name = try values.decode(String.self, forKey: .name)
        emails = try values.decode([String].self, forKey: .emails)
        // One role this Graft does not know, from a newer one, costs that
        // address its role, not the whole group.
        let raw = (try? values.decodeIfPresent([String: String].self, forKey: .roles)) ?? nil
        roles = (raw ?? [:]).compactMapValues(ArtifactRole.init(rawValue:))
    }

    func encode(to encoder: Encoder) throws {
        var values = encoder.container(keyedBy: CodingKeys.self)
        try values.encode(id, forKey: .id)
        try values.encode(name, forKey: .name)
        try values.encode(emails, forKey: .emails)
        let kept = roles.filter { emails.contains($0.key) && $0.value != .reader }
        if !kept.isEmpty { try values.encode(kept, forKey: .roles) }
    }

    private enum CodingKeys: String, CodingKey { case id, name, emails, roles }

    func role(of email: String) -> ArtifactRole { roles[email] ?? .reader }

    mutating func setRole(_ role: ArtifactRole, for email: String) {
        roles[email] = role == .reader ? nil : role
    }

    /// Everyone given one role, the way a group of editors or of commenters
    /// is made.
    mutating func setRoleForEveryone(_ role: ArtifactRole) {
        roles = role == .reader ? [:] : Dictionary(uniqueKeysWithValues: emails.map { ($0, role) })
    }

    /// The one role everyone has, or nil for a group that mixes them.
    var commonRole: ArtifactRole? {
        let all = Set(emails.map(role(of:)))
        return all.count == 1 ? all.first : (emails.isEmpty ? .reader : nil)
    }
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
    /// What the list says can be done with it, so nothing is offered that
    /// claude.ai would refuse.
    var renamable = false
    var duplicable = false
    /// A copy claude.ai would make, but not today: it caps copies a day.
    var duplicateAvailable = true

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

    /// Viewer unless a group says otherwise for that person.
    static func inviteBody(email: String, role: ArtifactRole = .reader) -> Data {
        (try? JSONSerialization.data(withJSONObject: ["email": email, "role": role.rawValue],
                                     options: [.sortedKeys])) ?? Data()
    }

    static func retitlePath(_ artifact: String, org: String) -> String {
        "/api/frame/retitle/\(component(artifact))?" + encode([("org", org)])
    }

    static func retitleBody(_ title: String) -> Data {
        (try? JSONSerialization.data(withJSONObject: ["title": title], options: [.sortedKeys])) ?? Data()
    }

    /// Posted with no body: the copy is of the version the owner last saved.
    static func duplicatePath(_ artifact: String, org: String) -> String {
        "/api/frame/duplicate/\(component(artifact))?" + encode([("org", org)])
    }

    /// `DELETE` removes the artifact; `GET` is what its viewer asks first,
    /// and names the version and the short-lived token its files are read
    /// with.
    static func artifactPath(_ artifact: String, org: String) -> String {
        "/api/frame/\(component(artifact))?" + encode([("org", org)])
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
                            ownerAccount: row["owner_account"] as? String,
                            renamable: row["renamable"] as? Bool ?? false,
                            duplicable: row["duplicable"] as? Bool ?? false,
                            duplicateAvailable: row["duplicate_available"] as? Bool ?? true)
        }
        let token = (object["next_page_token"] as? String).flatMap { $0.isEmpty ? nil : $0 }
        return (artifacts, token)
    }

    struct Permissions: Equatable {
        var version: String?
        var invited: [String]
        /// Whether this artifact takes commenters and editors at all. Its
        /// own panel offers only the roles these allow.
        var commenters = false
        var editors = false
    }

    static func permissions(from data: Data) -> Permissions? {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        let read = object["read"] as? [String: Any]
        // Older answers name editing `writersEnabled`; a flag that is not
        // there offers nothing, so a change in the answer cannot hand out
        // more than viewing.
        let editors = object["editorsEnabled"] as? Bool ?? object["writersEnabled"] as? Bool ?? false
        return Permissions(version: object["version"] as? String ?? (object["version"] as? Int).map(String.init),
                           invited: read?["users"] as? [String] ?? [],
                           commenters: object["commentersEnabled"] as? Bool ?? false,
                           editors: editors)
    }

    /// Why claude.ai refused, when it says: `title_invalid`, `org_mismatch`,
    /// a daily cap on copies.
    static func reason(from data: Data) -> String? {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        if let reason = object["reason"] as? String { return reason }
        return (object["error"] as? [String: Any])?["reason"] as? String
    }

    /// What reading an artifact's files takes: the version they belong to
    /// and the token its viewer reads them with.
    struct Source: Equatable {
        var version: String
        var token: String
        var title: String?
        /// Relative paths, `index.html` first. Anything that could climb out
        /// of the folder it is written into is dropped here.
        var files: [String]
    }

    static func source(from data: Data) -> Source? {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let token = object["assetToken"] as? String, !token.isEmpty else { return nil }
        let version = object["ver"] as? String ?? (object["ver"] as? Int).map(String.init)
        guard let version, !version.isEmpty else { return nil }
        let listed = (object["files"] as? [[String: Any]] ?? []).compactMap { $0["path"] as? String }
        var files = ["index.html"]
        for path in listed where isSafeFilePath(path) && !files.contains(path) { files.append(path) }
        return Source(version: version, token: token, title: object["title"] as? String, files: files)
    }

    static func isSafeFilePath(_ path: String) -> Bool {
        let parts = path.split(separator: "/", omittingEmptySubsequences: false)
        return !path.isEmpty && !path.hasPrefix("/") && !path.contains("\\")
            && parts.allSatisfy { !$0.isEmpty && $0 != "." && $0 != ".." }
    }

    /// Where an artifact's files are served, exactly as its viewer reads
    /// them. The token goes in the address because that is the only way the
    /// file host takes it; it is the artifact's own, lasts minutes, and is
    /// asked for afresh every export.
    static func fileURL(_ artifact: String, source: Source, path: String) -> URL? {
        let host = artifact.lowercased().filter { $0.isHexDigit || $0 == "-" }
        guard !host.isEmpty, isSafeFilePath(path) else { return nil }
        let encodedPath = path.split(separator: "/").map { component(String($0)) }.joined(separator: "/")
        return URL(string: "https://\(host).frame.claudeusercontent.com/_f/\(component(source.version))/_src/"
                   + encodedPath + "?" + encode([("__frame_t", source.token)]))
    }

    /// The most an export reads, as claude.ai's own download does.
    static let maximumExportBytes = 200 * 1024 * 1024

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

    /// A file that is not there and one that is there but cannot be read are
    /// told apart, because only the first may be written over: saving an empty
    /// list over one that would not decode throws away every account or group
    /// in it, and an account thrown away leaves its store with nobody to
    /// remove it.
    enum Reading<Value> {
        case missing
        case unreadable
        case read(Value)
    }

    static func read<T: Decodable>(_ type: T.Type, from url: URL) -> Reading<T> {
        guard FileManager.default.fileExists(atPath: url.path) else { return .missing }
        guard let data = try? Data(contentsOf: url),
              let value = try? JSONDecoder().decode(type, from: data) else { return .unreadable }
        return .read(value)
    }

    /// A list read one entry at a time, so an entry a newer Graft or a damaged
    /// write left unreadable costs that entry and not the whole list. `lost`
    /// says how many were left out, which is when the file is set aside before
    /// anything is saved over it.
    static func readEach<T: Decodable>(_ type: T.Type, from url: URL) -> Reading<(values: [T], lost: Int)> {
        switch read([Lenient<T>].self, from: url) {
        case .missing: return .missing
        case .unreadable: return .unreadable
        case .read(let entries):
            let values = entries.compactMap(\.value)
            return .read((values, entries.count - values.count))
        }
    }

    private struct Lenient<T: Decodable>: Decodable {
        let value: T?
        init(from decoder: Decoder) throws { value = try? T(from: decoder) }
    }

    /// Moves a file that could not be read out of the way, beside where it
    /// was, so the next save starts a new one and the old one is still there
    /// to be read by hand. Nil when it could not be moved, and then nothing
    /// may be saved over it.
    @discardableResult
    static func setAside(_ url: URL, at now: Date = Date()) -> URL? {
        let name = url.deletingPathExtension().lastPathComponent
            + ".unreadable-\(Int(now.timeIntervalSince1970))." + url.pathExtension
        let target = url.deletingLastPathComponent().appending(path: name)
        return (try? FileManager.default.moveItem(at: url, to: target)) != nil ? target : nil
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

/// The forms claude.ai's own export menu offers that need nothing but this
/// Mac to make. Google Docs and Notion are left out: each is a connection to
/// another service, not a file.
enum ArtifactExportFormat: String, CaseIterable, Identifiable {
    case markdown
    case pdf
    case word
    case html

    var id: String { rawValue }

    var label: String {
        switch self {
        case .markdown: return L10n.text("Markdown")
        case .pdf: return L10n.text("PDF")
        case .word: return L10n.text("Word Document")
        case .html: return L10n.text("Page Source")
        }
    }

    /// A page made of several files goes out as all of them, zipped, since
    /// its own page alone would open with pieces missing.
    func fileExtension(singleFile: Bool) -> String {
        switch self {
        case .markdown: return "md"
        case .pdf: return "pdf"
        case .word: return "docx"
        case .html: return singleFile ? "html" : "zip"
        }
    }

    /// The artifact's title as a file name: what a save panel would refuse,
    /// or Finder would read as a folder, made a dash.
    static func fileName(for title: String, format: ArtifactExportFormat, singleFile: Bool) -> String {
        var name = title.replacingOccurrences(of: "/", with: "-")
            .replacingOccurrences(of: ":", with: "-")
            .trimmingCharacters(in: .whitespacesAndNewlines.union(CharacterSet(charactersIn: ".")))
        if name.count > 120 { name = String(name.prefix(120)).trimmingCharacters(in: .whitespaces) }
        if name.isEmpty { name = L10n.text("Artifact") }
        return name + "." + format.fileExtension(singleFile: singleFile)
    }
}
