import SwiftUI

/// The small "!" beside a section heading. Hovering gives the standard tooltip,
/// clicking opens the same text as a popover — the explanations used to sit
/// under every section as a paragraph of grey text.
struct InfoButton: View {
    let text: String
    @State private var showing = false

    init(_ text: String) { self.text = L10n.text(text) }

    var body: some View {
        Button { showing.toggle() } label: {
            Image(systemName: "exclamationmark.circle")
                .imageScale(.medium)
                .foregroundStyle(.secondary)
        }
        .buttonStyle(.plain)
        .help(text)
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

    var body: some View {
        HStack(spacing: 5) {
            Text(L10n.text(title))
            InfoButton(info)
            Spacer()
        }
    }
}
