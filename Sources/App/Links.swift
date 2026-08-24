import AppKit
import Foundation
import SwiftUI

/// The addresses this app sends people to.
///
/// One account name, spelled once. The site, the source and the sponsor page
/// all hang off it, and a build already installed keeps whatever it was
/// compiled with — so a rename that touched two of the three would leave a
/// dead link in every copy out in the world. The suite checks that this name
/// and the one in `.github/FUNDING.yml` agree.
enum Links {
    static let owner = "aaditya-v-more"

    static let site = URL(string: "https://\(owner).github.io/claude-graft/")!
    static let source = URL(string: "https://github.com/\(owner)/claude-graft")!
    static let sponsor = URL(string: "https://github.com/sponsors/\(owner)")!

    static func open(_ url: URL) {
        NSWorkspace.shared.open(url)
    }
}

/// A quiet text link for the sidebar's footer. Underlined only under the
/// pointer, because two permanently underlined words at the foot of a sidebar
/// read as an error message rather than an invitation.
struct FooterLink: View {
    let title: String
    let icon: String
    let url: URL
    let help: String

    @State private var hovering = false

    init(_ title: String, icon: String, url: URL, help: String) {
        self.title = title
        self.icon = icon
        self.url = url
        self.help = help
    }

    var body: some View {
        Button { Links.open(url) } label: {
            Label(title, systemImage: icon)
                .font(.callout)
                .foregroundStyle(hovering ? AnyShapeStyle(.primary) : AnyShapeStyle(.secondary))
                .underline(hovering)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(help)
        .onHover { hovering = $0 }
    }
}
