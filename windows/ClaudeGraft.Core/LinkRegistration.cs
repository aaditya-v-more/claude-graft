using Microsoft.Win32;

namespace ClaudeGraft.Core;

/// <summary>
/// Offers Graft to Windows as a handler for claude:// links. This only makes it
/// a candidate: Windows lets nothing make itself the default, so the person
/// picks it in Default apps. Claude's own registration is never touched.
/// </summary>
public static class LinkRegistration
{
    public const string ProgId = "ClaudeGraft.Link";
    public const string ApplicationName = "Claude Graft";
    private const string Capabilities = @"Software\ClaudeGraft\Capabilities";

    /// Written at install and again at every start, since an update moves the exe.
    public static void Write(RegistryKey currentUser, string exe)
    {
        using (var progId = currentUser.CreateSubKey(@"Software\Classes\" + ProgId))
        {
            progId.SetValue("", "Claude link");
            progId.SetValue("URL Protocol", "");
            using var icon = progId.CreateSubKey("DefaultIcon");
            icon.SetValue("", $"\"{exe}\",0");
            // Windows' picker names a handler by these before it falls back to the exe's own file name.
            using (var application = progId.CreateSubKey("Application"))
            {
                application.SetValue("ApplicationName", ApplicationName);
                application.SetValue("ApplicationIcon", $"\"{exe}\",0");
            }
            using var open = progId.CreateSubKey(@"shell\open");
            open.SetValue("FriendlyAppName", ApplicationName);
            using var command = open.CreateSubKey("command");
            command.SetValue("", $"\"{exe}\" --open-link \"%1\"");
        }
        using (var capabilities = currentUser.CreateSubKey(Capabilities))
        {
            capabilities.SetValue("ApplicationName", ApplicationName);
            capabilities.SetValue("ApplicationDescription", "Sends a Claude sign-in to the profile that started it.");
            using var urls = capabilities.CreateSubKey("URLAssociations");
            urls.SetValue("claude", ProgId);
        }
        using var registered = currentUser.CreateSubKey(@"Software\RegisteredApplications");
        registered.SetValue(ApplicationName, Capabilities);
    }

    /// What Graft opens to have Windows ask which app takes Claude links: it
    /// arrives at whichever is chosen, and Graft lets it go unopened.
    public const string Probe = "claude://claude-graft/handler-chosen";

    /// The shell may hand it back with its case or a trailing slash changed.
    public static bool IsProbe(string? link) =>
        link is not null && link.TrimEnd('/').Equals(Probe, StringComparison.OrdinalIgnoreCase);

    private const string Seen = @"Software\Microsoft\Windows\CurrentVersion\ApplicationAssociationToasts";

    /// Windows offers "Set default" only for a handler it has not shown before,
    /// and remembers each one here; forgetting Graft's own entry, and nothing
    /// else, makes it new again without touching the choice already made.
    public static void MarkUnseen(RegistryKey currentUser)
    {
        using var seen = currentUser.OpenSubKey(Seen, writable: true);
        seen?.DeleteValue(ProgId + "_claude", throwOnMissingValue: false);
    }

    public static void Remove(RegistryKey currentUser)
    {
        MarkUnseen(currentUser);
        currentUser.DeleteSubKeyTree(@"Software\Classes\" + ProgId, throwOnMissingSubKey: false);
        currentUser.DeleteSubKeyTree(Capabilities, throwOnMissingSubKey: false);
        using var registered = currentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true);
        registered?.DeleteValue(ApplicationName, throwOnMissingValue: false);
        using var own = currentUser.OpenSubKey(@"Software\ClaudeGraft");
        if (own is { SubKeyCount: 0, ValueCount: 0 }) currentUser.DeleteSubKey(@"Software\ClaudeGraft", throwOnMissingSubKey: false);
    }
}
