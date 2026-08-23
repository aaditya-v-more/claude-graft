import Foundation

// Executable of a generated shortcut. It reads the profile description sitting
// beside it in the bundle, re-establishes the links, and opens Claude on that
// profile. Nothing here is interactive; the process exits immediately.

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
