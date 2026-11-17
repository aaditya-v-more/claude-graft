using System.Text.Json;

namespace ClaudeGraft.Core;

/// <summary>
/// The list of shortcuts this app keeps, in ClaudeGraft/shortcuts.json — the one
/// state file other processes read, so every write is atomic: a launcher reads
/// it to learn which profiles are this app's doing, and a plain write truncates
/// before it fills.
/// </summary>
public sealed class ShortcutStore
{
    public List<Shortcut> Shortcuts { get; private set; } = new();

    private static string File => Path.Combine(GraftPaths.OwnData, "shortcuts.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public ShortcutStore() => Load();

    /// Set when shortcuts.json is there but will not read. The list is then
    /// empty for display, and saving it would write that emptiness over every
    /// shortcut the person has, so <see cref="Save"/> refuses.
    public string? LoadError
    {
        get => _loadError ?? (Staging.ListUnreadable ? "shortcuts.json could not be read (staged from the debug window)." : null);
        private set => _loadError = value;
    }

    private string? _loadError;

    public void Load()
    {
        try
        {
            Shortcuts = StateFile.Read<List<Shortcut>>(File, Options);
            LoadError = null;
        }
        catch (UnreadableStateException e)
        {
            Shortcuts = new();
            LoadError = e.Message;
            Diagnostics.Note("shortcuts.unreadable", new Dictionary<string, object?> { ["error"] = e.InnerException?.Message });
        }
    }

    public void Save()
    {
        EnsureWritable();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            AtomicWrite.Bytes(File, JsonSerializer.SerializeToUtf8Bytes(Shortcuts, Options));
        }
        catch { }
    }

    public Shortcut? Get(Guid id) => Shortcuts.FirstOrDefault(s => s.Id == id);

    /// Where a shortcut actually reads its chats from, following one hop.
    public string? SourceDir(Shortcut shortcut) => shortcut.Source.Kind switch
    {
        SourceKind.Own => null,
        SourceKind.Main => GraftPaths.DefaultProfile,
        SourceKind.Shortcut => shortcut.Source.ShortcutId is Guid id ? Get(id)?.ProfileDir : null,
        _ => null,
    };

    /// The profile whose chat store a shortcut ends up reading, following the
    /// chain of sources to its end.
    public string ChatRoot(Shortcut shortcut)
    {
        var seen = new HashSet<Guid>();
        var current = shortcut;
        while (true)
        {
            switch (current.Source.Kind)
            {
                case SourceKind.Own: return current.ProfileDir;
                case SourceKind.Main: return GraftPaths.DefaultProfile;
                case SourceKind.Shortcut:
                    if (current.Source.ShortcutId is not Guid id || !seen.Add(current.Id) || Get(id) is not Shortcut next)
                        return current.ProfileDir;
                    current = next;
                    break;
            }
        }
    }

    public string Label(ShortcutSource source) => source.Kind switch
    {
        SourceKind.Own => "Its own chats",
        SourceKind.Main => "Main Claude",
        SourceKind.Shortcut => source.ShortcutId is Guid id ? Get(id)?.Name ?? "Removed shortcut" : "Removed shortcut",
        _ => "Main Claude",
    };

    /// What to call a profile on screen. Claude's own has no shortcut standing
    /// for it, and a folder name is not what anybody calls either of them.
    public string NameOfProfile(string profile)
    {
        if (Fs.SamePath(profile, GraftPaths.DefaultProfile)) return "Claude";
        return Shortcuts.FirstOrDefault(s => Fs.SamePath(s.ProfileDir, profile))?.Name
               ?? Path.GetFileName(profile.TrimEnd(Path.DirectorySeparatorChar));
    }

    /// Numbering starts at two, since the stock app is the first one.
    public string UniqueName(string @base = "Claude")
    {
        var n = 2;
        var candidate = $"{@base} {n}";
        while (Shortcuts.Any(s => s.Name == candidate)) candidate = $"{@base} {++n}";
        return candidate;
    }

    /// The colour a shortcut added now is handed: one no other shortcut wears.
    public string NextIconPreset() => IconPreset.Next(Shortcuts.Select(s => s.IconPreset));

    /// The GraftConfig a shortcut launches with — profile plus resolved source.
    public GraftConfig ConfigFor(Shortcut shortcut) => new()
    {
        ProfileDir = shortcut.ProfileDir,
        SourceDir = SourceDir(shortcut),
    };

    // MARK: - Editing

    private void EnsureWritable()
    {
        if (LoadError is not null) throw new UnreadableStateException(File, null);
    }

    public void Add(Shortcut shortcut)
    {
        EnsureWritable();
        Shortcuts.Add(shortcut);
        Save();
    }

    public void Update(Shortcut shortcut)
    {
        EnsureWritable();
        var i = Shortcuts.FindIndex(s => s.Id == shortcut.Id);
        if (i >= 0) Shortcuts[i] = shortcut; else Shortcuts.Add(shortcut);
        Save();
    }

    /// Removes the shortcut. The profile folder — a login and a chat history —
    /// only goes when explicitly asked for, and never while another shortcut
    /// still points at it. Returns a message when the profile could not be
    /// removed. (The desktop .lnk it installed is the installer's concern, not
    /// yet ported.)
    public string? Delete(Guid id, bool deletingProfile = false) =>
        // Held across the whole delete, so a busy lock refuses before the folder goes rather than after.
        StateLock.Run(() => DeleteUnlocked(id, deletingProfile));

    private string? DeleteUnlocked(Guid id, bool deletingProfile)
    {
        EnsureWritable();
        if (Get(id) is not Shortcut shortcut) return null;
        var sharedWithAnother = Shortcuts.Any(s => s.Id != id && s.Folder == shortcut.Folder);

        string? problem = null;
        if (deletingProfile)
        {
            if (sharedWithAnother)
                problem = "The profile folder was kept: another shortcut still uses it.";
            else
                try { Graft.DeleteProfile(shortcut.ProfileDir, ClaudeProcesses.IsRunning); }
                catch (ProfileException e) { problem = e.Message; }
        }

        // Nothing runs this shortcut's launcher again to undo its mirroring, so a
        // pair left behind goes on syncing whenever any other profile opens. The
        // copies stay — they are chats, and the folder was kept — but the two
        // folders stop being squared up.
        if (!sharedWithAnother) Graft.ForgetMirrors(shortcut.ProfileDir);
        Shortcuts.RemoveAll(s => s.Id == id);

        // Anything that borrowed from it would silently fall back to its own
        // chats, ungrafting on the next launch without a word.
        foreach (var other in Shortcuts.Where(s => s.Source.Kind == SourceKind.Shortcut && s.Source.ShortcutId == id))
            other.Source = ShortcutSource.Own;
        Save();
        return problem;
    }

    /// Sources that will not form a loop back to <paramref name="shortcut"/>.
    public List<ShortcutSource> AvailableSources(Shortcut shortcut)
    {
        var options = new List<ShortcutSource> { ShortcutSource.Own, ShortcutSource.Main };
        foreach (var other in Shortcuts.Where(s => s.Id != shortcut.Id))
            if (!LeadsBack(other.Id, shortcut.Id)) options.Add(ShortcutSource.Of(other.Id));
        return options;
    }

    private bool LeadsBack(Guid start, Guid target, int depth = 0)
    {
        if (depth > Shortcuts.Count || Get(start) is not Shortcut node) return false;
        if (node.Source.Kind == SourceKind.Shortcut && node.Source.ShortcutId is Guid next)
            return next == target || LeadsBack(next, target, depth + 1);
        return false;
    }
}
