using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClaudeGraft;

/// Puts an ⓘ on the middle of its label's letters, read from the label's font,
/// because centring two boxes leaves it high by an amount that grows with the font.
public static class InfoAlign
{
    public static readonly DependencyProperty ToTextProperty = DependencyProperty.RegisterAttached(
        "ToText", typeof(bool), typeof(InfoAlign), new PropertyMetadata(false, OnChanged));

    public static bool GetToText(DependencyObject o) => (bool)o.GetValue(ToTextProperty);
    public static void SetToText(DependencyObject o, bool v) => o.SetValue(ToTextProperty, v);

    private static void OnChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not FrameworkElement button || e.NewValue is not true) return;
        var placed = false;
        button.Loaded += (_, _) =>
        {
            // Once: a page moved between hosts loads again, and each load would add a handler.
            if (placed || Label(button) is not { } label) return;
            placed = true;
            // Every layout pass, not just size changes: the baseline is only known once text has rendered.
            button.LayoutUpdated += (_, _) => Place(button, label);
        };
    }

    // The label it explains: on its row, the text in the nearest column to its left,
    // or in a plain panel the nearest text before it.
    private static TextBlock? Label(FrameworkElement button)
    {
        if (VisualTreeHelper.GetParent(button) is not Panel panel) return null;
        var row = Grid.GetRow(button);
        var texts = panel.Children.OfType<TextBlock>().Where(t => t.Visibility == Visibility.Visible && Grid.GetRow(t) == row);
        if (panel is Grid)
            return texts.Where(t => Grid.GetColumn(t) < Grid.GetColumn(button)).MaxBy(Grid.GetColumn);
        var at = panel.Children.IndexOf(button);
        return texts.LastOrDefault(t => panel.Children.IndexOf(t) < at);
    }

    // Moved by a render offset, never a margin: a margin grows the row, which re-centres
    // the label and sends the icon chasing it.
    private static void Place(FrameworkElement button, TextBlock label)
    {
        if (VisualTreeHelper.GetParent(button) is not UIElement panel || button.ActualHeight == 0 || label.ActualHeight == 0) return;
        var shift = button.RenderTransform as TranslateTransform ?? new TranslateTransform();
        var labelTop = label.TransformToVisual(panel).TransformPoint(default).Y;
        var buttonTop = button.TransformToVisual(panel).TransformPoint(default).Y - shift.Y;
        // A label is a capital and lowercase, so its optical middle lies between the middle
        // of the lowercase and the middle of the capitals, measured up from the font's baseline.
        var size = label.FontSize;
        var middle = FontLines.Of(label.FontFamily.Source, label.FontWeight.Weight, (int)label.FontStyle) is { } font
            ? labelTop + font.Ascent * size - (font.XHeight + font.CapHeight) * size / 4
            : labelTop + label.ActualHeight / 2;
        var y = Math.Round(middle - buttonTop - IconMiddle(button), MidpointRounding.AwayFromZero);
        if (Math.Abs(y - shift.Y) < 0.5 && button.RenderTransform == shift) return;
        shift.Y = y;
        button.RenderTransform = shift;
    }

    // Where the glyph's ink is centred within the button, from the glyph's own outline
    // and the baseline of the text drawing it; the box's middle if either is unknown.
    private static double IconMiddle(FrameworkElement button)
    {
        if (button is ContentControl { Content: FontIcon icon } && Descendant<TextBlock>(icon) is { BaselineOffset: > 0 } glyph
            && glyph.Text.Length > 0 && FontLines.InkMiddle(icon.FontFamily.Source, char.ConvertToUtf32(glyph.Text, 0)) is { } ink)
            return glyph.TransformToVisual(button).TransformPoint(default).Y + glyph.BaselineOffset - ink * icon.FontSize;
        return button.ActualHeight / 2;
    }

    private static T? Descendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found || Descendant<T>(child) is { } deeper && (found = deeper) is not null) return found;
        }
        return null;
    }
}
