import SwiftUI

@main
struct ClaudeGraftApp: App {
    @StateObject private var store = ShortcutStore()

    var body: some Scene {
        WindowGroup("Claude Graft") {
            ContentView()
                .environmentObject(store)
                .frame(minWidth: 760, minHeight: 480)
        }
        .commands {
            CommandGroup(replacing: .newItem) {}
        }
    }
}
