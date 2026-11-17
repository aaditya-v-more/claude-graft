using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClaudeGraft;

/// Every dialog goes through here first, so each opens, rounds and themes alike.
public static class Dialogs
{
    // A dialog lives in the popup layer, beside the themed content rather than in it, so it
    // inherits neither the page's theme nor an app-level corner; both are set on it directly.
    public static void Prepare(ContentDialog dialog, XamlRoot root)
    {
        dialog.XamlRoot = root;
        dialog.RequestedTheme = Appearance.ToElementTheme(App.Settings.Theme);
        dialog.CornerRadius = new CornerRadius(8);
        dialog.Resources["OverlayCornerRadius"] = new CornerRadius(8);
        dialog.Loaded += (_, _) => { Enter(dialog); Dim(root); };
        dialog.Opened += (_, _) => Dim(root);
    }

    // The dimming behind a dialog is a popup of its own at the window's root, which keeps the
    // mode the app launched in; after a switch it washed the window light in dark mode.
    private static void Dim(XamlRoot root)
    {
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
            if (popup.Child is Microsoft.UI.Xaml.Shapes.Rectangle smoke)
                smoke.Fill = Themed.Brush("SmokeFillColorDefaultBrush");
    }

    // WinUI animates a dialog in only on its way to DialogShowing, and in this window most open
    // into DialogShowingWithoutSmokeLayer, which has none. Its entrance is played here instead,
    // on the template's own ScaleTransform, from a touch large and clear as WinUI's. Started at
    // Loaded, before the first frame; a start at Opened came a frame late and stuttered.
    private static void Enter(ContentDialog dialog)
    {
        if (Part(dialog, "BackgroundElement") is not UIElement box || box.RenderTransform is not ScaleTransform grow) return;
        box.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        var ease = new Microsoft.UI.Xaml.Media.Animation.ExponentialEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut, Exponent = 6 };
        var story = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        void Add(DependencyObject target, string property, double from, double to, int ms, Microsoft.UI.Xaml.Media.Animation.EasingFunctionBase? easing)
        {
            var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                { From = from, To = to, Duration = TimeSpan.FromMilliseconds(ms), EasingFunction = easing };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, target);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, property);
            story.Children.Add(animation);
        }
        Add(grow, "ScaleX", 1.05, 1, 167, ease);
        Add(grow, "ScaleY", 1.05, 1, 167, ease);
        Add(box, "Opacity", 0, 1, 83, null);
        story.Begin();
    }

    private static DependencyObject? Part(DependencyObject root, string name)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element && element.Name == name) return child;
            if (Part(child, name) is { } found) return found;
        }
        return null;
    }
}
