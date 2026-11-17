using ClaudeGraft.Core;
using Velopack;
using Velopack.Sources;

namespace ClaudeGraft;

/// <summary>
/// Graft's own updates. The feed is the releases.win.json each Windows release
/// carries; a Mac release beside it in the same repository carries none, and is
/// passed over without being read. Only a copy Velopack's setup installed can
/// update itself, so a development build or a hand-copied one never offers to.
/// </summary>
internal static class GraftUpdates
{
    private static readonly UpdateManager Manager = new(new GithubSource(Updater.RepoUrl, null, false));
    private static UpdateInfo? _found;

    public static bool IsInstalled => Manager.IsInstalled;

    /// The folder holding current\ and Update.exe, or null outside an install.
    public static string? Root => Manager.IsInstalled
        ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))
        : null;

    public sealed class NotInstalledException() : Exception("this copy of Claude Graft was not installed by its setup");

    /// Null when nothing is newer; throws on a failed check, or outside an install.
    public static async Task<Updater.Release?> CheckAsync()
    {
        if (!Manager.IsInstalled) throw new NotInstalledException();
        var info = await Manager.CheckForUpdatesAsync().ConfigureAwait(false);
        _found = info;
        if (info is null) return null;
        var v = info.TargetFullRelease.Version;
        if (!Updater.Offers(new Version(v.Major, v.Minor, v.Patch), Updater.Current)) return null;
        return new Updater.Release { Version = new Version(v.Major, v.Minor, v.Patch), Notes = info.TargetFullRelease.NotesMarkdown };
    }

    /// Velopack checks the package against the feed's SHA before keeping it.
    public static async Task DownloadAsync(Updater.Release release, IProgress<double> progress)
    {
        var info = Found(release);
        await Manager.DownloadUpdatesAsync(info, percent => progress.Report(percent / 100.0)).ConfigureAwait(false);
    }

    /// Velopack waits for this process to go before it swaps the folder, then starts the new one.
    public static void ApplyOnExit(Updater.Release release) =>
        Manager.WaitExitThenApplyUpdates(Found(release), silent: true, restart: true);

    private static UpdateInfo Found(Updater.Release release)
    {
        if (_found is not { } info) throw new InvalidOperationException("no update has been found to install");
        var v = info.TargetFullRelease.Version;
        if (new Version(v.Major, v.Minor, v.Patch) != release.Version)
            throw new InvalidOperationException($"the update found is {v}, not {release.Version}");
        return info;
    }
}
