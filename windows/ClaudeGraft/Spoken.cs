namespace ClaudeGraft;

/// <summary>
/// What a screen reader and UI automation find each control by. Every row has
/// its own Open and Start Session, so a button names the account it acts on,
/// and an automation ID reads <c>surface.profile.control</c>, the profile by
/// its folder, which is the same in every language and unique to one account.
/// </summary>
public static class Spoken
{
    public static string Id(string surface, string key, string control) => $"{surface}.{key}.{control}";

    public static string Act(string action, string name) => $"{action} {name}";

    public static string KeepWarm(string name) => $"Keep {name}'s 5-hour window open automatically";
}
