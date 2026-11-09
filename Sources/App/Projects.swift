import Foundation

/// A project on claude.ai, as its own Projects page lists it.
struct ClaudeProject: Equatable, Identifiable {
    var id: String
    /// The signed-in account that listed it.
    var account: UUID
    var name: String
    var summary: String
    var isPrivate: Bool
    var isStarred: Bool
    /// The "How to use Claude" project every account starts with.
    var isExample: Bool
    var creator: String?
    var updated: Date?

    var openURL: URL { ClaudeAPI.origin.appending(path: "project/\(id)") }
}

/// One thing a search found: a chat claude.ai's search matched, or a Code
/// session whose title or folder did.
struct SearchHit: Equatable, Identifiable {
    enum Kind: Equatable {
        case chat
        case code
    }

    /// Where a Code session ran, which is what says whether it can be picked
    /// up again on this Mac. A chat is always on claude.ai and has none.
    enum Place: Equatable {
        case thisMac
        /// Remote Control from a machine whose transcript is not here, or one
        /// carried here from a home folder this Mac does not have.
        case otherMac
        case cloud
        /// A transcript on this Mac that claude.ai no longer lists for its
        /// account: deleted there, or never sent.
        case goneFromClaude
    }

    var id: String
    var account: UUID
    var kind: Kind
    var title: String
    var snippet: String
    /// Where the query sits in `snippet` and in `title`, counted in UTF-16
    /// units, which is how the page's script counts them.
    var snippetMatches: [Range<Int>]
    var titleMatches: [Range<Int>]
    var project: String?
    var updated: Date?
    var isArchived: Bool
    var isStarred: Bool
    var place: Place?
    /// The folder a Code session worked in, when this Mac has its transcript.
    var folder: String?
    var transcript: URL?
    /// The order claude.ai ranked it in, within its own account.
    var rank: Int
    /// Nil for a session claude.ai no longer has, which is shown in Finder
    /// instead.
    var openURL: URL?
}

/// A Code session as claude.ai's own list has it.
struct RemoteCodeSession: Equatable {
    var id: String
    var title: String
    var isArchived: Bool
    var inCloud: Bool
    var updated: Date?
    var repository: String?
}

/// What this Mac's transcripts say about a Code session.
struct LocalCodeSession: Equatable {
    var bridgeIds: [String]
    var owner: String
    /// `ownerOrganizationUuid`: an account in two organizations has a
    /// session list in each, and only one of them is read.
    var organization: String
    var title: String
    var folder: String
    var transcript: URL
    var lastActive: Date
    var folderIsHere: Bool
    /// A record names it among the sessions its conversation grew out of. The
    /// conversation goes on under a later session, often under another
    /// account's list, so this half not being listed says nothing about it.
    var carriedOn = false
}

/// The requests claude.ai's Projects page and its search box make, read off
/// its web client in November 2026. None is documented, so each is kept to
/// exactly what that page sends. Unlike the artifact service, none takes the
/// `X-Frame-*` headers; `ClaudeWebSession` adds those to `/api/frame` alone.
enum ClaudeAPI {
    static let origin = ArtifactAPI.origin

    /// Thirty is what the Projects page asks for. Five pages is more projects
    /// than anyone keeps, and stops a list that never ends from being followed.
    static let projectPageSize = 30
    static let maximumProjectPages = 5

    static func projectsPath(org: String, offset: Int = 0) -> String {
        "/api/organizations/\(component(org))/projects_v2?" + encode([
            ("include_harmony_projects", "true"), ("limit", String(projectPageSize)),
            ("offset", String(offset)), ("order_by", "latest_chat"),
        ])
    }

    static func searchPath(org: String) -> String {
        "/api/organizations/\(component(org))/conversation/search/v2"
    }

    /// Twenty-five results with a hundred characters around each match is what
    /// the search box asks for. Archived chats come back with the rest, marked;
    /// the box hides them itself until "Show archived" is pressed.
    static let searchLimit = 25

    static func searchBody(query: String) -> Data {
        let body: [String: Any] = ["query": query, "n": searchLimit, "target_snippet_size": 100]
        return (try? JSONSerialization.data(withJSONObject: body, options: [.sortedKeys])) ?? Data()
    }

    /// The search box's Code tab asks for every session, active and archived
    /// alike, and matches titles in the page. Two hundred a page is what it
    /// asks; ten pages is four times what a heavy account here holds.
    static let codePageSize = 200
    static let maximumCodePages = 10

    static func codeSessionsPath(after cursor: String? = nil) -> String {
        var query = [("limit", String(codePageSize))]
        if let cursor { query.append(("cursor", cursor)) }
        return "/v1/code/sessions?" + encode(query)
    }

    /// Without the beta and the organization the session service answers 400.
    static func codeHeaders(org: String) -> [String: String] {
        ["anthropic-beta": "ccr-byoc-2025-07-29", "anthropic-version": "2023-06-01",
         "x-organization-uuid": org]
    }

    /// A session is listed as `cse_…` and opened as `session_…`, the same
    /// name after the prefix.
    static func codeSessionURL(_ id: String) -> URL {
        let name = id.hasPrefix("cse_") ? "session_" + id.dropFirst(4) : id
        return origin.appending(path: "code/\(name)")
    }

    static func projects(from data: Data, account: UUID) -> (projects: [ClaudeProject], hasMore: Bool)? {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let rows = object["data"] as? [[String: Any]] else { return nil }
        let projects = rows.compactMap { row -> ClaudeProject? in
            guard let id = row["uuid"] as? String else { return nil }
            // An archived project is off the Projects page, so it is off here.
            if let archived = row["archived_at"], !(archived is NSNull) { return nil }
            let name = (row["name"] as? String).flatMap { $0.isEmpty ? nil : $0 }
            return ClaudeProject(id: id, account: account,
                                 name: name ?? L10n.text("Untitled"),
                                 summary: (row["description"] as? String) ?? "",
                                 isPrivate: row["is_private"] as? Bool ?? true,
                                 isStarred: row["is_starred"] as? Bool ?? false,
                                 isExample: row["is_starter_project"] as? Bool ?? false,
                                 creator: (row["creator"] as? [String: Any])?["full_name"] as? String,
                                 updated: (row["updated_at"] as? String).flatMap(date))
        }
        let pagination = object["pagination"] as? [String: Any]
        return (projects, pagination?["has_more"] as? Bool ?? false)
    }

    static func chats(from data: Data, account: UUID) -> [SearchHit]? {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let rows = object["data"] as? [[String: Any]] else { return nil }
        return rows.enumerated().compactMap { index, row -> SearchHit? in
            guard let conversation = row["conversation"] as? [String: Any],
                  let id = conversation["uuid"] as? String else { return nil }
            let snippet = row["matched_snippet"] as? [String: Any]
            let title = (conversation["name"] as? String).flatMap { $0.isEmpty ? nil : $0 }
            return SearchHit(id: id, account: account, kind: .chat,
                             title: title ?? L10n.text("Untitled"),
                             snippet: (snippet?["text"] as? String) ?? "",
                             snippetMatches: ranges(snippet?["matches"]),
                             titleMatches: title == nil ? [] : ranges(row["title_matches"]),
                             project: conversation["project_uuid"] as? String,
                             updated: (conversation["updated_at"] as? String).flatMap(date),
                             isArchived: conversation["is_archived"] as? Bool ?? false,
                             isStarred: conversation["is_starred"] as? Bool ?? false,
                             place: nil, folder: nil, transcript: nil,
                             rank: (row["semantic_rank"] as? NSNumber)?.intValue ?? index,
                             openURL: origin.appending(path: "chat/\(id)"))
        }
    }

    static func codeSessions(from data: Data) -> (sessions: [RemoteCodeSession], next: String?)? {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let rows = object["data"] as? [[String: Any]] else { return nil }
        let sessions = rows.compactMap { row -> RemoteCodeSession? in
            guard let id = row["id"] as? String else { return nil }
            let sources = ((row["config"] as? [String: Any])?["sources"] as? [[String: Any]]) ?? []
            return RemoteCodeSession(id: id,
                                     title: (row["title"] as? String).flatMap { $0.isEmpty ? nil : $0 }
                                        ?? L10n.text("Untitled"),
                                     isArchived: row["status"] as? String == "archived",
                                     inCloud: row["environment_kind"] as? String == "anthropic_cloud",
                                     updated: (row["last_event_at"] as? String).flatMap(date)
                                        ?? (row["updated_at"] as? String).flatMap(date),
                                     repository: sources.first?["url"] as? String)
        }
        let next = (object["next_cursor"] as? String).flatMap { $0.isEmpty ? nil : $0 }
        return (sessions, next)
    }

    /// Every word of the query, in any order, somewhere in what is searched,
    /// as the Code tab matches; the title's own matches are picked out.
    static func codeMatch(_ query: String, title: String, also other: [String?]) -> [Range<Int>]? {
        let words = query.lowercased().split(whereSeparator: \.isWhitespace).map(String.init)
        guard !words.isEmpty else { return nil }
        let lowered = title.lowercased()
        let haystack = ([lowered] + other.compactMap { $0?.lowercased() }).joined(separator: "\n")
        guard words.allSatisfy(haystack.contains) else { return nil }
        let units = Array(lowered.utf16)
        return words.compactMap { word -> Range<Int>? in
            let needle = Array(word.utf16)
            guard needle.count <= units.count else { return nil }
            for start in 0...(units.count - needle.count) where Array(units[start..<start + needle.count]) == needle {
                return start..<start + needle.count
            }
            return nil
        }
    }

    /// One account's Code sessions that match, each placed by what this Mac's
    /// transcripts say about it, and the transcripts claude.ai no longer has.
    ///
    /// A session is only called gone when the account's whole list was read:
    /// one past the last page read is simply not known. So is one owned under
    /// another organization than the list was read for, which that list never
    /// holds, one whose transcript moved on after the list was read, which
    /// may have begun since, and an earlier half of a conversation still going.
    static func codeHits(_ query: String, account: UUID, accountUuid: String?, organization: String?,
                         remote: [RemoteCodeSession], complete: Bool, listedAt: Date?,
                         local: [LocalCodeSession]) -> [SearchHit] {
        var byBridge: [String: LocalCodeSession] = [:]
        for session in local {
            for id in session.bridgeIds { byBridge[id] = session }
        }
        var hits: [SearchHit] = []
        for (index, session) in remote.enumerated() {
            let here = byBridge[session.id]
            guard let matches = codeMatch(query, title: session.title,
                                          also: [session.repository, here?.folder]) else { continue }
            let place: SearchHit.Place = session.inCloud ? .cloud
                : here.map { $0.folderIsHere ? .thisMac : .otherMac } ?? .otherMac
            hits.append(SearchHit(id: session.id, account: account, kind: .code, title: session.title,
                                  snippet: here?.folder ?? session.repository ?? "",
                                  snippetMatches: [], titleMatches: matches, project: nil,
                                  updated: session.updated, isArchived: session.isArchived, isStarred: false,
                                  place: place, folder: here?.folder, transcript: here?.transcript,
                                  rank: index, openURL: codeSessionURL(session.id)))
        }
        guard complete, let accountUuid, let organization, let listedAt else { return hits }
        let listed = Set(remote.map(\.id))
        for session in local where session.owner == accountUuid && session.organization == organization
            && session.lastActive < listedAt && !session.carriedOn
            && !session.bridgeIds.contains(where: listed.contains) {
            guard let matches = codeMatch(query, title: session.title, also: [session.folder]) else { continue }
            hits.append(SearchHit(id: "local:" + session.transcript.path, account: account, kind: .code,
                                  title: session.title, snippet: session.folder, snippetMatches: [],
                                  titleMatches: matches, project: nil, updated: session.lastActive,
                                  isArchived: false, isStarred: false, place: .goneFromClaude,
                                  folder: session.folder, transcript: session.transcript,
                                  rank: hits.count, openURL: nil))
        }
        return hits
    }

    /// Whether a read of this Mac's transcripts can stand beside a session
    /// list: still fresh, and made no earlier than the list. One made earlier
    /// is missing whatever began here in between, which the list holds and
    /// would place on another Mac.
    static func localReadServes(_ read: Date, list: Date, now: Date, lifetime: TimeInterval) -> Bool {
        read >= list && now.timeIntervalSince(read) < lifetime
    }

    /// Every account's projects in one list: starred ones first, as on
    /// claude.ai, then the most recently changed.
    static func merge(_ lists: [[ClaudeProject]]) -> [ClaudeProject] {
        lists.joined().sorted { left, right in
            if left.isStarred != right.isStarred { return left.isStarred }
            return (left.updated ?? .distantPast) > (right.updated ?? .distantPast)
        }
    }

    /// Each account ranks its own chats and the ranks of two accounts mean
    /// nothing against each other, so the lists are dealt in turn: every
    /// account's best, then every account's second. Code sessions have no
    /// rank but their age, and follow, newest first.
    static func interleave(_ lists: [[SearchHit]]) -> [SearchHit] {
        let chats = lists.map { $0.filter { $0.kind == .chat }.sorted { $0.rank < $1.rank } }
        var merged: [SearchHit] = []
        for position in 0..<(chats.map(\.count).max() ?? 0) {
            for list in chats where position < list.count { merged.append(list[position]) }
        }
        merged += lists.joined().filter { $0.kind == .code }
            .sorted { ($0.updated ?? .distantPast) > ($1.updated ?? .distantPast) }
        return merged
    }

    /// The parts of `text` a search matched, picked out, with any range that
    /// does not fit the text dropped rather than trusted.
    static func highlighted(_ text: String, _ ranges: [Range<Int>]) -> AttributedString {
        var result = AttributedString(text)
        let units = text.utf16
        for range in ranges where range.lowerBound >= 0 && range.upperBound <= units.count
            && range.lowerBound < range.upperBound {
            let start = units.index(units.startIndex, offsetBy: range.lowerBound)
            let end = units.index(units.startIndex, offsetBy: range.upperBound)
            guard let lower = start.samePosition(in: text), let upper = end.samePosition(in: text),
                  let from = AttributedString.Index(lower, within: result),
                  let to = AttributedString.Index(upper, within: result) else { continue }
            result[from..<to].inlinePresentationIntent = .stronglyEmphasized
        }
        return result
    }

    private static func ranges(_ value: Any?) -> [Range<Int>] {
        ((value as? [[String: Any]]) ?? []).compactMap { match in
            guard let start = (match["start"] as? NSNumber)?.intValue,
                  let end = (match["end"] as? NSNumber)?.intValue, start < end else { return nil }
            return start..<end
        }
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

/// What a person narrows a search to. Every filter is applied to what came
/// back, so changing one asks claude.ai nothing.
struct SearchFilters: Equatable {
    enum Kind: String, CaseIterable, Identifiable {
        case all, chats, code
        var id: String { rawValue }
    }

    enum Status: String, CaseIterable, Identifiable {
        case active, archived, any
        var id: String { rawValue }
    }

    enum Place: String, CaseIterable, Identifiable {
        case anywhere, thisMac, otherMac, cloud, goneFromClaude
        var id: String { rawValue }

        var hitPlace: SearchHit.Place? {
            switch self {
            case .anywhere: return nil
            case .thisMac: return .thisMac
            case .otherMac: return .otherMac
            case .cloud: return .cloud
            case .goneFromClaude: return .goneFromClaude
            }
        }
    }

    enum Age: String, CaseIterable, Identifiable {
        case any, day, week, month, year
        var id: String { rawValue }

        var span: TimeInterval? {
            switch self {
            case .any: return nil
            case .day: return 86_400
            case .week: return 7 * 86_400
            case .month: return 31 * 86_400
            case .year: return 366 * 86_400
            }
        }
    }

    /// A project of a chat, or chats in no project at all.
    enum ProjectChoice: Equatable, Hashable {
        case any
        case none
        case project(String)
    }

    var kind = Kind.all
    /// Active only, as claude.ai's own box starts.
    var status = Status.active
    var place = Place.anywhere
    var age = Age.any
    var account: UUID?
    var project = ProjectChoice.any
    var starredOnly = false

    var isNarrowed: Bool { self != SearchFilters() }

    /// A place, a project or a star is something only one kind has, so asking
    /// for one leaves the other kind out rather than letting it through.
    func admits(_ hit: SearchHit, now: Date) -> Bool {
        switch kind {
        case .all: break
        case .chats: guard hit.kind == .chat else { return false }
        case .code: guard hit.kind == .code else { return false }
        }
        switch status {
        case .any: break
        case .active: guard !hit.isArchived else { return false }
        case .archived: guard hit.isArchived else { return false }
        }
        if let wanted = place.hitPlace, hit.place != wanted { return false }
        if let span = age.span {
            guard let updated = hit.updated, now.timeIntervalSince(updated) <= span else { return false }
        }
        if let account, hit.account != account { return false }
        switch project {
        case .any: break
        case .none: guard hit.kind == .chat, hit.project == nil else { return false }
        case .project(let id): guard hit.project == id else { return false }
        }
        if starredOnly, !hit.isStarred { return false }
        return true
    }
}

/// This Mac's Code transcripts, by the ids claude.ai lists their sessions
/// under. Read only: what the sweep already parsed comes out of its cache, and
/// a transcript it has not reached yet is parsed here without being added to
/// it, so nothing the sweep relies on is written from a search.
enum LocalCodeSessions {
    static func read() -> [LocalCodeSession] {
        let fm = FileManager.default
        let cached = (try? Data(contentsOf: Graft.transcriptCacheFile))
            .flatMap { try? JSONDecoder().decode([String: Graft.CachedTranscript].self, from: $0) } ?? [:]
        var sessions: [LocalCodeSession] = []
        let carriedOn = Graft.sessionStoreContents().superseded
        let root = Graft.claudeProjects
        let folders = (try? fm.contentsOfDirectory(atPath: root.path)) ?? []
        for folder in folders {
            let directory = root.appending(path: folder)
            let names = (try? fm.contentsOfDirectory(atPath: directory.path)) ?? []
            for name in names where name.hasSuffix(".jsonl") {
                let url = directory.appending(path: name)
                guard let attributes = try? fm.attributesOfItem(atPath: url.path) else { continue }
                let modified = (attributes[.modificationDate] as? Date)?.timeIntervalSince1970 ?? 0
                let size = (attributes[.size] as? NSNumber)?.intValue ?? 0
                let facts: Graft.SessionFacts?
                if let entry = cached[url.path], entry.modified == modified, entry.size == size {
                    facts = entry.facts
                } else {
                    facts = Graft.sessionFacts(inTranscriptAt: url, cliSessionId: String(name.dropLast(6)))
                }
                guard let facts else { continue }
                sessions.append(LocalCodeSession(
                    bridgeIds: facts.bridgeIds, owner: facts.ownerAccount,
                    organization: facts.ownerOrganization, title: facts.title,
                    folder: facts.cwd, transcript: url,
                    lastActive: Date(timeIntervalSince1970: facts.lastActivityAt / 1000),
                    folderIsHere: Graft.sessionFolderIsHere(facts.cwd),
                    carriedOn: carriedOn.contains(String(name.dropLast(6)))))
            }
        }
        return sessions
    }
}
