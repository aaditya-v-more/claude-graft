using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace ClaudeGraft;

/// Maps the stored appearance choices onto the WinUI types the windows apply.
/// Kept in one place so the manager window and the tray flyout dress themselves
/// the same way from the same setting.
internal static class Appearance
{
    // System is resolved here rather than left as Default: Default follows the app's
    // theme, which WinUI fixes at launch, so after Windows switched the backdrop
    // followed it and the text did not.
    public static ElementTheme ToElementTheme(AppTheme theme) => theme switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.Dark => ElementTheme.Dark,
        _ => WindowsIsDark() ? ElementTheme.Dark : ElementTheme.Light,
    };

    public static bool WindowsIsDark() =>
        new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Background).R < 128;

    /// The backdrop a window sets on itself. None returns null — the caller then
    /// paints an opaque themed surface, since a window with no backdrop and no
    /// background of its own is see-through onto the desktop.
    public static SystemBackdrop? ToBackdrop(BackdropMaterial material) => material switch
    {
        BackdropMaterial.Mica => new MicaBackdrop(),
        BackdropMaterial.MicaAlt => new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt },
        BackdropMaterial.Acrylic => new DesktopAcrylicBackdrop(),
        _ => null,
    };
}
