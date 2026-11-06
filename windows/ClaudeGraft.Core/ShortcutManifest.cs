using System.Text.Json;

namespace ClaudeGraft.Core;

public sealed record ShortcutManifest(int Version, Guid Id, string Name, GraftConfig Config)
{
    public static string FileFor(Guid id) => Path.Combine(GraftPaths.OwnData, "shortcuts", id.ToString("N"), "graft.json");

    public static void Save(Shortcut shortcut, GraftConfig config)
    {
        var path = FileFor(shortcut.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicWrite.Bytes(path, JsonSerializer.SerializeToUtf8Bytes(new ShortcutManifest(1, shortcut.Id, shortcut.Name, config)));
    }

    public static GraftConfig Read(string path)
    {
        var manifest = JsonSerializer.Deserialize<ShortcutManifest>(File.ReadAllBytes(path));
        if (manifest is null || manifest.Version != 1 || manifest.Config is null)
            throw new IOException("The shortcut's configuration is missing or unsupported.");
        Graft.ValidateProfilePath(manifest.Config.ProfileDir);
        if (manifest.Config.SourceDir is not null) Graft.ValidateProfilePath(manifest.Config.SourceDir, allowDefault: true);
        return manifest.Config;
    }
}
