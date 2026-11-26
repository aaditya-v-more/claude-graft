import SwiftUI

/// Every account in one place: which is used up first, which start their
/// sessions by themselves, and the ones signed in through Graft, with the way
/// to sign each in again or take it out of every Claude.
struct AccountsView: View {
    @ObservedObject private var pool = Shared.usagePool

    static let explanation = L10n.text("""
        An account signed in here has no Claude of its own. It answers the Code \
        tab of the Claudes that draw on it, and nothing else about it is used.

        Its usage is asked of Anthropic where its login allows that. Otherwise \
        the figures are what Anthropic said on its last answer, and Start \
        Session is the way to ask for new ones.
        """)

    static let orderExplanation = L10n.text("""
        Where Claudes share usage, a new conversation in the Code tab starts on \
        the first account here that has room, and stays on it until that account \
        has none left or you move it. By weekly reset, the account whose week \
        starts again soonest goes first, so what would be lost first is spent first.

        An account with nothing left, or nothing left for the model asked for, \
        is passed over wherever it stands: a model one account has run out of \
        starts on the next, while the other models still start on the first.

        With Auto-start on, an account is sent one short message whenever its \
        five-hour window has run out, so the next window is already counting \
        when you come to use it. The keychain is never asked for it: an account \
        whose login cannot be read, or is refused, is tried again later, and its \
        row says why.
        """)

    var body: some View {
        let order = pool.order()
        Form {
            Section {
                ForEach(Array(order.enumerated()), id: \.element) { place, key in
                    OrderRow(key: key, place: place, count: order.count)
                        .controls(key: key, pool: pool)
                }
            } header: {
                SectionHeader(title: "Order of use", info: Self.orderExplanation, id: "order-info") {
                    if order.count > 1 {
                        Picker(L10n.text("Order of use"), selection: Binding(
                            get: { pool.orderIsSet },
                            set: { pool.setOrder($0 ? order : []) })) {
                            Text("By weekly reset").tag(false)
                            Text("My order").tag(true)
                        }
                        .labelsHidden()
                        .pickerStyle(.menu)
                        .controlSize(.small)
                        .fixedSize()
                        .identified("order.mode")
                    }
                }
            }
            if pool.accounts.isEmpty {
                Section {
                    Text("No accounts are signed in. Add one from a Claude's Shared usage.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .identified("accounts.none")
                }
            }
            ForEach(pool.accounts) { account in
                AccountSection(account: account)
                    .controls(account: account.id)
            }
        }
        .formStyle(.grouped)
        .navigationSubtitle(L10n.text("Sessions, order and sign-ins"))
    }
}

private extension View {
    /// A row about a Claude or an account, whichever the key names.
    @ViewBuilder
    func controls(key: String, pool: UsagePoolController) -> some View {
        if let profile = pool.profile(of: key) { controls(profile: profile) } else { controls(account: key) }
    }
}

/// One account's switch for sessions that start by themselves.
struct AutoSessionToggle: View {
    let key: String
    let name: String
    @ObservedObject private var sessions = Shared.autoSessions

    var body: some View {
        Toggle(L10n.text("Auto-start"), isOn: Binding(get: { sessions.isOn(key) },
                                                      set: { sessions.set(key, on: $0) }))
            .help(sessions.state(of: key)
                  ?? L10n.text("Starts this account's next five-hour window by itself when the last one runs out"))
            .accessibilityLabel(L10n.format("Start sessions automatically for %@", name))
            // On macOS 27.2 the switch reached assistive apps with its value and no press.
            .accessibilityAction { sessions.set(key, on: !sessions.isOn(key)) }
            .identified("auto-session")
    }
}

/// One account: its place, what its week has left, and its two controls.
private struct OrderRow: View {
    let key: String
    let place: Int
    let count: Int

    @ObservedObject private var pool = Shared.usagePool
    @ObservedObject private var sessions = Shared.autoSessions

    var body: some View {
        let name = pool.name(of: key)
        HStack(spacing: 8) {
            if count > 1 {
                Text("\(place + 1)")
                    .font(.callout.monospacedDigit())
                    .foregroundStyle(.secondary)
                    .frame(width: 18, alignment: .trailing)
                    .accessibilityHidden(true)
            }
            if let image = FrontIcon.image(for: pool.preset(of: key)) {
                Image(nsImage: image).resizable().frame(width: 16, height: 16).accessibilityHidden(true)
            }
            Text(name).lineLimit(1).layoutPriority(1).identified("order.name")
            Spacer(minLength: 8)
            // On a clock of its own: a countdown no figure changes for.
            TimelineView(.periodic(from: .now, by: 30)) { context in
                let line = line(now: context.date)
                Text(line.text)
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(line.failed ? Color.orange : Color.secondary)
                    .lineLimit(1)
                    .help(line.text)
                    .identified("order.state")
            }
            if count > 1 {
                Menu {
                    Button("Use First") { pool.move(key, to: 0) }.disabled(place == 0)
                    Button("Move Up") { pool.move(key, to: place - 1) }.disabled(place == 0)
                    Button("Move Down") { pool.move(key, to: place + 1) }.disabled(place == count - 1)
                } label: {
                    Label(L10n.format("Move %@", name), systemImage: "arrow.up.arrow.down")
                        .labelStyle(.iconOnly)
                }
                .menuStyle(.borderlessButton)
                .menuIndicator(.hidden)
                .fixedSize()
                .help("Change its place in the order")
                .identified("order.move")
            }
            // Its label beside it, not as the row's: a switch in a form takes
            // the whole row for its label and leaves the name none.
            Text("Auto-start")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize()
                .accessibilityHidden(true)
            AutoSessionToggle(key: key, name: name)
                .labelsHidden()
                .toggleStyle(.switch)
                .controlSize(.mini)
        }
    }

    /// Why its sessions are not starting, when they are not; otherwise its week.
    private func line(now: Date) -> (text: String, failed: Bool) {
        if sessions.failed(key, now: now), let state = sessions.state(of: key, now: now) { return (state, true) }
        guard let figures = pool.figures(of: key) else { return (L10n.text("No usage reported yet"), false) }
        let week = L10n.format("Week %ld%%", figures.week)
        return (figures.weekReset.flatMap { Graft.countdown(to: $0, from: now) }.map { "\(week) (\($0))" } ?? week, false)
    }
}

private struct AccountSection: View {
    let account: PoolAccount

    @EnvironmentObject private var store: ShortcutStore
    @EnvironmentObject private var usage: UsageMonitor
    @ObservedObject private var pool = Shared.usagePool
    @ObservedObject private var signIn = Shared.accountSignIn
    @State private var name = ""
    @FocusState private var naming: Bool
    @State private var choosingColour = false
    @State private var starting = false
    @State private var problem: String?
    @State private var askingToRemove = false

    private var id: String { account.id }
    private var entry: UsageMonitor.Entry? { pool.entry(forAccount: id) }
    private var shownName: String { pool.name(of: id) }

    var body: some View {
        Section {
            HStack(spacing: 10) {
                colourButton
                TextField(L10n.text("Name"), text: $name, prompt: Text(account.email ?? ""))
                    .labelsHidden()
                    .textFieldStyle(.plain)
                    .focused($naming)
                    .onSubmit { pool.rename(id, to: name) }
                    // Kept on the way out as well: a name typed and left for
                    // another field is a name meant.
                    .onChange(of: naming) { editing in
                        if !editing { pool.rename(id, to: name) }
                    }
                    .identified("name")
                Spacer(minLength: 8)
                Text(state)
                    .font(.caption)
                    .foregroundStyle(pool.needsAttention(id) ? Color.orange : Color.secondary)
                    .lineLimit(1)
                    .help(state)
                    .identified("state")
            }
            // On one row, not the section, whose modifiers land on every row.
            .onAppear { name = account.name ?? "" }
            .confirmationDialog(L10n.format("Remove “%@”?", shownName), isPresented: $askingToRemove,
                                titleVisibility: .visible) {
                Button("Remove Account", role: .destructive) { pool.remove(account: id) }
                Button("Cancel", role: .cancel) {}
            } message: {
                Text(L10n.format("""
                    It is taken out of %@, and its login is deleted from this Mac. \
                    Signing in again brings it back.
                    """, borrowers))
            }
            .alert("Could not start a session", isPresented: .constant(problem != nil)) {
                Button("OK") { problem = nil }
            } message: {
                Text(problem ?? "")
            }
            UsageSummary(entry: entry)
            HStack(spacing: 8) {
                Text(details)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .help(details)
                    .identified("details")
                Spacer(minLength: 8)
                actions
            }
        } header: {
            SectionHeader(title: shownName, info: AccountsView.explanation, id: "account-info") {
                if let entry, asksAnthropic {
                    UsageFreshness(entry: entry) {
                        usage.refresh(store, interactive: true, account: id)
                    }
                    .fixedSize(horizontal: false, vertical: true)
                } else if let entry, entry.refreshed != nil {
                    Text(entry.freshness())
                        .font(.caption2)
                        .foregroundStyle(.tertiary)
                        .lineLimit(1)
                        .identified("freshness")
                }
            }
        }
    }

    /// The usage endpoint answers only a login allowed to read the profile.
    private var asksAnthropic: Bool { account.scopes.contains(ClaudeCredentials.usageScope) }

    private var borrowers: String {
        pool.borrowers(of: id).map(pool.name(of:)).joined(separator: ", ")
    }

    private var state: String {
        var parts: [String] = []
        if let claude = pool.claude(sameAccountAs: id) {
            parts.append(L10n.format("Same account as %@", pool.name(of: claude)))
        }
        parts.append(pool.condition(of: id))
        return parts.joined(separator: " · ")
    }

    /// Whose address it is, once it goes by another name, then who draws on
    /// it and how long its login lasts.
    private var details: String {
        var parts: [String] = []
        if account.name != nil, let email = account.email { parts.append(email) }
        parts.append(L10n.format("Used by %@", borrowers))
        parts.append(L10n.format("Login lasts until %@",
                                 account.expires.formatted(date: .abbreviated, time: .omitted)))
        return parts.joined(separator: " · ")
    }

    /// A sign-in under way, or one that failed, stands where the buttons are
    /// while it lasts, as it does in a Claude's own list.
    @ViewBuilder
    private var actions: some View {
        switch signIn.phase {
        case .waiting(id):
            ProgressView().controlSize(.small)
            Text("Finish signing in in your browser.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .lineLimit(1)
                .identified("signing-in")
            Button("Cancel") { signIn.cancel() }
                .controlSize(.small)
                .accessibilityLabel(L10n.text("Cancel Signing In"))
                .identified("cancel-sign-in")
        case .failed(id, let message):
            Text(message)
                .font(.caption)
                .foregroundStyle(.orange)
                .lineLimit(1)
                .help(message)
                .identified("sign-in-failed")
            Button("OK") { signIn.dismissFailure() }
                .controlSize(.small)
                .identified("dismiss-sign-in-failure")
        default:
            if pool.lending[id] == .ready, entry?.mayStartSession ?? true {
                Button(action: start) {
                    if starting {
                        ProgressView().controlSize(.small)
                    } else {
                        Text("Start Session")
                    }
                }
                .controlSize(.small)
                .disabled(starting)
                .help("Sends one short message to this account to open its five-hour window")
                .accessibilityLabel(L10n.format("Start Session for %@", shownName))
                .identified("start-session")
            }
            Button("Sign In Again") { signIn.renew(id, at: id, interactive: true) }
                .controlSize(.small)
                .disabled(signIn.phase != .idle)
                .help("Replaces this account's login with a new one, for another year")
                .accessibilityLabel(L10n.format("Sign in to %@ again", shownName))
                .identified("sign-in-again")
            Button("Remove…") { askingToRemove = true }
                .controlSize(.small)
                .accessibilityLabel(L10n.format("Remove %@", shownName))
                .identified("remove")
        }
    }

    private func start() {
        guard !starting else { return }
        starting = true
        pool.startSession(account: id) { failure in
            starting = false
            problem = failure
        }
    }

    /// An account that is also a Claude here wears that Claude's colour, which
    /// is changed on that Claude.
    private var colourButton: some View {
        let worn = pool.preset(of: id)
        let fixed = pool.claude(sameAccountAs: id)
        return Button { choosingColour.toggle() } label: {
            icon(worn).frame(width: 22, height: 22)
        }
        .buttonStyle(.plain)
        .disabled(fixed != nil)
        .help(fixed.map { L10n.format("Same account as %@", pool.name(of: $0)) } ?? L10n.text("Account colour"))
        .accessibilityLabel(L10n.text("Account colour"))
        .accessibilityValue(Text(worn.title))
        .identified("colour")
        .popover(isPresented: $choosingColour, arrowEdge: .bottom) {
            LazyVGrid(columns: Array(repeating: GridItem(.fixed(46), spacing: 6), count: 6), spacing: 6) {
                ForEach(Shortcut.IconPreset.allCases) { preset in
                    Button {
                        pool.recolour(id, to: preset)
                        choosingColour = false
                    } label: {
                        icon(preset)
                            .frame(width: 38, height: 38)
                            .padding(4)
                            .overlay(RoundedRectangle(cornerRadius: 8)
                                .stroke(preset == worn ? Color.accentColor : Color.secondary.opacity(0.2),
                                        lineWidth: preset == worn ? 2 : 1))
                    }
                    .buttonStyle(.plain)
                    .help(preset.title)
                    .accessibilityLabel(Text(preset.title))
                    .accessibilityValue(Text(preset == worn ? L10n.text("Selected") : ""))
                    .identified("colour.\(preset.rawValue)")
                }
            }
            .padding(12)
        }
    }

    @ViewBuilder
    private func icon(_ preset: Shortcut.IconPreset) -> some View {
        if let image = FrontIcon.image(for: preset) {
            Image(nsImage: image).resizable().scaledToFit()
        } else {
            Image(systemName: "app.dashed").resizable().scaledToFit().padding(4)
        }
    }
}
