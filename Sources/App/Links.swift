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

    /// The site's own address: the github.io one only redirects here.
    static let site = URL(string: "https://graft.aadityamore.com/")!
    static let source = URL(string: "https://github.com/\(owner)/claude-graft")!
    static let support = URL(string: "https://ko-fi.com/\(kofiAccount)")!
    static let releases = URL(string: "https://github.com/\(owner)/claude-graft/releases")!
    /// Set by the app: shows this version's list, or answers that it has none.
    static var showWhatsNew: () -> Bool = { false }
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
/// The Donate heart, in the same red as the Windows build: deeper on a light
/// background, lighter on a dark one so it keeps its weight.
extension Color {
    static let donateHeart = Color(nsColor: NSColor(name: nil) { appearance in
        appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
            ? NSColor(srgbRed: 0xEE / 255, green: 0x5A / 255, blue: 0x3A / 255, alpha: 1)
            : NSColor(srgbRed: 0xE0 / 255, green: 0x49 / 255, blue: 0x2B / 255, alpha: 1)
    })
}

struct FooterLink: View {
    let title: String
    let icon: String
    let iconTint: Color?
    let url: URL
    let help: String

    @State private var hovering = false

    init(_ title: String, icon: String, iconTint: Color? = nil, url: URL, help: String) {
        self.title = L10n.text(title)
        self.icon = icon
        self.iconTint = iconTint
        self.url = url
        self.help = L10n.text(help)
    }

    var body: some View {
        let text = hovering ? AnyShapeStyle(.primary) : AnyShapeStyle(.secondary)
        Button { Links.open(url) } label: {
            Label {
                Text(title)
            } icon: {
                Image(systemName: icon).foregroundStyle(iconTint.map { AnyShapeStyle($0) } ?? text)
            }
                .font(.callout)
                .foregroundStyle(text)
                .underline(hovering)
                .fixedSize()
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
            Button(L10n.text("What’s New in Claude Graft")) {
                if !Links.showWhatsNew() { Links.open(Links.releases) }
            }
            Divider()
            Button(L10n.text("Report a Problem…")) { Links.open(Links.newIssue) }
            Button(L10n.text("Email the Developer…")) { Links.open(Links.mail()) }
            Button(L10n.text("Aaditya More on LinkedIn")) { Links.open(Links.linkedIn) }
            Divider()
            Button(L10n.text("View Source on GitHub")) { Links.open(Links.source) }
            Button(L10n.text("Donate…")) { Links.open(Links.support) }
        }
    }
}
