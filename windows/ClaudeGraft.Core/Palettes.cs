namespace ClaudeGraft.Core;

/// The window's colour scheme. Graft is the Mac build's warm greys and Claude's clay;
/// Accent follows the Windows accent colour; the rest are Graft's greys turned toward a hue.
public enum AppPalette { Graft, Accent, Slate, Ocean, Forest, Plum, Rose }

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb Hex(string hex) => new(
        Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16));

    public (double H, double S, double L) Hsl()
    {
        double r = R / 255.0, g = G / 255.0, b = B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2;
        if (max == min) return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        var h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60, s, l);
    }

    public static Rgb FromHsl(double h, double s, double l)
    {
        l = Math.Clamp(l, 0, 1);
        s = Math.Clamp(s, 0, 1);
        double Channel(double t)
        {
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            t = ((t % 1) + 1) % 1;
            return t < 1 / 6.0 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6 : p;
        }
        var hue = h / 360;
        return new((byte)Math.Round(Channel(hue + 1 / 3.0) * 255), (byte)Math.Round(Channel(hue) * 255), (byte)Math.Round(Channel(hue - 1 / 3.0) * 255));
    }
}

/// Every colour one scheme gives one mode, light or dark.
public sealed record PaletteColors(
    Rgb Content, Rgb Sidebar, Rgb Selection, Rgb Header, Rgb Card, Rgb Divider,
    Rgb Button, Rgb ButtonHover, Rgb ButtonPressed, Rgb Field, Rgb Track,
    Rgb Accent, Rgb AccentLight1, Rgb AccentLight2, Rgb AccentLight3, Rgb AccentDark1, Rgb AccentDark2, Rgb AccentDark3);

public static class Palettes
{
    public static string Title(AppPalette palette) => palette switch
    {
        AppPalette.Accent => "System accent",
        _ => palette.ToString(),
    };

    private static readonly Rgb Clay = Rgb.Hex("#D97757");

    // Measured off the Mac build's window; the light set is the same warmth carried across.
    private static readonly PaletteColors GraftDark = new(
        Rgb.Hex("#2B2927"), Rgb.Hex("#2D2A27"), Rgb.Hex("#3B3835"), Rgb.Hex("#32302E"), Rgb.Hex("#312F2D"), Rgb.Hex("#3B3937"),
        Rgb.Hex("#454341"), Rgb.Hex("#4F4D4B"), Rgb.Hex("#3D3B39"), Rgb.Hex("#1F1E1D"), Rgb.Hex("#484644"),
        Clay, Rgb.Hex("#E08A6D"), Rgb.Hex("#E79E85"), Rgb.Hex("#EEB39F"), Rgb.Hex("#C6643F"), Rgb.Hex("#AB5333"), Rgb.Hex("#8E4428"));

    private static readonly PaletteColors GraftLight = GraftDark with
    {
        Content = Rgb.Hex("#F6F5F3"), Sidebar = Rgb.Hex("#ECEAE6"), Selection = Rgb.Hex("#DCD9D4"), Header = Rgb.Hex("#EFEDEA"),
        Card = Rgb.Hex("#FFFFFF"), Divider = Rgb.Hex("#E2DFDB"), Button = Rgb.Hex("#FFFFFF"), ButtonHover = Rgb.Hex("#F2F0ED"),
        ButtonPressed = Rgb.Hex("#E7E4E0"), Field = Rgb.Hex("#FFFFFF"), Track = Rgb.Hex("#DDDAD6"),
    };

    private static (double Hue, Rgb Accent) Hue(AppPalette palette) => palette switch
    {
        AppPalette.Slate => (220, Rgb.Hex("#6E8BBF")),
        AppPalette.Ocean => (205, Rgb.Hex("#2F8FD8")),
        AppPalette.Forest => (140, Rgb.Hex("#3FA66A")),
        AppPalette.Plum => (285, Rgb.Hex("#9D6BD1")),
        AppPalette.Rose => (345, Rgb.Hex("#D9668A")),
        _ => (0, Clay),
    };

    /// The colours for one scheme and mode. Accent needs the system's accent colour,
    /// which only the app can read; without one it falls back to Graft's.
    public static PaletteColors For(AppPalette palette, bool dark, Rgb? systemAccent = null)
    {
        var graft = dark ? GraftDark : GraftLight;
        if (palette == AppPalette.Graft || palette == AppPalette.Accent && systemAccent is null) return graft;
        var (hue, accent) = palette == AppPalette.Accent ? (systemAccent!.Value.Hsl().H, systemAccent.Value) : Hue(palette);
        // Graft's greys keep their lightness and take the hue, faintly, so each surface
        // stands where it stood against the others.
        var tint = dark ? 0.10 : 0.16;
        Rgb Turn(Rgb c) { var (_, _, l) = c.Hsl(); return Rgb.FromHsl(hue, tint, l); }
        var (h, s, l) = accent.Hsl();
        Rgb Shade(double by) => Rgb.FromHsl(h, s, l + by);
        return new(Turn(graft.Content), Turn(graft.Sidebar), Turn(graft.Selection), Turn(graft.Header), Turn(graft.Card), Turn(graft.Divider),
            Turn(graft.Button), Turn(graft.ButtonHover), Turn(graft.ButtonPressed), Turn(graft.Field), Turn(graft.Track),
            accent, Shade(0.06), Shade(0.12), Shade(0.18), Shade(-0.06), Shade(-0.12), Shade(-0.18));
    }
}
