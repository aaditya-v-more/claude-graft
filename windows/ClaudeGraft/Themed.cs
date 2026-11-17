using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace ClaudeGraft;

/// Theme brushes for code. Application.Current.Resources answers in the mode the app
/// launched in and never again, so code is handed one brush per key, recoloured here
/// whenever the mode changes.
internal static class Themed
{
    private static readonly Dictionary<string, SolidColorBrush> Brushes = new();

    public static Brush Brush(string key)
    {
        if (Brushes.TryGetValue(key, out var held)) return held;
        if (Colour(key) is not { } colour) return (Brush)Application.Current.Resources[key];
        return Brushes[key] = new SolidColorBrush(colour.Color) { Opacity = colour.Opacity };
    }

    // XAML reaches the same brushes as Graft<key>: a {ThemeResource} inside an item template
    // kept the mode it was drawn in when the mode changed from Settings.
    private static readonly string[] Published =
        ["TextFillColorPrimaryBrush", "TextFillColorSecondaryBrush", "TextFillColorTertiaryBrush", "SystemFillColorCautionBrush"];

    public static void Publish()
    {
        foreach (var key in Published) Application.Current.Resources["Graft" + key] = Brush(key);
    }

    public static void Refresh()
    {
        foreach (var (key, brush) in Brushes)
            if (Colour(key) is { } colour)
            {
                brush.Color = colour.Color;
                brush.Opacity = colour.Opacity;
            }
    }

    // The colour a brush is named after before the brush: WinUI's other-mode brushes carry the
    // launch mode's colour for some keys, while the colours themselves are right for each mode.
    private static (Windows.UI.Color Color, double Opacity)? Colour(string key)
    {
        var dark = Appearance.ToElementTheme(App.Settings.Theme) == ElementTheme.Dark;
        string[] themes = dark ? ["Dark", "Default"] : ["Light"];
        if (key.EndsWith("Brush") && Search(Application.Current.Resources, themes, key[..^5]) is Windows.UI.Color plain)
            return (plain, 1);
        return Search(Application.Current.Resources, themes, key) is SolidColorBrush brush ? (brush.Color, brush.Opacity) : null;
    }

    // A dictionary's own entries before its merged ones, and later merged ones first, as XAML looks.
    private static object? Search(ResourceDictionary root, string[] themes, string key)
    {
        foreach (var theme in themes)
            if (root.ThemeDictionaries.TryGetValue(theme, out var t) && t is ResourceDictionary d && d.TryGetValue(key, out var v))
                return v;
        for (var i = root.MergedDictionaries.Count - 1; i >= 0; i--)
            if (Search(root.MergedDictionaries[i], themes, key) is { } found) return found;
        return null;
    }
}
