using System.Reflection;
namespace ClaudeGraft.Core;

/// <summary>
/// The Windows counterpart to the Mac's Sparkle-and-appcast update path. Releases
/// are Velopack packages published as GitHub releases, and the feed is the
/// <c>releases.win.json</c> each Windows release carries; the app's own side of
/// that lives in the app project, which alone references Velopack. What is here
/// is the part that decides, kept apart so it can be tested.
/// </summary>
public static class Updater
{
    /// The repository the Windows build is released from. windows-release.yml
    /// publishes here, so this is where a machine that installed from it looks.
    public const string Repo = "snowtyler/claude-graft";

    public const string RepoUrl = "https://github.com/" + Repo;

    /// Not ClaudeGraft: Velopack deletes %LOCALAPPDATA%\&lt;id&gt; on uninstall,
    /// and that folder holds the relay and the artifact sign-ins.
    public const string PackId = "ClaudeGraftApp";

    /// Where 1.x put the app, and the Add/Remove Programs entries that name it:
    /// the Inno installer's, and the one install.ps1 writes for a local build.
    public static readonly string[] LegacyUninstallKeys =
    {
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{E8F3B2A1-7C4D-4E5F-9A1B-3D6E8F2C4A5B}_is1",
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ClaudeGraft",
    };

    /// A release worth offering: its version and the notes shown before install.
    public sealed record Release
    {
        public required Version Version { get; init; }
        public string? Notes { get; init; }
    }

    /// The running build's version, stamped from windows/VERSION by Directory.Build.props.
    public static Version Current =>
        typeof(Updater).Assembly.GetName().Version is { } v ? v : new Version(0, 0, 0);

    /// What a person is shown: `Current`, with `-dev` on a development build.
    public static string Shown => typeof(Updater).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? Current.ToString(3);

    /// Whether a found update installs itself now. Never under a hold, and
    /// never while a Graft window is open: applying it closes Graft, and a
    /// person mid-click would lose it under them. The next check tries again.
    /// A development build installs nothing on its own either: it is numbered
    /// below the release it leads to, and that release, once out, read as an
    /// update and quietly replaced the build being worked on.
    public static bool InstallsOnItsOwn(bool held, bool windowOpen, bool development = false) =>
        !held && !windowOpen && !development;

    /// Built from VERSION rather than a release tag (Directory.Build.props).
    public static bool IsDevelopment => Shown.Contains("-dev", StringComparison.Ordinal);

    /// Only a higher number is an update. A pre-release sorts below its release,
    /// so a development build of 2.0.0 was offered 2.0.0 itself.
    public static bool Offers(Version found, Version running) =>
        new Version(found.Major, found.Minor, Math.Max(found.Build, 0))
        > new Version(running.Major, running.Minor, Math.Max(running.Build, 0));

    /// The same file under <paramref name="to"/> for a path inside
    /// <paramref name="from"/>, or null for one outside it. Compared as the disk
    /// compares, ignoring case, and by whole components, so Programs\ClaudeGraft2
    /// is not taken for a file inside Programs\ClaudeGraft.
    public static string? Moved(string path, string from, string to)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(from)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        return Path.Combine(to, full[root.Length..]);
    }
}
