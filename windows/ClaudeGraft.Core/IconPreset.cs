namespace ClaudeGraft.Core;

/// <summary>
/// The look a shortcut's icon takes: Claude's own, recoloured, or recoloured
/// with a badge. Spelled as the Mac build spells it, so a list carried between
/// the two reads the same.
/// </summary>
public static class IconPreset
{
    public const string Original = "original";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Original, "blue", "violet", "green", "red", "pink", "teal", "graphite",
        "work", "personal", "code", "research",
    };

    // Claude's own icon is the main profile's, so it is never handed out; plain
    // colours go first, since a badged one repeats a colour.
    public static readonly IReadOnlyList<string> HandedOut = All.Skip(1).ToArray();

    /// A value this build does not know, from a newer one or a hand edit, wears
    /// Claude's own icon rather than failing the whole list.
    public static string Normalize(string? preset) =>
        preset is not null && All.Contains(preset) ? preset : Original;

    /// The first colour nothing wears yet; once every one is in use, the least
    /// used. Two Claudes that look alike in the taskbar are what this prevents.
    public static string Next(IEnumerable<string> taken)
    {
        var used = taken.Select(Normalize).ToList();
        return HandedOut.FirstOrDefault(p => !used.Contains(p))
            ?? HandedOut.MinBy(p => used.Count(u => u == p))!;
    }

    public static string Title(string preset)
    {
        var p = Normalize(preset);
        return char.ToUpperInvariant(p[0]) + p[1..];
    }

    /// The hue rotation in radians, the saturation, and the badge's glyph, or
    /// null where the preset has none.
    public static (double? HueAngle, double Saturation, string? Badge) Style(string preset) => Normalize(preset) switch
    {
        "red" => (-0.45, 1, null),
        "pink" => (-0.95, 1, null),
        "research" => (-0.95, 1, "research"),
        "blue" => (-2.8, 1, null),
        "work" => (-2.8, 1, "work"),
        "violet" => (-1.75, 1, null),
        "personal" => (-1.75, 1, "personal"),
        "teal" => (2.8, 1, null),
        "green" => (2.25, 1, null),
        "code" => (2.25, 1, "code"),
        "graphite" => (null, 0, null),
        _ => (null, 1, null),
    };
}
