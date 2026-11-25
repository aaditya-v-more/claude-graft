using System.Text.RegularExpressions;

namespace ClaudeGraft.Core;

/// <summary>The arithmetic behind the taskbar strip's steady width and its short animations.</summary>
public static class StripFace
{
    /// The usage text at its widest, so the strip keeps one width as the figure climbs.
    public static string WidestUsage(string usage) => Regex.Replace(usage, @"\d+", "100");

    /// True when the figure went down, as when a usage window resets, so it slides the other way.
    public static bool UsageFell(string? before, string? now)
    {
        var from = FirstNumber(before);
        var to = FirstNumber(now);
        return from is { } a && to is { } b && b < a;
    }

    private static double? FirstNumber(string? text)
    {
        var m = text is null ? null : Regex.Match(text, @"\d+(\.\d+)?");
        return m is { Success: true } ? double.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    public static double OutCubic(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);

    /// Scale of the signal dot as it appears: out to 1.3 and settling back to 1.
    public static double Pop(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        const double peak = 0.6;
        return t < peak ? 1.3 * OutCubic(t / peak) : 1.3 - 0.3 * OutCubic((t - peak) / (1 - peak));
    }
}
