import AppKit
import QuartzCore

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
/// drawn full size in a red ring and the rest small and faint, so which one
/// is spending is read off the bar without opening anything. The order never
/// changes; only the light moves, so an icon is always found where it was.
enum PoolStrip {
    static let faint: CGFloat = 13
    static let gap: CGFloat = 3
    /// The ring goes inside the lit icon's own square, since a taller strip
    /// would no longer sit where the figure beside it expects. Claude's icon
    /// fills nearly all of that square and is as warm as the ring, so drawn
    /// full size the two ran together; two points smaller leaves a hairline
    /// between them.
    static let ring: CGFloat = 1
    static let ringed: CGFloat = 16
    static let ringRadius: CGFloat = 4.75
    private static var drawn: [String: NSImage] = [:]

    static func image(_ presets: [Shortcut.IconPreset], lit: Int?) -> NSImage? {
        guard presets.count > 1 else { return nil }
        let key = presets.map(\.rawValue).joined(separator: ",") + "|\(lit ?? -1)"
        if let image = drawn[key] { return image }
        let icons = presets.compactMap(FrontIcon.image(for:))
        guard icons.count == presets.count else { return nil }
        let image = strip(icons, lit: lit)
        drawn[key] = image
        return image
    }

    static func strip(_ icons: [NSImage], lit: Int?) -> NSImage {
        let full = FrontIcon.side
        let sides = icons.indices.map { lit == nil || $0 == lit ? full : faint }
        let width = sides.reduce(0, +) + gap * CGFloat(sides.count - 1)
        return NSImage(size: NSSize(width: width, height: full), flipped: false) { _ in
            var x: CGFloat = 0
            for (index, icon) in icons.enumerated() {
                let side = sides[index]
                let slot = NSRect(x: x, y: ((full - side) / 2).rounded(), width: side, height: side)
                if index == lit {
                    let inset = (side - ringed) / 2
                    icon.draw(in: slot.insetBy(dx: inset, dy: inset), from: .zero, operation: .sourceOver,
                              fraction: 1)
                    let path = NSBezierPath(roundedRect: slot.insetBy(dx: ring / 2, dy: ring / 2),
                                            xRadius: ringRadius, yRadius: ringRadius)
                    path.lineWidth = ring
                    NSColor.systemRed.setStroke()
                    path.stroke()
                } else {
                    icon.draw(in: slot, from: .zero, operation: .sourceOver, fraction: lit == nil ? 1 : 0.45)
                }
                x += side + gap
            }
            return true
        }
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

/// The conversation on screen, running across the bar beside the figure for
/// as long as it is there: its title, or "New chat" until it has one, and the
/// account answering it. A conversation from before sharing began, opened to
/// read, has no account answering it and shows its title alone.
///
/// Held at a fixed width rather than set as text. Text that scrolls a
/// character at a time changes width with every letter in a proportional
/// font, and a status item that changes width moves every item to its left
/// with it, several times a second.
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

    static func text(title: String?, account: String?) -> String {
        let name = title ?? L10n.text("New chat")
        return account.map { L10n.format("%@ · %@", name, $0) } ?? name
    }

    static func spoken(title: String?, account: String?) -> String {
        let name = title ?? L10n.text("New chat")
        return account.map { L10n.format("Conversation on screen “%@”, answered by %@", name, $0) }
            ?? L10n.format("Conversation on screen “%@”", name)
    }

    static func attributes(_ font: NSFont) -> [NSAttributedString.Key: Any] {
        [.font: NSFont.systemFont(ofSize: font.pointSize - 1), .foregroundColor: NSColor.labelColor]
    }

    /// The length of one pass, or nil for text short enough to stand still.
    static func cycle(_ text: String, font: NSFont) -> CGFloat? {
        let length = ceil((text as NSString).size(withAttributes: attributes(font)).width)
        return length > width ? length + gap : nil
    }

    /// How far the text has moved left at each tick of one pass: held at the
    /// start for the pause and the tick that brought it round, then a step a
    /// tick until the next step would bring the start back.
    static func pass(cycle: CGFloat) -> [CGFloat] {
        var offsets = [CGFloat](repeating: 0, count: pause + 1)
        var moved: CGFloat = 0
        while moved + step < cycle {
            moved += step
            offsets.append(moved)
        }
        return offsets
    }

    /// The text as the status item holds it. Text that fits is drawn into the
    /// title. Text that moves leaves a blank of the same width there and runs
    /// in `strip`, laid over the blank, where Core Animation moves it.
    ///
    /// Moved by Graft, the text cost a redraw of the button on every tick, and
    /// AppKit drawing the button was nearly all of the five points of a core
    /// that a moving title took on the Mac Mini, where the same title standing
    /// still took about one. The render server shifts a layer it already
    /// holds, so Graft wakes for nothing while the text runs.
    final class Picture {
        let text: String
        let font: NSFont
        /// The length of one pass, or nil for text short enough to stand still.
        let cycle: CGFloat?
        let image: NSImage
        /// The moving text, nil for text that stands still.
        let strip: Strip?
        /// When the first pass began, so a picture rebuilt for the same
        /// conversation carries on from where the last one was.
        let started: CFTimeInterval
        /// Called after the title has been drawn, which is when the button's
        /// geometry is the one the blank was laid out in.
        var drawn: (() -> Void)?

        init(_ text: String, font: NSFont, started: CFTimeInterval? = nil) {
            self.text = text
            self.font = font
            let attributes = NewChatTicker.attributes(font)
            let size = (text as NSString).size(withAttributes: attributes)
            let length = ceil(size.width)
            let cycle = NewChatTicker.cycle(text, font: font)
            self.cycle = cycle
            let begun = started ?? CACurrentMediaTime()
            self.started = begun
            let height = ceil(size.height)
            strip = cycle.map { Strip(text, attributes: attributes, length: length, height: height,
                                      pass: NewChatTicker.pass(cycle: $0), started: begun) }
            var told: (() -> Void)?
            let marking = Marking()
            self.marking = marking
            // Drawn when shown rather than now, so the label colour is the one the
            // bar's appearance gives it at that moment.
            let image = NSImage(size: NSSize(width: cycle == nil ? length : NewChatTicker.width, height: height),
                                flipped: false) { rect in
                if cycle == nil {
                    (text as NSString).draw(at: .zero, withAttributes: attributes)
                } else if marking.on {
                    NewChatTicker.mark.setFill()
                    rect.fill()
                } else {
                    // A frame changed in the middle of the button's own drawing
                    // is one AppKit may draw again, so the strip moves after it.
                    DispatchQueue.main.async { told?() }
                }
                return true
            }
            // Kept from caching so the blank can be drawn marked when it is
            // looked for, and still text with it, so the text that stands and
            // the blank the strip is laid by are drawn the same way. Either is
            // drawn only when the button is.
            image.cacheMode = .never
            image.accessibilityDescription = text
            self.image = image
            told = { [weak self] in self?.drawn?() }
        }

        private final class Marking { var on = false }
        private let marking: Marking
        /// What the strip was last placed for, so a button drawn again
        /// unchanged is not looked over again.
        private var placedFor: (title: NSAttributedString, size: NSSize, scale: CGFloat)?

        /// Lays the strip over the blank the title holds for it, or hides it
        /// when the title no longer ends with that blank.
        ///
        /// AppKit publishes where a title's runs are drawn no more than it
        /// publishes its margins, and working it out from the layout was a
        /// pixel out whenever the icons made the line taller than the text:
        /// still text then stood a pixel above the same text moving. So the
        /// button is drawn once away from the screen with the blank filled in
        /// a colour, and the strip goes where the colour came out.
        func place(in button: NSButton) {
            guard let strip else { return }
            let title = button.attributedTitle
            guard title.length > 0, let window = button.window,
                  let attachment = title.attribute(.attachment, at: title.length - 1,
                                                   effectiveRange: nil) as? NSTextAttachment,
                  attachment.image === image
            else {
                placedFor = nil
                strip.isHidden = true
                return
            }
            let scale = window.backingScaleFactor
            if let placedFor, placedFor.title.isEqual(to: title), placedFor.size == button.bounds.size,
               placedFor.scale == scale, !strip.isHidden {
                return
            }
            let size = attachment.bounds.size
            let height = button.bounds.height
            // Not found only if AppKit stopped drawing the picture where it is
            // asked to; the end of the title, centred, keeps the text readable.
            let (left, top) = markedCorner(in: button, width: size.width, scale: scale)
                ?? (((button.cell as? NSButtonCell)?.titleRect(forBounds: button.bounds).maxX ?? 0) - size.width,
                    ((height - size.height) / 2).rounded())
            let frame = NSRect(x: left, y: button.isFlipped ? top : height - top - size.height,
                               width: size.width, height: size.height)
            if strip.frame != frame { strip.frame = frame }
            strip.isHidden = false
            placedFor = (title, button.bounds.size, scale)
        }

        /// The blank's top left corner, measured from the button's top left:
        /// the first row the marked blank fills across its whole width. Nothing
        /// else in a title is as wide, and the strip is a layer of its own,
        /// which drawing the button into a picture leaves out.
        private func markedCorner(in button: NSButton, width: CGFloat, scale: CGFloat) -> (CGFloat, CGFloat)? {
            guard let rep = button.bitmapImageRepForCachingDisplay(in: button.bounds) else { return nil }
            marking.on = true
            button.cacheDisplay(in: button.bounds, to: rep)
            marking.on = false
            let wide = Int((width * scale).rounded()) - 2
            for row in 0..<rep.pixelsHigh {
                let marked = (0..<rep.pixelsWide).filter { NewChatTicker.isMark(rep, $0, row) }
                if let first = marked.first, marked.count >= wide {
                    return (CGFloat(first) / scale, CGFloat(row) / scale)
                }
            }
            return nil
        }
    }

    /// A pink icon may come near it, but no icon comes near the blank's width,
    /// and the blank is found by both.
    fileprivate static let mark = NSColor(srgbRed: 1, green: 0, blue: 1, alpha: 1)

    fileprivate static func isMark(_ rep: NSBitmapImageRep, _ column: Int, _ row: Int) -> Bool {
        if rep.bitsPerSample == 8, rep.samplesPerPixel == 4, !rep.isPlanar, let data = rep.bitmapData {
            let at = data + row * rep.bytesPerRow + column * 4
            let alphaFirst = rep.bitmapFormat.contains(.alphaFirst)
            let (red, green, blue, alpha) = alphaFirst ? (at[1], at[2], at[3], at[0]) : (at[0], at[1], at[2], at[3])
            return alpha > 200 && red > 180 && green < 100 && blue > 180
        }
        guard let colour = rep.colorAt(x: column, y: row)?.usingColorSpace(.sRGB) else { return false }
        return colour.alphaComponent > 0.8 && colour.redComponent > 0.7 && colour.greenComponent < 0.4
            && colour.blueComponent > 0.7
    }

    /// Clips the moving text to the blank it covers. Clicks go through to the
    /// button underneath, and assistive clients read the title, not this.
    final class Strip: NSView {
        /// A plain layer rather than a view: AppKit draws a view backed by a
        /// layer only as far as it can be seen, which left everything past the
        /// first 150 points blank once the text moved.
        let words = CALayer()
        private let painter: Painter
        private let pass: [CGFloat]
        private let started: CFTimeInterval

        fileprivate init(_ text: String, attributes: [NSAttributedString.Key: Any], length: CGFloat,
                         height: CGFloat, pass: [CGFloat], started: CFTimeInterval) {
            painter = Painter(text: text, attributes: attributes, length: length)
            self.pass = pass
            self.started = started
            super.init(frame: NSRect(x: 0, y: 0, width: NewChatTicker.width, height: height))
            wantsLayer = true
            layer?.masksToBounds = true
            isHidden = true
            setAccessibilityElement(false)
            words.anchorPoint = .zero
            words.frame = CGRect(x: 0, y: 0, width: length * 2 + NewChatTicker.gap, height: height)
            words.delegate = painter
            words.needsDisplayOnBoundsChange = false
            painter.appearance = { [weak self] in self?.effectiveAppearance }
            layer?.addSublayer(words)
            words.setNeedsDisplay()
        }

        required init?(coder: NSCoder) { nil }

        override func hitTest(_ point: NSPoint) -> NSView? { nil }

        /// The bar's appearance follows the wallpaper behind it, and the
        /// label colour with it.
        override func viewDidChangeEffectiveAppearance() {
            super.viewDidChangeEffectiveAppearance()
            words.setNeedsDisplay()
        }

        override func viewDidChangeBackingProperties() {
            super.viewDidChangeBackingProperties()
            let scale = window?.backingScaleFactor ?? 2
            if words.contentsScale != scale {
                words.contentsScale = scale
                words.setNeedsDisplay()
            }
        }

        /// Added once the strip is in a window, and again should the layer
        /// have lost it, so the text never stands still part way.
        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            guard let window else { return }
            if words.contentsScale != window.backingScaleFactor {
                words.contentsScale = window.backingScaleFactor
                words.setNeedsDisplay()
            }
            guard words.animation(forKey: "pass") == nil else { return }
            let move = CAKeyframeAnimation(keyPath: "position.x")
            move.values = pass.map { -$0 }
            move.calculationMode = .discrete
            move.duration = Double(pass.count) * NewChatTicker.tick
            move.repeatCount = .infinity
            move.isAdditive = true
            move.beginTime = words.convertTime(started, from: nil)
            move.fillMode = .both
            words.add(move, forKey: "pass")
        }
    }

    /// Draws the text twice over, a gap between, once for every change of
    /// appearance or display; the move itself never draws.
    fileprivate final class Painter: NSObject, CALayerDelegate {
        let text: String
        let attributes: [NSAttributedString.Key: Any]
        let length: CGFloat
        var appearance: () -> NSAppearance? = { nil }

        init(text: String, attributes: [NSAttributedString.Key: Any], length: CGFloat) {
            self.text = text
            self.attributes = attributes
            self.length = length
        }

        func draw(_ layer: CALayer, in context: CGContext) {
            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current = NSGraphicsContext(cgContext: context, flipped: false)
            (appearance() ?? NSAppearance.currentDrawing()).performAsCurrentDrawingAppearance {
                (text as NSString).draw(at: .zero, withAttributes: attributes)
                (text as NSString).draw(at: NSPoint(x: length + NewChatTicker.gap, y: 0), withAttributes: attributes)
            }
            NSGraphicsContext.restoreGraphicsState()
        }

        /// No fade when the text is drawn again or the layer is placed.
        func action(for layer: CALayer, forKey event: String) -> CAAction? { NSNull() }
    }
}
