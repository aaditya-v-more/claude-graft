namespace ClaudeGraft.Core;

/// <summary>Why a profile could not be deleted.</summary>
public enum ProfileError
{
    MainProfile,
    OutsideProfilesRoot,
    Running,
    InUse,
}

public sealed class ProfileException : Exception
{
    public ProfileError Reason { get; }
    public ProfileException(ProfileError reason) : base(Describe(reason)) => Reason = reason;

    private static string Describe(ProfileError reason) => reason switch
    {
        ProfileError.MainProfile => "That folder belongs to Claude itself and will not be deleted.",
        ProfileError.OutsideProfilesRoot => "Only folders directly inside the Claude data folder can be deleted.",
        ProfileError.Running => "Claude is still running on this profile. Quit it first.",
        ProfileError.InUse => "Some of its files are still in use, so not all of it could be deleted. "
                              + "Close every Claude window, wait a moment and delete it again.",
        _ => "This profile cannot be deleted.",
    };
}

public static partial class Graft
{
    /// <summary>
    /// Bring a profile's storage in line with its configuration — the entry
    /// point a launcher and the window both reach the graft through. A source
    /// grafts onto it; no source undoes the graft, so a shortcut sent back to its
    /// own chats is put right on its next launch.
    ///
    /// Runs on every launch, which is why the graft it drives is built to be idempotent:
    /// the first pass stashes the profile's own chats and seeds them back, and
    /// every pass after leaves the merge where it is rather than stashing it away
    /// again.
    /// </summary>
    public static void Apply(GraftConfig config) =>
        StateLock.Run(() => ApplyUnlocked(config));

    private static void ApplyUnlocked(GraftConfig config)
    {
        var profile = config.ProfileDir;
        Directory.CreateDirectory(profile);
        if (!string.IsNullOrEmpty(config.SourceDir)) GraftInto(config.SourceDir!, profile);
        else Ungraft(profile);
    }

    /// <summary>
    /// Deletes a profile's data. Guarded on every side: the folder has to sit
    /// directly in the profiles root, must not be Claude's own profile, and must
    /// not be in use. Losing one means losing a login and its chats.
    ///
    /// <paramref name="isRunning"/> defaults to the real process check; a test
    /// supplies its own so it never depends on what is actually running.
    /// </summary>
    public static void DeleteProfile(string profile, Func<string, bool>? isRunning = null)
    {
        var parent = Path.GetDirectoryName(profile);
        if (parent is null || !Fs.SamePath(parent, GraftPaths.ProfilesRoot)
            || Path.GetFileName(profile).Length == 0)
            throw new ProfileException(ProfileError.OutsideProfilesRoot);
        if (Fs.SamePath(profile, GraftPaths.DefaultProfile))
            throw new ProfileException(ProfileError.MainProfile);
        if ((isRunning ?? ClaudeProcesses.IsRunning)(profile))
            throw new ProfileException(ProfileError.Running);
        if (!Fs.IsDirectory(profile)) return;
        try { Directory.Delete(profile, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Claude marks some of what it writes read-only, and its helpers can
            // hold a file for a moment after the window closes. Links are skipped
            // so clearing a flag never reaches through into the source profile.
            ClearReadOnly(profile);
            try { Directory.Delete(profile, recursive: true); }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException)
            {
                Diagnostics.Note("profile.deleteFailed", new Dictionary<string, object?>
                {
                    ["profile"] = Path.GetFileName(profile), ["error"] = again.GetType().Name + ": " + again.Message,
                });
                throw new ProfileException(ProfileError.InUse);
            }
        }
        ForgetMirrors(profile);
    }

    private static void ClearReadOnly(string profile)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
        };
        foreach (var file in Directory.EnumerateFiles(profile, "*", options))
            try
            {
                var attributes = File.GetAttributes(file);
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
            catch { }
    }
}
