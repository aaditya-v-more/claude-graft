import AppKit
import ApplicationServices
import Carbon.HIToolbox

/// Puts dictated words where the cursor is, in whatever app has it.
///
/// Typed as key presses carrying text, never pasted: a paste would cost the
/// person whatever they had copied.
enum DictationTyping {
    /// Stamped on every press made here, so the key monitor knows its own.
    static let mark: Int64 = 0x47_52_41_46

    static var isTrusted: Bool { AXIsProcessTrusted() }

    /// Raises the system's own request, once macOS has not been asked before.
    static func askForTrust() {
        let prompt = kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String
        _ = AXIsProcessTrustedWithOptions([prompt: true] as CFDictionary)
    }

    /// While a password field has the keyboard macOS keeps key presses from
    /// every monitor, the release of the dictation key among them.
    static var keyboardIsGuarded: Bool { IsSecureEventInputEnabled() }

    /// A modifier is read from the flags: `keyState` says a held modifier is
    /// up, which ended every held recording a third of a second in.
    static func isDown(_ key: DictationKey) -> Bool {
        if key.isModifier {
            return key.isDown(flags: UInt(CGEventSource.flagsState(.combinedSessionState).rawValue))
        }
        return CGEventSource.keyState(.combinedSessionState, key: CGKeyCode(key.code))
    }

    struct Place {
        var before: String?
        var isPassword = false
        var app: pid_t?
    }

    /// What can be learned about where the cursor is. Many apps say nothing,
    /// and that is an answer too.
    static func place() -> Place {
        let system = AXUIElementCreateSystemWide()
        // An app that has hung must not hold the words back with it.
        AXUIElementSetMessagingTimeout(system, 0.25)
        var focused: CFTypeRef?
        guard AXUIElementCopyAttributeValue(system, kAXFocusedUIElementAttribute as CFString, &focused) == .success,
              let focused, CFGetTypeID(focused) == AXUIElementGetTypeID()
        else { return Place() }
        let element = focused as! AXUIElement

        var place = Place()
        var pid: pid_t = 0
        if AXUIElementGetPid(element, &pid) == .success { place.app = pid }
        var subrole: CFTypeRef?
        if AXUIElementCopyAttributeValue(element, kAXSubroleAttribute as CFString, &subrole) == .success,
           subrole as? String == kAXSecureTextFieldSubrole as String {
            place.isPassword = true
            return place
        }

        var selection: CFTypeRef?
        var range = CFRange()
        guard AXUIElementCopyAttributeValue(element, kAXSelectedTextRangeAttribute as CFString, &selection) == .success,
              let selection, CFGetTypeID(selection) == AXValueGetTypeID(),
              AXValueGetValue(selection as! AXValue, .cfRange, &range)
        else { return place }
        guard range.location > 0 else {
            place.before = ""
            return place
        }
        // One character is all the spacing needs, and a document's whole text
        // is a great deal to ask an app for.
        var one = CFRange(location: range.location - 1, length: 1)
        var text: CFTypeRef?
        if let asked = AXValueCreate(.cfRange, &one),
           AXUIElementCopyParameterizedAttributeValue(element, kAXStringForRangeParameterizedAttribute as CFString,
                                                      asked, &text) == .success {
            place.before = text as? String
        }
        return place
    }

    /// Blocking; call it off the main thread.
    static func type(_ text: String) -> Bool {
        guard isTrusted, !keyboardIsGuarded,
              let source = CGEventSource(stateID: .combinedSessionState)
        else { return false }
        for stroke in DictationText.strokes(text) {
            for down in [true, false] {
                guard let event = CGEvent(keyboardEventSource: source, virtualKey: 0, keyDown: down)
                else { return false }
                // A modifier still held would turn the words into shortcuts.
                event.flags = []
                event.setIntegerValueField(.eventSourceUserData, value: mark)
                stroke.withUnsafeBufferPointer {
                    event.keyboardSetUnicodeString(stringLength: $0.count, unicodeString: $0.baseAddress)
                }
                event.post(tap: .cgAnnotatedSessionEventTap)
            }
            // Apps that take their keys one event loop turn at a time drop
            // what arrives faster than they turn.
            usleep(3000)
        }
        return true
    }

    static func copy(_ text: String) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
    }

    static func openSettings(_ pane: String) {
        guard let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?\(pane)") else { return }
        NSWorkspace.shared.open(url)
    }
}
