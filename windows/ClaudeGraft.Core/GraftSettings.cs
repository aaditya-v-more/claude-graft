using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeGraft.Core;

/// How the app should look. System follows Windows; the other two pin it.
public enum AppTheme { System, Light, Dark }

/// The window's backdrop. Mica is the default a modern Windows app wears; the
/// rest are there for taste and for machines where a translucent one costs too
/// much. None is an opaque themed surface with no transparency at all.
public enum BackdropMaterial { Mica, MicaAlt, Acrylic, None }

/// <summary>
/// The app's own preferences — theme, backdrop, and whether it comes up hidden
/// in the tray. Kept beside the shortcut list in this app's own data, and read
/// the same forgiving way: a file from an older version missing a key loads with
/// the default for it rather than failing.
///
/// Whether it starts with Windows is deliberately not here: that is a shortcut
/// in the Startup folder, and reading the folder is the truth, so a copy in this
/// file could only drift from it.
/// </summary>
public sealed class GraftSettings
{
    [JsonPropertyName("theme")] public AppTheme Theme { get; set; } = AppTheme.System;
    [JsonPropertyName("backdrop")] public BackdropMaterial Backdrop { get; set; } = BackdropMaterial.Mica;
    [JsonPropertyName("startHidden")] public bool StartHidden { get; set; } = true;

    private static string SettingsFile => Path.Combine(GraftPaths.OwnData, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static GraftSettings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<GraftSettings>(File.ReadAllBytes(SettingsFile), Options)
                   ?? new GraftSettings();
        }
        catch { return new GraftSettings(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            AtomicWrite.Bytes(SettingsFile, JsonSerializer.SerializeToUtf8Bytes(this, Options));
        }
        catch { }
    }
}
