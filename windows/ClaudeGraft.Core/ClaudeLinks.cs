// Classify and the sign-in hand-off are taken from ClaudeLinks in James's Windows
// port (aaditya-v-more/claude-graft#9), moved onto this app's Launcher.

using System.Diagnostics;

namespace ClaudeGraft.Core;

public enum ClaudeLinkKind { Invalid, SignIn, OpenClaude }

/// A running Claude a sign-in can be handed to, known by its process as well as
/// its profile so one that closed and reopened is not mistaken for it.
public sealed record SignInTarget(string Name, string Profile, int Pid, long Started);

/// <summary>
/// Google, SSO and magic-link sign-ins come back from the browser as a claude://
/// link, which Windows gives to one registered handler whichever profile asked.
/// With Graft as that handler, a sign-in goes to the running profile the person
/// picks and every other link to the main Claude as before. A link can carry a
/// one-time code, so nothing here writes one down.
/// </summary>
public static class ClaudeLinks
{
    public const int LongestLink = 16384;

    public static ClaudeLinkKind Classify(string value)
    {
        if (value.Length is 0 or >= LongestLink || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)
                || c is '"' or '\\') || !value.StartsWith("claude://", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(value, UriKind.Absolute, out var link) || link.Host.Length == 0
            || link.UserInfo.Length != 0 || link.Port != -1)
            return ClaudeLinkKind.Invalid;

        // Read from the string as given: Uri drops dot segments, and Claude is handed the original.
        var end = value.IndexOfAny(['?', '#']);
        var route = end < 0 ? value["claude://".Length..] : value["claude://".Length..end];
        if (route.StartsWith("login/", StringComparison.OrdinalIgnoreCase) || link.Host == "login")
            return route.Equals("login/google-auth", StringComparison.OrdinalIgnoreCase)
                ? ClaudeLinkKind.SignIn : ClaudeLinkKind.Invalid;
        if (link.Host is "claude.ai" or "claude.com")
        {
            if (link.AbsolutePath is "/magic-link" or "/sso-callback")
                return route.Equals(link.Host + link.AbsolutePath, StringComparison.OrdinalIgnoreCase)
                    ? ClaudeLinkKind.SignIn : ClaudeLinkKind.Invalid;
            // A variation of a sign-in route must never reach the main profile as an ordinary link.
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
            .Prepend(("Claude", GraftPaths.DefaultProfile));
        var processes = ClaudeProcesses.Enumerate();
        var result = new List<SignInTarget>();
        foreach (var (name, profile) in profiles.DistinctBy(p => p.Item2, StringComparer.OrdinalIgnoreCase))
        {
            if (ClaudeProcesses.ProcessIdentifier(profile, processes) is not int pid) continue;
            if (StartedAt(pid) is long started) result.Add(new(name, profile, pid, started));
        }
        return result;
    }

    /// Replaced in tests, which have no processes to time.
    public static Func<int, long?> StartedAt { get; set; } = pid =>
    {
        try { using var process = Process.GetProcessById(pid); return process.StartTime.ToUniversalTime().Ticks; }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    };

    /// The profile a link goes to: the main one for an ordinary link, and for a
    /// sign-in only the chosen Claude, still running as the same process.
    public static string Destination(string value, SignInTarget? chosen, IReadOnlyList<SignInTarget> running)
    {
        var kind = Classify(value);
        if (kind == ClaudeLinkKind.OpenClaude && chosen is null) return GraftPaths.DefaultProfile;
        if (kind != ClaudeLinkKind.SignIn || chosen is null || !running.Contains(chosen))
            throw new IOException("The Claude that started this sign-in is no longer open. Start sign-in again from it.");
        return chosen.Profile;
    }

    /// Takes no state lock: a busy one would hold a sign-in for its whole
    /// patience before the check that decides where the sign-in goes.
    public static void Forward(string value, SignInTarget? chosen)
    {
        var profile = Destination(value, chosen, chosen is null ? [] : RunningTargets());
        if (chosen is null && !ClaudeProcesses.IsRunning(profile))
        {
            Launcher.Open(new GraftConfig { ProfileDir = profile, SourceDir = null }, applyGraft: false, link: value);
            return;
        }
        if (!Launcher.Start(profile, value)) throw new IOException("Claude could not be started.");
    }
}
