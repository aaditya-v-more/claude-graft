// Reads a window of another process the way an assistive app does, from its
// own process. The layout checks read their windows from inside, and a live run
// found a button those reads named that an outside reader saw without a name.
import ApplicationServices
import Foundation

let pid = pid_t(CommandLine.arguments[1])!
let title = CommandLine.arguments[2]
let app = AXUIElementCreateApplication(pid)
AXUIElementSetMessagingTimeout(app, 5)

func attribute(_ element: AXUIElement, _ name: String) -> CFTypeRef? {
    var value: CFTypeRef?
    return AXUIElementCopyAttributeValue(element, name as CFString, &value) == .success ? value : nil
}
func text(_ element: AXUIElement, _ name: String) -> String { attribute(element, name) as? String ?? "" }

let windows = attribute(app, kAXWindowsAttribute) as? [AXUIElement] ?? []
guard let window = windows.first(where: { text($0, kAXTitleAttribute).hasPrefix(title) }) else {
    FileHandle.standardError.write(Data("no window titled \(title)\n".utf8))
    exit(1)
}
func walk(_ element: AXUIElement, depth: Int) {
    let identifier = text(element, "AXIdentifier")
    if !identifier.isEmpty {
        print([identifier, text(element, kAXRoleAttribute), text(element, kAXDescriptionAttribute),
               text(element, kAXTitleAttribute)].joined(separator: "\t"))
    }
    guard depth < 40 else { return }
    for child in attribute(element, kAXChildrenAttribute) as? [AXUIElement] ?? [] {
        walk(child, depth: depth + 1)
    }
}
walk(window, depth: 0)
