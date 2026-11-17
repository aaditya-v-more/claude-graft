using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClaudeGraft;

/// One setting after the Community Toolkit's settings card: an icon,
/// a name with a line under it, and the control on the right. Its look is in App.xaml.
public sealed class SettingsCard : ContentControl
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(SettingsCard), new PropertyMetadata(""));
    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingsCard), new PropertyMetadata("", (o, _) => ((SettingsCard)o).ShowDescription()));
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(SettingsCard), new PropertyMetadata(""));

    public string Header { get => (string)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    private TextBlock? _description;

    public event Action? GlyphTapped;

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _description = GetTemplateChild("DescriptionText") as TextBlock;
        // Set here rather than in App.xaml, which loads before Graft's own brushes exist.
        if (_description is not null) _description.Foreground = Themed.Brush("TextFillColorSecondaryBrush");
        if (GetTemplateChild("GlyphIcon") is FontIcon icon) icon.Tapped += (_, _) => GlyphTapped?.Invoke();
        ShowDescription();
    }

    // An empty second line would still take a line's height and lift the name off centre.
    private void ShowDescription()
    {
        if (_description is not null)
            _description.Visibility = string.IsNullOrEmpty(Description) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// The words a switch shows beside it, as WinUI's own do.
    public static string OnOff(bool on) => on ? "On" : "Off";
}
