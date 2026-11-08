import Foundation

// Executable of a generated shortcut. It reads the profile description sitting
// beside it in the bundle, re-establishes the links, and opens Claude on that
// profile — or brings forward the Claude already on it. Nothing here is
// interactive; the process exits as soon as it has handed over.

// Run from the cask's uninstall step, with Graft's own copy, while Graft is
// still there to run it from. Nothing else puts back what a borrowing Claude's
// environment held before sharing once Graft is gone.
if CommandLine.arguments.dropFirst().first == "--stop-sharing" {
    let left = PoolEnvironment.takeOutLines(running: Graft.isRunning(profile:))
    for path in left {
        print("still open, or changed since Graft read it: \(path)")
    }
    exit(0)
}

let executable = URL(fileURLWithPath: CommandLine.arguments[0]).resolvingSymlinksInPath()
let resources = executable            // .../Contents/MacOS/launcher
    .deletingLastPathComponent()      // .../Contents/MacOS
    .deletingLastPathComponent()      // .../Contents
    .appending(path: "Resources")

let configURL = Bundle.main.url(forResource: "graft", withExtension: "json")
    ?? resources.appending(path: "graft.json")

guard let data = try? Data(contentsOf: configURL),
      let config = try? JSONDecoder().decode(GraftConfig.self, from: data)
else {
    FileHandle.standardError.write(Data("no profile description at \(configURL.path)\n".utf8))
    exit(1)
}

Graft.run(config)
