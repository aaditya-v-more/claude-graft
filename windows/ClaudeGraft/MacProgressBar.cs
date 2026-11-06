using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClaudeGraft;

public sealed class MacProgressBar : ProgressBar
{
    private Border? _indicator;
    public MacProgressBar()
    {
        SizeChanged += (_, _) => UpdateFill();
        RegisterPropertyChangedCallback(ValueProperty, (_, _) => UpdateFill());
        RegisterPropertyChangedCallback(MaximumProperty, (_, _) => UpdateFill());
        RegisterPropertyChangedCallback(MinimumProperty, (_, _) => UpdateFill());
    }
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _indicator = GetTemplateChild("Indicator") as Border;
        UpdateFill();
    }
    private void UpdateFill()
    {
        if (_indicator is null) return;
        var fraction = Maximum > Minimum ? Math.Clamp((Value - Minimum) / (Maximum - Minimum), 0, 1) : 0;
        _indicator.Width = double.IsFinite(fraction) ? ActualWidth * fraction : 0;
    }
}
