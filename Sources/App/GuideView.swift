import SwiftUI

/// How to use the app, one subject at a time, each with a picture of what it
/// describes, so the page reads in one view like every other.
struct GuideView: View {
    @EnvironmentObject private var settings: AppSettings
    @State private var topic = Topic.claudes

    enum Topic: String, CaseIterable, Identifiable {
        case claudes, sharing, accounts, menuBar, everyAccount, dictation

        var id: Self { self }

        var title: String {
            switch self {
            case .claudes: return "A Claude for every account"
            case .sharing: return "Shared usage"
            case .accounts: return "Accounts and sessions"
            case .menuBar: return "The menu bar"
            case .everyAccount: return "Artifacts, projects and chats"
            case .dictation: return "Dictation"
            }
        }

        /// Short enough for three to a row, in either language.
        var short: String {
            switch self {
            case .claudes: return "Claudes"
            case .sharing: return "Shared usage"
            case .accounts: return "Accounts"
            case .menuBar: return "The menu bar"
            case .everyAccount: return "Every account"
            case .dictation: return "Dictation"
            }
        }

        var symbol: String {
            switch self {
            case .claudes: return "rectangle.stack"
            case .sharing: return "arrow.triangle.branch"
            case .accounts: return "person.2"
            case .menuBar: return "menubar.arrow.down.rectangle"
            case .everyAccount: return "square.on.square"
            case .dictation: return "mic"
            }
        }

        var steps: [String] {
            switch self {
            case .claudes:
                return ["Choose Claudes in the sidebar and press New Shortcut. Give it a name, then press Create Shortcut.",
                        "Graft puts a small app with that name in your Applications folder. Open it and sign in to another account.",
                        "It stays signed in, and runs beside your other Claudes, so switching accounts never means signing out.",
                        "A new Claude keeps chats of its own. Under Chats you can have it read another Claude’s Code tab chats instead."]
            case .sharing:
                return ["Open a Claude’s page and look for Shared usage. Add another of your Claudes, or sign in to another account.",
                        "Sharing starts the next time that Claude opens, so close it and open it again once.",
                        "When its own account nears the five-hour or weekly limit, the Code tab carries on with the next account’s allowance.",
                        "Only the answers come from the other account. Your chats, files and connectors stay where they are."]
            case .accounts:
                return ["Accounts shows every account once: what it has used, when its windows start again and which Claudes draw on it.",
                        "Start Session opens an account’s five-hour window with one short message, so it is counting before you need it.",
                        "Auto-start does that by itself each time the window closes. It is off until you switch it on, one account at a time.",
                        "Order of use decides which account is turned to next. Left alone, the one whose week ends soonest goes first."]
            case .menuBar:
                return ["The menu bar shows the Claude in front and how much its account has used.",
                        "Click it to see every account’s five-hour and weekly usage side by side, by Claude or by account.",
                        "Under a Claude that shares usage, pick which account answers each conversation. A tick marks the one in force.",
                        "Closing the window leaves Graft running in the menu bar. Quit Claude Graft, in the same menu, is what ends it."]
            case .everyAccount:
                return ["Open Artifacts and press Sign In…. The one sign-in serves Artifacts, Projects and Search Chats alike.",
                        "Add Account… signs in to a second address. Each page then shows every account’s work in one list.",
                        "Search Chats looks through the chats of all of them at once. Open takes you to the one you pick.",
                        "An address in several organizations chooses which one is shown, from its name at the top of the page."]
            case .dictation:
                return ["Open Dictation in the sidebar and turn on Dictate into any app. macOS asks for Accessibility and the microphone.",
                        "Put the cursor where the words should go, in any app. Hold the key and speak, and a small panel shows the words as you talk.",
                        "Let go, and they are typed. Tap the key twice to keep recording with your hands free, and press it once more to finish.",
                        "Escape throws a recording away. Nothing is ever typed into a password field."]
            }
        }
    }

    var body: some View {
        Form {
            Section {
                // All in view at once: a strip that scrolled sideways hid half of them.
                LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 6, alignment: .leading), count: 3),
                          alignment: .leading, spacing: 6) {
                    ForEach(Topic.allCases) { one in
                        Chip(title: L10n.text(one.short), symbol: one.symbol, isOn: topic == one) { topic = one }
                            .help(L10n.text(one.title))
                            .identified("guide.topic.\(one.rawValue)")
                    }
                }
            }
            Section {
                GuidePicture(topic: topic, key: DictationKey.key(code: settings.dictationKey))
                    .frame(maxWidth: .infinity)
                    .frame(height: 92)
                    .background(Color.primary.opacity(0.04), in: RoundedRectangle(cornerRadius: 10))
                    .accessibilityHidden(true)
                ForEach(Array(topic.steps.enumerated()), id: \.offset) { number, step in
                    HStack(alignment: .firstTextBaseline, spacing: 10) {
                        Text("\(number + 1)")
                            .font(.caption.weight(.bold).monospacedDigit())
                            .foregroundStyle(.white)
                            .frame(width: 18, height: 18)
                            .background(Color.accentColor, in: Circle())
                            .accessibilityHidden(true)
                        Text(L10n.text(step))
                            .font(.callout)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    .identified("guide.step.\(number + 1)")
                }
            } header: {
                Text(L10n.text(topic.title))
            }
        }
        .formStyle(.grouped)
        .navigationSubtitle(L10n.text("How to Use Graft"))
    }
}

/// What a subject looks like in the app, drawn rather than photographed so it
/// follows the appearance and the accent colour.
private struct GuidePicture: View {
    let topic: GuideView.Topic
    let key: DictationKey

    var body: some View {
        switch topic {
        case .claudes:
            HStack(spacing: 22) {
                tile(.orange, "house.fill")
                tile(.blue, "briefcase.fill")
                tile(.green, "person.fill")
                Image(systemName: "plus.circle").font(.title).foregroundStyle(.secondary)
            }
        case .sharing:
            HStack(spacing: 14) {
                tile(.orange, "chevron.left.forwardslash.chevron.right")
                Image(systemName: "arrow.right").font(.title3).foregroundStyle(.secondary)
                VStack(alignment: .leading, spacing: 8) {
                    meter(.orange, 0.96, answering: false)
                    meter(.blue, 0.4, answering: true)
                    meter(.green, 0.1, answering: false)
                }
            }
        case .accounts:
            VStack(alignment: .leading, spacing: 9) {
                ForEach(Array([(Color.orange, 0.7, true), (.blue, 0.35, false), (.green, 0.1, true)].enumerated()),
                        id: \.offset) { _, row in
                    HStack(spacing: 10) {
                        meter(row.0, row.1, answering: false)
                        Image(systemName: "play.circle\(row.2 ? ".fill" : "")")
                            .foregroundStyle(row.2 ? AnyShapeStyle(.tint) : AnyShapeStyle(.secondary))
                    }
                }
            }
        case .menuBar:
            VStack(alignment: .trailing, spacing: 6) {
                HStack(spacing: 6) {
                    Circle().fill(.orange).frame(width: 12, height: 12)
                    bar(.primary.opacity(0.7), 0.4, width: 34)
                    Image(systemName: "wifi").font(.caption)
                    Image(systemName: "battery.75").font(.caption)
                }
                .padding(.horizontal, 10)
                .padding(.vertical, 4)
                .background(Color.primary.opacity(0.1), in: Capsule())
                VStack(alignment: .leading, spacing: 7) {
                    meter(.orange, 0.7, answering: false)
                    meter(.blue, 0.35, answering: true)
                }
                .padding(10)
                .background(Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: 8))
                .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(Color.primary.opacity(0.12)))
            }
        case .everyAccount:
            HStack(spacing: 18) {
                VStack(spacing: 6) {
                    Circle().fill(.orange).frame(width: 16, height: 16)
                    Circle().fill(.blue).frame(width: 16, height: 16)
                }
                Image(systemName: "arrow.right").font(.title3).foregroundStyle(.secondary)
                tile(.gray, "square.on.square")
                tile(.gray, "folder")
                tile(.gray, "magnifyingglass")
            }
        case .dictation:
            HStack(spacing: 14) {
                KeyCap(key: key)
                Image(systemName: "arrow.right").font(.title3).foregroundStyle(.secondary)
                HStack(spacing: 6) {
                    Image(systemName: "waveform").foregroundStyle(.tint)
                    bar(.primary.opacity(0.5), 1, width: 70)
                }
                .padding(.horizontal, 12)
                .padding(.vertical, 8)
                .background(.regularMaterial, in: Capsule())
                .overlay(Capsule().strokeBorder(Color.primary.opacity(0.15)))
                Image(systemName: "arrow.right").font(.title3).foregroundStyle(.secondary)
                VStack(alignment: .leading, spacing: 5) {
                    bar(.primary.opacity(0.5), 1, width: 90)
                    HStack(spacing: 2) {
                        bar(.primary.opacity(0.5), 1, width: 54)
                        Rectangle().fill(.tint).frame(width: 2, height: 12)
                    }
                }
                .padding(10)
                .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 6))
                .overlay(RoundedRectangle(cornerRadius: 6).strokeBorder(Color.primary.opacity(0.12)))
            }
        }
    }

    private func tile(_ colour: Color, _ symbol: String) -> some View {
        RoundedRectangle(cornerRadius: 13)
            .fill(colour.gradient)
            .frame(width: 56, height: 56)
            .overlay(Image(systemName: symbol).font(.title2).foregroundStyle(.white))
            .shadow(color: .black.opacity(0.15), radius: 2, y: 1)
    }

    /// An account and how much of its allowance is gone.
    private func meter(_ colour: Color, _ used: Double, answering: Bool) -> some View {
        HStack(spacing: 7) {
            Circle().fill(colour).frame(width: 14, height: 14)
                .overlay(Circle().strokeBorder(Color.red, lineWidth: answering ? 2 : 0).padding(-3))
            bar(used > 0.9 ? .red : colour, used, width: 110)
        }
    }

    private func bar(_ colour: Color, _ filled: Double, width: CGFloat) -> some View {
        ZStack(alignment: .leading) {
            Capsule().fill(Color.primary.opacity(0.12))
            Capsule().fill(colour).frame(width: width * filled)
        }
        .frame(width: width, height: 6)
    }
}
