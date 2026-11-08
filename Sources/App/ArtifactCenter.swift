import AppKit
import Foundation
import WebKit

/// Asks claude.ai for one account's artifacts from inside a page of claude.ai.
///
/// The account's sign-in is a cookie in a web view store of its own, and the
/// requests go out from a claude.ai page loaded into that store, the way the
/// site's own pages make them. Graft never reads the cookie, never copies it
/// into a request of its own, and has nowhere to send it but claude.ai: the
/// store is not shared with any other site, and the only page ever loaded
/// into it outside the sign-in window is claude.ai's.
@MainActor
final class ClaudeWebSession: NSObject, WKNavigationDelegate {
    enum Failure: Error, Equatable {
        /// claude.ai did not take the sign-in, or there is none.
        case signedOut
        case unreachable
        case unreadable
    }

    let account: UUID
    private var webView: WKWebView?
    private var loading: [CheckedContinuation<Void, Error>] = []
    private var isLoaded = false
    /// Set when the account is signed out or replaced. Its store is about to
    /// be removed, and a page made on it now would keep the store, and the
    /// sign-in in it, alive.
    private var closed = false
    private var inFlight = 0
    private var drained: [CheckedContinuation<Void, Never>] = []

    init(account: UUID) {
        self.account = account
    }

    /// Each account its own store, so two sign-ins never meet. macOS 13 has no
    /// store that is both separate and kept, so there the sign-in lasts until
    /// Graft quits.
    static func dataStore(for account: UUID) -> WKWebsiteDataStore {
        if #available(macOS 14, *) { return WKWebsiteDataStore(forIdentifier: account) }
        if let store = volatile[account] { return store }
        let store = WKWebsiteDataStore.nonPersistent()
        volatile[account] = store
        return store
    }

    private static var volatile: [UUID: WKWebsiteDataStore] = [:]

    /// WebKit refuses to remove a store while a web view on it is alive, and
    /// one can outlive the last reference to it by a moment, so a refusal is
    /// written down and tried again. A store that still will not go is left
    /// to the next launch, which removes every store no account is listed
    /// under.
    static func forget(_ account: UUID) async {
        volatile[account] = nil
        guard #available(macOS 14, *) else { return }
        for attempt in 1...3 {
            do {
                try await WKWebsiteDataStore.remove(forIdentifier: account)
                return
            } catch {
                Diagnostics.note("artifacts.store removal failed", ["attempt": attempt, "error": "\(error)"])
                try? await Task.sleep(nanoseconds: UInt64(attempt) * 2_000_000_000)
            }
        }
    }

    /// The address bar of a Safari, so the sign-in page offers what it offers
    /// there rather than refusing a browser it does not recognize.
    static let userAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 "
        + "(KHTML, like Gecko) Version/18.0 Safari/605.1.15"

    static func configuration(for account: UUID) -> WKWebViewConfiguration {
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = dataStore(for: account)
        return configuration
    }

    func send(_ method: String, _ path: String, body: Data? = nil,
              headers: [String: String] = [:]) async throws -> (status: Int, data: Data) {
        try await ready()
        guard let webView else { throw Failure.unreachable }
        // The artifact service answers 404 without its own headers; the rest
        // of claude.ai's API is asked as its pages ask it, without them.
        var all = path.hasPrefix("/api/frame/") ? ArtifactAPI.headers : [:]
        all.merge(headers) { $1 }
        if body != nil { all["Content-Type"] = "application/json" }
        let script = """
            const response = await fetch(path, {method, credentials: "same-origin", headers,
                                                body: body === "" ? undefined : body});
            return {status: response.status, body: await response.text()};
            """
        let arguments: [String: Any] = ["path": path, "method": method, "headers": all,
                                        "body": body.map { String(decoding: $0, as: UTF8.self) } ?? ""]
        inFlight += 1
        defer { requestEnded() }
        let result: Any?
        do {
            result = try await webView.callAsyncJavaScript(script, arguments: arguments,
                                                           contentWorld: .defaultClient)
        } catch {
            throw Failure.unreachable
        }
        guard let answer = result as? [String: Any],
              let status = (answer["status"] as? NSNumber)?.intValue,
              let text = answer["body"] as? String else { throw Failure.unreadable }
        if ArtifactAPI.meansSignedOut(status, at: path) { throw Failure.signedOut }
        if ArtifactAPI.needsAccountCheck(status, at: path) {
            _ = try await send("GET", ArtifactAPI.accountPath)
        }
        return (status, Data(text.utf8))
    }

    /// One of an artifact's files, read from the host its viewer reads it
    /// from. No cookie goes with it: the file host is not claude.ai, and the
    /// token in the address is all it takes, as it is for claude.ai's own
    /// viewer. The bytes come back as base64, the one way binary survives the
    /// trip out of a page.
    ///
    /// `limit` is what is left of the export's budget, and it is held to in
    /// the page, before the file is encoded there: a file read whole first and
    /// measured after can take the page's process down with it, and that
    /// leaves the account answering nothing.
    func download(_ url: URL, limit: Int) async throws -> (status: Int, data: Data) {
        try await ready()
        guard let webView else { throw Failure.unreachable }
        let script = """
            const response = await fetch(url, {credentials: "omit", cache: "no-store",
                                               referrerPolicy: "no-referrer"});
            if (!response.ok) return {status: response.status, body: ""};
            const declared = Number(response.headers.get("Content-Length") || 0);
            if (declared > limit) return {status: response.status, body: "", tooLarge: true};
            const reader = response.body.getReader();
            const chunks = [];
            let size = 0;
            for (;;) {
                const {done, value} = await reader.read();
                if (done) break;
                size += value.length;
                if (size > limit) {
                    await reader.cancel();
                    return {status: response.status, body: "", tooLarge: true};
                }
                chunks.push(value);
            }
            const bytes = new Uint8Array(size);
            let offset = 0;
            for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; }
            let binary = "";
            for (let i = 0; i < bytes.length; i += 0x8000) {
                binary += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
            }
            return {status: response.status, body: btoa(binary)};
            """
        inFlight += 1
        defer { requestEnded() }
        let result: Any?
        do {
            result = try await webView.callAsyncJavaScript(script, arguments: ["url": url.absoluteString,
                                                                               "limit": max(0, limit)],
                                                           contentWorld: .defaultClient)
        } catch {
            throw Failure.unreachable
        }
        guard let answer = result as? [String: Any] else { throw Failure.unreadable }
        if answer["tooLarge"] as? Bool == true { throw ArtifactExport.Failure.tooLarge }
        guard let status = (answer["status"] as? NSNumber)?.intValue,
              let text = answer["body"] as? String,
              let data = Data(base64Encoded: text) else { throw Failure.unreadable }
        return (status, data)
    }

    func identity() async throws -> ArtifactAPI.Identity {
        let answer = try await send("GET", ArtifactAPI.accountPath)
        guard answer.status == 200 else { throw Failure.signedOut }
        guard let identity = ArtifactAPI.identity(from: answer.data) else { throw Failure.unreadable }
        return identity
    }

    /// Drops the page, so the next request loads a fresh one. A sign-in made
    /// since is only seen by a page loaded after it.
    ///
    /// Requests already waiting for the old page wait for the new one instead.
    /// Dropped with the page, they were never answered, and the next request
    /// queued behind them never started a page of its own, so every request
    /// for the account hung until Graft quit.
    func reset() {
        webView?.navigationDelegate = nil
        webView = nil
        isLoaded = false
        if !loading.isEmpty, !closed { startLoading() }
    }

    /// For good. Whatever is waiting is told the account is signed out, and
    /// nothing asked of this session afterwards loads a page.
    func close() {
        closed = true
        webView?.navigationDelegate = nil
        webView = nil
        isLoaded = false
        let waiting = loading
        loading = []
        for continuation in waiting { continuation.resume(throwing: Failure.signedOut) }
    }

    /// Returns once every request already sent from the page has come back,
    /// which is when the last thing holding the page lets go of it.
    func released() async {
        guard inFlight > 0 else { return }
        await withCheckedContinuation { drained.append($0) }
    }

    private func requestEnded() {
        inFlight -= 1
        guard inFlight == 0 else { return }
        let waiting = drained
        drained = []
        for continuation in waiting { continuation.resume() }
    }

    /// How long a page may take to load before the requests waiting on it are
    /// told claude.ai did not answer. A page that never finishes and never
    /// fails would otherwise hold them for good.
    static let loadLimit: UInt64 = 30_000_000_000

    private func ready() async throws {
        if closed { throw Failure.signedOut }
        if isLoaded, webView != nil { return }
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            loading.append(continuation)
            guard loading.count == 1 else { return }
            startLoading()
        }
    }

    private func startLoading() {
        let view = WKWebView(frame: .zero, configuration: Self.configuration(for: account))
        view.customUserAgent = Self.userAgent
        view.navigationDelegate = self
        webView = view
        view.load(URLRequest(url: ArtifactAPI.origin.appending(path: ArtifactAPI.accountPath)))
        Task { [weak self, weak view] in
            try? await Task.sleep(nanoseconds: Self.loadLimit)
            guard let self, let view, view === self.webView, !self.isLoaded else { return }
            self.finishLoading(view, Failure.unreachable)
        }
    }

    /// Only the page now in use may finish a load. A page dropped by `reset`
    /// can still report in afterwards, and taken at its word it would mark the
    /// new page loaded before it was.
    private func finishLoading(_ view: WKWebView, _ error: Error?) {
        guard view === webView else { return }
        isLoaded = error == nil
        if error != nil {
            webView?.navigationDelegate = nil
            webView = nil
        }
        let waiting = loading
        loading = []
        for continuation in waiting {
            if let error { continuation.resume(throwing: error) } else { continuation.resume() }
        }
    }

    nonisolated func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        Task { @MainActor in
            // A challenge page or a redirect elsewhere is not a claude.ai page
            // to ask from, and a request from it would carry nothing.
            if webView.url?.host == ArtifactAPI.origin.host {
                self.finishLoading(webView, nil)
            } else {
                self.finishLoading(webView, Failure.signedOut)
            }
        }
    }

    nonisolated func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) {
        Task { @MainActor in self.finishLoading(webView, Failure.unreachable) }
    }

    nonisolated func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!,
                             withError error: Error) {
        Task { @MainActor in self.finishLoading(webView, Failure.unreachable) }
    }

    /// A page whose process died still reads as loaded, and every request
    /// sent to it fails as though claude.ai were down until something else
    /// drops it. So it is dropped here, and the next request loads another.
    nonisolated func webViewWebContentProcessDidTerminate(_ webView: WKWebView) {
        Task { @MainActor in
            guard webView === self.webView else { return }
            Diagnostics.note("artifacts.page ended", [:])
            if self.isLoaded {
                self.reset()
            } else {
                self.finishLoading(webView, Failure.unreachable)
            }
        }
    }
}

/// Every account's artifacts in one list, and the groups they are shared with.
@MainActor
final class ArtifactCenter: ObservableObject {
    enum AccountState: Equatable {
        case unknown
        case loading
        case ready
        case signedOut
        case failed
    }

    @Published private(set) var accounts: [ArtifactAccount]
    @Published private(set) var states: [UUID: AccountState] = [:]
    @Published private(set) var artifacts: [Artifact] = []
    @Published var groups: [ArtifactGroup] {
        didSet { if groups != oldValue, mayWriteGroups { ArtifactFiles.save(groups, to: ArtifactFiles.groups) } }
    }
    /// One line saying how the last thing done went.
    @Published private(set) var status: String?
    @Published private(set) var busy: Set<String> = []
    /// Which Claude on this Mac each account belongs to, by account UUID.
    @Published private(set) var claudeNames: [String: String] = [:]
    /// The last sign-in to finish, whichever pane it was started from, so
    /// every pane can ask for that account at once rather than at its next
    /// refresh.
    @Published private(set) var lastSignIn: SignIn?

    struct SignIn: Equatable {
        let account: UUID
        let at: Date
    }

    /// Set for the layout checks, which draw sample artifacts and must never
    /// reach claude.ai.
    private let offline: Bool
    private var sessions: [UUID: ClaudeWebSession] = [:]
    private var lists: [UUID: [Artifact]] = [:]
    /// Which load of each account is the latest, so one a sign-in has since
    /// superseded writes nothing over the one that followed it.
    private var loadGenerations: [UUID: Int] = [:]
    private var signIn: ArtifactSignInWindow?
    /// False only when a file that would not be read could not be set aside
    /// either, so saving would write over it.
    private var mayWriteAccounts = true
    private var mayWriteGroups = true

    init() {
        offline = false
        var accountsWereRead = false
        switch ArtifactFiles.read([ArtifactAccount].self, from: ArtifactFiles.accounts) {
        case .read(let list):
            accounts = list
            accountsWereRead = true
        case .missing:
            accounts = []
        case .unreadable:
            accounts = []
            mayWriteAccounts = Self.setAside(ArtifactFiles.accounts)
        }
        switch ArtifactFiles.readEach(ArtifactGroup.self, from: ArtifactFiles.groups) {
        case .read(let found):
            groups = found.values
            // The whole file is kept aside before the groups that did read
            // are written back, so the one that did not is still on disk.
            if found.lost > 0 {
                mayWriteGroups = Self.setAside(ArtifactFiles.groups)
                if mayWriteGroups { ArtifactFiles.save(found.values, to: ArtifactFiles.groups) }
            }
        case .missing:
            groups = []
        case .unreadable:
            groups = []
            mayWriteGroups = Self.setAside(ArtifactFiles.groups)
        }
        // Only against a list that was read: one set aside, or never written,
        // may name the accounts these stores belong to.
        if accountsWereRead, #available(macOS 14, *) {
            Task { await removeStrayStores() }
        }
    }

    /// Stores no listed account is under: a removal that would not go, a
    /// sign-in window closed before it finished, a Graft that quit before
    /// either. Asked once, before this launch has made a page on any of them.
    @available(macOS 14, *)
    private func removeStrayStores() async {
        for id in await WKWebsiteDataStore.allDataStoreIdentifiers {
            // A sign-in started since launch is writing into its store.
            guard account(id) == nil, sessions[id] == nil, signIn?.account != id else { continue }
            Diagnostics.note("artifacts.store stray", ["store": String(id.uuidString.prefix(8))])
            await ClaudeWebSession.forget(id)
        }
    }

    /// For the layout checks, which must not reach claude.ai.
    init(accounts: [ArtifactAccount], artifacts: [Artifact], groups: [ArtifactGroup]) {
        self.accounts = accounts
        self.artifacts = artifacts
        self.groups = groups
        offline = true
        for account in accounts { states[account.id] = .ready }
    }

    private static func setAside(_ file: URL) -> Bool {
        let moved = ArtifactFiles.setAside(file)
        Diagnostics.note("artifacts.file unreadable", ["file": file.lastPathComponent,
                                                       "setAside": moved?.lastPathComponent ?? "could not move"])
        return moved != nil
    }

    private func saveAccounts() {
        guard mayWriteAccounts else { return }
        ArtifactFiles.save(accounts, to: ArtifactFiles.accounts)
    }

    func state(of account: UUID) -> AccountState { states[account] ?? .unknown }

    /// What another pane learned about a sign-in, so every pane's chip says
    /// the same thing about it.
    func noteSignIn(_ account: UUID, accepted: Bool) {
        guard self.account(account) != nil, states[account] != .loading else { return }
        if accepted {
            if states[account] != .ready { states[account] = .ready }
        } else {
            states[account] = .signedOut
        }
    }

    func account(_ id: UUID) -> ArtifactAccount? { accounts.first { $0.id == id } }

    /// The name a person knows the account by here: the Claude it is signed in
    /// to on this Mac when there is one, its address otherwise.
    func name(of id: UUID) -> String {
        guard let account = account(id) else { return "" }
        if let uuid = account.accountUuid, let claude = claudeNames[uuid] { return claude }
        return account.email ?? L10n.text("Signing in…")
    }

    var anyLoading: Bool { states.values.contains(.loading) }

    // MARK: - Signing in

    func signInNewAccount() {
        signIn(ArtifactAccount(id: UUID()))
    }

    func signInAgain(_ id: UUID) {
        guard let account = account(id) else { return }
        signIn(account)
    }

    private func signIn(_ account: ArtifactAccount) {
        signIn?.close()
        let window = ArtifactSignInWindow(account: account.id) { [weak self] identity in
            self?.signedIn(account.id, identity)
        }
        signIn = window
        window.show()
    }

    private func signedIn(_ id: UUID, _ identity: ArtifactAPI.Identity) {
        signIn = nil
        let wasAnother = account(id).map { $0.accountUuid != identity.accountUuid } ?? false
        let change = ArtifactAccounts.signedIn(accounts, store: id, as: identity)
        accounts = change.accounts
        saveAccounts()
        for earlier in change.retired { retire(earlier) }
        if wasAnother { lists[id] = nil }
        if !change.retired.isEmpty { status = L10n.format("Signed in to %@ again", identity.email) }
        sessions[id]?.reset()
        Task { await load(id, again: true) }
        lastSignIn = SignIn(account: id, at: Date())
    }

    func signOut(_ id: UUID) {
        if signIn?.account == id {
            signIn?.close()
            signIn = nil
        }
        accounts.removeAll { $0.id == id }
        saveAccounts()
        retire(id)
        artifacts = ArtifactAPI.merge(accounts.compactMap { lists[$0.id] })
    }

    /// Lets go of everything made on an account's store, then removes the
    /// store. WebKit will not remove one a web view still uses, and the error
    /// used to be dropped, which left a kept store holding a live claude.ai
    /// sign-in that nothing listed and nothing would ever remove.
    private func retire(_ id: UUID) {
        let session = sessions.removeValue(forKey: id)
        session?.close()
        lists[id] = nil
        states[id] = nil
        loadGenerations[id] = nil
        Task {
            await session?.released()
            await ClaudeWebSession.forget(id)
        }
    }

    // MARK: - Listing

    /// From the view's appearance and the refresh button, never a timer: each
    /// pass is several requests per account to a service that did not invite
    /// them.
    func refresh() {
        guard !offline else { return }
        Task {
            await withTaskGroup(of: Void.self) { group in
                for account in accounts { group.addTask { await self.load(account.id) } }
            }
        }
        refreshNames()
    }

    func refreshNames() {
        Task.detached(priority: .utility) {
            let names = Self.claudeNamesOnThisMac()
            await MainActor.run { self.claudeNames = names }
        }
    }

    /// The organization an account's chats, projects and artifacts live
    /// under, asked of claude.ai the first time and kept after that.
    func organization(of id: UUID) async throws -> String {
        var account = account(id) ?? ArtifactAccount(id: id)
        if account.org == nil || account.email == nil {
            let identity = try await session(id).identity()
            account.email = identity.email
            account.accountUuid = identity.accountUuid
            account.org = identity.org
            if let index = accounts.firstIndex(where: { $0.id == id }) {
                accounts[index] = account
                saveAccounts()
            }
        }
        guard let org = account.org else { throw ClaudeWebSession.Failure.unreadable }
        return org
    }

    /// Shared with the Projects and Search panes, which ask from the same
    /// signed-in page rather than loading a second one per account.
    ///
    /// An account no longer listed gets one that answers signed out and loads
    /// nothing: a load or search still running after a sign-out would
    /// otherwise build a page on the store being removed, and keep it.
    func session(_ id: UUID) -> ClaudeWebSession {
        if let session = sessions[id] { return session }
        let session = ClaudeWebSession(account: id)
        guard account(id) != nil else {
            session.close()
            return session
        }
        sessions[id] = session
        return session
    }

    /// `again` is for a sign-in just made: a load already under way was
    /// asked of the page before it, and is left to finish without writing.
    private func load(_ id: UUID, again: Bool = false) async {
        guard account(id) != nil, again || states[id] != .loading else { return }
        let generation = (loadGenerations[id] ?? 0) + 1
        loadGenerations[id] = generation
        states[id] = .loading
        let session = session(id)
        let outcome: Result<[Artifact], Error>
        do {
            let org = try await organization(of: id)
            var found: [Artifact] = []
            for rel in ["mine", "shared"] {
                var token: String?
                for _ in 0..<ArtifactAPI.maximumPages {
                    let answer = try await session.send("GET", ArtifactAPI.listPath(rel: rel, org: org, after: token))
                    guard answer.status == 200,
                          let page = ArtifactAPI.page(from: answer.data, account: id, org: org)
                    else { throw ClaudeWebSession.Failure.unreadable }
                    found += page.artifacts
                    token = page.next
                    if token == nil { break }
                }
            }
            outcome = .success(found)
        } catch {
            outcome = .failure(error)
        }
        // A newer load owns what is shown now, and an account signed out
        // while this one ran owns nothing: written back, its list came back
        // with it at the next sign-out.
        guard loadGenerations[id] == generation, account(id) != nil else { return }
        switch outcome {
        case .success(let found):
            lists[id] = found
            states[id] = .ready
        case .failure(ClaudeWebSession.Failure.signedOut):
            lists[id] = nil
            states[id] = .signedOut
        case .failure(let error):
            states[id] = .failed
            Diagnostics.note("artifacts.list failed", ["reason": "\(error)"])
        }
        artifacts = ArtifactAPI.merge(accounts.compactMap { lists[$0.id] })
    }

    // MARK: - Sharing

    func setPublic(_ artifact: Artifact, _ isPublic: Bool) {
        let key = "access-\(artifact.id)"
        guard artifact.access.changes(toPublic: isPublic), !busy.contains(key) else { return }
        busy.insert(key)
        Task {
            defer { busy.remove(key) }
            let session = session(artifact.account)
            do {
                let current = try await session.send("GET", ArtifactAPI.permPath(artifact.id, org: artifact.org))
                guard current.status == 200, let permissions = ArtifactAPI.permissions(from: current.data)
                else { throw ClaudeWebSession.Failure.unreadable }
                // Changed elsewhere since the list was read: what it is now is
                // shown, and nothing is sent to make it what it already is.
                if let mode = permissions.mode, !ArtifactAccess(audience: mode).changes(toPublic: isPublic) {
                    replace(artifact.id) { $0.audience = mode }
                    return
                }
                let answer = try await session.send(
                    "PATCH", ArtifactAPI.permPath(artifact.id, org: artifact.org),
                    body: ArtifactAPI.readModeBody(public: isPublic, invited: permissions.invited),
                    headers: permissions.version.map { ["If-Match": $0] } ?? [:])
                guard (200..<300).contains(answer.status) else {
                    status = L10n.format("claude.ai refused the change (%ld)", answer.status)
                    return
                }
                status = isPublic ? L10n.format("Anyone with the link can view “%@”", artifact.title)
                    : L10n.format("“%@” is private again", artifact.title)
                replace(artifact.id) { $0.audience = isPublic ? "public" : (permissions.invited.isEmpty ? "owner" : "users") }
            } catch {
                status = message(for: error, account: artifact.account)
            }
        }
    }

    /// One invitation per address, each as claude.ai sends it from its own
    /// panel, which also emails the person. An address that fails does not
    /// stop the rest.
    ///
    /// Each person gets the role the group gives them where this artifact
    /// takes it. Where it does not, they get the most it does take, never
    /// more, and the result says how many were given less.
    func share(_ artifact: Artifact, with group: ArtifactGroup) {
        let key = "share-\(artifact.id)"
        guard !busy.contains(key), !group.emails.isEmpty else { return }
        busy.insert(key)
        Task {
            defer { busy.remove(key) }
            let session = session(artifact.account)
            var invited = 0
            var lowered = 0
            var refusal: String?
            let permissions: ArtifactAPI.Permissions
            do {
                permissions = try await self.permissions(of: artifact, session: session)
            } catch {
                status = message(for: error, account: artifact.account)
                return
            }
            for email in group.emails {
                let wanted = group.role(of: email)
                let role = wanted.offered(by: permissions)
                do {
                    let answer = try await session.send("POST", ArtifactAPI.invitePath(artifact.id, org: artifact.org),
                                                        body: ArtifactAPI.inviteBody(email: email, role: role))
                    if (200..<300).contains(answer.status) {
                        invited += 1
                        if role != wanted { lowered += 1 }
                    } else {
                        refusal = refusal ?? "\(answer.status)"
                    }
                } catch ClaudeWebSession.Failure.signedOut {
                    // Every address after this one would be turned away the
                    // same way.
                    status = message(for: ClaudeWebSession.Failure.signedOut, account: artifact.account)
                    return
                } catch {
                    refusal = refusal ?? "unanswered"
                }
            }
            status = lowered == 0
                ? L10n.format("Shared “%1$@” with %2$ld of %3$ld in %4$@",
                              artifact.title, invited, group.emails.count, group.name)
                : L10n.format("Shared “%1$@” with %2$ld of %3$ld in %4$@, %5$ld with less than asked",
                              artifact.title, invited, group.emails.count, group.name, lowered)
            if invited > 0, artifact.access == .onlyYou {
                replace(artifact.id) { $0.audience = "users" }
            }
            if let refusal {
                Diagnostics.note("artifacts.invite refused", ["status": refusal])
            }
        }
    }

    private func permissions(of artifact: Artifact, session: ClaudeWebSession) async throws -> ArtifactAPI.Permissions {
        let answer = try await session.send("GET", ArtifactAPI.permPath(artifact.id, org: artifact.org))
        guard answer.status == 200, let permissions = ArtifactAPI.permissions(from: answer.data)
        else { throw ClaudeWebSession.Failure.unreadable }
        return permissions
    }

    // MARK: - Changing an artifact

    func rename(_ artifact: Artifact, to title: String) {
        let title = title.trimmingCharacters(in: .whitespacesAndNewlines)
        let key = "rename-\(artifact.id)"
        guard artifact.isOwn, !title.isEmpty, title != artifact.title, !busy.contains(key) else { return }
        busy.insert(key)
        Task {
            defer { busy.remove(key) }
            do {
                let answer = try await session(artifact.account).send(
                    "POST", ArtifactAPI.retitlePath(artifact.id, org: artifact.org),
                    body: ArtifactAPI.retitleBody(title))
                guard (200..<300).contains(answer.status) else {
                    status = refused(answer)
                    return
                }
                replace(artifact.id) { $0.title = title }
                status = L10n.format("Renamed “%@”", title)
            } catch {
                status = message(for: error, account: artifact.account)
            }
        }
    }

    /// The copy belongs to the account that made it, which is why only an
    /// artifact that account can see is offered, and the list is asked again
    /// for it rather than guessed.
    func duplicate(_ artifact: Artifact) {
        let key = "duplicate-\(artifact.id)"
        guard artifact.duplicable, !busy.contains(key) else { return }
        busy.insert(key)
        Task {
            defer { busy.remove(key) }
            do {
                let answer = try await session(artifact.account).send(
                    "POST", ArtifactAPI.duplicatePath(artifact.id, org: artifact.org))
                guard (200..<300).contains(answer.status) else {
                    status = refused(answer)
                    return
                }
                status = L10n.format("Made a copy of “%@”", artifact.title)
                await load(artifact.account)
            } catch {
                status = message(for: error, account: artifact.account)
            }
        }
    }

    /// claude.ai keeps no bin for artifacts, so this is for good; the view
    /// asks first and says so.
    func delete(_ artifact: Artifact) {
        let key = "delete-\(artifact.id)"
        guard artifact.isOwn, !busy.contains(key) else { return }
        busy.insert(key)
        Task {
            defer { busy.remove(key) }
            do {
                let answer = try await session(artifact.account).send(
                    "DELETE", ArtifactAPI.artifactPath(artifact.id, org: artifact.org))
                // Already gone is what was wanted.
                guard (200..<300).contains(answer.status)
                        || (answer.status == 404 && ArtifactAPI.reason(from: answer.data) == "not_found") else {
                    status = refused(answer)
                    return
                }
                for key in lists.keys { lists[key]?.removeAll { $0.id == artifact.id } }
                artifacts.removeAll { $0.id == artifact.id }
                status = L10n.format("Deleted “%@”", artifact.title)
            } catch {
                status = message(for: error, account: artifact.account)
            }
        }
    }

    // MARK: - Exporting

    /// Reads the artifact's files as its viewer does, asks where to put the
    /// export, and makes it on this Mac. Nothing of the export goes anywhere
    /// but the file chosen.
    func export(_ artifact: Artifact, as format: ArtifactExportFormat) {
        let key = "export-\(artifact.id)"
        guard !busy.contains(key) else { return }
        busy.insert(key)
        Task {
            defer { busy.remove(key) }
            let folder = FileManager.default.temporaryDirectory
                .appending(path: "graft-export-\(UUID().uuidString)")
            defer { try? FileManager.default.removeItem(at: folder) }
            do {
                let files = try await fetchFiles(of: artifact, into: folder)
                let single = files.count == 1
                guard let destination = ArtifactExport.askWhere(title: artifact.title, format: format,
                                                                singleFile: single) else { return }
                try await ArtifactExport.write(folder: folder, format: format, singleFile: single, to: destination)
                status = L10n.format("Exported “%1$@” to %2$@", artifact.title, destination.lastPathComponent)
            } catch let failure as ArtifactExport.Failure {
                status = failure.message
                Diagnostics.note("artifacts.export failed", ["reason": "\(failure)", "format": format.rawValue])
            } catch {
                status = message(for: error, account: artifact.account)
            }
        }
    }

    private func fetchFiles(of artifact: Artifact, into folder: URL) async throws -> [String] {
        let session = session(artifact.account)
        let answer = try await session.send("GET", ArtifactAPI.artifactPath(artifact.id, org: artifact.org))
        guard answer.status == 200 else {
            throw answer.status == 404 ? ArtifactExport.Failure.gone : ArtifactExport.Failure.refused(answer.status)
        }
        guard let source = ArtifactAPI.source(from: answer.data) else { throw ClaudeWebSession.Failure.unreadable }
        var total = 0
        var written: [String] = []
        for path in source.files {
            guard let url = ArtifactAPI.fileURL(artifact.id, source: source, path: path) else { continue }
            let file = try await session.download(url, limit: ArtifactAPI.maximumExportBytes - total)
            guard file.status == 200 else {
                // Only the page itself is needed; a file it lists that will
                // not come is left out, as a broken link in the page would be.
                if path == "index.html" { throw ArtifactExport.Failure.refused(file.status) }
                continue
            }
            total += file.data.count
            guard total <= ArtifactAPI.maximumExportBytes else { throw ArtifactExport.Failure.tooLarge }
            let target = folder.appending(path: path)
            try FileManager.default.createDirectory(at: target.deletingLastPathComponent(),
                                                    withIntermediateDirectories: true)
            try file.data.write(to: target)
            written.append(path)
        }
        return written
    }

    private func refused(_ answer: (status: Int, data: Data)) -> String {
        switch ArtifactAPI.reason(from: answer.data) {
        case "title_invalid": return L10n.text("claude.ai won't take that title")
        case "org_mismatch": return L10n.text("claude.ai won't copy it into this account")
        case let reason? where reason.contains("limit") || reason.contains("cap"):
            return L10n.text("claude.ai won't make more copies today")
        default: return L10n.format("claude.ai refused the change (%ld)", answer.status)
        }
    }

    func copyLink(_ artifact: Artifact) {
        guard let url = artifact.openURL else { return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(url.absoluteString, forType: .string)
        status = artifact.access == .onlyYou || artifact.access == .invited
            ? L10n.text("Link copied · only people with access can open it")
            : L10n.text("Link copied")
    }

    func clearStatus() { status = nil }

    private func replace(_ id: String, _ change: (inout Artifact) -> Void) {
        for index in artifacts.indices where artifacts[index].id == id { change(&artifacts[index]) }
        for key in lists.keys {
            guard var list = lists[key] else { continue }
            for index in list.indices where list[index].id == id { change(&list[index]) }
            lists[key] = list
        }
    }

    private func message(for error: Error, account: UUID) -> String {
        if error as? ClaudeWebSession.Failure == .signedOut {
            states[account] = .signedOut
            return L10n.format("Sign in to %@ again", name(of: account))
        }
        return L10n.text("claude.ai didn't answer")
    }

    // MARK: - Groups

    func addGroup(named name: String) -> ArtifactGroup {
        let group = ArtifactGroup(id: UUID(), name: name, emails: [])
        groups.append(group)
        return group
    }

    func deleteGroup(_ id: UUID) {
        groups.removeAll { $0.id == id }
    }

    /// Off the main thread: each Claude's account is read out of its profile.
    nonisolated static func claudeNamesOnThisMac() -> [String: String] {
        var names: [String: String] = [:]
        let main = (Graft.mainProfile, "Claude")
        let saved = (try? Data(contentsOf: Graft.shortcutsFile))
            .flatMap { try? JSONDecoder().decode([Shortcut].self, from: $0) } ?? []
        let shortcuts = saved.map { ($0.profileDir, $0.name) }
        for (profile, name) in [main] + shortcuts {
            guard let uuid = Graft.account(of: profile), names[uuid] == nil else { continue }
            names[uuid] = name
        }
        return names
    }
}

/// claude.ai's own sign-in page, in a window of Graft's, writing into the
/// store of the account being signed in. It closes itself once the page says
/// who is signed in.
@MainActor
final class ArtifactSignInWindow: NSObject, WKNavigationDelegate, WKUIDelegate, NSWindowDelegate {
    let account: UUID
    private let done: (ArtifactAPI.Identity) -> Void
    private var window: NSWindow?
    private var webView: WKWebView?
    private var popups: [NSWindow] = []
    private var urlWatch: NSKeyValueObservation?
    private var finished = false
    private var checking = false
    private var askAgain = false

    init(account: UUID, done: @escaping (ArtifactAPI.Identity) -> Void) {
        self.account = account
        self.done = done
    }

    func show() {
        let view = WKWebView(frame: NSRect(x: 0, y: 0, width: 520, height: 680),
                             configuration: ClaudeWebSession.configuration(for: account))
        view.customUserAgent = ClaudeWebSession.userAgent
        view.navigationDelegate = self
        view.uiDelegate = self
        // claude.ai finishes a sign-in by moving to its chat page inside the
        // page already loaded, which ends no navigation. The address changing
        // is the one thing every way of signing in does, so that is what asks.
        // Waiting for `didFinish` alone left a person signed in, looking at
        // their chats in this window, and Graft still saying "Sign in".
        urlWatch = view.observe(\.url, options: [.new]) { [weak self] webView, _ in
            Task { @MainActor in await self?.check(webView) }
        }
        let window = NSWindow(contentRect: view.frame, styleMask: [.titled, .closable, .resizable],
                              backing: .buffered, defer: false)
        window.title = L10n.text("Sign In to claude.ai")
        window.contentView = Self.withHint(view)
        window.isReleasedWhenClosed = false
        window.delegate = self
        window.center()
        self.window = window
        webView = view
        view.load(URLRequest(url: ArtifactAPI.signInURL))
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
    }

    func close() {
        release()
    }

    /// Lets go of every web view this window made, at once rather than when
    /// AppKit next gets round to it, since the store they write into cannot
    /// be removed while one of them is alive.
    private func release() {
        urlWatch = nil
        for popup in popups {
            popup.delegate = nil
            popup.contentView = nil
            popup.close()
        }
        popups = []
        webView?.navigationDelegate = nil
        webView?.uiDelegate = nil
        webView = nil
        window?.delegate = nil
        window?.contentView = nil
        window?.close()
        window = nil
    }

    /// The emailed code is the one way of signing in seen through to the end
    /// here, so the window says so above claude.ai's page, where a person
    /// chooses how.
    private static func withHint(_ page: WKWebView) -> NSView {
        let hint = NSTextField(labelWithString: L10n.text(ArtifactsView.signInHint))
        hint.font = .systemFont(ofSize: NSFont.smallSystemFontSize)
        hint.textColor = .secondaryLabelColor
        hint.alignment = .center
        hint.lineBreakMode = .byTruncatingTail
        hint.setAccessibilityIdentifier("signin.hint")
        let bar = NSStackView(views: [hint])
        bar.edgeInsets = NSEdgeInsets(top: 6, left: 12, bottom: 6, right: 12)
        let stack = NSStackView(views: [bar, page])
        stack.orientation = .vertical
        stack.spacing = 0
        stack.alignment = .width
        stack.frame = page.frame
        page.setContentHuggingPriority(.defaultLow, for: .vertical)
        return stack
    }

    nonisolated func windowWillClose(_ notification: Notification) {
        let closing = notification.object as? NSWindow
        Task { @MainActor in
            if let closing, closing !== self.window {
                self.popups.removeAll { $0 === closing }
                return
            }
            self.release()
        }
    }

    /// A provider's sign-in opens in a window of its own and hands its answer
    /// back to the page that opened it, so it gets a real one. Loading it over
    /// claude.ai instead left that answer with nowhere to go: Google said yes
    /// and claude.ai never heard. The configuration WebKit passes is the
    /// opener's, so the popup writes into the same store.
    nonisolated func webView(_ webView: WKWebView, createWebViewWith configuration: WKWebViewConfiguration,
                             for navigationAction: WKNavigationAction,
                             windowFeatures: WKWindowFeatures) -> WKWebView? {
        MainActor.assumeIsolated {
            let popup = WKWebView(frame: NSRect(x: 0, y: 0, width: 500, height: 640),
                                  configuration: configuration)
            popup.customUserAgent = ClaudeWebSession.userAgent
            popup.uiDelegate = self
            let window = NSWindow(contentRect: popup.frame, styleMask: [.titled, .closable, .resizable],
                                  backing: .buffered, defer: false)
            window.title = L10n.text("Sign In to claude.ai")
            window.contentView = popup
            window.isReleasedWhenClosed = false
            window.delegate = self
            if let parent = self.window {
                window.setFrameTopLeftPoint(NSPoint(x: parent.frame.minX + 40, y: parent.frame.maxY - 40))
            } else {
                window.center()
            }
            popups.append(window)
            window.makeKeyAndOrderFront(nil)
            return popup
        }
    }

    nonisolated func webViewDidClose(_ webView: WKWebView) {
        MainActor.assumeIsolated {
            popups.first { $0.contentView === webView }?.close()
            // Its answer has reached the page by now, and the page may not
            // move again on its own.
            if let main = self.webView {
                Task { await self.check(main) }
            }
        }
    }

    nonisolated func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        Task { @MainActor in await self.check(webView) }
    }

    /// Asked whenever the page moves on claude.ai, since the page that ends a
    /// sign-in is not one that can be named in advance. One question at a
    /// time: a move made while one is out is asked about once it comes back.
    private func check(_ webView: WKWebView) async {
        guard !finished, webView === self.webView, webView.url?.host == ArtifactAPI.origin.host else { return }
        guard !checking else { askAgain = true; return }
        checking = true
        defer {
            checking = false
            if askAgain, !finished {
                askAgain = false
                Task { await self.check(webView) }
            }
        }
        let script = """
            const response = await fetch(path, {credentials: "same-origin"});
            return {status: response.status, body: await response.text()};
            """
        let answer: [String: Any]
        do {
            guard let result = try await webView.callAsyncJavaScript(
                    script, arguments: ["path": ArtifactAPI.accountPath],
                    contentWorld: .defaultClient) as? [String: Any] else { return }
            answer = result
        } catch {
            // The page moving on mid-question throws, and that move asks again.
            return
        }
        // Until the sign-in is through, the page is answered 401 or 403 on
        // every move, and that is not worth writing down.
        guard (answer["status"] as? NSNumber)?.intValue == 200, let body = answer["body"] as? String else { return }
        // Signed in and still not read is the one failure nothing on screen
        // shows, so it is the one written down. The shape, never the body.
        guard let identity = ArtifactAPI.identity(from: Data(body.utf8)) else {
            let keys = ((try? JSONSerialization.jsonObject(with: Data(body.utf8))) as? [String: Any])?
                .keys.sorted() ?? []
            Diagnostics.note("artifacts.signin unreadable", ["keys": keys])
            return
        }
        finished = true
        // The window and its pages go first, and the sign-in is handed on
        // only once this question has let go of its page too. Handed on
        // first, a store removed because of it was still in use, and stayed.
        release()
        let done = self.done
        Task { done(identity) }
    }
}
