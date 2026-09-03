import AppKit
import Foundation
import SwiftUI

/// The addresses this app sends people to.
///
/// One account name, spelled once. The site and the source both hang off it,
/// and a build already installed keeps whatever it was compiled with — so a
/// rename that touched one of the two would leave a dead link in every copy out
/// in the world.
///
/// The tip jar is a second account somewhere else entirely, and it is spelled
/// differently. GitHub Sponsors cannot pay into an Indian account, so the money
/// goes through Ko-fi, whose handle carries no hyphens — nothing may derive one
/// name from the other. The suite reads both spellings out of this file and
/// checks the site, the README and `.github/FUNDING.yml` against them.
enum Links {
    static let owner = "aaditya-v-more"
    static let kofiAccount = "aadityavmore"

    static let site = URL(string: "https://\(owner).github.io/claude-graft/")!
    static let source = URL(string: "https://github.com/\(owner)/claude-graft")!
    static let support = URL(string: "https://ko-fi.com/\(kofiAccount)")!

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
        self.title = L10n.text(title)
        self.icon = icon
        self.url = url
        self.help = L10n.text(help)
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
