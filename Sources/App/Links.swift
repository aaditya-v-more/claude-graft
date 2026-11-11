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
    static let releases = URL(string: "https://github.com/\(owner)/claude-graft/releases")!
    static let newIssue = URL(string: "https://github.com/\(owner)/claude-graft/issues/new")!

    /// The developer, as a person rather than a repository. A third and fourth
    /// spelling, for the same reason as the tip jar: neither is the GitHub name.
    static let email = "aadityavmore@gmail.com"
    static let linkedIn = URL(string: "https://www.linkedin.com/in/aadityavmore/")!

    /// A message that already says which Graft and which macOS it comes from,
    /// because the first reply to a problem report is otherwise always a
    /// question asking for exactly that.
    static func mail(version: String = Installer.graftVersion,
                     system: String = ProcessInfo.processInfo.operatingSystemVersionString) -> URL {
        var parts = URLComponents()
        parts.scheme = "mailto"
        parts.path = email
        parts.queryItems = [
            URLQueryItem(name: "subject", value: "Claude Graft \(version)"),
            URLQueryItem(name: "body", value: "\n\n—\nClaude Graft \(version), macOS \(system)"),
        ]
        return parts.url!
    }

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

/// The Help menu. SwiftUI's own offers a single item that says help isn't
/// available, which on a Mac reads as an app nobody looks after.
struct GraftHelpCommands: Commands {
    var body: some Commands {
        CommandGroup(replacing: .help) {
            Button(L10n.text("Claude Graft Help")) { Links.open(Links.site) }
            Button(L10n.text("What’s New in Claude Graft")) { Links.open(Links.releases) }
            Divider()
            Button(L10n.text("Report a Problem…")) { Links.open(Links.newIssue) }
            Button(L10n.text("Email the Developer…")) { Links.open(Links.mail()) }
            Button(L10n.text("Aaditya More on LinkedIn")) { Links.open(Links.linkedIn) }
            Divider()
            Button(L10n.text("View Source on GitHub")) { Links.open(Links.source) }
            Button(L10n.text("Support Claude Graft…")) { Links.open(Links.support) }
        }
    }
}
