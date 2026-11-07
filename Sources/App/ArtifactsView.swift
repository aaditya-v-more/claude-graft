import AppKit
import SwiftUI

/// Every signed-in account's artifacts in one list, with who can open each and
/// a way to share it with a group in one go.
struct ArtifactsView: View {
    @ObservedObject var center: ArtifactCenter
    @State private var filter = Filter.all
    @State private var search = ""
    @State private var editingGroups = false

    enum Filter: String, CaseIterable, Identifiable {
        case all, mine, shared
        var id: String { rawValue }
        var label: String {
            switch self {
            case .all: return L10n.text("All")
            case .mine: return L10n.text("Yours")
            case .shared: return L10n.text("Shared with You")
            }
        }
    }

    static let note = """
        Graft signs in to claude.ai once for each account, in a window of its own, \
        and keeps that sign-in in a web store of its own for that account. One \
        sign-in serves Artifacts, Projects and Search Chats alike. It is used \
        only by claude.ai's own pages and is never read by Graft or sent \
        anywhere else. Signing out deletes it.

        Groups are kept by Graft on this Mac. Sharing with a group invites each \
        address as a viewer, and claude.ai emails each person, as it does when \
        you invite them from the artifact's own Share panel.
        """

    var body: some View {
        VStack(spacing: 0) {
            accountsBar
                .padding(.horizontal, 16)
                .padding(.vertical, 10)
            Divider()
            if center.accounts.isEmpty {
                signedOut
            } else {
                filters
                    .padding(.horizontal, 16)
                    .padding(.vertical, 8)
                list
            }
        }
        .safeAreaInset(edge: .bottom, spacing: 0) { bottomBar }
        .sheet(isPresented: $editingGroups) { GroupsEditor(center: center) }
        .onAppear { center.refresh() }
    }

    // MARK: - Accounts

    private var accountsBar: some View {
        HStack(spacing: 8) {
            Image(systemName: "square.on.square")
                .foregroundStyle(.secondary)
                .accessibilityHidden(true)
            Text("Artifacts").font(.headline)
            InfoButton(Self.note, about: "Artifacts", id: "artifacts-info")
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 6) {
                    ForEach(center.accounts) { account in
                        AccountChip(center: center, account: account)
                    }
                }
            }
            Spacer(minLength: 4)
            Button { center.signInNewAccount() } label: {
                Label(L10n.text(center.accounts.isEmpty ? "Sign In…" : "Add Account…"), systemImage: "plus")
            }
            .help("Sign in to another claude.ai account")
            .identified("artifacts.add-account")
            Button { center.refresh() } label: {
                if center.anyLoading {
                    ProgressView().controlSize(.small)
                } else {
                    Image(systemName: "arrow.clockwise")
                }
            }
            .buttonStyle(.borderless)
            .disabled(center.accounts.isEmpty || center.anyLoading)
            .help("Ask claude.ai for every account's artifacts again")
            .accessibilityLabel(L10n.text("Refresh Artifacts"))
            .identified("artifacts.refresh")
        }
    }

    private var signedOut: some View {
        VStack(spacing: 10) {
            Image(systemName: "square.on.square")
                .font(.system(size: 34, weight: .light))
                .foregroundStyle(.tertiary)
                .accessibilityHidden(true)
            OneLineNote(text: L10n.text("Sign in to see artifacts"), more: Self.note,
                        topic: "Artifacts", id: "artifacts.sign-in-note")
            Button("Sign In…") { center.signInNewAccount() }
                .identified("artifacts.sign-in")
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    // MARK: - The list

    private var filters: some View {
        HStack(spacing: 10) {
            Picker("Show", selection: $filter) {
                ForEach(Filter.allCases) { Text($0.label).tag($0) }
            }
            .pickerStyle(.menu)
            .labelsHidden()
            .fixedSize()
            .identified("artifacts.filter")
            TextField("Search", text: $search)
                .textFieldStyle(.roundedBorder)
                .frame(maxWidth: 220)
                .accessibilityLabel(L10n.text("Search Artifacts"))
                .identified("artifacts.search")
            Spacer()
            Text(L10n.format("%ld artifacts", shown.count))
                .font(.caption)
                .foregroundStyle(.secondary)
                .identified("artifacts.count")
        }
    }

    private var shown: [Artifact] {
        let query = search.trimmingCharacters(in: .whitespaces).lowercased()
        return center.artifacts.filter { artifact in
            switch filter {
            case .all: break
            case .mine: guard artifact.isOwn else { return false }
            case .shared: guard !artifact.isOwn else { return false }
            }
            return query.isEmpty || artifact.title.lowercased().contains(query)
        }
    }

    @ViewBuilder
    private var list: some View {
        if shown.isEmpty {
            Text(center.anyLoading ? L10n.text("Asking claude.ai…")
                 : search.isEmpty ? L10n.text("No artifacts") : L10n.text("Nothing matches"))
                .foregroundStyle(.secondary)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .identified("artifacts.empty")
        } else {
            List(shown) { artifact in
                ArtifactRow(center: center, artifact: artifact)
            }
            .listStyle(.inset)
        }
    }

    // MARK: - The foot

    private var bottomBar: some View {
        VStack(spacing: 0) {
            Divider()
            HStack(spacing: 8) {
                if let status = center.status {
                    Text(status)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                        .truncationMode(.tail)
                        .help(status)
                        .identified("artifacts.status")
                }
                Spacer(minLength: 8)
                Button { editingGroups = true } label: {
                    Label(L10n.format("Groups (%ld)", center.groups.count), systemImage: "person.3")
                }
                .help("Make and change the groups artifacts can be shared with")
                .identified("artifacts.groups")
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 10)
        }
        .background(.bar)
    }
}

/// One signed-in account, with its way to sign in again or out. The same
/// chip heads Artifacts, Projects and Search Chats, since the sign-in behind it
/// is one sign-in for all three.
struct AccountChip: View {
    @ObservedObject var center: ArtifactCenter
    let account: ArtifactAccount
    var scope = "artifacts"

    var body: some View {
        let state = center.state(of: account.id)
        let name = center.name(of: account.id)
        Menu {
            if let email = account.email, email != name { Text(email) }
            Button("Sign In Again…") { center.signInAgain(account.id) }
            Divider()
            Button("Sign Out", role: .destructive) { center.signOut(account.id) }
        } label: {
            HStack(spacing: 4) {
                Circle()
                    .fill(color(state))
                    .frame(width: 7, height: 7)
                Text(name).lineLimit(1)
            }
        }
        .menuStyle(.borderlessButton)
        .fixedSize()
        .help(help(state))
        .accessibilityLabel(name)
        .accessibilityValue(help(state))
        .identified("\(scope).account.\(account.id.uuidString.prefix(8))")
    }

    private func color(_ state: ArtifactCenter.AccountState) -> Color {
        switch state {
        case .ready: return .green
        case .signedOut, .failed: return .orange
        case .loading, .unknown: return .secondary
        }
    }

    private func help(_ state: ArtifactCenter.AccountState) -> String {
        switch state {
        case .ready: return L10n.text("Signed in")
        case .signedOut: return L10n.text("Signed out · sign in again")
        case .failed: return L10n.text("claude.ai didn't answer")
        case .loading: return L10n.text("Asking claude.ai…")
        case .unknown: return L10n.text("Not asked yet")
        }
    }
}

private struct ArtifactRow: View {
    @ObservedObject var center: ArtifactCenter
    let artifact: Artifact

    private var token: String { String(artifact.id.prefix(8)) }

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: artifact.kind == "page" ? "doc.richtext" : "square.on.square")
                .foregroundStyle(.secondary)
                .frame(width: 18)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 1) {
                Text(artifact.title).lineLimit(1)
                Text(subtitle)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            .spoken(artifact.title, value: subtitle)
            .identified("artifact.\(token)")
            Spacer(minLength: 8)
            access
            shareMenu
            Button { center.copyLink(artifact) } label: { Image(systemName: "link") }
                .buttonStyle(.borderless)
                .disabled(artifact.openURL == nil)
                .help("Copy link")
                .accessibilityLabel(L10n.format("Copy Link to %@", artifact.title))
                .identified("artifact.\(token).copy-link")
            Button {
                if let url = artifact.openURL { NSWorkspace.shared.open(url) }
            } label: { Image(systemName: "arrow.up.right.square") }
                .buttonStyle(.borderless)
                .disabled(artifact.openURL == nil)
                .help("Open in the browser")
                .accessibilityLabel(L10n.format("Open %@", artifact.title))
                .identified("artifact.\(token).open")
        }
        .padding(.vertical, 2)
    }

    private var subtitle: String {
        let who = center.name(of: artifact.account)
        let owner = artifact.isOwn ? who : L10n.format("Shared with %@", who)
        guard let time = artifact.sortTime else { return owner }
        return owner + " · " + UsageMonitor.clock(time)
    }

    /// Only the owner can change who opens it; for anyone else it is said, not
    /// offered.
    @ViewBuilder
    private var access: some View {
        let current = artifact.access
        if artifact.isOwn {
            Menu {
                Picker("Who Can Open It", selection: Binding(
                    get: { current == .anyoneWithLink },
                    set: { center.setPublic(artifact, $0) })) {
                    Text("Only People Invited").tag(false)
                    Text("Anyone with the Link").tag(true)
                }
                .pickerStyle(.inline)
                .labelsHidden()
            } label: {
                accessLabel(current)
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
            .disabled(center.busy.contains("access-\(artifact.id)"))
            .help(current.label)
            .accessibilityLabel(L10n.format("Who Can Open %@", artifact.title))
            .accessibilityValue(current.label)
            .identified("artifact.\(token).access")
        } else {
            accessLabel(current)
                .foregroundStyle(.secondary)
                .help(current.label)
                .accessibilityHidden(true)
        }
    }

    /// A menu drawn as a picture is read out by the picture's own description,
    /// whatever label the menu is given, which is how this read as "Globe".
    /// So the picture carries the name.
    private func named(_ symbol: String, _ name: String) -> Image {
        NSImage(systemSymbolName: symbol, accessibilityDescription: name).map(Image.init(nsImage:))
            ?? Image(systemName: symbol)
    }

    private func accessLabel(_ access: ArtifactAccess) -> some View {
        named(access.symbol, L10n.format("Who Can Open %@", artifact.title))
            .frame(width: 18)
    }

    @ViewBuilder
    private var shareMenu: some View {
        let busy = center.busy.contains("share-\(artifact.id)")
        Menu {
            if center.groups.isEmpty {
                Text("No groups yet")
            }
            ForEach(center.groups) { group in
                Button(L10n.format("%1$@ (%2$ld)", group.name, group.emails.count)) {
                    center.share(artifact, with: group)
                }
                .disabled(group.emails.isEmpty)
            }
        } label: {
            if busy {
                ProgressView().controlSize(.small)
            } else {
                named("person.badge.plus", L10n.format("Share %@ with a Group", artifact.title))
            }
        }
        .menuStyle(.borderlessButton)
        .fixedSize()
        .disabled(!artifact.isOwn || busy)
        .help(artifact.isOwn ? L10n.text("Share with a group") : L10n.text("Only its owner can share it"))
        .accessibilityLabel(L10n.format("Share %@ with a Group", artifact.title))
        .identified("artifact.\(token).share")
    }
}

/// Groups on the left, the chosen one's name and addresses on the right.
private struct GroupsEditor: View {
    @ObservedObject var center: ArtifactCenter
    @Environment(\.dismiss) private var dismiss
    @State private var selection: UUID?
    @State private var addresses = ""

    var body: some View {
        VStack(spacing: 0) {
            HSplitView {
                VStack(spacing: 0) {
                    List(selection: $selection) {
                        ForEach(center.groups) { group in
                            HStack {
                                Text(group.name).lineLimit(1)
                                Spacer()
                                Text("\(group.emails.count)")
                                    .foregroundStyle(.secondary)
                                    .monospacedDigit()
                            }
                            .spoken(group.name, value: L10n.format("%ld people", group.emails.count))
                            .tag(group.id)
                        }
                    }
                    .listStyle(.sidebar)
                    HStack(spacing: 4) {
                        Button {
                            selection = center.addGroup(named: L10n.text("New Group")).id
                        } label: { Image(systemName: "plus") }
                            .buttonStyle(.borderless)
                            .accessibilityLabel(L10n.text("New Group"))
                            .identified("groups.add")
                        Button {
                            if let selection { center.deleteGroup(selection) }
                            selection = center.groups.first?.id
                        } label: { Image(systemName: "minus") }
                            .buttonStyle(.borderless)
                            .disabled(selection == nil)
                            .accessibilityLabel(L10n.text("Delete Group"))
                            .identified("groups.delete")
                        Spacer()
                    }
                    .padding(8)
                }
                .frame(minWidth: 160, idealWidth: 180, maxWidth: 220)
                detail
                    .frame(minWidth: 300, maxWidth: .infinity, maxHeight: .infinity)
            }
            Divider()
            HStack {
                OneLineNote(text: L10n.text("Sharing invites each address as a viewer"),
                            more: ArtifactsView.note, topic: "Groups", id: "groups.note")
                Spacer()
                Button("Done") { dismiss() }
                    .keyboardShortcut(.defaultAction)
                    .identified("groups.done")
            }
            .padding(12)
        }
        .frame(width: 560, height: 380)
        .onAppear {
            selection = selection ?? center.groups.first?.id
            loadAddresses()
        }
        .onChange(of: selection) { _ in loadAddresses() }
    }

    @ViewBuilder
    private var detail: some View {
        if let index = center.groups.firstIndex(where: { $0.id == selection }) {
            Form {
                TextField("Name", text: $center.groups[index].name)
                    .identified("groups.name")
                Section {
                    TextEditor(text: $addresses)
                        .font(.body.monospaced())
                        .frame(minHeight: 150)
                        .accessibilityLabel(L10n.text("Email Addresses"))
                        .identified("groups.addresses")
                        .onChange(of: addresses) { text in
                            center.groups[index].emails = GroupAddresses.parse(text)
                        }
                } header: {
                    Text(L10n.format("Email addresses · %ld", center.groups[index].emails.count))
                }
            }
            .formStyle(.grouped)
        } else {
            Text(center.groups.isEmpty ? L10n.text("No groups yet") : L10n.text("Select a group"))
                .foregroundStyle(.secondary)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }

    private func loadAddresses() {
        addresses = center.groups.first { $0.id == selection }?.emails.joined(separator: "\n") ?? ""
    }
}
