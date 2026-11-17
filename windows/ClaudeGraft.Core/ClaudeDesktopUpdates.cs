using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeGraft.Core;

/// <summary>
/// Claude Desktop's own Squirrel install: which version is installed, which a
/// running Claude is on, and what Claude's update feed offers. Squirrel installs
/// a new version beside the running one and uses it at the next launch, so
/// nothing here ever needs a Claude closed.
/// </summary>
public static partial class ClaudeDesktopUpdates
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnthropicClaude");

    public static string UpdateExe => Path.Combine(Root, "Update.exe");

    /// Whether this machine's Claude is the Squirrel install Graft can update.
    /// A Store install is updated by the Store.
    public static bool IsSquirrel => File.Exists(UpdateExe) && InstalledVersion() is not null;

    /// The version the next launch runs: the newest app-<version> folder.
    public static Version? InstalledVersion() =>
        Directory.Exists(Root) && Launcher.NewestApp(Directory.EnumerateDirectories(Root, "app-*")
            .Where(d => File.Exists(Path.Combine(d, "claude.exe")))) is string dir
            ? VersionOfAppFolder(dir) : null;

    /// The version a running Claude was started from, read off its command line.
    public static Version? RunningVersion(string command) =>
        AppFolder().Match(command) is { Success: true } m && Version.TryParse(m.Groups[1].Value, out var v) ? v : null;

    private static Version? VersionOfAppFolder(string dir) =>
        Version.TryParse(Path.GetFileName(dir)["app-".Length..], out var v) ? v : null;

    [GeneratedRegex(@"AnthropicClaude\\app-(\d+(?:\.\d+){1,3})\\", RegexOptions.IgnoreCase)]
    private static partial Regex AppFolder();

    // MARK: - The feed

    private static string Arch => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    /// The feed as Claude's own updater asks it, under a device ID of Graft's
    /// own, so a check follows the same staged rollout and carries no account.
    public static string FeedUrl(Version installed, string deviceId) =>
        $"https://api.anthropic.com/api/desktop/win32/{Arch}/squirrel/update"
        + $"?device_id={deviceId}&version={installed}&os_version={Environment.OSVersion.Version}";

    public static string ReleasesUrl(Version installed, string deviceId) =>
        $"https://api.anthropic.com/api/desktop/win32/{Arch}/squirrel/update/RELEASES"
        + $"?device_id={deviceId}&version={installed}&os_version={Environment.OSVersion.Version}"
        + $"&id=AnthropicClaude&localVersion={installed}&arch={(Arch == "x64" ? "amd64" : Arch)}";

    /// The newest version a Squirrel RELEASES file names: lines of
    /// "&lt;sha1&gt; AnthropicClaude-&lt;version&gt;-full.nupkg &lt;size&gt;".
    public static Version? LatestIn(string releases)
    {
        Version? best = null;
        foreach (Match m in Package().Matches(releases))
            if (Version.TryParse(m.Groups[1].Value, out var v) && (best is null || v > best)) best = v;
        return best;
    }

    [GeneratedRegex(@"AnthropicClaude-(\d+(?:\.\d+){1,3})-(?:full|delta)\.nupkg", RegexOptions.IgnoreCase)]
    private static partial Regex Package();

    private sealed record Identity(string DeviceId);

    /// A device ID kept for Graft's checks alone, never Claude's.
    public static string DeviceId()
    {
        var file = Path.Combine(GraftPaths.OwnData, "claude-updater.json");
        try
        {
            if (JsonSerializer.Deserialize<Identity>(File.ReadAllBytes(file)) is { DeviceId: { Length: > 0 } id }) return id;
        }
        catch { }
        var fresh = Guid.NewGuid().ToString();
        try
        {
            Directory.CreateDirectory(GraftPaths.OwnData);
            AtomicWrite.Bytes(file, JsonSerializer.SerializeToUtf8Bytes(new Identity(fresh)));
        }
        catch { }
        return fresh;
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// The newest version the feed offers this machine, or null when it offers
    /// nothing. Throws on a network failure so it reads as one.
    public static async Task<Version?> LatestAsync(Version installed, CancellationToken ct = default)
    {
        using var response = await Http.GetAsync(ReleasesUrl(installed, DeviceId()), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return LatestIn(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    // MARK: - Squirrel's restart

    public static string ProcessStartLog => Path.Combine(Root, "Squirrel-ProcessStart.log");

    /// Every restart Squirrel's log records: when it ran, and the Claude it
    /// waited on, which is the one that quit to update. Squirrel relaunches
    /// with no arguments, so a shortcut's Claude comes back as the main one.
    public static IReadOnlyList<(DateTime At, int ParentPid)> Restarts(string log)
    {
        var found = new List<(DateTime, int)>();
        foreach (Match m in WaitLine().Matches(log))
            if (DateTime.TryParseExact(m.Groups[1].Value, "dd/MM/yy HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var at)
                && int.TryParse(m.Groups[2].Value, out var pid))
                found.Add((at, pid));
        return found;
    }

    [GeneratedRegex(@"^\[(\d\d/\d\d/\d\d \d\d:\d\d:\d\d)\] info: Program: About to wait for parent PID (\d+)", RegexOptions.Multiline)]
    private static partial Regex WaitLine();

    /// Whether the Claude that just exited quit for Squirrel to restart it: the
    /// log names its pid after that process started, so a pid reused from an
    /// older restart is not mistaken for it. The log keeps whole seconds.
    public static bool QuitToUpdate(string log, int pid, DateTime started) =>
        Restarts(log).Any(r => r.ParentPid == pid && r.At >= started.AddSeconds(-1));
}
