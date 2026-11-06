using System.Diagnostics;
using ClaudeGraft.Core;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace ClaudeGraft;

internal sealed record InstalledClaude(Version Version, string Publisher, string Architecture, string Executable)
{
    public static InstalledClaude Read()
    {
        var installation = ClaudeInstallation.Discover() ?? throw new IOException("Install Claude Desktop first.");
        var package = new PackageManager().FindPackagesForUser("", ClaudeInstallation.PackageFamily)
            .FirstOrDefault(p => Fs.SamePath(Path.Combine(p.InstalledLocation.Path, "app", "Claude.exe"), installation.Executable))
            ?? throw new IOException("This standalone Claude installation updates through Claude's own menu. In-app manual installation requires the current Windows MSIX.");
        var version = package.Id.Version;
        var architecture = package.Id.Architecture.ToString().ToLowerInvariant();
        if (architecture is not ("x64" or "arm64")) throw new IOException("This Claude package has an unsupported architecture.");
        return new(new Version(version.Major, version.Minor, version.Build, version.Revision),
            package.Id.Publisher, architecture, installation.Executable);
    }
}

internal static class ClaudeDesktopUpdate
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromMinutes(15) };

    internal static async Task<ClaudeDesktopRelease> Check(InstalledClaude installed, CancellationToken token)
    {
        var feed = new Uri($"https://api.anthropic.com/api/desktop/win32/{installed.Architecture}/msix/latest");
        using var response = await Client.GetAsync(feed, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(32 * 1024, token);
        return ClaudeDesktopRelease.Parse(await response.Content.ReadAsStringAsync(token), installed.Architecture);
    }

    internal static List<ClaudeUpdateProcess> Running(InstalledClaude installed)
    {
        var result = new List<ClaudeUpdateProcess>();
        using var self = Process.GetCurrentProcess();
        foreach (var (pid, command) in ClaudeProcesses.Enumerate())
        {
            var args = WindowsCommandLine.Parse(command);
            if (args.Length == 0 || !ClaudeProcesses.IsClaudeDesktop(command) || ClaudeProcesses.IsHelper(command)) continue;
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.SessionId != self.SessionId)
                    throw new IOException("Claude is running in another Windows session. Close it there before updating.");
                if (!Fs.SamePath(args[0], installed.Executable))
                    throw new IOException("Another installation of Claude Desktop is running. Quit it before updating this one.");
                var folder = WindowsCommandLine.Option(args, "--user-data-dir");
                result.Add(new(pid, process.StartTime.ToUniversalTime().Ticks,
                    string.IsNullOrEmpty(folder) ? "Main Claude" : Path.GetFileName(Path.TrimEndingDirectorySeparator(folder))));
            }
            catch (ArgumentException) { } // It exited during the snapshot.
        }
        return result;
    }

    internal static async Task Install(InstalledClaude installed, ClaudeDesktopRelease approvedRelease,
        List<ClaudeUpdateProcess> approved, ClaudePolicyState approvedPolicy, IProgress<string> progress, CancellationToken token)
    {
        if (GraftPaths.ProfilesRootOverride is not null) throw new IOException("Updates are unavailable in the isolated test manager.");
        var release = await Check(installed, token);
        if (release != approvedRelease || release.Version <= installed.Version)
            throw new IOException("The release changed. Check again before installing.");
        var directory = Directory.CreateTempSubdirectory("ClaudeGraft-");
        var file = Path.Combine(directory.FullName, "Claude.msix");
        try
        {
            progress.Report("Downloading the Claude Desktop update…");
            using (var response = await Client.GetAsync(release.Download, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                await using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await response.Content.CopyToAsync(output, token);
            }
            using var verifiedFile = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            progress.Report("Verifying the downloaded package…");
            await Task.Run(() => WindowsPackageTrust.Verify(file), token);
            release.CheckPackage(verifiedFile, installed.Publisher, installed.Architecture);
            // Mutex ownership is thread-affine. All operations from the last
            // process check through version verification stay on this worker.
            await Task.Run(() =>
            {
                using var gate = StateLock.Acquire();
                if (approvedPolicy == ClaudePolicyState.Managed || InstalledClaude.Read() != installed || ClaudeUpdatePolicy.Read() != approvedPolicy)
                    throw new IOException("Claude's installation or update policy changed. Check again.");
                var current = Running(installed);
                ClaudeUpdateProcess.RequireConsent(approved, current);
                token.ThrowIfCancellationRequested();
                progress.Report("Waiting for the approved Claude windows to close…");
                foreach (var instance in current)
                {
                    ClaudeUpdateProcess.RequireConsent(approved, Running(installed));
                    try
                    {
                        using var process = Process.GetProcessById(instance.Pid);
                        if (process.StartTime.ToUniversalTime().Ticks != instance.Started)
                            throw new IOException("A Claude process changed. Review the profiles again.");
                        if (!process.CloseMainWindow() && !process.HasExited)
                            throw new IOException($"Quit {instance.Profile} from Claude's tray menu, then check again.");
                    }
                    catch (ArgumentException) { }
                }
                var wait = Stopwatch.StartNew();
                while ((current = Running(installed)).Count != 0)
                {
                    ClaudeUpdateProcess.RequireConsent(approved, current);
                    if (wait.Elapsed > TimeSpan.FromSeconds(20))
                        throw new IOException("Claude is still running. Finish your work, quit Claude from its tray menu, and check again.");
                    token.ThrowIfCancellationRequested();
                    Thread.Sleep(250);
                }
                token.ThrowIfCancellationRequested();
                if (InstalledClaude.Read() != installed || ClaudeUpdatePolicy.Read() != approvedPolicy)
                    throw new IOException("Claude's installation or update policy changed before deployment. Check again.");
                progress.Report("Windows is verifying and installing the signed Claude Desktop package…");
                var deployment = new PackageManager().UpdatePackageAsync(new Uri(file), null, DeploymentOptions.None)
                    .AsTask().GetAwaiter().GetResult();
                if (deployment.ExtendedErrorCode is { } error && error.HResult < 0) throw error;
                var updated = InstalledClaude.Read();
                if (updated.Version != release.Version || updated.Publisher != installed.Publisher)
                    throw new IOException("Windows finished deployment, but the offered Claude version could not be verified.");
            }, token);
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
            directory.Delete();
        }
    }
}
