import SwiftUI

/// The small ⓘ beside a heading or a note. Hovering gives the standard
/// tooltip, clicking opens the same text as a popover. The explanations used
/// to sit under every section as paragraphs of grey text, and the window had to
/// scroll to get past them.
struct InfoButton: View {
    let text: String
    let topic: String
    let id: String
    @State private var showing = false

    /// `topic` is what VoiceOver says the explanation is about, since the
    /// symbol is all there is to see and SwiftUI reads that one as "Error".
    init(_ text: String, about topic: String, id: String) {
        self.text = text
        self.topic = topic
        self.id = id
    }

    var body: some View {
        Button { showing.toggle() } label: {
            Image(systemName: "info.circle")
                .imageScale(.medium)
                .foregroundStyle(.secondary)
        }
        .buttonStyle(.plain)
        .help(text)
        .accessibilityLabel(L10n.format("About %@", L10n.text(topic)))
        .identified(id)
        .popover(isPresented: $showing, arrowEdge: .bottom) {
            Text(text)
                .font(.callout)
                .fixedSize(horizontal: false, vertical: true)
                .frame(width: 300, alignment: .leading)
                .padding(14)
        }
    }
}

/// A section heading with its explanation tucked behind the ⓘ.
struct SectionHeader: View {
    let title: String
    let info: String
    let id: String

    var body: some View {
        HStack(spacing: 5) {
            Text(L10n.text(title))
            InfoButton(info, about: title, id: id)
            Spacer()
        }
    }
}

/// A note held to one line, with anything longer behind the ⓘ beside it, so a
/// window built from notes still fits without scrolling.
struct OneLineNote: View {
    let text: String
    var more: String?
    var topic = "Details"
    var attention = false
    let id: String

    var body: some View {
        HStack(spacing: 4) {
            Text(text)
                .font(.caption)
                .foregroundStyle(attention ? Color.orange : Color.secondary)
                .lineLimit(1)
                .truncationMode(.tail)
                .help(more ?? text)
                .identified(id)
            if let more { InfoButton(more, about: topic, id: "\(id)-info") }
        }
    }
}
