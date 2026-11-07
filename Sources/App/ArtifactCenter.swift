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

    static func forget(_ account: UUID) async {
        volatile[account] = nil
        if #available(macOS 14, *) {
            try? await WKWebsiteDataStore.remove(forIdentifier: account)
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
        var all = ArtifactAPI.headers
        all.merge(headers) { $1 }
        if body != nil { all["Content-Type"] = "application/json" }
        let script = """
            const response = await fetch(path, {method, credentials: "same-origin", headers,
                                                body: body === "" ? undefined : body});
            return {status: response.status, body: await response.text()};
            """
        let arguments: [String: Any] = ["path": path, "method": method, "headers": all,
                                        "body": body.map { String(decoding: $0, as: UTF8.self) } ?? ""]
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
        if status == 401 || status == 403 { throw Failure.signedOut }
        return (status, Data(text.utf8))
    }

    func identity() async throws -> ArtifactAPI.Identity {
        let answer = try await send("GET", ArtifactAPI.accountPath)
        guard answer.status == 200 else { throw Failure.signedOut }
        guard let identity = ArtifactAPI.identity(from: answer.data) else { throw Failure.unreadable }
        return identity
    }

    /// Drops the page, so the next request loads a fresh one. A sign-in made
    /// since is only seen by a page loaded after it.
    func reset() {
        webView?.navigationDelegate = nil
        webView = nil
        isLoaded = false
    }

    private func ready() async throws {
        if isLoaded, webView != nil { return }
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            loading.append(continuation)
            guard loading.count == 1 else { return }
            let view = WKWebView(frame: .zero, configuration: Self.configuration(for: account))
            view.customUserAgent = Self.userAgent
            view.navigationDelegate = self
            webView = view
            view.load(URLRequest(url: ArtifactAPI.origin.appending(path: ArtifactAPI.accountPath)))
        }
    }

    private func finishLoading(_ error: Error?) {
        isLoaded = error == nil
        if error != nil { webView = nil }
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
                self.finishLoading(nil)
            } else {
                self.finishLoading(Failure.signedOut)
            }
        }
    }

    nonisolated func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) {
        Task { @MainActor in self.finishLoading(Failure.unreachable) }
    }

    nonisolated func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!,
                             withError error: Error) {
        Task { @MainActor in self.finishLoading(Failure.unreachable) }
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
        didSet { if groups != oldValue { ArtifactFiles.save(groups, to: ArtifactFiles.groups) } }
    }
    /// One line saying how the last thing done went.
    @Published private(set) var status: String?
    @Published private(set) var busy: Set<String> = []
    /// Which Claude on this Mac each account belongs to, by account UUID.
    @Published private(set) var claudeNames: [String: String] = [:]

    /// Set for the layout checks, which draw sample artifacts and must never
    /// reach claude.ai.
    private let offline: Bool
    private var sessions: [UUID: ClaudeWebSession] = [:]
    private var lists: [UUID: [Artifact]] = [:]
    private var signIn: ArtifactSignInWindow?

    init() {
        accounts = ArtifactFiles.load([ArtifactAccount].self, from: ArtifactFiles.accounts) ?? []
        groups = ArtifactFiles.load([ArtifactGroup].self, from: ArtifactFiles.groups) ?? []
        offline = false
    }

    /// For the layout checks, which must not reach claude.ai.
    init(accounts: [ArtifactAccount], artifacts: [Artifact], groups: [ArtifactGroup]) {
        self.accounts = accounts
        self.artifacts = artifacts
        self.groups = groups
        offline = true
        for account in accounts { states[account.id] = .ready }
    }

    func state(of account: UUID) -> AccountState { states[account] ?? .unknown }

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
        // The same account signed in twice keeps its first store, and the
        // second goes, so nothing is listed twice under two names.
        if let existing = accounts.first(where: { $0.accountUuid == identity.accountUuid && $0.id != id }) {
            Task { await ClaudeWebSession.forget(id) }
            sessions[existing.id]?.reset()
            status = L10n.format("%@ was already signed in", identity.email)
            Task { await load(existing.id) }
            return
        }
        let account = ArtifactAccount(id: id, email: identity.email, accountUuid: identity.accountUuid,
                                      org: identity.org)
        if let index = accounts.firstIndex(where: { $0.id == id }) {
            accounts[index] = account
        } else {
            accounts.append(account)
        }
        ArtifactFiles.save(accounts, to: ArtifactFiles.accounts)
        sessions[id]?.reset()
        Task { await load(id) }
    }

    func signOut(_ id: UUID) {
        accounts.removeAll { $0.id == id }
        ArtifactFiles.save(accounts, to: ArtifactFiles.accounts)
        sessions[id]?.reset()
        sessions[id] = nil
        lists[id] = nil
        states[id] = nil
        artifacts = ArtifactAPI.merge(Array(lists.values))
        Task { await ClaudeWebSession.forget(id) }
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
        Task.detached(priority: .utility) {
            let names = Self.claudeNamesOnThisMac()
            await MainActor.run { self.claudeNames = names }
        }
    }

    private func session(_ id: UUID) -> ClaudeWebSession {
        if let session = sessions[id] { return session }
        let session = ClaudeWebSession(account: id)
        sessions[id] = session
        return session
    }

    private func load(_ id: UUID) async {
        guard states[id] != .loading else { return }
        states[id] = .loading
        let session = session(id)
        do {
            var account = account(id) ?? ArtifactAccount(id: id)
            if account.org == nil || account.email == nil {
                let identity = try await session.identity()
                account.email = identity.email
                account.accountUuid = identity.accountUuid
                account.org = identity.org
                if let index = accounts.firstIndex(where: { $0.id == id }) {
                    accounts[index] = account
                    ArtifactFiles.save(accounts, to: ArtifactFiles.accounts)
                }
            }
            guard let org = account.org else { throw ClaudeWebSession.Failure.unreadable }
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
            lists[id] = found
            states[id] = .ready
        } catch ClaudeWebSession.Failure.signedOut {
            lists[id] = nil
            states[id] = .signedOut
        } catch {
            states[id] = .failed
            Diagnostics.note("artifacts.list failed", ["reason": "\(error)"])
        }
        guard account(id) != nil else { return }
        artifacts = ArtifactAPI.merge(accounts.compactMap { lists[$0.id] })
    }

    // MARK: - Sharing

    func setPublic(_ artifact: Artifact, _ isPublic: Bool) {
        let key = "access-\(artifact.id)"
        guard !busy.contains(key) else { return }
        busy.insert(key)
        Task {
            defer { busy.remove(key) }
            let session = session(artifact.account)
            do {
                let current = try await session.send("GET", ArtifactAPI.permPath(artifact.id, org: artifact.org))
                guard current.status == 200, let permissions = ArtifactAPI.permissions(from: current.data)
                else { throw ClaudeWebSession.Failure.unreadable }
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
    func share(_ artifact: Artifact, with group: ArtifactGroup) {
        let key = "share-\(artifact.id)"
        guard !busy.contains(key), !group.emails.isEmpty else { return }
        busy.insert(key)
        Task {
            defer { busy.remove(key) }
            let session = session(artifact.account)
            var invited = 0
            var refusal: String?
            for email in group.emails {
                do {
                    let answer = try await session.send("POST", ArtifactAPI.invitePath(artifact.id, org: artifact.org),
                                                        body: ArtifactAPI.inviteBody(email: email))
                    if (200..<300).contains(answer.status) {
                        invited += 1
                    } else {
                        refusal = refusal ?? "\(answer.status)"
                    }
                } catch {
                    status = message(for: error, account: artifact.account)
                    return
                }
            }
            status = L10n.format("Shared “%1$@” with %2$ld of %3$ld in %4$@",
                                 artifact.title, invited, group.emails.count, group.name)
            if invited > 0, artifact.access == .onlyYou {
                replace(artifact.id) { $0.audience = "users" }
            }
            if let refusal {
                Diagnostics.note("artifacts.invite refused", ["status": refusal])
            }
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
    private let account: UUID
    private let done: (ArtifactAPI.Identity) -> Void
    private var window: NSWindow?
    private var webView: WKWebView?
    private var finished = false

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
        let window = NSWindow(contentRect: view.frame, styleMask: [.titled, .closable, .resizable],
                              backing: .buffered, defer: false)
        window.title = L10n.text("Sign In to claude.ai")
        window.contentView = view
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
        window?.close()
    }

    nonisolated func windowWillClose(_ notification: Notification) {
        Task { @MainActor in
            self.webView?.navigationDelegate = nil
            self.webView = nil
            self.window = nil
        }
    }

    /// A sign-in through another provider opens a second window; it is kept in
    /// this one, so it writes into the same store.
    nonisolated func webView(_ webView: WKWebView, createWebViewWith configuration: WKWebViewConfiguration,
                             for navigationAction: WKNavigationAction,
                             windowFeatures: WKWindowFeatures) -> WKWebView? {
        MainActor.assumeIsolated { _ = webView.load(navigationAction.request) }
        return nil
    }

    nonisolated func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        Task { @MainActor in await self.check(webView) }
    }

    /// Asked after every page on claude.ai, since the page that ends a sign-in
    /// is not one that can be named in advance.
    private func check(_ webView: WKWebView) async {
        guard !finished, webView.url?.host == ArtifactAPI.origin.host else { return }
        let script = """
            const response = await fetch(path, {credentials: "same-origin"});
            return {status: response.status, body: await response.text()};
            """
        guard let answer = try? await webView.callAsyncJavaScript(
                script, arguments: ["path": ArtifactAPI.accountPath], contentWorld: .defaultClient) as? [String: Any],
              (answer["status"] as? NSNumber)?.intValue == 200,
              let body = answer["body"] as? String,
              let identity = ArtifactAPI.identity(from: Data(body.utf8)) else { return }
        finished = true
        done(identity)
        window?.close()
    }
}
