using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace ClaudeGraft;

/// Sweeps a ProgressBar from empty up to its current value whenever a counter it
/// watches changes. The bar's Value is bound as usual; this only plays the fill.
///
/// Driven off a monotonic pulse rather than the value itself, so a refresh that
/// lands on the same figure still animates — which is the point: the sweep is the
/// sign the usage was actually re-read, not that the number moved.
public static class BarPulse
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(int), typeof(BarPulse), new PropertyMetadata(0, OnKeyChanged));

    public static int GetKey(DependencyObject o) => (int)o.GetValue(KeyProperty);
    public static void SetKey(DependencyObject o, int value) => o.SetValue(KeyProperty, value);

    private static void OnKeyChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        // The very first bind, from the default 0 to the first pulse, still sweeps;
        // there is nothing on screen before it to jar.
        if (o is not ProgressBar bar) return;

        var animation = new DoubleAnimation
        {
            From = 0,
            To = bar.Value,   // the Value binding has already applied this refresh's figure
            Duration = TimeSpan.FromMilliseconds(600),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            // Value drives the indicator's layout, so the animation has to be
            // allowed to touch a dependent property to play at all.
            EnableDependentAnimation = true,
            // Hand the property back to its binding when the sweep ends, rather
            // than holding the end value: a held animation masks the bound Value,
            // so the next refresh would read this old figure off the bar and fill
            // to it instead of to the new one. Stop lands on the same number the
            // binding already holds, so there is no jump.
            FillBehavior = FillBehavior.Stop,
        };
        Storyboard.SetTarget(animation, bar);
        Storyboard.SetTargetProperty(animation, "Value");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }
}
