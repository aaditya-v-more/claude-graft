using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClaudeGraft;

public sealed class ProjectsPage : Page
{
    private readonly ProjectCenter _center;
    private readonly TextBox _filter = new() { PlaceholderText = "Filter projects", MaxWidth = 320, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _refresh = new() { Content = "Refresh" };
    private readonly ListView _list = ProjectPageControls.List("projects.list");
    private readonly TextBlock _empty = ProjectPageControls.Text("");
    private readonly TextBlock _status = ProjectPageControls.Text("", 12);
    private readonly TextBlock _count = ProjectPageControls.Text("", 12);

    public ProjectsPage() : this(ProjectCenter.For(App.Accounts)) { }
    public ProjectsPage(ProjectCenter center)
    {
        _center = center;
        var grid = ProjectPageControls.Rows(GridLength.Auto, GridLength.Auto, GridLength.Auto, new(1, GridUnitType.Star), GridLength.Auto);
        grid.Padding = new Thickness(20);
        var heading = new StackPanel { Spacing = 5 };
        heading.Children.Add(ProjectPageControls.Text("Projects", 24));
        heading.Children.Add(ProjectPageControls.Text("Projects from your signed-in claude.ai accounts. Open a project in your browser to change it.", 12));
        ProjectPageControls.Add(grid, heading, 0); ProjectPageControls.Add(grid, new AccountsBar(center.Accounts), 1);
        var tools = new Grid { ColumnSpacing = 10 };
        tools.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); tools.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); tools.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        tools.Children.Add(_filter); Grid.SetColumn(_count, 1); _count.VerticalAlignment = VerticalAlignment.Center; tools.Children.Add(_count);
        Grid.SetColumn(_refresh, 2); tools.Children.Add(_refresh); ProjectPageControls.Add(grid, tools, 2);
        var content = new Grid(); content.Children.Add(_list); _empty.HorizontalAlignment = HorizontalAlignment.Center; _empty.VerticalAlignment = VerticalAlignment.Center; content.Children.Add(_empty);
        ProjectPageControls.Add(grid, content, 3); ProjectPageControls.Add(grid, _status, 4); Content = grid;
        ProjectPageControls.Identify(_filter, "projects.filter", "Filter Projects"); ProjectPageControls.Identify(_refresh, "projects.refresh", "Refresh Projects");
        ProjectPageControls.Identify(_empty, "projects.empty", "Project list status"); ProjectPageControls.Identify(_status, "projects.status", "Project request status");
        _filter.TextChanged += (_, _) => Refresh(); _refresh.Click += async (_, _) => await _center.Catalog.RefreshProjectsAsync();
        Loaded += async (_, _) => { _center.Catalog.Changed += Changed; Refresh(); await _center.Catalog.RefreshProjectsAsync(); };
        Unloaded += (_, _) => _center.Catalog.Changed -= Changed;
    }
    private void Changed(object? sender, EventArgs e) => Refresh();
    private void Refresh()
    {
        var catalog = _center.Catalog; var query = _filter.Text.Trim();
        var shown = catalog.Projects.Where(project => project.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || project.Summary.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        _list.Items.Clear();
        foreach (var project in shown)
        {
            var subtitle = string.Join(" · ", new[] { ProjectPageControls.AccountName(_center.Accounts, project.Account), project.IsPrivate ? "Private" : "Shared with organization",
                project.IsExample ? "Example" : "", ProjectPageControls.When(project.Updated) }.Where(text => text.Length > 0));
            var row = ProjectPageControls.Row((project.IsStarred ? "★ " : "") + project.Name, subtitle, project.Summary, [], [], "project." + project.Account + "." + project.Id,
                () => { try { Links.Open(project.OpenUrl.AbsoluteUri); } catch { _status.Text = "The browser could not open this project."; } });
            _list.Items.Add(row);
        }
        _count.Text = shown.Length + " projects"; _refresh.IsEnabled = _center.Accounts.Accounts.Count > 0 && !catalog.LoadingProjects;
        _refresh.Content = catalog.LoadingProjects ? "Loading…" : "Refresh";
        _empty.Text = _center.Accounts.Accounts.Count == 0 ? "Sign in to see projects" : catalog.LoadingProjects ? "Asking claude.ai…" : query.Length == 0 ? "No projects" : "Nothing matches";
        _empty.Visibility = shown.Length == 0 ? Visibility.Visible : Visibility.Collapsed; _status.Text = string.Join(" · ", catalog.Trouble(false));
        ProjectPageControls.Identify(_empty, "projects.empty", _empty.Text);
        ProjectPageControls.Identify(_status, "projects.status", _status.Text);
    }
}
