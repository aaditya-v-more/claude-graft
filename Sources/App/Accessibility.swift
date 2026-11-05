import SwiftUI

/// What VoiceOver says for a control, and what a script asks for it by.
///
/// SwiftUI fills in both on its own and gets both wrong. A button holding only
/// a symbol is named after the symbol, so the button explaining each section
/// read out as "Error" and the one showing a folder in Finder as "Move". The
/// identifier it leaves is that symbol's name or nothing at all, so the only
/// ways to find a switch from outside were where it happened to be drawn and
/// the words on it, which change with the language.
///
/// An identifier reads `<surface>.<profile>.<control>`, the middle part only
/// for the controls that act on one profile. The surface is there because the
/// dropdown and Settings carry the same switches and can be open at once. The
/// profile is its folder, which no two profiles share and which is the same in
/// every language, where the name on screen is neither.
enum ControlSurface {
    static let dropdown = "dropdown"
    static let window = "window"
    static let settings = "settings"
}

private struct SurfaceKey: EnvironmentKey { static let defaultValue = "" }
private struct ProfileKey: EnvironmentKey { static let defaultValue = "" }

extension EnvironmentValues {
    var controlSurface: String {
        get { self[SurfaceKey.self] }
        set { self[SurfaceKey.self] = newValue }
    }

    var controlProfile: String {
        get { self[ProfileKey.self] }
        set { self[ProfileKey.self] = newValue }
    }
}

private struct Identified: ViewModifier {
    @Environment(\.controlSurface) private var surface
    @Environment(\.controlProfile) private var profile
    let name: String

    func body(content: Content) -> some View {
        content.accessibilityIdentifier([surface, profile, name].filter { !$0.isEmpty }
            .joined(separator: "."))
    }
}

extension View {
    /// Goes on the control itself. On a stack it would land on every control
    /// inside, and one identifier naming several controls names none of them.
    func identified(_ name: String) -> some View {
        modifier(Identified(name: name))
    }

    /// The controls below this point act on this profile.
    func controls(profile: URL) -> some View {
        environment(\.controlProfile, profile.lastPathComponent)
    }

    /// Read as one thing, "Name, Claude". A form row otherwise reaches
    /// VoiceOver as its label, its value, and the value a second time. Marked
    /// as text because SwiftUI leaves an element it was told to make with no
    /// role at all, AXUnknown, and nothing to say what kind of thing it is.
    func spoken(_ label: String, value: String) -> some View {
        accessibilityElement(children: .ignore)
            .accessibilityLabel(label)
            .accessibilityValue(value)
            .accessibilityAddTraits(.isStaticText)
    }
}
