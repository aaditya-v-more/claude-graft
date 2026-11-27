import SwiftUI

/// Dictation's own page: every switch it has, and how the key is used.
struct DictationView: View {
    @EnvironmentObject private var settings: AppSettings
    @ObservedObject private var history = Shared.dictationHistory
    @State private var showingHistory = false

    static let privacy = """
        What is heard goes to Anthropic to be written down, with the login of a Claude you are signed in to. \
        Claude Graft never keeps the sound, and keeps the words only if you turn History on, on this Mac alone.
        """

    var body: some View {
        Form {
            Section {
                Text("Claude's own built-in dictation, working in every app.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .identified("dictation-what")
                DictationControl()
                Toggle("Also copy what was said to the clipboard", isOn: $settings.dictationCopies)
                    .help("The words are typed as before, and are on the clipboard as well, to paste again somewhere else.")
                    .disabled(!settings.dictationEnabled)
                    .font(.callout)
                    .identified("dictation-copies")
                Picker("History", selection: $settings.dictationHistory) {
                    Text("Off").tag(0)
                    ForEach(DictationHistory.sizes, id: \.self) { count in
                        Text(L10n.format("The last %ld", count)).tag(count)
                    }
                }
                .help("Keeps the words of what you dictate, on this Mac, so you can find and copy them again. The sound is never kept. Off, nothing is kept and what was kept is deleted.")
                .font(.callout)
                .identified("dictation-history")
                if settings.dictationHistory > 0 {
                    HStack(spacing: 8) {
                        Text(L10n.format("%ld kept on this Mac", history.entries.count))
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .identified("dictation-history-count")
                        Spacer(minLength: 0)
                        Button("Show History…") { showingHistory = true }
                            .controlSize(.small)
                            .identified("dictation-history-show")
                    }
                }
            } header: {
                SectionHeader(title: "Dictation", info: L10n.text(DictationControl.how), id: "dictation-info")
            }
            Section {
                DictationSteps(key: DictationKey.key(code: settings.dictationKey))
            } header: {
                Text("How to use it")
            } footer: {
                Text(L10n.text(Self.privacy))
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.top, 2)
                    .identified("dictation-privacy")
            }
        }
        .formStyle(.grouped)
        .navigationSubtitle(L10n.text("Claude's built-in dictation, in any app"))
        .sheet(isPresented: $showingHistory) {
            DictationHistoryView(history: history) { showingHistory = false }
                .environment(\.controlSurface, ControlSurface.window)
        }
    }
}

/// What has been kept, newest first, each to copy again or take out.
struct DictationHistoryView: View {
    @ObservedObject var history: DictationHistory
    let done: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Text("Dictation history")
                .font(.headline)
                .padding(14)
            Divider()
            if history.entries.isEmpty {
                Text("Nothing has been dictated since History was turned on.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .identified("history.none")
            } else {
                List {
                    ForEach(Array(history.entries.enumerated()), id: \.element.id) { place, entry in
                        HStack(alignment: .top, spacing: 8) {
                            VStack(alignment: .leading, spacing: 2) {
                                Text(entry.at.formatted(date: .abbreviated, time: .shortened))
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                                Text(entry.words)
                                    .font(.callout)
                                    .lineLimit(4)
                                    .textSelection(.enabled)
                            }
                            .frame(maxWidth: .infinity, alignment: .leading)
                            Button { DictationTyping.copy(entry.words) } label: {
                                Label("Copy", systemImage: "doc.on.doc").labelStyle(.iconOnly)
                            }
                            .help("Copy")
                            .identified("history.\(place).copy")
                            Button { history.remove(entry.id) } label: {
                                Label("Remove", systemImage: "trash").labelStyle(.iconOnly)
                            }
                            .help("Remove")
                            .identified("history.\(place).remove")
                        }
                        .buttonStyle(.borderless)
                    }
                }
            }
            Divider()
            HStack {
                Button("Clear History", role: .destructive) { history.keep(0) }
                    .disabled(history.entries.isEmpty)
                    .identified("history.clear")
                Spacer()
                Button("Done", action: done)
                    .keyboardShortcut(.defaultAction)
                    .identified("history.done")
            }
            .padding(14)
        }
        .frame(width: 520, height: 420)
    }
}

/// The three things the key does, each under a picture of the key doing it.
struct DictationSteps: View {
    let key: DictationKey

    var body: some View {
        HStack(alignment: .top, spacing: 14) {
            step("Hold and speak", "Let go, and the words are typed where the cursor is.") {
                KeyCap(key: key)
                Image(systemName: "waveform").font(.title2).foregroundStyle(.tint)
            }
            step("Tap twice", "Keeps recording with your hands free. Press once more to finish.") {
                KeyCap(key: key)
                KeyCap(key: key)
            }
            step("Escape", "Throws the recording away.") {
                KeyCap(legend: "esc")
            }
        }
        .padding(.vertical, 4)
    }

    private func step<Picture: View>(_ title: String, _ detail: String,
                                     @ViewBuilder picture: () -> Picture) -> some View {
        VStack(spacing: 6) {
            HStack(spacing: 6) { picture() }
                .frame(height: 46)
                .accessibilityHidden(true)
            Text(L10n.text(title))
                .font(.callout.weight(.semibold))
            Text(L10n.text(detail))
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity)
        .accessibilityElement(children: .combine)
    }
}

/// A key as it looks on the keyboard.
struct KeyCap: View {
    let glyph: String?
    let legend: String

    init(legend: String, glyph: String? = nil) {
        self.legend = legend
        self.glyph = glyph
    }

    init(key: DictationKey) {
        switch key {
        case .rightCommand: self.init(legend: "command", glyph: "⌘")
        case .rightOption: self.init(legend: "option", glyph: "⌥")
        case .rightControl: self.init(legend: "control", glyph: "⌃")
        case .function: self.init(legend: "fn")
        default: self.init(legend: key.name)
        }
    }

    var body: some View {
        VStack(spacing: 0) {
            if let glyph {
                Text(glyph).font(.system(size: 15, weight: .medium))
            }
            Text(legend).font(.system(size: glyph == nil ? 13 : 9, weight: .medium))
        }
        .foregroundStyle(.primary)
        .frame(minWidth: 46, minHeight: 40)
        .padding(.horizontal, 4)
        .background(RoundedRectangle(cornerRadius: 8).fill(Color(nsColor: .controlBackgroundColor))
            .shadow(color: .black.opacity(0.25), radius: 0.5, y: 1.5))
        .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(Color.primary.opacity(0.18)))
    }
}

/// One of a row of choices that fills the page under it.
struct Chip: View {
    let title: String
    let symbol: String
    let isOn: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Label(title, systemImage: symbol)
                .font(.callout)
                .lineLimit(1)
                .padding(.horizontal, 10)
                .padding(.vertical, 5)
                .background(isOn ? Color.accentColor.opacity(0.2) : Color.primary.opacity(0.06), in: Capsule())
                .overlay(Capsule().strokeBorder(isOn ? Color.accentColor.opacity(0.7) : Color.clear))
                .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(isOn ? .isSelected : [])
    }
}
