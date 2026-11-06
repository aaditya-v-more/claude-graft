using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ClaudeGraft.Core;
using ClaudeGraft.Platform;
using Microsoft.UI.Xaml;
using Microsoft.Win32;

namespace ClaudeGraft;

internal sealed class GraftUpdateManager(Action quit)
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromHours(1) };
    private readonly string cache = Path.Combine(GraftPaths.OwnData, "updates");
    private readonly Version current = typeof(App).Assembly.GetName().Version!;
    private GraftRelease? available;
    private bool busy;
    public event Action? Changed;
    public string Status { get; private set; } = "";
    public string ButtonText => available is null ? "Check for Graft updates" : $"Install Graft {available.Version}…";
    public bool CanCheck => !busy;

    public void Start()
    {
        timer.Tick += (_, _) => _ = CheckAsync(userInitiated: false);
        timer.Start();
        _ = CheckAsync(userInitiated: false);
    }

    private bool InstalledBySetup()
    {
        const string key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{B0C98D46-A272-4C54-8EDC-A31D441BAE9B}_is1";
        using var registration = Registry.CurrentUser.OpenSubKey(key);
        return registration?.GetValue("InstallLocation") is string directory
            && Path.IsPathFullyQualified(directory)
            && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)),
                Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase);
    }

    public async Task CheckAsync(bool userInitiated)
    {
        if (busy) return;
        busy = true;
        try
        {
            var stamp = Path.Combine(cache, "last-check");
            var sinceLastCheck = DateTime.UtcNow - File.GetLastWriteTimeUtc(stamp);
            if (!userInitiated && sinceLastCheck >= TimeSpan.Zero && sinceLastCheck < TimeSpan.FromHours(1)) return;
            Directory.CreateDirectory(cache);
            // Reuse one path and clean up a previous successful or interrupted transfer.
            var setup = Path.Combine(cache, "ClaudeGraft-setup.exe");
            File.Delete(setup);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var installRequested = userInitiated && available is not null;
            Status = installRequested ? "Downloading Graft update…" : "Checking for Graft updates…";
            Changed?.Invoke();
            if (!installRequested)
            {
                File.WriteAllText(stamp, "");
                var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
                available = await GraftRelease.FindAsync(current, arch, timeout.Token);
            }
            if (available is null) { Status = "No newer signed Windows release is available."; return; }
            var installed = InstalledBySetup();
            if (!installRequested && (App.Settings.HoldGraftUpdates || !installed))
            {
                Status = installed ? $"Graft {available.Version} is available." :
                    $"Graft {available.Version} is available. Install opens its per-user setup wizard.";
                return;
            }
            Status = $"Verifying Graft {available.Version}…";
            Changed?.Invoke();
            var expectedHash = await available.DownloadAsync(setup, timeout.Token);
            // Hold a read-only handle from verification through CreateProcess so the
            // path cannot be replaced or modified between the two operations.
            using var verified = new FileStream(setup, FileMode.Open, FileAccess.Read, FileShare.Read);
            await GraftRelease.VerifyInstallerAsync(verified, expectedHash, timeout.Token);
            if (!installRequested && App.Settings.HoldGraftUpdates) { Status = $"Graft {available.Version} is available."; return; }
            var start = new ProcessStartInfo(setup) { UseShellExecute = false };
            if (installed)
            {
                foreach (var arg in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/GRAFTUPDATE=1", "/DIR=" + AppContext.BaseDirectory.TrimEnd('\\') })
                    start.ArgumentList.Add(arg);
            }
            using var installer = Process.Start(start) ?? throw new IOException("Setup could not be opened.");
            quit();
        }
        catch (Exception error)
        {
            Status = error is CryptographicException ? error.Message : "Graft could not check or install the update. Try again.";
            Diagnostics.Note("graft.update.failed", new Dictionary<string, object?> { ["error"] = error.GetType().Name });
        }
        finally { busy = false; Changed?.Invoke(); }
    }
}
