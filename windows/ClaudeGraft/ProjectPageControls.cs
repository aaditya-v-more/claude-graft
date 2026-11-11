using System.Diagnostics;
using ClaudeGraft.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace ClaudeGraft;

internal static class ProjectPageControls
{
    internal static TextBlock Text(string value, double size = 14) => new() { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap };
    internal static void Identify(DependencyObject control, string id, string name)
    { AutomationProperties.SetAutomationId(control, id); AutomationProperties.SetName(control, name); }
    internal static Grid Rows(params GridLength[] rows)
    {
        var grid = new Grid { RowSpacing = 12 };
        foreach (var height in rows) grid.RowDefinitions.Add(new() { Height = height });
        return grid;
    }
    internal static void Add(Grid grid, FrameworkElement element, int row) { Grid.SetRow(element, row); grid.Children.Add(element); }
    internal static ListView List(string id)
    {
        var style = new Style(typeof(ListViewItem));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        var list = new ListView { SelectionMode = ListViewSelectionMode.None, ItemContainerStyle = style };
        Identify(list, id, id.StartsWith("projects") ? "Projects" : "Search results"); return list;
    }
    internal static FrameworkElement Row(string title, string subtitle, string snippet, TextMatch[] titleMatches,
        TextMatch[] snippetMatches, string id, Action open, bool local = false)
    {
        var row = new Grid { Padding = new Thickness(0, 8, 0, 8), ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var details = new StackPanel { Spacing = 3 };
        details.Children.Add(Highlighted(title, titleMatches, 15));
        if (!string.IsNullOrEmpty(snippet)) details.Children.Add(Highlighted(snippet, snippetMatches, 12));
        details.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Opacity = .7, TextWrapping = TextWrapping.Wrap });
        row.Children.Add(details);
        var button = new Button { Content = new FontIcon { Glyph = local ? "\uE838" : "\uE8A7", FontSize = 16 }, VerticalAlignment = VerticalAlignment.Center };
        Identify(button, id + ".open", (local ? "Show transcript in File Explorer: " : "Open in browser: ") + title);
        ToolTipService.SetToolTip(button, local ? "Show transcript in File Explorer" : "Open in browser");
        button.Click += (_, _) => open(); Grid.SetColumn(button, 1); row.Children.Add(button);
        Identify(details, id, title + ". " + subtitle); row.DoubleTapped += (_, e) => { if (e.OriginalSource is not Button) open(); };
        return row;
    }
    private static TextBlock Highlighted(string text, TextMatch[] matches, double size)
    {
        var block = new TextBlock { FontSize = size, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis };
        var marked = new bool[text.Length];
        foreach (var match in ProjectApi.ValidMatches(text, matches)) Array.Fill(marked, true, match.Start, match.End - match.Start);
        for (var start = 0; start < text.Length;)
        {
            var end = start + 1; while (end < text.Length && marked[end] == marked[start]) end++;
            block.Inlines.Add(new Run { Text = text[start..end], FontWeight = marked[start] ? FontWeights.SemiBold : FontWeights.Normal }); start = end;
        }
        return block;
    }
    internal static string AccountName(AccountCenter accounts, Guid id) => accounts.Accounts.FirstOrDefault(account => account.Id == id)?.DisplayName ?? "";
    internal static string When(DateTimeOffset? date) => date?.ToLocalTime().ToString("g") ?? "";
    internal static string Place(SessionPlace? place) => place switch {
        SessionPlace.ThisComputer => "On this computer", SessionPlace.OtherComputer => "On another computer",
        SessionPlace.Cloud => "In the cloud", SessionPlace.GoneFromClaude => "Gone from claude.ai", _ => "" };
    internal static void Open(SearchHit hit)
    {
        if (hit.OpenUrl is { } url) Links.Open(url.AbsoluteUri);
        else if (hit.Transcript is { } path && File.Exists(path))
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = true };
            start.Arguments = "/select,\"" + path + "\""; Process.Start(start);
        }
        else throw new IOException("This transcript is no longer on this computer.");
    }
}
