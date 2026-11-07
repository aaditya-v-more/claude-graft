import SwiftUI

/// The accounts one Claude may draw on, in that Claude's own pane. Nothing
/// about the Claude itself changes: it keeps its account, its chats and its
/// sidebar, and only the model requests its Claude Code makes can be answered
/// by an account listed here.
struct SharedUsageSection: View {
    let claude: String

    @EnvironmentObject private var settings: AppSettings
    @EnvironmentObject private var usage: UsageMonitor
    @ObservedObject private var pool = Shared.usagePool
    @ObservedObject private var signIn = Shared.accountSignIn
    @State private var problem: String?

    static let explanation = L10n.text("""
        The Code tab in this Claude can be answered by the accounts added here. Each \
        request goes to whichever has room and the soonest weekly reset, and a \
        conversation stays on one account while its cache is still warm there. \
        Chats, artifacts, connectors and Claude in Chrome stay with this Claude's \
        own account, and the Chat tab is never shared.

        A request answered by another account carries the conversation to that \
        account. Anthropic's terms for Claude plans may not allow one person to \
        combine subscriptions this way, so read them before adding one.
        """)

    var body: some View {
        Section {
            if !UsagePoolController.isSupported {
                Text("Sharing usage needs macOS 15 or later.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .identified("shared-usage.unsupported")
            } else {
                ForEach(lenders, id: \.self) { lender in
                    LenderRow(claude: claude, lender: lender,
                              entry: pool.profile(of: lender).flatMap(usage.entry(for:)))
                }
                signInState
                addMenu
                notes
            }
        } header: {
            SectionHeader(title: "Shared usage", info: Self.explanation, id: "shared-usage-info")
        }
    }

    private var lenders: [String] { pool.lenders(of: claude) }

    /// Other Claudes and accounts already signed in elsewhere, then a new one.
    private var candidates: [String] {
        let taken = Set(lenders + [claude])
        return (pool.claudes + pool.accounts.map(\.id)).filter { !taken.contains($0) }
    }

    private var isSigningIn: Bool {
        if case .waiting(claude) = signIn.phase { return true }
        return false
    }

    private var addMenu: some View {
        HStack {
            Menu {
                ForEach(candidates, id: \.self) { key in
                    Button(pool.name(of: key)) { pool.addLender(key, to: claude) }
                }
                if !candidates.isEmpty { Divider() }
                Button("Sign In to Another Account…") { signIn.start(for: claude, interactive: true) }
                    .disabled(isSigningIn)
            } label: {
                Label(lenders.isEmpty ? "Add an Account" : "Add Another Account", systemImage: "plus")
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
            .identified("shared-usage.add")
            Spacer()
        }
    }

    @ViewBuilder
    private var signInState: some View {
        switch signIn.phase {
        case .waiting(claude):
            HStack(spacing: 8) {
                ProgressView().controlSize(.small)
                Text("Finish signing in in your browser.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .identified("shared-usage.signing-in")
                Spacer()
                // A bare "Cancel" in a list of every control says nothing
                // about what it would stop.
                Button("Cancel") { signIn.cancel() }
                    .accessibilityLabel(L10n.text("Cancel Signing In"))
                    .identified("shared-usage.cancel-sign-in")
            }
        case .failed(claude, let message):
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text(message)
                    .font(.callout)
                    .foregroundStyle(.orange)
                    .lineLimit(1)
                    .help(message)
                    .identified("shared-usage.sign-in-failed")
                Spacer()
                Button("OK") { signIn.dismissFailure() }
                    .identified("shared-usage.dismiss-sign-in-failure")
            }
        default:
            EmptyView()
        }
    }

    @ViewBuilder
    private var notes: some View {
        if let line = environmentLine {
            OneLineNote(text: line.text, attention: line.attention, id: "shared-usage.environment")
        }
        if !lenders.isEmpty, let problem = problem ?? pool.problem {
            OneLineNote(text: problem, attention: true, id: "shared-usage.problem")
        }
        // The relay is what a connected Claude Code sends everything through,
        // so after a restart it has to be back before that Claude is used.
        if !lenders.isEmpty, !settings.openAtLogin {
            HStack(alignment: .firstTextBaseline) {
                OneLineNote(text: L10n.text("Open at Login keeps this working after a restart"),
                            id: "shared-usage.open-at-login")
                Spacer()
                Button("Turn On") { problem = settings.setOpenAtLogin(true) }
                    .controlSize(.small)
                    .accessibilityLabel(L10n.text("Turn On Open at Login"))
                    .identified("shared-usage.turn-on-open-at-login")
            }
        }
    }

    private var environmentLine: (text: String, attention: Bool)? {
        switch pool.environment[claude] {
        case .waiting:
            return (L10n.text("Starts the next time this Claude opens."), false)
        case .leaving:
            return (L10n.text("Stops when this Claude next closes."), false)
        case .conflict(let name):
            return (L10n.format("Not shared: this Claude already sets %@", name), true)
        case .unreadable:
            return (L10n.text("Not shared: this Claude's Code environment is unreadable"), true)
        case .on, .off, nil:
            return nil
        }
    }
}

private struct LenderRow: View {
    let claude: String
    let lender: String
    let entry: UsageMonitor.Entry?
    @ObservedObject private var pool = Shared.usagePool

    var body: some View {
        let name = pool.name(of: lender)
        // One line per account: two bars under each of them made the list
        // taller than the window, and the figures read as well as text.
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            // Name and state are one element, and the button beside them is
            // another: spoken over the whole row, the button took the row's
            // name and VoiceOver said the account twice and the action never.
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text(name).fontWeight(.medium).lineLimit(1)
                    .layoutPriority(1)
                Text(state)
                    .font(.caption)
                    .foregroundStyle(attention ? Color.orange : Color.secondary)
                    .lineLimit(1)
                    .help(state)
            }
            .spoken(name, value: state)
            .identified("lender.\(token)")
            Spacer(minLength: 8)
            if let figures {
                Text(figuresLine(figures))
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(entry?.isDimmed == true ? Color.secondary.opacity(0.6) : Color.secondary)
                    .lineLimit(1)
                    .help(figuresHelp(figures))
                    .identified("lender.\(token).usage")
            }
            Button {
                pool.removeLender(lender, from: claude)
            } label: {
                Image(systemName: "minus.circle")
            }
            .buttonStyle(.borderless)
            .help("Stop drawing on this account")
            .accessibilityLabel(L10n.format("Stop drawing on %@", name))
            .identified("lender.\(token).remove")
        }
        .padding(.vertical, 2)
    }

    private func figuresLine(_ figures: (fiveHour: Int?, fiveHourReset: Date?, week: Int, weekReset: Date?)) -> String {
        let week = L10n.format("Week %ld%%", figures.week)
        guard let fiveHour = figures.fiveHour else { return week }
        return L10n.format("5h %ld%%", fiveHour) + " · " + week
    }

    private func figuresHelp(_ figures: (fiveHour: Int?, fiveHourReset: Date?, week: Int, weekReset: Date?)) -> String {
        [figures.fiveHourReset.map { L10n.format("5 hours reset %@", UsageMonitor.clock($0)) },
         figures.weekReset.map { L10n.format("Week resets %@", UsageMonitor.clock($0)) }]
            .compactMap { $0 }.joined(separator: "\n")
    }

    private var isAccount: Bool { lender.hasPrefix(UsagePoolController.accountPrefix) }
    private var reported: PoolMemberStatus? { pool.status?.members.first { $0.id == lender } }

    /// A Claude goes by its profile folder, as every other control naming a
    /// profile does, and an account by the id it was saved under, which is the
    /// same in every language where the email on screen may not be unique.
    private var token: String {
        if let profile = pool.profile(of: lender) { return profile.lastPathComponent }
        return isAccount ? String(lender.dropFirst(UsagePoolController.accountPrefix.count)) : lender
    }

    /// A Claude's own figure where there is one, since Graft asks for it; an
    /// account signed in here has only what the service said on its answers.
    ///
    /// The service's headers name a five-hour reset on every answer for an
    /// account that has the window, so one without a reset is taken to have
    /// none rather than shown at a 0% that never moves.
    private var figures: (fiveHour: Int?, fiveHourReset: Date?, week: Int, weekReset: Date?)? {
        if let usage = entry?.usage {
            return (usage.fiveHour, usage.fiveHourReset, usage.week, usage.weekReset)
        }
        guard let limits = reported?.limits else { return nil }
        let fiveHour = limits.fiveHourReset == nil && limits.fiveHour == 0
            ? nil : Int((limits.fiveHour * 100).rounded())
        return (fiveHour, limits.fiveHourReset, Int((limits.week * 100).rounded()), limits.weekReset)
    }

    private var sameAsBorrower: Bool {
        guard let own = pool.accountUuid(of: claude) else { return false }
        return pool.accountUuid(of: lender) == own
    }

    private var attention: Bool {
        sameAsBorrower || reported?.tokenRefused == true
            || (pool.lending[lender] != nil && pool.lending[lender] != .ready)
    }

    private var state: String {
        var parts: [String] = []
        if sameAsBorrower {
            parts.append(L10n.text("Same account as this Claude"))
        } else if let other = pool.sameAccount(as: lender, besides: claude) {
            parts.append(L10n.format("Same account as %@", pool.name(of: other)))
        }
        parts.append(condition)
        return parts.joined(separator: " · ")
    }

    private var condition: String {
        if reported?.tokenRefused == true {
            return isAccount ? L10n.text("Login refused · sign in again")
                             : L10n.text("Login refused · open that Claude to renew it")
        }
        switch pool.lending[lender] {
        case nil:
            return L10n.text("Checking its login…")
        case .signedOut:
            return L10n.text("Not signed in")
        case .expired:
            return isAccount ? L10n.text("Login expired · sign in again")
                             : L10n.text("Login expired · open that Claude to renew it")
        case .unreadable:
            return L10n.text("Login locked · turn on live usage in the menu bar")
        case .ready:
            break
        }
        if let limits = reported?.limits, !PoolPolicy.hasRoom(limits, now: Date()) {
            if let until = limits.refusedUntil ?? limits.fiveHourReset {
                return L10n.format("Full until %@", until.formatted(date: .omitted, time: .shortened))
            }
            return L10n.text("Full")
        }
        if let served = reported?.served, served > 0 {
            return L10n.format("Ready · answered %ld requests", served)
        }
        return L10n.text("Ready")
    }
}

/// Conversations in the dropdown, each with the account answering it and a
/// menu to move it. Only conversations from a Claude that has accounts to draw
/// on are listed, the one in front first.
struct SharedChats: View {
    @ObservedObject private var pool = Shared.usagePool
    @ObservedObject private var frontmost = Shared.frontmost

    var body: some View {
        ConversationList(chats: pool.chats(front: frontmost.profile))
    }
}

/// Apart from where the list comes from, so the layout suite can draw it from
/// figures: in there no relay has carried anything.
struct ConversationList: View {
    let chats: [UsagePoolController.Chat]

    var body: some View {
        if !chats.isEmpty {
            VStack(alignment: .leading, spacing: 6) {
                Text("Claude Code")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .accessibilityAddTraits(.isHeader)
                    .identified("conversations")
                ForEach(chats) { chat in
                    ChatRow(chat: chat, showClaude: Set(chats.map(\.claude)).count > 1)
                }
            }
            .padding(.horizontal, 14)
            .padding(.top, 8)
            .padding(.bottom, 2)
        }
    }
}

private struct ChatRow: View {
    let chat: UsagePoolController.Chat
    let showClaude: Bool
    @ObservedObject private var pool = Shared.usagePool

    var body: some View {
        let title = chat.title ?? L10n.text("Untitled conversation")
        HStack(spacing: 8) {
            VStack(alignment: .leading, spacing: 1) {
                Text(title)
                    .font(.callout)
                    .lineLimit(1)
                    .truncationMode(.tail)
                if showsDetail {
                    Text(detail)
                        .font(.caption)
                        .foregroundStyle(chat.passedOver ? Color.orange : Color.secondary)
                        .lineLimit(1)
                }
            }
            .spoken(title, value: showsDetail ? detail : "")
            .identified("conversation.\(chat.id)")
            Spacer(minLength: 4)
            Menu {
                Button {
                    pool.assign(chat.id, to: nil)
                } label: {
                    if chat.assigned == nil { Label("Automatic", systemImage: "checkmark") } else { Text("Automatic") }
                }
                Divider()
                ForEach(chat.choices, id: \.self) { choice in
                    Button {
                        pool.assign(chat.id, to: choice)
                    } label: {
                        if chat.assigned == choice {
                            Label(pool.name(of: choice), systemImage: "checkmark")
                        } else {
                            Text(pool.name(of: choice))
                        }
                    }
                }
            } label: {
                Text(answering)
                    .font(.caption)
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
            .help("Which account answers this conversation")
            // The menu shows only the account, and five of them reading
            // "Claude 2" would not say which conversation each one moves.
            .accessibilityLabel(L10n.format("Account answering “%@”", title))
            .accessibilityValue(answering)
            .identified("conversation.\(chat.id).account")
        }
    }

    private var showsDetail: Bool { showClaude || chat.passedOver }

    /// Who answered last, or who it was moved to and has not answered yet.
    private var answering: String {
        if let served = chat.servedBy { return pool.name(of: served) }
        if let assigned = chat.assigned { return pool.name(of: assigned) }
        return L10n.text("Automatic")
    }

    private var detail: String {
        if chat.passedOver, let assigned = chat.assigned {
            return L10n.format("%@ had no room", pool.name(of: assigned))
        }
        return L10n.format("in %@", pool.name(of: chat.claude))
    }
}
