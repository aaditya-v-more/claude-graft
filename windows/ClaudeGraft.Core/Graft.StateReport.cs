using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeGraft.Core;

/// The chat stores as they stand after a pass, rewritten in full each time:
/// the log says what happened, this says what is true now.
public static partial class Graft
{
    /// One organization folder on disk against what this app believes: Stashed is
    /// null where no stash sits beside it, Remembered counts records seen filed there.
    public sealed record FolderReport(
        string Store, string Pair, int Records, bool Linked, int? Stashed, int Remembered);

    public sealed record ProfileReport(
        string Name, string? Account, bool ConfigReadable, string? Device,
        bool IsShortcut, bool Running, IReadOnlyList<FolderReport> Folders);

    /// The account every bridged session on this machine is stamped with,
    /// whichever profile's window it was typed into. The login's email is never read.
    public static (string Account, string Organization)? CommandLineLogin()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(GraftPaths.ClaudeConfigFile));
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("oauthAccount", out var oauth)
                && oauth.ValueKind == JsonValueKind.Object
                && oauth.TryGetProperty("accountUuid", out var account) && account.ValueKind == JsonValueKind.String
                && oauth.TryGetProperty("organizationUuid", out var org) && org.ValueKind == JsonValueKind.String)
                return (account.GetString()!, org.GetString()!);
        }
        catch { }
        return null;
    }

    /// The device a profile registered as. Each profile has its own, so chats
    /// made under an earlier one show as from another device.
    public static string? DeviceIdentifier(string profile)
    {
        try
        {
            var encoded = File.ReadAllText(Path.Combine(profile, "ant-did")).Trim();
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch { return null; }
    }

    public static List<ProfileReport> StateReport(bool checkingRunning = true) => StateReport(checkingRunning, out _);

    private static List<ProfileReport> StateReport(bool checkingRunning, out bool recordsReadable)
    {
        var counted = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        recordsReadable = true;
        try
        {
            foreach (var store in SessionRecordState.Load(SessionRecordStateFile).Records.Values)
                if (Path.IsPathRooted(store)) counted[store] = counted.GetValueOrDefault(store) + 1;
        }
        catch (UnreadableStateException) { recordsReadable = false; }

        IReadOnlyList<(int pid, string command)> processes = Array.Empty<(int, string)>();
        // Asked once for every profile: each query is a WMI call, and this stands in front of a window opening.
        if (checkingRunning)
            try { processes = ClaudeProcesses.Enumerate(); }
            catch { }

        static int RecordsIn(string dir) =>
            SafeEntries(dir).Count(n => n.StartsWith("local_") && n.EndsWith(".json"));

        var shortcuts = ShortcutProfiles();

        return SessionStoreProfiles().Select(profile =>
        {
            var folders = new List<FolderReport>();
            foreach (var store in GraftPaths.ChatStoreNames)
            {
                var storeDir = Path.Combine(profile, store);
                foreach (var account in SafeEntries(storeDir).OrderBy(n => n, StringComparer.Ordinal))
                {
                    if (account.StartsWith('.') || GraftPaths.NonAccountStoreItems.Contains(account)) continue;
                    var accountDir = Path.Combine(storeDir, account);
                    if (!Fs.IsDirectory(accountDir)) continue;
                    foreach (var org in SafeEntries(accountDir).OrderBy(n => n, StringComparer.Ordinal))
                    {
                        if (org.StartsWith('.')) continue;
                        var orgDir = Path.Combine(accountDir, org);
                        if (!Fs.IsDirectory(orgDir)) continue;
                        var stash = StashPath(orgDir);
                        folders.Add(new FolderReport(
                            store, $"{account}/{org}", RecordsIn(orgDir), Junction.IsLink(orgDir),
                            Fs.Exists(stash) ? RecordsIn(stash) : null,
                            counted.GetValueOrDefault(Fs.Resolve(orgDir))));
                    }
                }
            }
            var config = ReadableConfigJson(profile);
            return new ProfileReport(
                Path.GetFileName(profile),
                config?["lastKnownAccountUuid"] is JsonValue value && value.TryGetValue<string>(out var signedIn) ? signedIn : null,
                config is not null,
                DeviceIdentifier(profile),
                shortcuts.Any(s => Fs.SamePath(s, profile)),
                ClaudeProcesses.IsRunning(profile, processes),
                folders);
        }).ToList();
    }

    /// What is wrong right now, in the words the symptom arrives in: each of
    /// these has been reported as something else first.
    public static List<string> StateFindings(
        IReadOnlyList<ProfileReport> report, (string Account, string Organization)? login,
        IReadOnlyList<string>? unreadable = null)
    {
        var findings = new List<string>();

        if (login is (string loggedIn, _))
        {
            var holder = report.FirstOrDefault(p => p.Account == loggedIn)?.Name;
            foreach (var profile in report.Where(p => p.Account is not null && p.Account != loggedIn))
                findings.Add(
                    $"{profile.Name} is signed into {Short(profile.Account!)}, but the command line is logged in as " +
                    $"{Short(loggedIn)}{(holder is null ? "" : $" ({holder})")}. Chats started in {profile.Name} are " +
                    $"stamped with that account and filed there, so they appear in {holder ?? "the other profile"} " +
                    $"rather than in {profile.Name}. This is one shared login, not two profiles syncing.");
        }
        else
        {
            findings.Add("No command line login found, so no session on this machine has an owner and none can be filed.");
        }

        foreach (var profile in report)
        {
            if (!profile.ConfigReadable)
                findings.Add($"{profile.Name} has a config.json that will not parse. Nothing may decide where its " +
                             "chats go until it can be read.");
            foreach (var folder in profile.Folders)
            {
                var where = $"{profile.Name} {folder.Store} {folder.Pair} holds {folder.Records} record{Plural(folder.Records)}";
                if (folder.Stashed is int stashed && stashed > folder.Records)
                    findings.Add($"{where} with {stashed} put aside beside it. The stash is what the profile owned " +
                                 "before a graft, so a graft was undone without handing them back and the sidebar " +
                                 $"is short by {stashed}.");
                if (folder.Remembered > folder.Records + 1 && folder.Stashed is null && !folder.Linked)
                    findings.Add($"{where} where {folder.Remembered} were filed. Records go missing when a sidebar " +
                                 "is cleared by hand and when a folder is moved out from under this app, and only " +
                                 "the second is a fault.");
            }
        }

        // Read as empty, either file would forget what every earlier pass learned, so a pass stops instead.
        foreach (var file in unreadable ?? Array.Empty<string>())
            findings.Add($"{file} will not read, so each pass stops at it and nothing it guards moves until it can be read.");
        return findings;
    }

    private static string Short(string uuid) => uuid.Length > 8 ? uuid[..8] : uuid;

    private static string Plural(int count) => count == 1 ? "" : "s";

    /// Never throws: a pass that could not write down what it left is still a pass that ran.
    public static void WriteStateReport(bool checkingRunning = true)
    {
        try
        {
            var text = StateReportText(checkingRunning);
            Directory.CreateDirectory(Path.GetDirectoryName(Diagnostics.ReportFile)!);
            AtomicWrite.Bytes(Diagnostics.ReportFile, Encoding.UTF8.GetBytes(text));
        }
        catch { }
    }

    /// Built as a string so the suite can read what an arrangement of folders produces.
    public static string StateReportText(bool checkingRunning = true)
    {
        var report = StateReport(checkingRunning, out var recordsReadable);
        var login = CommandLineLogin();
        var unreadable = new List<string>();
        if (!recordsReadable) unreadable.Add(Path.GetFileName(SessionRecordStateFile));
        List<string>? pairs = null;
        try { pairs = LoadMirrorState().Pairs.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(); }
        catch (UnreadableStateException) { unreadable.Add(Path.GetFileName(MirrorStateFile)); }
        var findings = StateFindings(report, login, unreadable);
        var lines = new List<string>
        {
            "Claude Graft — state of the chat stores",
            $"read {DateTimeOffset.UtcNow.ToString("s")}Z by {Diagnostics.Who}",
            "",
        };

        if (login is (string account, string organization))
        {
            var holder = report.FirstOrDefault(p => p.Account == account)?.Name ?? "no profile here";
            lines.Add(@"Command line login (%USERPROFILE%\.claude.json)");
            lines.Add($"  account       {account}  — held by {holder}");
            lines.Add($"  organization  {organization}");
            lines.Add("  Every bridged session started from any profile is stamped with this");
            lines.Add("  account and filed into the profile holding it, whichever window it");
            lines.Add("  was typed into.");
        }
        else
        {
            lines.Add(@"Command line login (%USERPROFILE%\.claude.json): none");
        }
        lines.Add("");

        foreach (var profile in report)
        {
            var marks = new List<string> { profile.IsShortcut ? "shortcut" : "not a shortcut" };
            if (profile.Running) marks.Add("running");
            if (!profile.ConfigReadable) marks.Add("config.json unreadable");
            lines.Add($"{profile.Name}  [{string.Join(", ", marks)}]");
            lines.Add($"  account  {profile.Account ?? "none"}");
            lines.Add($"  device   {profile.Device ?? "none"}");
            foreach (var folder in profile.Folders)
            {
                var note = $"{folder.Records} record{Plural(folder.Records)}";
                if (folder.Linked) note += ", linked";
                if (folder.Stashed is int stashed) note += $", {stashed} stashed beside it";
                if (folder.Remembered > 0) note += $", {folder.Remembered} filed here";
                lines.Add($"  {folder.Store}/{folder.Pair}");
                lines.Add($"      {note}");
            }
            lines.Add("");
        }

        if (pairs is null) lines.Add("Mirrored folder pairs: mirrored-chats.json will not read");
        else
        {
            lines.Add($"Mirrored folder pairs: {pairs.Count}");
            foreach (var key in pairs)
                lines.Add("  " + key.Replace(PairSeparator, "\n    against "));
        }
        lines.Add("");

        lines.Add(findings.Count == 0 ? "Nothing to report." : "Findings");
        lines.AddRange(findings.Select(f => "  - " + f));

        Diagnostics.Note("state", new Dictionary<string, object?>
        {
            ["profiles"] = report.Select(p => new Dictionary<string, object?>
            {
                ["name"] = p.Name,
                ["account"] = p.Account ?? "",
                ["running"] = p.Running,
                ["folders"] = p.Folders.Select(f => new Dictionary<string, object?>
                {
                    ["pair"] = f.Pair, ["store"] = f.Store, ["records"] = f.Records,
                    ["linked"] = f.Linked, ["stashed"] = f.Stashed ?? -1, ["remembered"] = f.Remembered,
                }).ToList(),
            }).ToList(),
            ["login"] = login?.Account ?? "",
            ["findings"] = findings,
        });
        return string.Join("\n", lines) + "\n";
    }
}
