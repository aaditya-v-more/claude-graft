namespace ClaudeGraft.Core;

/// <summary>
/// Where Claude Desktop keeps its profiles on Windows. The Mac build reads
/// these out of <c>~/Library/Application Support</c>; on Windows every profile
/// — the default one and each graft beside it — is a folder directly under
/// <c>%APPDATA%</c> (Roaming), the same store folder for folder. Verified on a
/// real install carrying the same <c>config.json</c>, <c>claude-code-sessions</c>,
/// <c>local-agent-mode-sessions</c> and <c>plan-usage-history.json</c> the Mac
/// layout has.
/// </summary>
public static class GraftPaths
{
    /// Redirected by the test suite so nothing it does can reach real profiles.
    /// The equivalent of the Mac build's <c>applicationSupportOverride</c>.
    public static string? ProfilesRootOverride { get; set; }

    /// The directory every profile is a child of. Grafts are named siblings of
    /// the default profile here, exactly as they are siblings of Claude under
    /// Application Support on the Mac.
    public static string ProfilesRoot =>
        ProfilesRootOverride
        ?? ClaudeInstallation.Discover()?.ProfilesRoot
        ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// The profile Claude opens with no --user-data-dir. Its absence of a flag
    /// is the only thing that marks it out, as on the Mac.
    public static string DefaultProfile => Path.Combine(ProfilesRoot, "Claude");

    /// This app's own state — the shortcut list, the caches, the diagnostics.
    /// Off in a folder of its own beside the profiles, never inside one.
    public static string OwnData => Path.Combine(ProfilesRootOverride
        ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeGraft");

    /// Redirected by the test suite, like <see cref="ProfilesRootOverride"/>.
    public static string? ClaudeProjectsOverride { get; set; }

    /// Where the command line files one transcript per session, under one
    /// directory per working directory. <c>~/.claude/projects</c> on the Mac;
    /// the user profile holds the same <c>.claude</c> on Windows.
    public static string ClaudeProjects =>
        ClaudeProjectsOverride
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    /// Chat history is <store>/<accountUuid>/<orgUuid>/ in both of these, the
    /// same nesting the Mac build depends on. A profile reads only the account
    /// it is signed into, so a graft is made one level deep — mapping this
    /// profile's <account>/<org> onto the source's active one.
    public static readonly string[] ChatStoreNames =
    {
        "claude-code-sessions",
        "local-agent-mode-sessions",
    };

    /// Sits beside the account directories keyed by organization, not account,
    /// so a walk that takes it for an account reports its organization folders
    /// as chat stores. Named here so both the sweep and the state report skip
    /// it, as on the Mac.
    public static readonly string[] NonAccountStoreItems = { "skills-plugin" };

    public static IEnumerable<string> ChatStores(string profile) =>
        ChatStoreNames.Select(name => Path.Combine(profile, name));

    public static string Profile(string folder) => Path.Combine(ProfilesRoot, folder);
}
