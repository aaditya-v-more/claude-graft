using System.Diagnostics;

namespace ClaudeGraft.Core;

public enum ClaudeLinkKind { Invalid, SignIn, OpenClaude }

public sealed record SignInTarget(string Name, string Profile, int Pid, long Started);

public static class ClaudeLinks
{
    public static ClaudeLinkKind Classify(string value)
    {
        if (value.Length is 0 or >= 16384 || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)
                || c is '"' or '\\') || !value.StartsWith("claude://", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(value, UriKind.Absolute, out var link) || link.Host.Length == 0
            || link.UserInfo.Length != 0 || link.Port != -1)
            return ClaudeLinkKind.Invalid;

        // Inspect the original route. URI normalization can silently remove dot
        // segments, while Electron receives the original string as its argument.
        var end = value.IndexOfAny(['?', '#']);
        var route = end < 0 ? value[9..] : value[9..end];
        if (route.StartsWith("login/", StringComparison.OrdinalIgnoreCase) || link.Host == "login")
            return route.Equals("login/google-auth", StringComparison.OrdinalIgnoreCase)
                ? ClaudeLinkKind.SignIn : ClaudeLinkKind.Invalid;
        if (link.Host is "claude.ai" or "claude.com")
        {
            if (link.AbsolutePath is "/magic-link" or "/sso-callback")
                return route.Equals(link.Host + link.AbsolutePath, StringComparison.OrdinalIgnoreCase)
                    ? ClaudeLinkKind.SignIn : ClaudeLinkKind.Invalid;
            // Never send a variation of a login route through ordinary main-profile dispatch.
            var path = Uri.UnescapeDataString(link.AbsolutePath);
            if (path.StartsWith("/magic-link", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/sso-callback", StringComparison.OrdinalIgnoreCase))
                return ClaudeLinkKind.Invalid;
        }
        return ClaudeLinkKind.OpenClaude;
    }

    public static List<SignInTarget> RunningTargets()
    {
        var store = new ShortcutStore();
        if (store.LoadError is not null) throw new IOException(store.LoadError);
        var profiles = store.Shortcuts.Select(s => (s.Name, s.ProfileDir))
            .Prepend(("Main Claude", GraftPaths.DefaultProfile));
        var processes = ClaudeProcesses.Enumerate();
        var result = new List<SignInTarget>();
        foreach (var (name, profile) in profiles.DistinctBy(p => p.Item2, StringComparer.OrdinalIgnoreCase))
        {
            Graft.ValidateProfilePath(profile, allowDefault: true);
            var pid = ClaudeProcesses.ProcessIdentifier(profile, processes);
            if (pid is null) continue;
            try
            {
                using var process = Process.GetProcessById(pid.Value);
                result.Add(new(name, profile, pid.Value, process.StartTime.ToUniversalTime().Ticks));
            }
            catch (ArgumentException) { } // The window closed while the list was being built.
            catch (InvalidOperationException) { }
        }
        return result;
    }

    public static ProcessStartInfo Dispatch(string value, SignInTarget? selected,
        IReadOnlyList<SignInTarget> running, string exe)
    {
        var kind = Classify(value);
        if (kind == ClaudeLinkKind.OpenClaude && selected is null)
        {
            // Shell activation would call this handler again.
            var start = Launcher.StartInfo(exe, GraftPaths.DefaultProfile);
            start.ArgumentList.Add(value);
            return start;
        }
        if (kind != ClaudeLinkKind.SignIn || selected is null || !running.Contains(selected))
            throw new IOException("The profile that started sign-in is no longer available. Start sign-in again in that profile.");
        return Launcher.StartInfo(exe, selected.Profile, value);
    }

    public static void Forward(string value, SignInTarget? selected)
    {
        using var stateLock = StateLock.Acquire();
        var exe = Launcher.ClaudeExe() ?? throw new IOException("Install Claude Desktop before opening a Claude link.");
        var running = selected is null ? [] : RunningTargets();
        using var process = Process.Start(Dispatch(value, selected, running, exe));
    }
}
