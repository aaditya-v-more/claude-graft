import AppKit

/// Which profile's Claude is in front, so the menu bar can show it.
///
/// Every running Claude is `Claude.app`, so the Dock gives each of them
/// Claude's own name and icon, and macOS takes no other name for an app from
/// outside it: a rename or a badge sent from another process is answered with
/// success and ignored. The menu bar is the one place left to say which of them
/// is in use.
final class FrontmostClaude: ObservableObject {
    /// Nil while anything other than a Claude is in front.
    @Published private(set) var profile: URL?

    /// Called with the profile behind every Claude somebody brings forward,
    /// which is the moment its figure matters. The usage refresh hangs here so
    /// that one lookup serves both.
    var onActivation: ((URL) -> Void)?

    private var generation = 0
    private var watch: NSObjectProtocol?

    /// What one app coming to the front says about which Claude is in use.
    enum Meaning: Equatable {
        /// A Claude, still to be matched to its profile by its command line.
        case claude(pid_t)
        /// Graft itself. Opening the dropdown is asking about the Claude that
        /// was just in use, and dropping it from the bar then would also
        /// narrow the item under the popover anchored to it.
        case keep
        /// Anything else, which means no Claude is in front.
        case clear
    }

    static func meaning(bundle: URL?, pid: pid_t, ownPid: pid_t, claudeApp: URL) -> Meaning {
        if let bundle, Graft.samePath(bundle, claudeApp) { return .claude(pid) }
        return pid == ownPid ? .keep : .clear
    }

    /// Returns the ticket a Claude's lookup has to still hold when it lands.
    @discardableResult
    func activated(_ meaning: Meaning) -> Int? {
        switch meaning {
        case .keep:
            return nil
        case .clear:
            generation += 1
            if profile != nil { profile = nil }
            return nil
        case .claude:
            generation += 1
            return generation
        }
    }

    /// Matching a command line takes a `ps`, and a flick past one Claude on
    /// the way to another can finish before it does. A lookup that lands after
    /// the next switch names a Claude that is no longer in front, so only the
    /// newest one is shown.
    func resolved(ticket: Int, profile found: URL?, switched: Bool = true) {
        if switched, let found { onActivation?(found) }
        guard ticket == generation, profile != found else { return }
        profile = found
    }

    func start(watching store: ShortcutStore) {
        guard watch == nil else { return }
        watch = NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didActivateApplicationNotification, object: nil, queue: .main
        ) { [weak self, weak store] note in
            guard let self, let store,
                  let app = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication
            else { return }
            self.arrived(app, store: store, switched: true)
        }
        // Whatever is in front as Graft starts is a state to show, not a
        // switch: nobody brought it forward, so it asks for no figures.
        if let front = NSWorkspace.shared.frontmostApplication {
            arrived(front, store: store, switched: false)
        }
    }

    private func arrived(_ app: NSRunningApplication, store: ShortcutStore, switched: Bool) {
        let meaning = Self.meaning(bundle: app.bundleURL,
                                   pid: app.processIdentifier,
                                   ownPid: ProcessInfo.processInfo.processIdentifier,
                                   claudeApp: Graft.claudeApp)
        guard let ticket = activated(meaning), case .claude(let pid) = meaning else { return }
        let profiles = [Graft.mainProfile] + store.shortcuts
            .filter { $0.installedName != nil }.map(\.profileDir)
        DispatchQueue.global(qos: .userInitiated).async {
            let found = Graft.processes().first { $0.id == pid }
                .flatMap { UsageMonitor.profile(launchedBy: $0.command, among: profiles) }
            DispatchQueue.main.async {
                self.resolved(ticket: ticket, profile: found, switched: switched)
            }
        }
    }
}

/// The name beside the figure: the Claude in front, on the colour of its
/// shortcut's icon, which is what a person already tells their shortcuts
/// apart by.
enum FrontTag {
    static let height: CGFloat = 16
    /// The bar is shared, and an item macOS has no room for is dropped without
    /// a word (`MenuBarPlacement` has the numbers), so a long name is cut short
    /// rather than given its full length.
    static let maximumWidth: CGFloat = 116
    /// What white text at eleven points needs against the fill to be read.
    static let minimumContrast: CGFloat = 3.2

    static func image(name: String, preset: Shortcut.IconPreset) -> NSImage {
        let font = NSFont.systemFont(ofSize: 11, weight: .semibold)
        let style = NSMutableParagraphStyle()
        style.lineBreakMode = .byTruncatingTail
        let text = NSAttributedString(string: name, attributes: [
            .font: font, .foregroundColor: NSColor.white, .paragraphStyle: style,
        ])
        let symbol = preset.badgeSymbol
            .flatMap { NSImage(systemSymbolName: $0, accessibilityDescription: nil) }
            .flatMap {
                $0.withSymbolConfiguration(NSImage.SymbolConfiguration(pointSize: 8.5, weight: .bold)
                    .applying(NSImage.SymbolConfiguration(paletteColors: [.white])))
            }
        let padding: CGFloat = 7
        let symbolWidth = symbol.map { ceil($0.size.width) + 3 } ?? 0
        let width = min(maximumWidth, ceil(padding * 2 + symbolWidth + text.size().width))
        let fill = fill(for: preset.accentColor)

        let image = NSImage(size: NSSize(width: width, height: height), flipped: false) { rect in
            fill.setFill()
            NSBezierPath(roundedRect: rect, xRadius: height / 2, yRadius: height / 2).fill()
            if let symbol {
                symbol.draw(in: NSRect(x: padding, y: (height - symbol.size.height) / 2,
                                       width: symbol.size.width, height: symbol.size.height))
            }
            // Centred on the capitals rather than the line box, which carries
            // room for descenders and would sit the name visibly high.
            let baseline = ((height - font.capHeight) / 2).rounded()
            let x = padding + symbolWidth
            text.draw(with: NSRect(x: x, y: baseline + font.descender,
                                   width: rect.width - x - padding + 1,
                                   height: font.ascender - font.descender),
                      options: [.usesLineFragmentOrigin, .truncatesLastVisibleLine])
            return true
        }
        image.accessibilityDescription = name
        return image
    }

    /// The icon's own colour, darkened only as far as white text needs. Taken
    /// as the light appearance has it, because the icons are pictures and do
    /// not change with it.
    static func fill(for accent: NSColor) -> NSColor {
        var color = accent.usingColorSpace(.sRGB) ?? accent
        NSAppearance(named: .aqua)?.performAsCurrentDrawingAppearance {
            color = accent.usingColorSpace(.sRGB) ?? accent
        }
        for _ in 0..<10 where contrastWithWhite(color) < minimumContrast {
            color = color.blended(withFraction: 0.08, of: .black) ?? color
        }
        return color
    }

    static func contrastWithWhite(_ color: NSColor) -> CGFloat {
        guard let color = color.usingColorSpace(.sRGB) else { return 1 }
        func linear(_ c: CGFloat) -> CGFloat { c <= 0.03928 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4) }
        let luminance = 0.2126 * linear(color.redComponent)
            + 0.7152 * linear(color.greenComponent)
            + 0.0722 * linear(color.blueComponent)
        return 1.05 / (luminance + 0.05)
    }

}

/// The icon beside the figure: the one the shortcut wears in Finder and on a
/// pinned tile, drawn by the same code that drew it there, so the bar shows
/// what the person already picked rather than a second likeness of it.
enum FrontIcon {
    static let side: CGFloat = 18
    /// Drawn once per preset. Rendering filters the whole of Claude's icon,
    /// and the title is rebuilt on every switch and every pass.
    private static var drawn: [Shortcut.IconPreset: NSImage] = [:]

    static func image(for preset: Shortcut.IconPreset) -> NSImage? {
        if let image = drawn[preset] { return image }
        guard let image = Installer.previewIcon(for: preset) else { return nil }
        image.size = NSSize(width: side, height: side)
        drawn[preset] = image
        return image
    }
}

/// Everyone the Claude in front draws on, in the order it draws on them, its
/// own account first. The account answering the conversation in front is
/// drawn full size and the rest small and faint, so which one is spending is
/// read off the bar without opening anything. The order never changes; only
/// the light moves, so an icon is always found where it was.
enum PoolStrip {
    static let faint: CGFloat = 13
    static let gap: CGFloat = 3
    private static var drawn: [String: NSImage] = [:]

    static func image(_ presets: [Shortcut.IconPreset], lit: Int?) -> NSImage? {
        guard presets.count > 1 else { return nil }
        let key = presets.map(\.rawValue).joined(separator: ",") + "|\(lit ?? -1)"
        if let image = drawn[key] { return image }
        let icons = presets.compactMap(FrontIcon.image(for:))
        guard icons.count == presets.count else { return nil }
        let full = FrontIcon.side
        let sides = presets.indices.map { lit == nil || $0 == lit ? full : faint }
        let width = sides.reduce(0, +) + gap * CGFloat(sides.count - 1)
        let image = NSImage(size: NSSize(width: width, height: full), flipped: false) { _ in
            var x: CGFloat = 0
            for (index, icon) in icons.enumerated() {
                let side = sides[index]
                icon.draw(in: NSRect(x: x, y: ((full - side) / 2).rounded(), width: side, height: side),
                          from: .zero, operation: .sourceOver,
                          fraction: lit == nil || index == lit ? 1 : 0.45)
                x += side + gap
            }
            return true
        }
        drawn[key] = image
        return image
    }
}

/// The status item's title once it shows the Claude in front.
enum FrontTitle {
    /// How far each picture is drawn below the centre of its own box.
    ///
    /// The status item centres its title as one block, a point above the
    /// button's middle, and a picture taller than the text is that block:
    /// whatever offset it is given, its centre lands on the block's and only
    /// the figure moves. Graft's symbol is drawn half a point below the middle,
    /// so the pictures sat a point and a half above it — 10.0 points down a
    /// 22-point button against the symbol's 11.5, read off a real status item.
    /// Empty space on top of each picture takes what can be seen down that far
    /// and leaves the block, and with it the figure, where they were.
    static let drop: CGFloat = 1.5

    /// How far the figure is lowered to meet the symbol. AppKit sets status
    /// item text higher than the symbol, its middle 10.2 points down against
    /// 11.4, and text lands on whole pixels: a point and a half put the figure
    /// half a pixel below the symbol, where one point stops a little short.
    static let figureDrop: CGFloat = 1

    /// No colour is given to the figure, so it keeps the one the bar chooses
    /// for the wallpaper behind it; only the icon and the tag bring their own.
    static func make(icon: NSImage?, tag: NSImage?, figure: Graft.Usage.Headline?,
                     font: NSFont, ticker: NSImage? = nil, afterImage: Bool = true) -> NSAttributedString {
        let title = NSMutableAttributedString()
        // The gap is there to part the title from the symbol before it; with
        // no symbol it is a space the bar spends on nothing.
        func gap(_ text: String) -> NSAttributedString {
            NSAttributedString(string: title.length == 0 && !afterImage ? "" : text, attributes: [.font: font])
        }
        for picture in [icon, tag].compactMap({ $0 }) {
            let size = picture.size
            let lowered = NSImage(size: NSSize(width: size.width, height: size.height + drop * 2),
                                  flipped: false) { _ in
                picture.draw(in: NSRect(origin: .zero, size: size))
                return true
            }
            lowered.accessibilityDescription = picture.accessibilityDescription
            let attachment = NSTextAttachment()
            attachment.image = lowered
            // The box grows by the same amount above and below where the
            // picture alone would have sat, which is what keeps the block.
            attachment.bounds = NSRect(x: 0, y: ((font.capHeight - size.height) / 2).rounded() - drop,
                                       width: lowered.size.width, height: lowered.size.height)
            title.append(gap(" "))
            title.append(NSAttributedString(attachment: attachment))
        }
        if let figure {
            title.append(gap(" "))
            title.append(NSAttributedString(string: text(figure), attributes: [
                .font: font, .baselineOffset: -figureDrop,
            ]))
        }
        if let ticker {
            let attachment = NSTextAttachment()
            attachment.image = ticker
            // Set like the figure: the text inside is the bar's own size, a
            // point smaller, and lowered by as much as the figure is.
            attachment.bounds = NSRect(x: 0, y: font.descender - figureDrop,
                                       width: ticker.size.width, height: ticker.size.height)
            title.append(gap("  "))
            title.append(NSAttributedString(attachment: attachment))
        }
        return title
    }

    /// A week's figure looks exactly like a five-hour one, and the two run out
    /// at very different rates, so the week carries a letter saying which.
    static func text(_ figure: Graft.Usage.Headline) -> String {
        figure.isWeekly ? L10n.format("W %ld%%", figure.percent) : "\(figure.percent)%"
    }

    /// The figure read out, with the words for what the letter only hints at.
    static func spoken(_ name: String, _ figures: Graft.Usage) -> String {
        figures.fiveHour.map { L10n.format("%@, %ld%% of 5 hours", name, $0) }
            ?? L10n.format("%@, %ld%% of the week", name, figures.week)
    }

    /// One account in the tooltip. One without a five-hour limit says so,
    /// where a zero would pass for a window nobody has started yet.
    static func tooltipLine(_ name: String, _ figures: Graft.Usage) -> String {
        var line = figures.fiveHour.map {
            L10n.format("%@: %ld%% of 5 hours, %ld%% of the week", name, $0, figures.week)
        } ?? L10n.format("%@: no 5-hour limit, %ld%% of the week", name, figures.week)
        if let fable = figures.fable { line += L10n.format(", %ld%% Fable", fable) }
        return line
    }
}

/// A conversation that has just begun, running across the bar beside the
/// figure: its title, or "New chat" until it has one, and the account
/// answering it.
///
/// Drawn into a picture of fixed width rather than set as text. Text that
/// scrolls a character at a time changes width with every letter in a
/// proportional font, and a status item that changes width moves every item
/// to its left with it, several times a second.
enum NewChatTicker {
    static let width: CGFloat = 150
    /// The space between the end of the text and its start coming round again.
    static let gap: CGFloat = 28
    /// Points moved per tick, and the tick, which together make about thirty
    /// points a second: readable, and slow enough not to pull the eye.
    static let step: CGFloat = 2.5
    static let tick: TimeInterval = 0.08
    /// Ticks held at the start of each pass, so the beginning can be read.
    static let pause = 18

    static func text(title: String?, account: String) -> String {
        L10n.format("%@ · %@", title ?? L10n.text("New chat"), account)
    }

    static func spoken(title: String?, account: String) -> String {
        L10n.format("New conversation “%@”, answered by %@", title ?? L10n.text("New chat"), account)
    }

    static func attributes(_ font: NSFont) -> [NSAttributedString.Key: Any] {
        [.font: NSFont.systemFont(ofSize: font.pointSize - 1), .foregroundColor: NSColor.labelColor]
    }

    /// The length of one pass, or nil for text short enough to stand still.
    static func cycle(_ text: String, font: NSFont) -> CGFloat? {
        let length = ceil((text as NSString).size(withAttributes: attributes(font)).width)
        return length > width ? length + gap : nil
    }

    /// `offset` is how far the text has moved left, in points.
    static func image(_ text: String, offset: CGFloat, font: NSFont) -> NSImage {
        let attributes = attributes(font)
        let size = (text as NSString).size(withAttributes: attributes)
        let scrolls = cycle(text, font: font) != nil
        let shown = scrolls ? width : ceil(size.width)
        let height = ceil(size.height)
        // Drawn when shown rather than now, so the label colour is the one the
        // bar's appearance gives it at that moment.
        let image = NSImage(size: NSSize(width: shown, height: height), flipped: false) { rect in
            NSBezierPath(rect: rect).addClip()
            let x = scrolls ? -offset : 0
            (text as NSString).draw(at: NSPoint(x: x, y: 0), withAttributes: attributes)
            if scrolls {
                (text as NSString).draw(at: NSPoint(x: x + ceil(size.width) + gap, y: 0),
                                        withAttributes: attributes)
            }
            return true
        }
        image.accessibilityDescription = text
        return image
    }
}
