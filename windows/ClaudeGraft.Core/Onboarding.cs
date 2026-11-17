namespace ClaudeGraft.Core;

public enum SetupState { Ready, NotInstalled, NotSignedIn }

/// <summary>
/// Whether there is anything for Graft to show yet. Every card it draws is a
/// Claude Desktop profile, so with Claude missing, or never signed in anywhere,
/// the cards would be empty shells whose Open and Start Session cannot work.
/// </summary>
public static class Onboarding
{
    public static SetupState Check(ShortcutStore store) => Staging.Setup ??
        Check(Launcher.ClaudeExe() is not null,
              store.Shortcuts.Select(s => GraftPaths.Profile(s.Folder)).Prepend(GraftPaths.DefaultProfile));

    public static SetupState Check(bool installed, IEnumerable<string> profiles)
    {
        if (!installed) return SetupState.NotInstalled;
        return profiles.Any(IsSignedIn) ? SetupState.Ready : SetupState.NotSignedIn;
    }

    /// A config.json that will not parse counts as signed in: it is one caught
    /// mid-rename, and hiding every card on the strength of that would flicker.
    public static bool IsSignedIn(string profile)
    {
        if (Staging.For(profile)?.SignedIn is { } staged) return staged;
        if (!Fs.Exists(Path.Combine(profile, "config.json"))) return false;
        var config = Graft.ReadableConfigJson(profile);
        return config is null
            || ClaudeCredentials.HasCachedLogin(config)
            || config.ContainsKey("lastKnownAccountUuid");
    }
}
