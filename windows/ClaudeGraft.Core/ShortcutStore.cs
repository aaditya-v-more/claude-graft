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
    public string? LoadError { get; private set; }

    private static string File => Path.Combine(GraftPaths.OwnData, "shortcuts.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public ShortcutStore() => Load();

    public void Load()
    {
        LoadError = null;
        try
        {
            Shortcuts = JsonSerializer.Deserialize<List<Shortcut>>(
                System.IO.File.ReadAllBytes(File), Options) ?? throw new IOException("Invalid shortcut data.");
            foreach (var s in Shortcuts)
                if (s is null || Graft.ValidateFolder(s.Folder) is not null || Graft.ValidateWindowsName(s.Name) is not null
                    || s.Source is null || !Enum.IsDefined(s.Source.Kind)) throw new IOException("Invalid shortcut data.");
        }
        catch (FileNotFoundException) { Shortcuts = new(); }
        catch (DirectoryNotFoundException) { Shortcuts = new(); }
        catch { Shortcuts = new(); LoadError = "The shortcut list could not be read. It has been left untouched."; }
    }

    public void Save()
    {
        using var stateLock = StateLock.Acquire();
        if (LoadError is not null) throw new IOException(LoadError);
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            AtomicWrite.Bytes(File, JsonSerializer.SerializeToUtf8Bytes(Shortcuts, Options));
        foreach (var shortcut in Shortcuts) ShortcutManifest.Save(shortcut, ConfigFor(shortcut));
    }

    public Shortcut? Get(Guid id) => Shortcuts.FirstOrDefault(s => s.Id == id);

    /// Where a shortcut actually reads its chats from, following one hop.
    public string? SourceDir(Shortcut shortcut) => shortcut.Source.Kind switch
    {
        SourceKind.Own => null,
        SourceKind.Main => GraftPaths.DefaultProfile,
        SourceKind.Shortcut => shortcut.Source.ShortcutId is Guid id && Get(id) is Shortcut source
            ? source.ProfileDir : throw new IOException("The selected chat source no longer exists."),
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
                default: throw new IOException("The shortcut has an unknown chat source.");
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

    /// Numbering starts at two, since the stock app is the first one.
    public string UniqueName(string @base = "Claude")
    {
        var n = 2;
        var candidate = $"{@base} {n}";
        while (Shortcuts.Any(s => s.Name == candidate)) candidate = $"{@base} {++n}";
        return candidate;
    }

    /// The GraftConfig a shortcut launches with — profile plus resolved source.
    public GraftConfig ConfigFor(Shortcut shortcut) => new()
    {
        ProfileDir = shortcut.ProfileDir,
        SourceDir = SourceDir(shortcut),
    };

    // MARK: - Editing

    public void Add(Shortcut shortcut)
    {
        Shortcuts.Add(shortcut);
        Save();
    }

    public void Update(Shortcut shortcut)
    {
        var i = Shortcuts.FindIndex(s => s.Id == shortcut.Id);
        if (i >= 0) Shortcuts[i] = shortcut; else Shortcuts.Add(shortcut);
        Save();
    }

    /// Removes the shortcut. The profile folder — a login and a chat history —
    /// only goes when explicitly asked for, and never while another shortcut
    /// still points at it. Returns a message when the profile could not be
    /// removed. (The desktop .lnk it installed is the installer's concern, not
    /// yet ported.)
    public string? Delete(Guid id, bool deletingProfile = false)
    {
        if (Get(id) is not Shortcut shortcut) return null;
        var sharedWithAnother = Shortcuts.Any(s => s.Id != id && Fs.SamePath(s.ProfileDir, shortcut.ProfileDir));
        var isSource = Shortcuts.Any(s => s.Id != id && s.Source.Kind == SourceKind.Shortcut && s.Source.ShortcutId == id);

        string? problem = null;
        if (deletingProfile)
        {
            if (sharedWithAnother || isSource)
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
        System.IO.File.Delete(ShortcutManifest.FileFor(id));
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
