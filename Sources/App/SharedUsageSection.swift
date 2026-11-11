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
                notes
                if showsNothing {
                    Text("No accounts added")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .identified("shared-usage.none")
                }
            }
        } header: {
            // Adding is in the heading, where it belongs to the whole list
            // and costs no row of its own.
            SectionHeader(title: "Shared usage", info: Self.explanation, id: "shared-usage-info") {
                if UsagePoolController.isSupported { headerTrailing }
            }
        }
    }

    private var lenders: [String] { pool.lenders(of: claude) }

    /// Other Claudes and accounts already signed in elsewhere, then a new one.
    private var candidates: [String] {
        let taken = Set(lenders + [claude])
        return (pool.claudes + pool.accounts.map(\.id)).filter { !taken.contains($0) }
    }

    /// A section with nothing in it draws an empty box under its heading.
    private var showsNothing: Bool {
        lenders.isEmpty && noteLines.isEmpty
    }

    private var isSigningIn: Bool {
        if case .waiting(claude) = signIn.phase { return true }
        return false
    }

    private var addMenu: some View {
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
    }

    /// A sign-in under way, or one that failed, stands where Add is while it
    /// lasts: Add has nothing to offer then, and a row of its own pushed the
    /// list past the bottom of the window.
    @ViewBuilder
    private var headerTrailing: some View {
        switch signIn.phase {
        case .waiting(claude):
            HStack(spacing: 6) {
                ProgressView().controlSize(.small)
                Text("Finish signing in in your browser.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .identified("shared-usage.signing-in")
                // A bare "Cancel" in a list of every control says nothing
                // about what it would stop.
                Button("Cancel") { signIn.cancel() }
                    .controlSize(.small)
                    .accessibilityLabel(L10n.text("Cancel Signing In"))
                    .identified("shared-usage.cancel-sign-in")
            }
        case .failed(claude, let message):
            HStack(spacing: 6) {
                Text(message)
                    .font(.caption)
                    .foregroundStyle(.orange)
                    .lineLimit(1)
                    .help(message)
                    .identified("shared-usage.sign-in-failed")
                Button("OK") { signIn.dismissFailure() }
                    .controlSize(.small)
                    .identified("shared-usage.dismiss-sign-in-failure")
            }
        default:
            addMenu
        }
    }

    /// One line at most, whatever needs saying. Three of them under the list
    /// were three rows below the bottom of the window, so the most pressing
    /// is shown and the others wait behind its ⓘ.
    @ViewBuilder
    private var notes: some View {
        let lines = noteLines
        if let first = lines.first {
            HStack(alignment: .firstTextBaseline) {
                OneLineNote(text: first.text,
                            more: lines.count > 1 ? lines.map(\.text).joined(separator: "\n\n") : nil,
                            topic: "Shared usage", attention: first.attention, id: first.id)
                Spacer()
                // Only beside its own line: beside another, "Turn On" would
                // read as turning that on.
                if first.id == Self.openAtLoginNote {
                    Button("Turn On") { problem = settings.setOpenAtLogin(true) }
                        .controlSize(.small)
                        .accessibilityLabel(L10n.text("Turn On Open at Login"))
                        .identified("shared-usage.turn-on-open-at-login")
                }
            }
        }
    }

    private static let openAtLoginNote = "shared-usage.open-at-login"

    /// Most pressing first: something wrong, then what this Claude is waiting
    /// on, then the reminder.
    private var noteLines: [(text: String, attention: Bool, id: String)] {
        var lines: [(text: String, attention: Bool, id: String)] = []
        if !lenders.isEmpty, let problem = problem ?? pool.problem {
            lines.append((problem, true, "shared-usage.problem"))
        }
        if let line = environmentLine {
            lines.append((line.text, line.attention, "shared-usage.environment"))
        }
        // The relay is what a connected Claude Code sends everything through,
        // so after a restart it has to be back before that Claude is used.
        if !lenders.isEmpty, !settings.openAtLogin {
            lines.append((L10n.text("Open at Login keeps this working after a restart"), false, Self.openAtLoginNote))
        }
        return lines.filter(\.attention) + lines.filter { !$0.attention }
    }

    private var environmentLine: (text: String, attention: Bool)? {
        switch pool.environment[claude] {
        case .waiting:
            return (L10n.text("Starts the next time this Claude opens."), false)
        case .leaving:
            return (L10n.text("Stops when this Claude next closes."), false)
        case .conflict(let name, _):
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
            HStack(alignment: .center, spacing: 8) {
                // The colour it wears in the menu bar, which is how a
                // lit icon there is matched to an account here.
                if let icon = FrontIcon.image(for: pool.preset(of: lender)) {
                    Image(nsImage: icon).resizable().frame(width: 16, height: 16)
                        .accessibilityHidden(true)
                }
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
            return L10n.text("Login locked · open the keychain from the menu bar")
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
        ConversationList(chats: pool.chats(front: frontmost.profile),
                         front: pool.claudeKey(for: frontmost.profile))
    }
}

/// The dropdown's word that the relay is carrying on with the logins it was
/// last given, because this Graft cannot read the key to give it current ones.
/// Every update arrives under a signature the keychain item has never seen,
/// and nothing else said why lending had stopped following the logins.
struct LendingPausedNotice: View {
    @ObservedObject private var pool = Shared.usagePool

    var body: some View {
        if pool.lendingPaused {
            Button { pool.openKeychain(interactive: true) } label: {
                Label("Lending paused · open the keychain", systemImage: "lock")
                    .font(.callout)
            }
            .buttonStyle(.link)
            .help("Graft cannot read Claude's logins until macOS lets it into the keychain, so it cannot hand them to the relay. macOS asks once; choose Always Allow.")
            .identified("lending-paused")
            .padding(.horizontal, 14)
            .padding(.bottom, 8)
        }
    }
}

/// Apart from where the list comes from, so the layout suite can draw it from
/// figures: in there no relay has carried anything.
///
/// The conversation in front comes first and on its own, since it is the one
/// whose account the menu bar lights and the one somebody opening the dropdown
/// is asking about. The rest wait behind a line that says how many there are:
/// five rows of other conversations pushed the figures out of a short screen.
struct ConversationList: View {
    let chats: [UsagePoolController.Chat]
    var front: String? = nil
    @State private var othersShown: Bool

    init(chats: [UsagePoolController.Chat], front: String? = nil, othersShown: Bool = false) {
        self.chats = chats
        self.front = front
        _othersShown = State(initialValue: othersShown)
    }

    private var inFront: UsagePoolController.Chat? {
        chats.first.flatMap { $0.claude == front ? $0 : nil }
    }

    var body: some View {
        if !chats.isEmpty {
            let others = chats.filter { $0.id != inFront?.id }
            let showClaude = Set(chats.map(\.claude)).count > 1
            VStack(alignment: .leading, spacing: 6) {
                Text("Claude Code")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .accessibilityAddTraits(.isHeader)
                    .identified("conversations")
                if let inFront {
                    ChatRow(chat: inFront, showClaude: false, inFront: true)
                    if !others.isEmpty {
                        Button {
                            othersShown.toggle()
                        } label: {
                            HStack(spacing: 4) {
                                Image(systemName: othersShown ? "chevron.down" : "chevron.right")
                                    .font(.caption2.weight(.semibold))
                                Text(L10n.format("Other conversations · %ld", others.count))
                                    .font(.caption)
                            }
                            .foregroundStyle(.secondary)
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                        .accessibilityLabel(L10n.format("Other conversations · %ld", others.count))
                        .accessibilityValue(othersShown ? L10n.text("Shown") : L10n.text("Hidden"))
                        .identified("conversations.others")
                    }
                }
                if inFront == nil || othersShown {
                    ForEach(others) { chat in
                        ChatRow(chat: chat, showClaude: showClaude, inFront: false)
                    }
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
    var inFront = false
    @ObservedObject private var pool = Shared.usagePool

    var body: some View {
        let title = chat.title ?? L10n.text("Untitled conversation")
        HStack(spacing: 8) {
            VStack(alignment: .leading, spacing: 1) {
                Text(title)
                    .font(.callout)
                    .fontWeight(inFront ? .medium : .regular)
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
                // A picker, because a menu draws a picker's choice with the
                // checkmark it draws for any menu item that is on. A label
                // carrying a checkmark symbol was dropped as an icon, and the
                // menu showed no choice at all.
                Picker(selection: Binding(get: { chat.assigned ?? "" },
                                          set: { pool.assign(chat.id, to: $0.isEmpty ? nil : $0) })) {
                    Text(pool.automaticTitle(chat)).tag("")
                    ForEach(chat.choices, id: \.self) { choice in
                        Text(pool.name(of: choice)).tag(choice)
                    }
                } label: {
                    EmptyView()
                }
                .pickerStyle(.inline)
                .labelsHidden()
            } label: {
                // The same picture the menu bar lights, so the two are read
                // as one fact.
                HStack(spacing: 4) {
                    if let key = answeringKey, let icon = FrontIcon.image(for: pool.preset(of: key)) {
                        Image(nsImage: icon).resizable().frame(width: 14, height: 14)
                    }
                    Text(answering).font(.caption)
                }
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

    private var answeringKey: String? { UsagePoolController.answeringKey(chat) }

    /// Who answered last, or who it was moved to and has not answered yet,
    /// and whether that was chosen or left to the ranking: with "Auto" in
    /// front it may move on its own, without it it stays where it was put.
    private var answering: String {
        guard let key = answeringKey else { return L10n.text("Automatic") }
        return chat.assigned == nil ? L10n.format("Auto · %@", pool.name(of: key)) : pool.name(of: key)
    }

    private var detail: String {
        if chat.passedOver, let assigned = chat.assigned {
            return L10n.format("%@ had no room", pool.name(of: assigned))
        }
        return L10n.format("in %@", pool.name(of: chat.claude))
    }
}
