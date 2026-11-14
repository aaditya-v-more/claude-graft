using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace ClaudeGraft;

public sealed class SearchChatsPage : Page
{
    private readonly ProjectCenter _center;
    private readonly TextBox _query = new() { PlaceholderText = "Search every account's chats" };
    private readonly Button _search = new() { Content = "Search" };
    private readonly Button _filters = new() { Content = "Filters" };
    private readonly Flyout _filterFlyout = new();
    private readonly ListView _list = ProjectPageControls.List("search.list");
    private readonly TextBlock _empty = ProjectPageControls.Text("");
    private readonly TextBlock _status = ProjectPageControls.Text("", 12);
    private sealed record Choice(string Label, object? Value) { public override string ToString() => Label; }

    public SearchChatsPage() : this(ProjectCenter.For(App.Accounts)) { }
    public SearchChatsPage(ProjectCenter center)
    {
        _center = center; _query.Text = center.Catalog.Searched ?? "";
        var grid = ProjectPageControls.Rows(GridLength.Auto, GridLength.Auto, GridLength.Auto, new(1, GridUnitType.Star), GridLength.Auto);
        grid.Padding = new Thickness(20);
        var heading = new StackPanel { Spacing = 5 };
        heading.Children.Add(ProjectPageControls.Text("Search Chats", 24));
        var note = ProjectPageControls.Text("Search chats and Code sessions across your signed-in accounts. Filters only narrow the results already returned.", 12);
        ToolTipService.SetToolTip(note, "Code titles and folders are matched against claude.ai's session lists and this computer's transcripts. A session absent from a complete account and organization list can be shown in File Explorer. Deleted web chats cannot be recovered.");
        heading.Children.Add(note); ProjectPageControls.Add(grid, heading, 0); ProjectPageControls.Add(grid, new AccountsBar(center.Accounts), 1);
        var tools = new Grid { ColumnSpacing = 8 };
        tools.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); tools.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); tools.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        tools.Children.Add(_query); Grid.SetColumn(_search, 1); tools.Children.Add(_search); Grid.SetColumn(_filters, 2); tools.Children.Add(_filters);
        _filters.Flyout = _filterFlyout; _filterFlyout.Opening += (_, _) => BuildFilters(); ProjectPageControls.Add(grid, tools, 2);
        var content = new Grid(); content.Children.Add(_list); _empty.HorizontalAlignment = HorizontalAlignment.Center; _empty.VerticalAlignment = VerticalAlignment.Center; content.Children.Add(_empty);
        ProjectPageControls.Add(grid, content, 3); ProjectPageControls.Add(grid, _status, 4); Content = grid;
        ProjectPageControls.Identify(_query, "search.query", "Search Chats"); ProjectPageControls.Identify(_search, "search.go", "Search all accounts");
        ProjectPageControls.Identify(_filters, "search.filters", "Filter search results"); ProjectPageControls.Identify(_empty, "search.empty", "Search status");
        ProjectPageControls.Identify(_status, "search.status", "Search results and request status");
        _search.Click += async (_, _) => await Search();
        _query.KeyDown += async (_, e) => { if (e.Key == VirtualKey.Enter && !_center.Catalog.Searching) { e.Handled = true; await Search(); } };
        _query.TextChanged += (_, _) => Buttons();
        Loaded += (_, _) => { _center.Catalog.Changed += Changed; Refresh(); _query.Focus(FocusState.Programmatic); };
        Unloaded += (_, _) => _center.Catalog.Changed -= Changed;
    }

    private Task Search() => _center.Catalog.SearchAsync(_query.Text);
    private void Changed(object? sender, EventArgs e) => Refresh();
    private void Buttons()
    {
        _search.IsEnabled = !_center.Catalog.Searching && _query.Text.Trim().Length > 0 && _center.Accounts.Accounts.Count > 0;
        _search.Content = _center.Catalog.Searching ? "Searching…" : "Search";
        _filters.Content = _center.Catalog.Filters.IsNarrowed ? "Filters •" : "Filters";
    }
    private void Refresh()
    {
        var catalog = _center.Catalog; var shown = catalog.Shown.ToArray(); Buttons(); _list.Items.Clear();
        foreach (var hit in shown)
        {
            var project = catalog.Projects.FirstOrDefault(project => project.Account == hit.Account && project.Id == hit.Project)?.Name;
            var subtitle = string.Join(" · ", new[] { ProjectPageControls.AccountName(_center.Accounts, hit.Account), hit.Kind == SearchKind.Code ? "Code" : "Chat",
                ProjectPageControls.Place(hit.Place), project ?? "", hit.IsArchived ? "Archived" : "", ProjectPageControls.When(hit.Updated) }.Where(text => text.Length > 0));
            var title = hit.IsStarred ? "★ " + hit.Title : hit.Title;
            var titleMatches = hit.IsStarred ? hit.TitleMatches.Select(match => new TextMatch(match.Start + 2, match.End + 2)).ToArray() : hit.TitleMatches;
            _list.Items.Add(ProjectPageControls.Row(title, subtitle, hit.Snippet, titleMatches, hit.SnippetMatches,
                "hit." + hit.Account + "." + hit.Id, () => { try { ProjectPageControls.Open(hit); } catch { _status.Text = "This result could not be opened."; } }, hit.OpenUrl is null));
        }
        _empty.Text = _center.Accounts.Accounts.Count == 0 ? "Sign in to search chats" : catalog.Searching ? "Asking claude.ai…"
            : catalog.Searched is null ? "Type and press Return" : catalog.Results.Length == 0 ? "Nothing matches" : "Nothing matches these filters";
        _empty.Visibility = shown.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var trouble = catalog.Trouble(true);
        _status.Text = trouble.Length > 0 ? string.Join(" · ", trouble) : catalog.Searched is null || catalog.Searching ? ""
            : shown.Length == catalog.Results.Length ? shown.Length + " results" : shown.Length + " of " + catalog.Results.Length + " results";
        ProjectPageControls.Identify(_empty, "search.empty", _empty.Text);
        ProjectPageControls.Identify(_status, "search.status", _status.Text);
    }

    private void BuildFilters()
    {
        var panel = new StackPanel { Spacing = 10, Width = 280 };
        var filter = _center.Catalog.Filters;
        void Apply(SearchFilters value) { _center.Catalog.Filters = value; Refresh(); }
        void Select(string label, string id, Choice[] choices, object? selected, Action<object?> changed)
        {
            var combo = new ComboBox { Header = label, HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = choices,
                SelectedItem = choices.FirstOrDefault(choice => Equals(choice.Value, selected)) ?? choices[0] };
            ProjectPageControls.Identify(combo, "search.filter." + id, label);
            combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is Choice choice) changed(choice.Value); }; panel.Children.Add(combo);
        }
        Select("Kind", "kind", [new("Chats and Code", SearchKindFilter.All), new("Chats", SearchKindFilter.Chats), new("Code sessions", SearchKindFilter.Code)], filter.Kind,
            value => Apply(_center.Catalog.Filters with { Kind = (SearchKindFilter)value! }));
        Select("Status", "status", [new("Active", SearchStatus.Active), new("Archived", SearchStatus.Archived), new("Active and archived", SearchStatus.Any)], filter.Status,
            value => Apply(_center.Catalog.Filters with { Status = (SearchStatus)value! }));
        Select("Where", "place", [new("Anywhere", null), new("On this computer", SessionPlace.ThisComputer), new("On another computer", SessionPlace.OtherComputer),
            new("In the cloud", SessionPlace.Cloud), new("Gone from claude.ai", SessionPlace.GoneFromClaude)], filter.Place,
            value => Apply(_center.Catalog.Filters with { Place = (SessionPlace?)value }));
        Select("Changed", "age", [new("Any time", SearchAge.Any), new("Past day", SearchAge.Day), new("Past week", SearchAge.Week), new("Past month", SearchAge.Month), new("Past year", SearchAge.Year)], filter.Age,
            value => Apply(_center.Catalog.Filters with { Age = (SearchAge)value! }));
        Select("Account", "account", new[] { new Choice("Every account", null) }.Concat(_center.Accounts.Accounts.Select(account => new Choice(account.DisplayName, account.Id))).ToArray(), filter.Account,
            value => { Apply(_center.Catalog.Filters with { Account = (Guid?)value, Project = null, NoProject = false }); BuildFilters(); });
        Select("Project", "project", new[] { new Choice("Any project", null), new Choice("No project", true) }.Concat(_center.Catalog.Projects
            .Where(project => filter.Account is null || project.Account == filter.Account).Select(project => new Choice(project.Name + " · " + ProjectPageControls.AccountName(_center.Accounts, project.Account), project.Id))).ToArray(),
            filter.NoProject ? true : filter.Project, value => Apply(_center.Catalog.Filters with { Project = value as string, NoProject = value is true }));
        var starred = new CheckBox { Content = "Starred only", IsChecked = filter.StarredOnly };
        ProjectPageControls.Identify(starred, "search.filter.starred", "Starred only");
        starred.Checked += (_, _) => Apply(_center.Catalog.Filters with { StarredOnly = true }); starred.Unchecked += (_, _) => Apply(_center.Catalog.Filters with { StarredOnly = false }); panel.Children.Add(starred);
        var reset = new Button { Content = "Reset filters", HorizontalAlignment = HorizontalAlignment.Stretch };
        reset.Click += (_, _) => { Apply(new()); BuildFilters(); }; ProjectPageControls.Identify(reset, "search.filter.reset", "Reset filters"); panel.Children.Add(reset);
        _filterFlyout.Content = new ScrollViewer { Content = panel, MaxHeight = 520, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
}
