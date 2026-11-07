import AppKit
import Combine
import Foundation

/// Every signed-in account's projects in one list, and one search across all
/// of their chats.
///
/// The accounts are the Artifacts pane's: the same claude.ai sign-in, in the
/// same web store, asked from the same page. Nothing here signs in or keeps a
/// sign-in of its own, so an account added or signed out in any one of the
/// three panes is added or gone in all of them at once.
@MainActor
final class ProjectCenter: ObservableObject {
    enum State: Equatable {
        case unknown
        case loading
        case ready
        case signedOut
        case failed
    }

    let accounts: ArtifactCenter
    @Published private(set) var projects: [ClaudeProject] = []
    @Published private(set) var projectStates: [UUID: State] = [:]

    /// Everything the last search found, before the filters.
    @Published private(set) var results: [SearchHit] = []
    @Published private(set) var searchStates: [UUID: State] = [:]
    /// The query the results answer, which is not always what is in the box.
    @Published private(set) var searched: String?
    @Published var filters = SearchFilters()

    /// Set for the layout checks, which draw sample rows and must never reach
    /// claude.ai.
    private let offline: Bool
    private var projectLists: [UUID: [ClaudeProject]] = [:]
    private var searchGeneration = 0
    /// Each account's whole Code session list, kept for the searches after
    /// the one that read it: three pages of two hundred is a lot to ask again
    /// for every word typed into the box.
    private var codeLists: [UUID: (read: Date, sessions: [RemoteCodeSession], complete: Bool)] = [:]
    static let codeListLifetime: TimeInterval = 120
    /// This Mac's transcripts, kept as long as the session lists: a cold read
    /// of three hundred of them took three seconds here, too long to stand in
    /// front of every search.
    private var localRead: (read: Date, sessions: [LocalCodeSession])?
    /// Whether the Projects pane has asked yet. A sign-in made before it was
    /// ever shown waits for it, like every other account.
    private var askedForProjects = false
    private var watching: [AnyCancellable] = []

    init(accounts: ArtifactCenter) {
        self.accounts = accounts
        offline = false
        // `@Published` hands over the new list before the property holds it,
        // so the list is taken from what is handed over.
        watching.append(accounts.$accounts.dropFirst().sink { [weak self] list in
            self?.accountsChanged(to: Set(list.map(\.id)))
        })
        watching.append(accounts.$lastSignIn.compactMap { $0 }.sink { [weak self] signIn in
            self?.signedIn(signIn.account)
        })
    }

    /// For the layout checks, which must not reach claude.ai.
    init(accounts: ArtifactCenter, projects: [ClaudeProject], results: [SearchHit], searched: String?) {
        self.accounts = accounts
        self.projects = projects
        self.results = results
        self.searched = searched
        offline = true
        for account in accounts.accounts {
            projectStates[account.id] = .ready
            searchStates[account.id] = .ready
        }
    }

    var loadingProjects: Bool { projectStates.values.contains(.loading) }
    var searching: Bool { searchStates.values.contains(.loading) }

    /// The accounts claude.ai would not answer for, by name, for the one line
    /// under a list.
    func trouble(in states: [UUID: State]) -> [String] {
        accounts.accounts.compactMap { account in
            switch states[account.id] {
            case .signedOut: return L10n.format("Sign in to %@ again", accounts.name(of: account.id))
            case .failed: return L10n.format("%@ didn't answer", accounts.name(of: account.id))
            default: return nil
            }
        }
    }

    func projectName(_ id: String?, account: UUID) -> String? {
        guard let id else { return nil }
        return projects.first { $0.id == id && $0.account == account }?.name
    }

    // MARK: - Accounts

    /// An account signed out anywhere leaves every list here with it.
    private func accountsChanged(to ids: Set<UUID>) {
        projectLists = projectLists.filter { ids.contains($0.key) }
        projectStates = projectStates.filter { ids.contains($0.key) }
        searchStates = searchStates.filter { ids.contains($0.key) }
        codeLists = codeLists.filter { ids.contains($0.key) }
        projects = projects.filter { ids.contains($0.account) }
        results = results.filter { ids.contains($0.account) }
        if let account = filters.account, !ids.contains(account) { filters.account = nil }
    }

    /// An account signed in from any pane is asked for here as well, once,
    /// because a person just asked for it; a search on screen is run again
    /// so it covers the new account too.
    private func signedIn(_ id: UUID) {
        guard !offline else { return }
        codeLists[id] = nil
        projectStates[id] = nil
        if askedForProjects { Task { await loadProjects(id) } }
        if let searched { search(searched) }
    }

    // MARK: - Projects

    /// From the pane's appearance and its refresh button, never a timer.
    func refreshProjects() {
        guard !offline else { return }
        askedForProjects = true
        accounts.refreshNames()
        Task {
            await withTaskGroup(of: Void.self) { group in
                for account in accounts.accounts { group.addTask { await self.loadProjects(account.id) } }
            }
        }
    }

    private func loadProjects(_ id: UUID) async {
        guard projectStates[id] != .loading else { return }
        projectStates[id] = .loading
        do {
            let org = try await accounts.organization(of: id)
            let session = accounts.session(id)
            var found: [ClaudeProject] = []
            for page in 0..<ClaudeAPI.maximumProjectPages {
                let answer = try await session.send(
                    "GET", ClaudeAPI.projectsPath(org: org, offset: page * ClaudeAPI.projectPageSize))
                guard answer.status == 200, let list = ClaudeAPI.projects(from: answer.data, account: id)
                else { throw ClaudeWebSession.Failure.unreadable }
                found += list.projects
                if !list.hasMore { break }
            }
            projectLists[id] = found
            projectStates[id] = .ready
            accounts.noteSignIn(id, accepted: true)
        } catch ClaudeWebSession.Failure.signedOut {
            projectLists[id] = nil
            projectStates[id] = .signedOut
            accounts.noteSignIn(id, accepted: false)
        } catch {
            projectStates[id] = .failed
            Diagnostics.note("projects.list failed", ["reason": "\(error)"])
        }
        let signedIn = Set(accounts.accounts.map(\.id))
        projectLists = projectLists.filter { signedIn.contains($0.key) }
        projects = ClaudeAPI.merge(accounts.accounts.compactMap { projectLists[$0.id] })
    }

    // MARK: - Search

    /// What the filters let through, in the order found.
    var shown: [SearchHit] {
        let now = Date()
        return results.filter { filters.admits($0, now: now) }
    }

    /// When a person presses Return or the search button, never as they type:
    /// each search asks every account, and claude.ai's own box asks once per
    /// pause, not once per key. The filters narrow what came back and ask
    /// nothing.
    func search(_ text: String) {
        let query = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !offline else { return }
        searchGeneration += 1
        let generation = searchGeneration
        guard !query.isEmpty else {
            clearSearch()
            return
        }
        searched = query
        results = []
        let ids = accounts.accounts.map(\.id)
        for id in ids { searchStates[id] = .loading }
        if projects.isEmpty { refreshProjects() }
        Task {
            let local: [LocalCodeSession]
            if let kept = localRead, Date().timeIntervalSince(kept.read) < Self.codeListLifetime {
                local = kept.sessions
            } else {
                local = await Task.detached(priority: .userInitiated) { LocalCodeSessions.read() }.value
                localRead = (Date(), local)
            }
            var lists: [UUID: [SearchHit]] = [:]
            await withTaskGroup(of: (UUID, Result<[SearchHit], Error>).self) { group in
                for id in ids {
                    group.addTask { (id, await self.searchOne(id, query, local: local)) }
                }
                for await (id, outcome) in group {
                    // A newer search has started; this one's answers would
                    // only mix into its list.
                    guard generation == self.searchGeneration else { continue }
                    switch outcome {
                    case .success(let found):
                        lists[id] = found
                        searchStates[id] = .ready
                        accounts.noteSignIn(id, accepted: true)
                    case .failure(ClaudeWebSession.Failure.signedOut):
                        searchStates[id] = .signedOut
                        accounts.noteSignIn(id, accepted: false)
                    case .failure(let error):
                        searchStates[id] = .failed
                        Diagnostics.note("search failed", ["reason": "\(error)"])
                    }
                    results = ClaudeAPI.interleave(ids.compactMap { lists[$0] })
                }
            }
        }
    }

    private func searchOne(_ id: UUID, _ query: String, local: [LocalCodeSession]) async -> Result<[SearchHit], Error> {
        do {
            let org = try await accounts.organization(of: id)
            let session = accounts.session(id)
            let answer = try await session.send("POST", ClaudeAPI.searchPath(org: org),
                                                body: ClaudeAPI.searchBody(query: query))
            guard answer.status == 200, let chats = ClaudeAPI.chats(from: answer.data, account: id)
            else { throw ClaudeWebSession.Failure.unreadable }
            let code = try await codeSessions(id, org: org, session: session)
            let hits = ClaudeAPI.codeHits(query, account: id, accountUuid: accounts.account(id)?.accountUuid,
                                          remote: code.sessions, complete: code.complete, local: local)
            return .success(chats + hits)
        } catch {
            return .failure(error)
        }
    }

    /// An account with no Code sessions, or whose session list will not
    /// answer, still has its chats searched; only the chats' failure fails it.
    private func codeSessions(_ id: UUID, org: String,
                              session: ClaudeWebSession) async throws -> (sessions: [RemoteCodeSession], complete: Bool) {
        if let kept = codeLists[id], Date().timeIntervalSince(kept.read) < Self.codeListLifetime {
            return (kept.sessions, kept.complete)
        }
        var found: [RemoteCodeSession] = []
        var cursor: String?
        var complete = false
        do {
            for _ in 0..<ClaudeAPI.maximumCodePages {
                let answer = try await session.send("GET", ClaudeAPI.codeSessionsPath(after: cursor),
                                                    headers: ClaudeAPI.codeHeaders(org: org))
                guard answer.status == 200, let page = ClaudeAPI.codeSessions(from: answer.data) else {
                    Diagnostics.note("search.code-sessions refused", ["status": "\(answer.status)"])
                    return (found, false)
                }
                found += page.sessions
                cursor = page.next
                if cursor == nil { complete = true; break }
            }
        } catch ClaudeWebSession.Failure.signedOut {
            throw ClaudeWebSession.Failure.signedOut
        } catch {
            return (found, false)
        }
        codeLists[id] = (Date(), found, complete)
        return (found, complete)
    }

    func clearSearch() {
        searchGeneration += 1
        searched = nil
        results = []
        searchStates = [:]
    }

    /// A session claude.ai no longer has is shown where its transcript is.
    func open(_ hit: SearchHit) {
        if let url = hit.openURL {
            NSWorkspace.shared.open(url)
        } else if let transcript = hit.transcript {
            NSWorkspace.shared.activateFileViewerSelecting([transcript])
        }
    }

    func open(_ url: URL) {
        NSWorkspace.shared.open(url)
    }
}
