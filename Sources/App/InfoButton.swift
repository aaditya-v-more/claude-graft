import SwiftUI

/// The small "!" beside a section heading. Hovering gives the standard tooltip,
/// clicking opens the same text as a popover — the explanations used to sit
/// under every section as a paragraph of grey text.
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
            Image(systemName: "exclamationmark.circle")
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

/// A section heading with its explanation tucked behind the "!".
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
