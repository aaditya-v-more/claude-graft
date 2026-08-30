import Foundation

/// A durable note of what each pass saw and what it did about it.
///
/// Every rule in this app about chat stores turns on telling two
/// indistinguishable situations apart: a folder read and found empty against
/// one that could not be read at all; a stash holding a duplicate against a
/// stash holding the only copy; a record missing because somebody deleted it
/// against one missing because it is out of sight. Get one of those wrong and
/// the symptom arrives hours later naming none of it — chats gone from a
/// sidebar, a history back with its archive flags cleared — by which time the
/// pass that did it has exited and left nothing behind but file timestamps.
/// Both of the worst incidents in this project were understood only because
/// somebody happened to be watching the store while it happened, which is not
/// a diagnostic technique.
///
/// The launchers write here too, and they are the reason this exists rather
/// than a debug print: a launcher is the process that runs the sweep in front
/// of a window opening, it runs when nobody is looking at a terminal, and it
/// is gone by the time anyone thinks to ask what it saw.
enum Diagnostics {
    /// Beside the state files it describes, so a copy of one is a copy of the
    /// other and a report is never read against somebody else's stores.
    static var file: URL {
        Graft.applicationSupport.appending(path: "ClaudeGraft/diagnostics.log")
    }

    /// The current shape of things, rewritten in full each pass. The log says
    /// what happened; this says what is true now, which is the question asked
    /// first every time and the one that took an hour of shell to answer.
    static var reportFile: URL {
        Graft.applicationSupport.appending(path: "ClaudeGraft/state-report.txt")
    }

    /// Kept small enough to read and to paste. A pass writes a handful of
    /// lines, so this is months of them.
    static let sizeLimit = 4 * 1024 * 1024

    /// Which process is speaking. A launcher and the app run the same sweep
    /// over the same stores, and knowing which one filed a record is most of
    /// knowing why it filed it then.
    static var who: String = ProcessInfo.processInfo.processName

    private static let lock = NSLock()

    private static let stamp: ISO8601DateFormatter = {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter
    }()

    /// Write one event down. Never throws, never blocks on anything but the
    /// file, and is safe to call from a pass that is midway through moving a
    /// profile's chats around — which is exactly when it is worth calling.
    static func note(_ event: String, _ fields: [String: Any] = [:]) {
        var line: [String: Any] = fields
        line["at"] = stamp.string(from: Date())
        line["who"] = who
        line["pid"] = ProcessInfo.processInfo.processIdentifier
        line["event"] = event

        guard let data = try? JSONSerialization.data(withJSONObject: sanitised(line),
                                                     options: [.sortedKeys])
        else { return }

        lock.lock()
        defer { lock.unlock() }
        rotateIfLarge()
        try? Graft.fm.createDirectory(at: file.deletingLastPathComponent(),
                                      withIntermediateDirectories: true)
        // O_APPEND rather than a handle held open, because the app and any
        // number of launchers write here at once and each of them is a
        // short-lived process that may be killed mid-pass.
        let descriptor = open(file.path, O_WRONLY | O_APPEND | O_CREAT, 0o644)
        guard descriptor >= 0 else { return }
        var payload = data
        payload.append(0x0a)
        payload.withUnsafeBytes { _ = write(descriptor, $0.baseAddress, $0.count) }
        close(descriptor)
    }

    /// One generation kept. The interesting window is the last few passes, and
    /// an unbounded log in Application Support is its own bug report.
    private static func rotateIfLarge() {
        var info = stat()
        guard lstat(file.path, &info) == 0, info.st_size > sizeLimit else { return }
        let previous = file.deletingLastPathComponent().appending(path: "diagnostics.log.1")
        try? Graft.fm.removeItem(at: previous)
        try? Graft.fm.moveItem(at: file, to: previous)
    }

    /// JSONSerialization refuses anything it does not recognise and takes the
    /// whole line down with it, so a URL or a Date in one field would cost the
    /// event rather than the field.
    private static func sanitised(_ value: Any) -> Any {
        switch value {
        case let dictionary as [String: Any]:
            return dictionary.mapValues(sanitised)
        case let array as [Any]:
            return array.map(sanitised)
        case let url as URL:
            return url.path
        case let date as Date:
            return stamp.string(from: date)
        case is String, is Int, is Double, is Bool, is NSNumber:
            return value
        default:
            return String(describing: value)
        }
    }
}

extension Graft {
    /// Where the command line keeps the one login it has for the whole
    /// machine. Redirected by the suite, like the other two.
    static var claudeConfigFileOverride: URL?

    static var claudeConfigFile: URL {
        claudeConfigFileOverride
            ?? fm.homeDirectoryForCurrentUser.appending(path: ".claude.json")
    }

    /// The account every bridged session on this machine is stamped with,
    /// whichever profile's window it was typed into.
    ///
    /// This is the single most confusing fact about running more than one
    /// profile, and nothing in the interface says it: a profile pays for its
    /// own inference with its own token, but the conversation is filed under
    /// whoever the command line is logged in as. So a chat started in one
    /// profile is owned by another, appears in that other one's sidebar and
    /// its cloud history, and looks for all the world like the two profiles
    /// are syncing to each other. They are not. They share one login.
    static func commandLineLogin() -> (account: String, organization: String)? {
        guard let data = try? Data(contentsOf: claudeConfigFile),
              let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let oauth = root["oauthAccount"] as? [String: Any],
              let account = oauth["accountUuid"] as? String,
              let organization = oauth["organizationUuid"] as? String
        else { return nil }
        return (account, organization)
    }

    /// The device this profile registered as. A profile is a whole browser
    /// profile of its own, so it has its own device identity, and chats made
    /// under a previous one are shown as coming from another device however
    /// firmly they belong to the account reading them.
    static func deviceIdentifier(of profile: URL) -> String? {
        guard let encoded = try? String(contentsOf: profile.appending(path: "ant-did"),
                                        encoding: .utf8),
              let data = Data(base64Encoded: encoded.trimmingCharacters(in: .whitespacesAndNewlines))
        else { return nil }
        return String(data: data, encoding: .utf8)
    }

    /// One organization folder as it sits on disk, against what this app
    /// believes about it.
    struct FolderReport {
        var store: String
        var pair: String
        var path: String
        var records: Int
        var linked: Bool
        /// Records in the hidden sibling, or nil where there is no sibling.
        /// A stash is the profile's own chats waiting to come back, so a big
        /// one beside a nearly empty folder is a graft that was undone
        /// halfway.
        var stashed: Int?
        /// Records this app remembers filing here. Far more than are there is
        /// how the sweep sees a sidebar somebody emptied, and it is also how
        /// it sees a folder that was moved out from under it.
        var remembered: Int
    }

    struct ProfileReport {
        var name: String
        var account: String?
        var configReadable: Bool
        var device: String?
        var isShortcut: Bool
        var running: Bool
        var folders: [FolderReport]
    }

    /// Everything worth knowing about the chat stores on this machine, read
    /// in one pass and in one place.
    static func stateReport(checkingRunning: Bool = true) -> [ProfileReport] {
        let remembered = loadSessionRecordState().records
        var counted: [String: Int] = [:]
        for store in remembered.values { counted[store, default: 0] += 1 }

        func records(in dir: URL) -> Int {
            let names = (try? fm.contentsOfDirectory(atPath: dir.path)) ?? []
            return names.filter { $0.hasPrefix("local_") && $0.hasSuffix(".json") }.count
        }

        let shortcuts = Set(shortcutProfiles().map(\.path))

        return sessionStoreProfiles().sorted { $0.path < $1.path }.map { profile in
            var folders: [FolderReport] = []
            for store in chatStores {
                let storeDir = profile.appending(path: store)
                for account in ((try? fm.contentsOfDirectory(atPath: storeDir.path)) ?? []).sorted()
                where !account.hasPrefix(".") && !nonAccountStoreItems.contains(account) {
                    let accountDir = storeDir.appending(path: account)
                    guard isDirectory(accountDir) else { continue }
                    for org in ((try? fm.contentsOfDirectory(atPath: accountDir.path)) ?? []).sorted()
                    where !org.hasPrefix(".") {
                        let orgDir = accountDir.appending(path: org)
                        guard isDirectory(orgDir) else { continue }
                        let stash = stashURL(for: orgDir)
                        folders.append(FolderReport(
                            store: store,
                            pair: "\(account)/\(org)",
                            path: orgDir.path,
                            records: records(in: orgDir),
                            linked: isSymlink(orgDir),
                            stashed: exists(stash) ? records(in: stash) : nil,
                            remembered: counted[orgDir.resolvingSymlinksInPath().path] ?? 0))
                    }
                }
            }
            let config = readableConfigJSON(of: profile)
            return ProfileReport(
                name: profile.lastPathComponent,
                account: config?["lastKnownAccountUuid"] as? String,
                configReadable: config != nil,
                device: deviceIdentifier(of: profile),
                isShortcut: shortcuts.contains(profile.path),
                running: checkingRunning && isRunning(profile: profile),
                folders: folders)
        }
    }

    /// What is wrong right now, in the words the symptom arrives in.
    ///
    /// The point of naming these is that every one of them has been reported
    /// as something else — chats that unarchive themselves, profiles that
    /// sync to each other, a sidebar that emptied overnight — and the gap
    /// between the symptom and the cause has been the expensive part every
    /// time.
    static func stateFindings(_ report: [ProfileReport],
                              login: (account: String, organization: String)?) -> [String] {
        var findings: [String] = []

        if let login {
            let holder = report.first { $0.account == login.account }?.name
            for profile in report where profile.account != nil && profile.account != login.account {
                findings.append("""
                    \(profile.name) is signed into \(short(profile.account!)), but the command \
                    line is logged in as \(short(login.account))\
                    \(holder.map { " (\($0))" } ?? ""). Chats started in \(profile.name) are \
                    stamped with that account and filed there, so they appear in \
                    \(holder ?? "the other profile") rather than in \(profile.name). This is one \
                    shared login, not two profiles syncing.
                    """)
            }
        } else {
            findings.append("No command line login found, so no session on this machine has an "
                            + "owner and none can be filed.")
        }

        for profile in report {
            if !profile.configReadable {
                findings.append("\(profile.name) has a config.json that will not parse. Nothing "
                                + "may decide where its chats go until it can be read.")
            }
            for folder in profile.folders {
                if let stashed = folder.stashed, stashed > folder.records {
                    findings.append("""
                        \(profile.name) \(folder.store) \(folder.pair) holds \(folder.records) \
                        record\(folder.records == 1 ? "" : "s") with \(stashed) put aside beside \
                        it. The stash is what the profile owned before a graft, so a graft was \
                        undone without handing them back and the sidebar is short by \(stashed).
                        """)
                }
                if folder.remembered > folder.records + 1, folder.stashed == nil, !folder.linked {
                    findings.append("""
                        \(profile.name) \(folder.store) \(folder.pair) holds \(folder.records) \
                        record\(folder.records == 1 ? "" : "s") where \(folder.remembered) were \
                        filed. Records go missing when a sidebar is cleared by hand and when a \
                        folder is moved out from under this app, and only the second is a fault.
                        """)
                }
            }
        }
        return findings
    }

    private static func short(_ uuid: String) -> String { String(uuid.prefix(8)) }

    /// The report as something a person reads, rewritten in full each pass.
    static func writeStateReport(checkingRunning: Bool = true) {
        let text = stateReportText(checkingRunning: checkingRunning)
        try? fm.createDirectory(at: Diagnostics.reportFile.deletingLastPathComponent(),
                                withIntermediateDirectories: true)
        try? text.write(to: Diagnostics.reportFile, atomically: true, encoding: .utf8)
    }

    /// Built as a string rather than written straight out, so the suite can
    /// read what a given arrangement of folders produces.
    static func stateReportText(checkingRunning: Bool = true) -> String {
        let report = stateReport(checkingRunning: checkingRunning)
        let login = commandLineLogin()
        let findings = stateFindings(report, login: login)
        var lines: [String] = []

        lines.append("Claude Graft — state of the chat stores")
        lines.append("read \(ISO8601DateFormatter().string(from: Date())) by \(Diagnostics.who)")
        lines.append("")

        if let login {
            let holder = report.first { $0.account == login.account }?.name ?? "no profile here"
            lines.append("Command line login (~/.claude.json)")
            lines.append("  account       \(login.account)  — held by \(holder)")
            lines.append("  organization  \(login.organization)")
            lines.append("  Every bridged session started from any profile is stamped with this")
            lines.append("  account and filed into the profile holding it, whichever window it")
            lines.append("  was typed into.")
        } else {
            lines.append("Command line login (~/.claude.json): none")
        }
        lines.append("")

        for profile in report {
            var marks: [String] = []
            if profile.isShortcut { marks.append("shortcut") } else { marks.append("not a shortcut") }
            if profile.running { marks.append("running") }
            if !profile.configReadable { marks.append("config.json unreadable") }
            lines.append("\(profile.name)  [\(marks.joined(separator: ", "))]")
            lines.append("  account  \(profile.account ?? "none")")
            lines.append("  device   \(profile.device ?? "none")")
            for folder in profile.folders {
                var note = "\(folder.records) record\(folder.records == 1 ? "" : "s")"
                if folder.linked { note += ", linked" }
                if let stashed = folder.stashed { note += ", \(stashed) stashed beside it" }
                if folder.remembered > 0 { note += ", \(folder.remembered) filed here" }
                lines.append("  \(folder.store)/\(folder.pair)")
                lines.append("      \(note)")
            }
            lines.append("")
        }

        let mirrors = loadMirrorState().pairs
        lines.append("Mirrored folder pairs: \(mirrors.count)")
        for key in mirrors.keys.sorted() {
            lines.append("  \(key.replacingOccurrences(of: "\0", with: "\n    against "))")
        }
        lines.append("")

        lines.append(findings.isEmpty ? "Nothing to report." : "Findings")
        for finding in findings {
            lines.append("  - " + finding.split(separator: "\n")
                .map { $0.trimmingCharacters(in: .whitespaces) }
                .joined(separator: " "))
        }

        Diagnostics.note("state", [
            "profiles": report.map { profile in
                [
                    "name": profile.name,
                    "account": profile.account ?? "",
                    "running": profile.running,
                    "folders": profile.folders.map {
                        ["pair": $0.pair, "store": $0.store, "records": $0.records,
                         "linked": $0.linked, "stashed": $0.stashed ?? -1,
                         "remembered": $0.remembered]
                    },
                ] as [String: Any]
            },
            "login": login?.account ?? "",
            "findings": findings,
        ])
        return lines.joined(separator: "\n") + "\n"
    }
}
