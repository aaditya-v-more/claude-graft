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
    [JsonPropertyName("palette")] public AppPalette Palette { get; set; } = AppPalette.Graft;
    [JsonPropertyName("startHidden")] public bool StartHidden { get; set; } = true;

    /// Whether the app keeps the main Claude's five-hour window open on its own.
    /// The grafted profiles carry this on their shortcut; the main one has no
    /// shortcut to hold it, so it lives here beside the rest of the preferences.
    [JsonPropertyName("keepMainWarm")] public bool KeepMainWarm { get; set; }

    /// Graft's own updates wait for a press instead of installing themselves.
    /// Absent reads as off, so every machine that updates into this keeps
    /// installing on its own as before.
    [JsonPropertyName("holdUpdates")] public bool HoldUpdates { get; set; }

    /// The Claude in front, its usage and the conversation on screen, on the
    /// taskbar beside the notification area. Off unless turned on: the strip
    /// follows a taskbar Windows gives apps no place in.
    [JsonPropertyName("taskbarStrip")] public bool TaskbarStrip { get; set; }

    /// A sound when a Claude in the background finishes a turn or waits on a
    /// permission. Off unless turned on: it puts an entry in Claude Code's own settings.
    [JsonPropertyName("chime")] public bool Chime { get; set; }

    /// Shared usage was explained, and agreed to, before the first account was added.
    [JsonPropertyName("sharingExplained")] public bool SharingExplained { get; set; }

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
