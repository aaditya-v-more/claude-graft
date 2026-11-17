using System.Diagnostics;
using ClaudeGraft.Core;
using Microsoft.UI.Dispatching;

namespace ClaudeGraft;

/// <summary>
/// Claude Desktop's version, what its feed offers, and the two things a person
/// can do about it: install the new version, which Squirrel puts beside the
/// running one without closing anything, and restart the open Claudes onto it,
/// which closes them and asks first.
/// </summary>
public static class ClaudeUpdateCenter
{
    public enum Phase { Hidden, Store, Checking, UpToDate, Available, Installing, NeedsRestart, Restarting, Failed }

    public static Phase Now { get; private set; } = Phase.Hidden;
    public static Version? Installed { get; private set; }
    public static Version? Latest { get; private set; }
    public static int OnOlderVersion { get; private set; }
    public static string? Problem { get; private set; }

    /// Raised on the UI thread whenever the line should be redrawn.
    public static event Action? Changed;

    private static DispatcherQueue? _ui;
    private static Timer? _timer;
    private static int _busy;
    // A press that lands while a background check holds _busy, run once the check lets go.
    private static int _updateWanted;
    private static bool _checkFailed, _installFailed;

    public static void Start(DispatcherQueue ui)
    {
        _ui = ui;
        // At start and hourly, as the Mac checks; whether a Claude is open or not.
        _timer = new Timer(_ => _ = CheckAsync(), null, TimeSpan.FromSeconds(2), TimeSpan.FromHours(1));
        Set(Phase.Checking);
    }

    public static string Line => Now switch
    {
        Phase.Store => "Claude Desktop · updated by the Microsoft Store",
        Phase.Checking => $"Claude Desktop {Installed} · checking",
        Phase.UpToDate => $"Claude Desktop {Installed} · Up to date" + Held,
        Phase.Available => $"Claude Desktop {Latest} is available · you have {Installed}",
        Phase.Installing => $"Installing Claude Desktop {Latest}…",
        Phase.NeedsRestart => $"Claude Desktop {Installed} is installed · "
            + (OnOlderVersion == 1 ? "an open Claude is" : $"{OnOlderVersion} open Claudes are") + " still on the old version",
        Phase.Restarting => "Restarting Claude…",
        Phase.Failed => $"Claude Desktop {Installed} · {Problem}",
        _ => "",
    };

    private static string Held => ManualUpdates.Enabled ? " · updates when you ask" : "";

    /// The one button the line offers, or null for none.
    public static string? Action => Now switch
    {
        Phase.Available => "Update",
        Phase.NeedsRestart => "Restart Claudes…",
        Phase.Failed => "Try Again",
        _ => null,
    };

    public static bool Alarming => Now is Phase.Available or Phase.Failed;

    /// After something the line shows changed outside it, such as manual mode.
    public static void Redraw() => _ui?.TryEnqueue(() => Changed?.Invoke());

    /// A phase the debug window put up, with made-up versions; nothing it offers is acted on.
    public static bool Staged { get; private set; }

    public static void Stage(Phase phase)
    {
        Staged = true;
        Installed ??= new Version(1, 0, 0);
        Latest = new Version(Installed.Major, Installed.Minor + 1, 0);
        OnOlderVersion = phase == Phase.NeedsRestart ? 2 : 0;
        Set(phase, phase == Phase.Failed ? "could not check for updates" : null);
    }

    public static void Unstage()
    {
        if (!Staged) return;
        Staged = false;
        Latest = null;
        _ = CheckAsync();
    }

    private static void Set(Phase phase, string? problem = null)
    {
        Now = phase;
        Problem = problem;
        _ui?.TryEnqueue(() => Changed?.Invoke());
    }

    // What the disk says: which version is installed, and how many open Claudes
    // started before it.
    private static void ReadLocal()
    {
        if (!ClaudeDesktopUpdates.IsSquirrel)
        {
            Set(ClaudeInstallation.Current?.Package is null ? Phase.Hidden : Phase.Store);
            return;
        }
        Installed = ClaudeDesktopUpdates.InstalledVersion();
        try
        {
            OnOlderVersion = ClaudeProcesses.Enumerate()
                .Where(p => ClaudeProcesses.IsClaudeDesktop(p.command) && !ClaudeProcesses.IsHelper(p.command))
                .Count(p => ClaudeDesktopUpdates.RunningVersion(p.command) is { } v && v < Installed);
        }
        catch (IOException) { OnOlderVersion = 0; }
        Settle();
    }

    private static void Settle()
    {
        if (_installFailed) Set(Phase.Failed, "the update could not be installed");
        else if (Latest is not null && Installed is not null && Latest > Installed) Set(Phase.Available);
        else if (OnOlderVersion > 0) Set(Phase.NeedsRestart);
        else if (_checkFailed && Latest is null) Set(Phase.Failed, "could not check for updates");
        else Set(Phase.UpToDate);
    }

    /// The Failed line's button retries whichever of the two failed.
    public static Task RetryAsync() => Staged ? Task.CompletedTask : _installFailed ? UpdateAsync() : CheckAsync();

    /// A press of the sidebar's button: the line says it is checking for long
    /// enough to be seen, since an unchanged answer otherwise looks like nothing.
    public static Task CheckNowAsync()
    {
        if (Volatile.Read(ref _busy) != 0) return Task.CompletedTask;
        Staged = false;
        Set(Phase.Checking);
        return CheckAsync(Task.Delay(800));
    }

    public static async Task CheckAsync() => await CheckAsync(null);

    private static async Task CheckAsync(Task? shown)
    {
        // The hourly check would put the real answer over a staged one.
        if (Staged) return;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try
        {
            if (!ClaudeDesktopUpdates.IsSquirrel) { ReadLocalOnly(); return; }
            Installed = ClaudeDesktopUpdates.InstalledVersion();
            if (Installed is null) { ReadLocal(); return; }
            try
            {
                Latest = await ClaudeDesktopUpdates.LatestAsync(Installed);
                _checkFailed = false;
            }
            catch (Exception e)
            {
                // The last answer stands; only a check that never answered says so.
                _checkFailed = true;
                Diagnostics.Note("claude-update.check-failed", new Dictionary<string, object?> { ["error"] = e.Message });
            }
            if (shown is not null) await shown;
            Interlocked.Exchange(ref _busy, 0);
            if (Volatile.Read(ref _updateWanted) == 0) ReadLocal();
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
            if (Interlocked.Exchange(ref _updateWanted, 0) != 0) _ = UpdateAsync();
        }
    }

    private static void ReadLocalOnly() => Set(ClaudeInstallation.Current?.Package is null ? Phase.Hidden : Phase.Store);

    /// Runs Claude's own updater against the feed. It installs beside the
    /// running version and closes nothing; the open Claudes move to it when
    /// they next start.
    public static async Task UpdateAsync()
    {
        if (Staged) return;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            // The hourly check can sit on a slow network for a minute or more, and a press it swallowed looked like a dead button.
            if (Now == Phase.Available) { Interlocked.Exchange(ref _updateWanted, 1); Set(Phase.Installing); }
            Diagnostics.Note("claude-update.install-queued", new Dictionary<string, object?> { ["phase"] = Now.ToString() });
            return;
        }
        try
        {
            if (Installed is null)
            {
                Diagnostics.Note("claude-update.install-no-version", null);
                _installFailed = true;
                Settle();
                return;
            }
            Set(Phase.Installing);
            var url = ClaudeDesktopUpdates.FeedUrl(Installed, ClaudeDesktopUpdates.DeviceId());
            var exit = await Task.Run(() =>
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = ClaudeDesktopUpdates.UpdateExe,
                    ArgumentList = { "--update", url },
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    // It restarts Claude, which keeps this as its working folder.
                    WorkingDirectory = Path.GetDirectoryName(ClaudeDesktopUpdates.UpdateExe)!,
                });
                if (process is null) return -1;
                return process.WaitForExit(TimeSpan.FromMinutes(10)) ? process.ExitCode : -2;
            });
            Diagnostics.Note("claude-update.install", new Dictionary<string, object?> { ["exit"] = exit, ["to"] = Latest?.ToString() });
            _installFailed = exit != 0;
            Interlocked.Exchange(ref _busy, 0);
            ReadLocal();
        }
        catch (Exception e)
        {
            Diagnostics.Note("claude-update.install-failed", new Dictionary<string, object?> { ["error"] = e.Message });
            _installFailed = true;
            Settle();
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// The open Claudes, each by the profile it runs, for the warning and the restart.
    public static IReadOnlyList<(GracefulQuit.Target Target, GraftConfig Config, string Name)> OpenClaudes()
    {
        var shortcuts = App.Store.Shortcuts.ToList();
        var open = new List<(GracefulQuit.Target, GraftConfig, string)>();
        foreach (var (pid, command) in ClaudeProcesses.Enumerate())
        {
            if (ClaudeProcesses.ProfileOf(command, shortcuts) is not string profile) continue;
            if (GracefulQuit.Pin(pid) is not { } target) continue;
            var shortcut = shortcuts.FirstOrDefault(s => Fs.SamePath(s.ProfileDir, profile));
            var config = shortcut is null
                ? new GraftConfig { ProfileDir = profile, SourceDir = null }
                : App.Store.ConfigFor(shortcut);
            open.Add((target, config, shortcut?.Name ?? "Claude"));
        }
        return open;
    }

    /// Closes the Claudes a person agreed to close, then opens each again on
    /// its own profile and the new version. A Claude started since the warning
    /// was not agreed to and is left alone; one that will not close is
    /// reported, never forced.
    public static async Task<string?> RestartAsync(IReadOnlyList<(GracefulQuit.Target Target, GraftConfig Config, string Name)> agreed)
    {
        if (Staged) return "That update was staged from the debug window; nothing was restarted.";
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return "Claude is already being updated.";
        try
        {
            Set(Phase.Restarting);
            var stayed = await Task.Run(() => GracefulQuit.Quit(agreed.Select(a => a.Target).ToList(), TimeSpan.FromSeconds(60)));
            var closed = agreed.Where(a => !stayed.Contains(a.Target)).ToList();
            foreach (var (_, config, _) in closed)
                await Task.Run(() => { try { Launcher.Open(config); } catch (Exception e) { Diagnostics.Note("claude-update.reopen-failed", new Dictionary<string, object?> { ["error"] = e.Message }); } });
            Interlocked.Exchange(ref _busy, 0);
            ReadLocal();
            if (stayed.Count == 0) return null;
            var names = string.Join(", ", agreed.Where(a => stayed.Contains(a.Target)).Select(a => a.Name));
            return $"Did not close, and stayed on the old version: {names}. Quit it from its own menu, then open it again.";
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }
}
