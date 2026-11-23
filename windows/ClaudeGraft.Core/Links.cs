namespace ClaudeGraft.Core;

/// <summary>
/// Where the app sends people: tips to the author and to the Windows port's
/// maintainer, and the code this build was made from.
/// </summary>
public static class Links
{
    public const string SupportAuthor = "https://ko-fi.com/aadityavmore";
    public const string SupportWindows = "https://ko-fi.com/snowtyler";
    public const string Source = "https://github.com/" + Updater.Repo;
    public const string WindowsPort = "https://github.com/snowtyler";
    public const string Site = "https://graft.aadityamore.com/";
    public const string Releases = "https://github.com/" + Updater.Repo + "/releases";
    public const string NewIssue = "https://github.com/" + Updater.Repo + "/issues/new";
    /// The developer's own address, as on the Mac.
    public const string Email = "aadityavmore@gmail.com";

    /// Already says which Graft and which Windows it comes from, since the
    /// first reply to a problem report otherwise asks for exactly that.
    public static string Mail(string version, string system) =>
        $"mailto:{Email}?subject={Uri.EscapeDataString("Claude Graft " + version)}"
        + $"&body={Uri.EscapeDataString($"\n\n—\nClaude Graft {version}, {system}")}";

    public static string Mail() => Mail(Updater.Shown, Environment.OSVersion.VersionString);
}
