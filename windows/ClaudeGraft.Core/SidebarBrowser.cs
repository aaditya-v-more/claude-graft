using System.Diagnostics;
using System.Text.Json;

namespace ClaudeGraft.Core;

internal static class SidebarBrowser
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Only this Claude build has been matched to the packaged Chromium. Using
    // a newer engine could upgrade storage that an older Claude still needs.
    internal static string? CompatibleRuntime()
    {
        var installed = ClaudeInstallation.Discover();
        if (installed is null || FileVersionInfo.GetVersionInfo(installed.Executable).ProductVersion != "4.16336.0")
            return null;
        foreach (var subdirectory in new[] { "sidebar-runtime", Path.Combine("launcher", "sidebar-runtime") })
        {
            var runtime = Path.Combine(AppContext.BaseDirectory, subdirectory);
            if (File.Exists(Path.Combine(runtime, "sidebar.cjs")) &&
                File.Exists(Path.Combine(runtime, "electron.exe")) &&
                File.ReadAllText(Path.Combine(runtime, "version")).Trim() == "44.9.3") return runtime;
        }
        return null;
    }

    internal static Dictionary<string, SidebarSync.Snapshot> ReadOrWrite(string runtime, string[] profiles,
        Dictionary<string, object>? changes = null)
    {
        var root = GraftPaths.OwnData;
        var scratch = Path.Combine(root, ".sidebar-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(scratch, "result.json");
        var request = Path.Combine(scratch, "request.json");
        var runtimeData = Path.Combine(scratch, "runtime");
        var backups = Path.Combine(root, "sidebar-backups");
        if (Junction.IsLink(root) || Junction.IsLink(backups))
            throw new IOException("Sidebar output must stay in Graft's data folder.");
        Directory.CreateDirectory(runtimeData);
        try
        {
            if (changes is not null) Directory.CreateDirectory(backups);
            foreach (var profile in profiles)
            {
                foreach (var name in new[] { "Local Storage", "IndexedDB" })
                    RequireLocalTree(Path.Combine(profile, name));
                if (Junction.IsLink(Path.Combine(profile, "claude_desktop_config.json")))
                    throw new IOException("Linked desktop settings.");
            }
            File.WriteAllBytes(request, JsonSerializer.SerializeToUtf8Bytes(new
            {
                action = changes is null ? "read" : "write",
                profiles = profiles.Select(path => new { path, change = changes?.GetValueOrDefault(path) }),
                scratch, output, backup = Path.Combine(backups, Guid.NewGuid().ToString("N") + ".json")
            }, Json));
            var start = new ProcessStartInfo(Path.Combine(runtime, "electron.exe"))
            {
                WorkingDirectory = scratch, UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            // NODE_OPTIONS or a Chromium override inherited from the launching
            // shell could execute scripts or direct the helper at another profile.
            foreach (var key in start.Environment.Keys.Where(k =>
                k.StartsWith("ELECTRON_", StringComparison.OrdinalIgnoreCase) ||
                k.StartsWith("NODE_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
            start.ArgumentList.Add("--user-data-dir=" + runtimeData);
            start.ArgumentList.Add(Path.Combine(runtime, "sidebar.cjs"));
            start.ArgumentList.Add(request);
            using var process = Process.Start(start) ?? throw new IOException("Sidebar runtime did not start.");
            if (!process.WaitForExit(25_000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                throw new IOException("Sidebar runtime timed out.");
            }
            if (process.ExitCode != 0) throw new IOException("Sidebar runtime failed.");
            using var reply = JsonDocument.Parse(File.ReadAllBytes(output));
            var result = reply.RootElement;
            if (!result.GetProperty("ok").GetBoolean()) throw new IOException("Sidebar storage is unavailable.");
            var snapshots = result.GetProperty("profiles").EnumerateArray().ToDictionary(
                item => item.GetProperty("path").GetString()!,
                item => item.Deserialize<SidebarSync.Snapshot>(Json)!, StringComparer.OrdinalIgnoreCase);
            if (!snapshots.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(profiles))
                throw new IOException("Incomplete sidebar storage response.");
            return snapshots;
        }
        finally
        {
            // Junctions point at real databases. Detach them explicitly before
            // removing the private Chromium profile, including after a timeout.
            foreach (var profile in Directory.EnumerateDirectories(scratch, "profile-*"))
                foreach (var name in new[] { "Local Storage", "IndexedDB" })
                    Junction.Remove(Path.Combine(profile, name));
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static void RequireLocalTree(string directory)
    {
        if (Junction.IsLink(directory)) throw new IOException("Linked browser storage.");
        foreach (var item in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(item);
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Linked browser storage.");
            if (attributes.HasFlag(FileAttributes.Directory)) RequireLocalTree(item);
        }
    }
}
