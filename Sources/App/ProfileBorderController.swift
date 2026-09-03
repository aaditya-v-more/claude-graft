import AppKit
import Combine

enum ProfileBorder {
    static let lineWidth: CGFloat = 1
    static let opacity: CGFloat = 0.55

    static func preset(for command: String, shortcuts: [Shortcut]) -> Shortcut.IconPreset? {
        guard Graft.isClaudeProcess(command) else { return nil }
        if Graft.isDefaultInstance(command) { return .original }
        guard let shortcut = shortcuts.first(where: {
            Graft.carriesDataDir(command, $0.profileDir)
        }), shortcut.showsWindowOutline else { return nil }
        return shortcut.iconPreset
    }

    static func appKitFrame(for quartzBounds: CGRect, primaryScreenMaxY: CGFloat) -> CGRect {
        CGRect(x: quartzBounds.minX,
               y: primaryScreenMaxY - quartzBounds.maxY,
               width: quartzBounds.width,
               height: quartzBounds.height)
    }

    static func color(for preset: Shortcut.IconPreset) -> NSColor {
        switch preset {
        case .original: return NSColor(srgbRed: 0.85, green: 0.36, blue: 0.24, alpha: 1)
        case .red: return .systemRed
        case .pink, .research: return .systemPink
        case .violet, .personal: return .systemPurple
        case .blue, .work: return .systemBlue
        case .teal: return .systemTeal
        case .green, .code: return .systemGreen
        case .graphite: return .systemGray
        }
    }
}

/// Draws a passive outline over the frontmost managed Claude window. It uses
/// the public window list for geometry, so it needs no Accessibility or Screen
/// Recording permission and never reaches into Claude itself.
final class ProfileBorderController {
    private let store: ShortcutStore
    private let panel: NSPanel
    private let border = NSView()
    private var activePID: pid_t?
    private var activeWindowID: CGWindowID?
    private var timer: Timer?
    private var activationWatch: NSObjectProtocol?
    private var shortcutWatch: AnyCancellable?

    init(store: ShortcutStore) {
        self.store = store
        panel = NSPanel(contentRect: .zero,
                        styleMask: [.borderless, .nonactivatingPanel],
                        backing: .buffered, defer: false)
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.hidesOnDeactivate = false
        panel.ignoresMouseEvents = true
        panel.isReleasedWhenClosed = false
        panel.level = .floating
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .ignoresCycle]

        border.wantsLayer = true
        border.layer?.backgroundColor = NSColor.clear.cgColor
        border.layer?.borderWidth = ProfileBorder.lineWidth
        border.layer?.cornerRadius = 10
        panel.contentView = border

        let center = NSWorkspace.shared.notificationCenter
        activationWatch = center.addObserver(
            forName: NSWorkspace.didActivateApplicationNotification,
            object: nil, queue: .main) { [weak self] _ in self?.refreshApplication() }
        shortcutWatch = store.$shortcuts
            .receive(on: RunLoop.main)
            .sink { [weak self] _ in self?.refreshApplication() }
        refreshApplication()
    }

    deinit {
        timer?.invalidate()
        if let activationWatch {
            NSWorkspace.shared.notificationCenter.removeObserver(activationWatch)
        }
    }

    private func refreshApplication() {
        guard let app = NSWorkspace.shared.frontmostApplication,
              let command = Graft.processes().first(where: { $0.id == app.processIdentifier })?.command,
              let preset = ProfileBorder.preset(for: command, shortcuts: store.shortcuts)
        else { return hide() }

        activePID = app.processIdentifier
        activeWindowID = nil
        border.layer?.borderColor = ProfileBorder.color(for: preset)
            .withAlphaComponent(ProfileBorder.opacity).cgColor
        position()
        if timer == nil {
            let fps = max(NSScreen.screens.map(\.maximumFramesPerSecond).max() ?? 60, 60)
            let timer = Timer(timeInterval: 1.0 / Double(fps), repeats: true) { [weak self] _ in
                self?.position()
            }
            timer.tolerance = 0
            RunLoop.main.add(timer, forMode: .common)
            self.timer = timer
        }
    }

    private func hide() {
        activePID = nil
        activeWindowID = nil
        timer?.invalidate()
        timer = nil
        panel.orderOut(nil)
    }

    private func position() {
        guard let activePID else { return }
        guard let bounds = activeWindowBounds(of: activePID),
              let primaryMaxY = NSScreen.screens.first?.frame.maxY
        else {
            // Claude stays active after its last window closes. Keep watching
            // so a reopened window gets its outline without an activation event.
            panel.orderOut(nil)
            return
        }

        let frame = ProfileBorder.appKitFrame(for: bounds, primaryScreenMaxY: primaryMaxY)
        if panel.frame != frame { panel.setFrame(frame, display: true) }
        if !panel.isVisible { panel.orderFrontRegardless() }
    }

    private func activeWindowBounds(of pid: pid_t) -> CGRect? {
        if let activeWindowID,
           let window = (CGWindowListCopyWindowInfo(
                [.optionIncludingWindow, .excludeDesktopElements], activeWindowID)
                as? [[String: Any]])?.first,
           let bounds = bounds(of: window, ownedBy: pid) {
            return bounds
        }

        let options: CGWindowListOption = [.optionOnScreenOnly, .excludeDesktopElements]
        let windows = CGWindowListCopyWindowInfo(options, kCGNullWindowID)
            as? [[String: Any]] ?? []
        for window in windows {
            guard let id = window[kCGWindowNumber as String] as? CGWindowID,
                  let bounds = bounds(of: window, ownedBy: pid)
            else { continue }
            activeWindowID = id
            return bounds
        }
        return nil
    }

    private func bounds(of window: [String: Any], ownedBy pid: pid_t) -> CGRect? {
        guard window[kCGWindowOwnerPID as String] as? pid_t == pid,
              window[kCGWindowLayer as String] as? Int == 0,
              window[kCGWindowIsOnscreen as String] as? Bool != false,
              let dictionary = window[kCGWindowBounds as String] as? NSDictionary,
              let bounds = CGRect(dictionaryRepresentation: dictionary),
              bounds.width > 200, bounds.height > 120
        else { return nil }
        return bounds
    }
}
