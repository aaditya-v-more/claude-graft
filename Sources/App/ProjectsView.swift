import AppKit
import SwiftUI

/// Every signed-in account's projects in one list. It only lists: a project is
/// opened on claude.ai, and changed there.
struct ProjectsView: View {
    @ObservedObject var center: ProjectCenter
    @ObservedObject var accounts: ArtifactCenter
    @State private var filter = ""

    init(center: ProjectCenter) {
        self.center = center
        accounts = center.accounts
    }

    static let note = """
        Projects are listed from every account signed in under Artifacts, with \
        the same sign-in. Graft only lists them; opening one takes you to \
        claude.ai, where it is changed.

        claude.ai is asked when this page is shown and when you refresh it, \
        never in the background.
        """

    var body: some View {
        VStack(spacing: 0) {
            ClaudeAIHeader(title: "Projects", symbol: "folder", note: Self.note, id: "projects",
                           accounts: accounts, busy: center.loadingProjects,
                           refreshHelp: "Ask claude.ai for every account's projects again",
                           refreshLabel: "Refresh Projects", refresh: center.refreshProjects)
            Divider()
            if accounts.accounts.isEmpty {
                SignInPrompt(text: "Sign in to see projects", symbol: "folder", note: Self.note,
                             topic: "Projects", id: "projects", accounts: accounts)
            } else {
                HStack(spacing: 10) {
                    TextField("Filter", text: $filter)
                        .textFieldStyle(.roundedBorder)
                        .frame(maxWidth: 220)
                        .accessibilityLabel(L10n.text("Filter Projects"))
                        .identified("projects.filter")
                    Spacer()
                    Text(L10n.format("%ld projects", shown.count))
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .identified("projects.count")
                }
                .padding(.horizontal, 16)
                .padding(.vertical, 8)
                list
            }
        }
        .safeAreaInset(edge: .bottom, spacing: 0) {
            TroubleBar(lines: center.trouble(in: center.projectStates), id: "projects.status")
        }
        .onAppear { center.refreshProjects() }
    }

    private var shown: [ClaudeProject] {
        let query = filter.trimmingCharacters(in: .whitespaces).lowercased()
        guard !query.isEmpty else { return center.projects }
        return center.projects.filter {
            $0.name.lowercased().contains(query) || $0.summary.lowercased().contains(query)
        }
    }

    @ViewBuilder
    private var list: some View {
        if shown.isEmpty {
            Text(center.loadingProjects ? L10n.text("Asking claude.ai…")
                 : filter.isEmpty ? L10n.text("No projects") : L10n.text("Nothing matches"))
                .foregroundStyle(.secondary)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .identified("projects.empty")
        } else {
            List(shown) { project in
                ProjectRow(center: center, project: project)
            }
            .listStyle(.inset)
        }
    }
}

private struct ProjectRow: View {
    @ObservedObject var center: ProjectCenter
    let project: ClaudeProject

    private var token: String { String(project.id.prefix(8)) }

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: project.isStarred ? "star.fill" : "folder")
                .foregroundStyle(project.isStarred ? AnyShapeStyle(.yellow) : AnyShapeStyle(.secondary))
                .frame(width: 18)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 1) {
                Text(project.name).lineLimit(1)
                Text(subtitle)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            .help(project.summary)
            .spoken(project.name, value: subtitle)
            .identified("project.\(token)")
            Spacer(minLength: 8)
            if !project.isPrivate {
                Image(systemName: "person.2")
                    .foregroundStyle(.secondary)
                    .help(L10n.text("Shared with your organization"))
                    .accessibilityHidden(true)
            }
            Button { center.open(project.openURL) } label: { Image(systemName: "arrow.up.right.square") }
                .buttonStyle(.borderless)
                .help("Open in the browser")
                .accessibilityLabel(L10n.format("Open %@", project.name))
                .identified("project.\(token).open")
        }
        .padding(.vertical, 2)
        .contentShape(Rectangle())
        .onTapGesture(count: 2) { center.open(project.openURL) }
    }

    private var subtitle: String {
        var parts = [center.accounts.name(of: project.account)]
        if project.isExample { parts.append(L10n.text("Example")) }
        if let time = project.updated { parts.append(UsageMonitor.clock(time)) }
        return parts.joined(separator: " · ")
    }
}

/// Every signed-in account's chats and Code sessions searched at once, the
/// way claude.ai's own search box searches one account's, then narrowed by
/// kind, status, where a session ran, age, account, project and star.
struct ChatSearchView: View {
    @ObservedObject var center: ProjectCenter
    @ObservedObject var accounts: ArtifactCenter
    @State private var query: String
    @FocusState private var focused: Bool

    init(center: ProjectCenter) {
        self.center = center
        accounts = center.accounts
        _query = State(initialValue: center.searched ?? "")
    }

    static let note = """
        Searches the chats of every account signed in under Artifacts with \
        claude.ai's own search, and their Code sessions by title and folder. \
        Each search asks every account once, when you press Return; the \
        filters only narrow what came back.

        Where a Code session ran is read from this Mac's own transcripts. One \
        whose transcript is here but which claude.ai no longer lists is shown \
        as gone from claude.ai, and opens in Finder. A chat deleted on \
        claude.ai is not kept anywhere and cannot be found.
        """

    var body: some View {
        VStack(spacing: 0) {
            ClaudeAIHeader(title: "Search Chats", symbol: "magnifyingglass", note: Self.note, id: "search",
                           accounts: accounts, busy: false, refreshHelp: "", refreshLabel: "", refresh: nil)
            Divider()
            if accounts.accounts.isEmpty {
                SignInPrompt(text: "Sign in to search chats", symbol: "magnifyingglass", note: Self.note,
                             topic: "Search Chats", id: "search", accounts: accounts)
            } else {
                HStack(spacing: 8) {
                    TextField("Search every account's chats", text: $query)
                        .textFieldStyle(.roundedBorder)
                        .focused($focused)
                        .onSubmit { if !center.searching { center.search(query) } }
                        .accessibilityLabel(L10n.text("Search Chats"))
                        .identified("search.query")
                    Button {
                        center.search(query)
                    } label: {
                        if center.searching {
                            ProgressView().controlSize(.small)
                        } else {
                            Text("Search")
                        }
                    }
                    .disabled(query.trimmingCharacters(in: .whitespaces).isEmpty || center.searching)
                    .identified("search.go")
                    SearchFilterMenu(center: center)
                }
                .padding(.horizontal, 16)
                .padding(.vertical, 8)
                results
            }
        }
        .safeAreaInset(edge: .bottom, spacing: 0) {
            TroubleBar(lines: center.trouble(in: center.searchStates), id: "search.status",
                       summary: summary)
        }
        .onAppear { focused = true }
    }

    private var summary: String? {
        guard center.searched != nil, !center.searching else { return nil }
        let shown = center.shown.count
        return shown == center.results.count ? L10n.format("%ld results", shown)
            : L10n.format("%1$ld of %2$ld results", shown, center.results.count)
    }

    @ViewBuilder
    private var results: some View {
        let shown = center.shown
        if shown.isEmpty {
            Text(center.searching ? L10n.text("Asking claude.ai…")
                 : center.searched == nil ? L10n.text("Type and press Return")
                 : center.results.isEmpty ? L10n.text("Nothing matches")
                 : L10n.text("Nothing matches these filters"))
                .foregroundStyle(.secondary)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .identified("search.empty")
        } else {
            List(shown) { hit in
                HitRow(center: center, hit: hit)
            }
            .listStyle(.inset)
        }
    }
}

/// Every filter behind one small icon beside the search button, filled while
/// any of them narrows the list. All of them narrow what came back and ask
/// claude.ai nothing.
private struct SearchFilterMenu: View {
    @ObservedObject var center: ProjectCenter

    var body: some View {
        let narrowed = center.filters.isNarrowed
        Menu {
            Picker("Kind", selection: $center.filters.kind) {
                ForEach(SearchFilters.Kind.allCases) { Text(SearchLabels.kind($0)).tag($0) }
            }
            Picker("Status", selection: $center.filters.status) {
                ForEach(SearchFilters.Status.allCases) { Text(SearchLabels.status($0)).tag($0) }
            }
            Picker("Where", selection: $center.filters.place) {
                ForEach(SearchFilters.Place.allCases) { Text(SearchLabels.place($0)).tag($0) }
            }
            Picker("Changed", selection: $center.filters.age) {
                ForEach(SearchFilters.Age.allCases) { Text(SearchLabels.age($0)).tag($0) }
            }
            Picker("Account", selection: $center.filters.account) {
                Text("Every Account").tag(UUID?.none)
                ForEach(center.accounts.accounts) { account in
                    Text(center.accounts.name(of: account.id)).tag(UUID?.some(account.id))
                }
            }
            Picker("Project", selection: $center.filters.project) {
                Text("Any Project").tag(SearchFilters.ProjectChoice.any)
                Text("No Project").tag(SearchFilters.ProjectChoice.none)
                ForEach(center.projects.filter { project in
                    center.filters.account == nil || project.account == center.filters.account
                }) { project in
                    Text(project.name).tag(SearchFilters.ProjectChoice.project(project.id))
                }
            }
            Toggle("Starred Only", isOn: $center.filters.starredOnly)
            Divider()
            Button("Reset Filters") { center.filters = SearchFilters() }
                .disabled(!narrowed)
        } label: {
            // A menu drawn as a picture is read out by the picture's own
            // description, whatever label the menu is given, so the picture
            // carries the name.
            let symbol = narrowed ? "line.3.horizontal.decrease.circle.fill" : "line.3.horizontal.decrease.circle"
            NSImage(systemSymbolName: symbol, accessibilityDescription: L10n.text("Filters"))
                .map(Image.init(nsImage:)) ?? Image(systemName: symbol)
        }
        .menuStyle(.borderlessButton)
        .menuIndicator(.hidden)
        .fixedSize()
        .help(narrowed ? SearchLabels.describe(center.filters) : L10n.text("Filter the results"))
        .accessibilityLabel(L10n.text("Filters"))
        .accessibilityValue(narrowed ? SearchLabels.describe(center.filters) : "")
        .identified("search.filters")
    }
}

/// What each filter and each place is called, in one place so the rows and
/// the menus say the same thing.
enum SearchLabels {
    static func kind(_ kind: SearchFilters.Kind) -> String {
        switch kind {
        case .all: return L10n.text("Chats and Code")
        case .chats: return L10n.text("Chats")
        case .code: return L10n.text("Code Sessions")
        }
    }

    static func status(_ status: SearchFilters.Status) -> String {
        switch status {
        case .active: return L10n.text("Active")
        case .archived: return L10n.text("Archived")
        case .any: return L10n.text("Active and Archived")
        }
    }

    static func place(_ place: SearchFilters.Place) -> String {
        switch place {
        case .anywhere: return L10n.text("Anywhere")
        case .thisMac: return L10n.text("On This Mac")
        case .otherMac: return L10n.text("On Another Mac")
        case .cloud: return L10n.text("In the Cloud")
        case .goneFromClaude: return L10n.text("Gone from claude.ai")
        }
    }

    static func age(_ age: SearchFilters.Age) -> String {
        switch age {
        case .any: return L10n.text("Any Time")
        case .day: return L10n.text("Past Day")
        case .week: return L10n.text("Past Week")
        case .month: return L10n.text("Past Month")
        case .year: return L10n.text("Past Year")
        }
    }

    /// What is narrowing the list, for the icon's tooltip and for VoiceOver,
    /// since nothing else on screen says.
    static func describe(_ filters: SearchFilters) -> String {
        let plain = SearchFilters()
        var parts: [String] = []
        if filters.kind != plain.kind { parts.append(kind(filters.kind)) }
        if filters.status != plain.status { parts.append(status(filters.status)) }
        if filters.place != plain.place { parts.append(place(filters.place)) }
        if filters.age != plain.age { parts.append(age(filters.age)) }
        if filters.account != nil { parts.append(L10n.text("One account")) }
        switch filters.project {
        case .any: break
        case .none: parts.append(L10n.text("No Project"))
        case .project: parts.append(L10n.text("One project"))
        }
        if filters.starredOnly { parts.append(L10n.text("Starred Only")) }
        return parts.joined(separator: " · ")
    }

    static func place(of hit: SearchHit) -> String? {
        switch hit.place {
        case .thisMac: return L10n.text("On this Mac")
        case .otherMac: return L10n.text("On another Mac")
        case .cloud: return L10n.text("In the cloud")
        case .goneFromClaude: return L10n.text("Gone from claude.ai")
        case nil: return nil
        }
    }

    static func symbol(of hit: SearchHit) -> String {
        if hit.kind == .chat { return hit.isArchived ? "archivebox" : "bubble.left" }
        switch hit.place {
        case .cloud: return "cloud"
        case .goneFromClaude: return "trash"
        case .otherMac: return "laptopcomputer"
        default: return "chevron.left.forwardslash.chevron.right"
        }
    }
}

private struct HitRow: View {
    @ObservedObject var center: ProjectCenter
    let hit: SearchHit

    private var token: String {
        hit.openURL == nil ? String(hit.transcript?.deletingPathExtension().lastPathComponent.prefix(8) ?? "local")
            : String(hit.id.replacingOccurrences(of: "cse_", with: "").prefix(8))
    }

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: SearchLabels.symbol(of: hit))
                .foregroundStyle(.secondary)
                .frame(width: 18)
                .padding(.top, 1)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 4) {
                    if hit.isStarred {
                        Image(systemName: "star.fill")
                            .font(.caption)
                            .foregroundStyle(.yellow)
                            .accessibilityHidden(true)
                    }
                    Text(ClaudeAPI.highlighted(hit.title, hit.titleMatches)).lineLimit(1)
                }
                if !hit.snippet.isEmpty {
                    Text(ClaudeAPI.highlighted(hit.snippet, hit.snippetMatches))
                        .font(hit.kind == .code ? .caption.monospaced() : .callout)
                        .foregroundStyle(.secondary)
                        .lineLimit(hit.kind == .code ? 1 : 2)
                        .truncationMode(.middle)
                }
                Text(subtitle)
                    .font(.caption)
                    .foregroundStyle(.tertiary)
                    .lineLimit(1)
            }
            .spoken(hit.title, value: subtitle)
            .identified("hit.\(token)")
            Spacer(minLength: 8)
            Button { center.open(hit) } label: {
                Image(systemName: hit.openURL == nil ? "folder" : "arrow.up.right.square")
            }
            .buttonStyle(.borderless)
            .help(L10n.text(hit.openURL == nil ? "Show the transcript in Finder" : "Open in the browser"))
            .accessibilityLabel(L10n.format(hit.openURL == nil ? "Show %@ in Finder" : "Open %@", hit.title))
            .identified("hit.\(token).open")
        }
        .padding(.vertical, 2)
        .contentShape(Rectangle())
        .onTapGesture(count: 2) { center.open(hit) }
    }

    private var subtitle: String {
        var parts = [center.accounts.name(of: hit.account)]
        if hit.kind == .code { parts.append(L10n.text("Code")) }
        if let place = SearchLabels.place(of: hit) { parts.append(place) }
        if let project = center.projectName(hit.project, account: hit.account) { parts.append(project) }
        if hit.isArchived { parts.append(L10n.text("Archived")) }
        if let time = hit.updated { parts.append(UsageMonitor.clock(time)) }
        return parts.joined(separator: " · ")
    }
}

// MARK: - Pieces both panes share

/// The heading row of a pane that asks claude.ai: what it is, who is signed
/// in, and the way to sign in another.
private struct ClaudeAIHeader: View {
    let title: String
    let symbol: String
    let note: String
    let id: String
    @ObservedObject var accounts: ArtifactCenter
    let busy: Bool
    let refreshHelp: String
    let refreshLabel: String
    let refresh: (() -> Void)?

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: symbol)
                .foregroundStyle(.secondary)
                .accessibilityHidden(true)
            Text(L10n.text(title)).font(.headline)
            InfoButton(note, about: title, id: "\(id)-info")
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 6) {
                    ForEach(accounts.accounts) { account in
                        AccountChip(center: accounts, account: account, scope: id)
                    }
                }
            }
            Spacer(minLength: 4)
            Button { accounts.signInNewAccount() } label: {
                Label(L10n.text(accounts.accounts.isEmpty ? "Sign In…" : "Add Account…"), systemImage: "plus")
            }
            .help("Sign in to another claude.ai account")
            .identified("\(id).add-account")
            if let refresh {
                Button(action: refresh) {
                    if busy {
                        ProgressView().controlSize(.small)
                    } else {
                        Image(systemName: "arrow.clockwise")
                    }
                }
                .buttonStyle(.borderless)
                .disabled(accounts.accounts.isEmpty || busy)
                .help(L10n.text(refreshHelp))
                .accessibilityLabel(L10n.text(refreshLabel))
                .identified("\(id).refresh")
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 10)
    }
}

private struct SignInPrompt: View {
    let text: String
    let symbol: String
    let note: String
    let topic: String
    let id: String
    @ObservedObject var accounts: ArtifactCenter

    var body: some View {
        VStack(spacing: 10) {
            Image(systemName: symbol)
                .font(.system(size: 34, weight: .light))
                .foregroundStyle(.tertiary)
                .accessibilityHidden(true)
            OneLineNote(text: L10n.text(text), more: note, topic: topic, id: "\(id).sign-in-note")
            Button("Sign In…") { accounts.signInNewAccount() }
                .identified("\(id).sign-in")
            Text(L10n.text(ArtifactsView.signInHint))
                .font(.caption)
                .foregroundStyle(.secondary)
                .lineLimit(1)
                .identified("\(id).sign-in-hint")
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

/// One line at the foot: what was found, or which account would not answer.
private struct TroubleBar: View {
    let lines: [String]
    let id: String
    var summary: String? = nil

    var body: some View {
        let text = lines.isEmpty ? summary : lines.joined(separator: " · ")
        if let text {
            VStack(spacing: 0) {
                Divider()
                HStack {
                    Text(text)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                        .truncationMode(.tail)
                        .help(text)
                        .identified(id)
                    Spacer()
                }
                .padding(.horizontal, 16)
                .padding(.vertical, 8)
            }
            .background(.bar)
        }
    }
}
