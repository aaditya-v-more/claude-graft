using System.Text.RegularExpressions;

namespace ClaudeGraft.Core;

/// <summary>The arithmetic behind the taskbar strip's steady width and its short animations.</summary>
public static class StripFace
{
    /// The usage text at its widest, so the strip keeps one width as the figure climbs:
    /// two digits, and a third only once the figure has one.
    public static string WidestUsage(string usage) =>
        Regex.Replace(usage, @"\d+", m => new string('8', Math.Max(2, m.Value.Length)));

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

    /// How far a slide travels: half the strip, so the old part is mostly gone before it clears the edge.
    public static double Travel(double height) => height / 2;

    /// The old part's opacity, spent by 60% of the slide so old and new are never both strong.
    public static double OutgoingOpacity(double t) => 1 - Math.Clamp(t / 0.6, 0, 1);

    public static double IncomingOpacity(double t) => Math.Clamp(t, 0, 1);

    public static double Lerp(double from, double to, double t) => from + (to - from) * Math.Clamp(t, 0, 1);

    /// What a title's end pixel keeps of its colour, `i` pixels in from the edge of a feather
    /// `edge` wide; strength 0 leaves it whole, so feathering can be eased in and out.
    public static double FeatherKeep(int i, int edge, double strength) =>
        Lerp(1, Math.Clamp((i + 0.5) / edge, 0, 1), strength);
}
