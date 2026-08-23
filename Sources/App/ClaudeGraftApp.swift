import SwiftUI

@main
struct ClaudeGraftApp: App {
    @StateObject private var store = ShortcutStore()

    var body: some Scene {
        WindowGroup("Claude Graft") {
            ContentView()
                .environmentObject(store)
                .frame(minWidth: 720, minHeight: 460)
        }
        .defaultSize(width: 820, height: 560)
        .commands {
            CommandGroup(replacing: .newItem) {}
        }
    }
}
