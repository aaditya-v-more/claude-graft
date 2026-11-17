using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace ClaudeGraft;

/// Puts the chosen theme into the brushes the controls draw with. Each is replaced, and
/// `Refresh` then has every control look its brushes up again.
internal static class Theming
{
    private static readonly UISettings System = new();
    private static bool _watching;

    // Which colour of a scheme each brush wears; the brushes App.xaml and WinUI define.
    private static readonly (string Key, Func<PaletteColors, Rgb> Slot, Surface Kind)[] Surfaces =
    [
        ("GraftHeaderBrush", c => c.Header, Surface.Behind),
        ("GraftSidebarBrush", c => c.Sidebar, Surface.Behind),
        ("NavigationViewDefaultPaneBackground", c => c.Sidebar, Surface.Behind),
        ("NavigationViewExpandedPaneBackground", c => c.Sidebar, Surface.Behind),
        ("ApplicationPageBackgroundThemeBrush", c => c.Content, Surface.Page),
        ("NavigationViewContentBackground", c => c.Content, Surface.Page),
        ("LayerFillColorDefaultBrush", c => c.Content, Surface.Page),
        ("GraftTrackBrush", c => c.Track, Surface.Solid),
        ("NavigationViewContentGridBorderBrush", c => c.Divider, Surface.Edge),
        ("DividerStrokeColorDefaultBrush", c => c.Divider, Surface.Solid),
        ("CardBackgroundFillColorDefaultBrush", c => c.Card, Surface.Solid),
        ("CardBackgroundFillColorSecondaryBrush", c => c.Header, Surface.Solid),
    ];

    // Edge: the lines framing the page, kept over a backdrop too, as WinUI keeps them.
    private enum Surface { Solid, Behind, Page, Edge }

    private const byte PageAlpha = 0x40;

    public static void Apply(GraftSettings settings)
    {
        var accent = SystemAccent();
        var behind = settings.Backdrop != BackdropMaterial.None;
        var resources = Application.Current.Resources;
        foreach (var (theme, dark) in new[] { ("Default", true), ("Dark", true), ("Light", false) })
        {
            var colors = Palettes.For(settings.Palette, dark, accent);
            // Surfaces only in Graft's own dictionary: WinUI's brushes are shared between many
            // keys, and recolouring one there repainted unrelated controls, the sidebar's hover among them.
            if (Own(resources)?.ThemeDictionaries.TryGetValue(theme, out var own) == true && own is ResourceDictionary ours)
                foreach (var (key, slot, kind) in Surfaces)
                    // Over Mica or Acrylic the sidebar and title bar let the material through,
                    // and the page wears only a light wash of its colour over it.
                    Paint(ours, key, slot(colors), kind switch
                    {
                        Surface.Behind when behind => 0,
                        Surface.Page when behind => PageAlpha,
                        _ => 0xFF,
                    });
            // The accent goes in by its app-wide colours alone: a brush written into WinUI's own
            // dictionaries froze every built-in control in the mode it was drawn in.
            if (theme == "Default")
            {
                colors = Palettes.For(settings.Palette, true, accent);
                resources["SystemAccentColor"] = Color(colors.Accent, 0xFF);
                resources["SystemAccentColorLight1"] = Color(colors.AccentLight1, 0xFF);
                resources["SystemAccentColorLight2"] = Color(colors.AccentLight2, 0xFF);
                resources["SystemAccentColorLight3"] = Color(colors.AccentLight3, 0xFF);
                resources["SystemAccentColorDark1"] = Color(colors.AccentDark1, 0xFF);
                resources["SystemAccentColorDark2"] = Color(colors.AccentDark2, 0xFF);
                resources["SystemAccentColorDark3"] = Color(colors.AccentDark3, 0xFF);
            }
        }
        if (!_watching)
        {
            _watching = true;
            var ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            // A new Windows accent or mode reaches a theme that follows it without a restart.
            System.ColorValuesChanged += (_, _) => ui?.TryEnqueue(() =>
            {
                if (App.Settings.Palette != AppPalette.Accent && App.Settings.Theme != AppTheme.System) return;
                App.LooksChanged();
            });
        }
    }

    /// How much of the selected sidebar item's fill shows: on the icon rail only its bar marks it.
    public static void SelectionFill(double shown)
    {
        foreach (var theme in new[] { "Default", "Dark", "Light" })
            if (Own(Application.Current.Resources)?.ThemeDictionaries.TryGetValue(theme, out var own) == true && own is ResourceDictionary ours
                && ours.TryGetValue("NavigationViewItemBackgroundSelected", out var b) && b is SolidColorBrush brush)
                brush.Opacity = shown;
    }
    private static Rgb SystemAccent()
    {
        var c = System.GetColorValue(UIColorType.Accent);
        return new Rgb(c.R, c.G, c.B);
    }

    private static Color Color(Rgb c, byte alpha) => Windows.UI.Color.FromArgb(alpha, c.R, c.G, c.B);

    // A new brush rather than a recoloured one: a control's hover fade runs from the colour
    // its brush was first drawn with, so a recoloured brush flashed its old colour on hover.
    // An accent brush keeps the opacity WinUI gave it.
    private static void Paint(ResourceDictionary dictionary, string key, Rgb color, byte? alpha)
    {
        if (dictionary.TryGetValue(key, out var value) && value is SolidColorBrush brush)
            dictionary[key] = new SolidColorBrush(Color(color, alpha ?? brush.Color.A));
    }

    // The dictionary App.xaml merges after WinUI's, which holds Graft's colours.
    private static ResourceDictionary? Own(ResourceDictionary root) =>
        root.MergedDictionaries.LastOrDefault(d => d.ThemeDictionaries.TryGetValue("Default", out var t) && t is ResourceDictionary r && r.ContainsKey("GraftSidebarBrush"));

    /// Has an element re-read its theme brushes, which it otherwise keeps from when it was drawn.
    public static void Refresh(FrameworkElement root, ElementTheme wanted)
    {
        root.RequestedTheme = root.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
        root.RequestedTheme = wanted;
    }


}