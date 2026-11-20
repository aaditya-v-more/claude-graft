using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace ClaudeGraft;

/// <summary>
/// Markup's way into the catalog: <c>local:Localized.Text="Plan usage"</c> puts
/// that English, or its translation, where <c>Text</c> would have it. A plain
/// attribute rather than a markup extension, so the English needs no quoting
/// and reads in the markup exactly as it does in the catalog.
/// </summary>
public static class Localized
{
    public static readonly DependencyProperty TextProperty = Attach("Text", (o, s) =>
    {
        switch (o)
        {
            case TextBlock t: t.Text = s; break;
            case Run r: r.Text = s; break;
            case MenuFlyoutItem m: m.Text = s; break;
            default: Unknown(o, "Text"); break;
        }
    });

    public static readonly DependencyProperty ContentProperty = Attach("Content", (o, s) =>
    {
        if (o is ContentControl c) c.Content = s;
        else Unknown(o, "Content");
    });

    public static readonly DependencyProperty HeaderProperty = Attach("Header", (o, s) =>
    {
        switch (o)
        {
            case SettingsCard c: c.Header = s; break;
            case ToggleSwitch t: t.Header = s; break;
            case ComboBox b: b.Header = s; break;
            case TextBox t: t.Header = s; break;
            default: Unknown(o, "Header"); break;
        }
    });

    public static readonly DependencyProperty DescriptionProperty = Attach("Description", (o, s) =>
    {
        if (o is SettingsCard c) c.Description = s;
        else Unknown(o, "Description");
    });

    public static readonly DependencyProperty PlaceholderTextProperty = Attach("PlaceholderText", (o, s) =>
    {
        switch (o)
        {
            case TextBox t: t.PlaceholderText = s; break;
            case ComboBox b: b.PlaceholderText = s; break;
            default: Unknown(o, "PlaceholderText"); break;
        }
    });

    public static readonly DependencyProperty ToolTipProperty = Attach("ToolTip", (o, s) => ToolTipService.SetToolTip(o, s));

    /// What a screen reader calls the control, through AutomationProperties.Name.
    public static readonly DependencyProperty AutomationNameProperty = Attach("AutomationName", (o, s) => AutomationProperties.SetName(o, s));

    public static string GetText(DependencyObject o) => (string)o.GetValue(TextProperty);
    public static void SetText(DependencyObject o, string value) => o.SetValue(TextProperty, value);
    public static string GetContent(DependencyObject o) => (string)o.GetValue(ContentProperty);
    public static void SetContent(DependencyObject o, string value) => o.SetValue(ContentProperty, value);
    public static string GetHeader(DependencyObject o) => (string)o.GetValue(HeaderProperty);
    public static void SetHeader(DependencyObject o, string value) => o.SetValue(HeaderProperty, value);
    public static string GetDescription(DependencyObject o) => (string)o.GetValue(DescriptionProperty);
    public static void SetDescription(DependencyObject o, string value) => o.SetValue(DescriptionProperty, value);
    public static string GetPlaceholderText(DependencyObject o) => (string)o.GetValue(PlaceholderTextProperty);
    public static void SetPlaceholderText(DependencyObject o, string value) => o.SetValue(PlaceholderTextProperty, value);
    public static string GetToolTip(DependencyObject o) => (string)o.GetValue(ToolTipProperty);
    public static void SetToolTip(DependencyObject o, string value) => o.SetValue(ToolTipProperty, value);
    public static string GetAutomationName(DependencyObject o) => (string)o.GetValue(AutomationNameProperty);
    public static void SetAutomationName(DependencyObject o, string value) => o.SetValue(AutomationNameProperty, value);

    private static DependencyProperty Attach(string name, Action<DependencyObject, string> apply) =>
        DependencyProperty.RegisterAttached(name, typeof(string), typeof(Localized), new PropertyMetadata(null, (o, e) =>
        {
            if (e.NewValue is string english) apply(o, L10n.Text(english));
        }));

    // Noted rather than thrown: a missing word is a smaller fault than a window that will not build.
    private static void Unknown(DependencyObject o, string property) =>
        Diagnostics.Note("localized.unknown-target", new Dictionary<string, object?> { ["type"] = o.GetType().Name, ["property"] = property });
}
