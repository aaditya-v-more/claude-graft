using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClaudeGraft;

/// <summary>
/// Every signed-in account's projects in one list. It only lists: a project is
/// opened on claude.ai, and changed there.
/// </summary>
public sealed partial class ProjectsPage : Page
{
    public const string Note =
        "Projects are listed from every account signed in under Artifacts, with the same sign-in. Graft only lists "
        + "them; opening one takes you to claude.ai, where it is changed.\n\n"
        + "claude.ai is asked when this page is shown and when you refresh it, never in the background.";

    private readonly ProjectCenter _center = ProjectCenter.Shared;
    private string _shownSignature = "";

    public ProjectsPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        NoteText.Text = Note;
        SignInHint.Text = ArtifactsPage.SignInHintText;
        Loaded += (_, _) =>
        {
            _center.Changed += Render;
            Render();
            _center.Refresh();
        };
        Unloaded += (_, _) => _center.Changed -= Render;
    }

    private void AddAccount_Click(object sender, RoutedEventArgs e) => _center.Accounts.SignInNewAccount();
    private void Refresh_Click(object sender, RoutedEventArgs e) => _center.Refresh();
    private void Filter_Changed(object sender, TextChangedEventArgs e) => Render();

    private List<ClaudeProject> Shown()
    {
        var query = FilterBox.Text.Trim();
        return query.Length == 0 ? _center.Projects
            : _center.Projects.Where(p => p.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || p.Summary.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }

    private void Render()
    {
        var accounts = _center.Accounts;
        var signedIn = accounts.Accounts.Count > 0;
        AddAccountText.Text = signedIn ? "Add Account…" : "Sign In…";
        AutomationProperties.SetName(AddAccountButton, signedIn ? "Add Account" : "Sign In");
        RefreshIcon.Visibility = _center.Loading ? Visibility.Collapsed : Visibility.Visible;
        RefreshRing.Visibility = _center.Loading ? Visibility.Visible : Visibility.Collapsed;
        RefreshRing.IsActive = _center.Loading;
        RefreshButton.IsEnabled = signedIn && !_center.Loading;

        ArtifactsPage.ShowChips(AccountChips, accounts, "projects");

        var trouble = _center.Trouble();
        TroubleText.Text = string.Join(" · ", trouble);
        ToolTipService.SetToolTip(TroubleText, trouble.Count == 0 ? null : TroubleText.Text);
        TroubleBar.Visibility = trouble.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        FilterBar.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        SignedOutPanel.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        var shown = signedIn ? Shown() : [];
        CountText.Text = shown.Count == 1 ? "1 project" : $"{shown.Count} projects";
        EmptyText.Visibility = signedIn && shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _center.Loading ? "Asking claude.ai…" : FilterBox.Text.Trim().Length == 0 ? "No projects" : "Nothing matches";
        ProjectList.Visibility = shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var signature = string.Join("\n", shown.Select(p => $"{p.Id}|{p.Name}|{p.IsStarred}|{p.IsPrivate}|{p.Updated}|{accounts.NameOf(p.Account)}"));
        if (signature == _shownSignature) return;
        _shownSignature = signature;
        ProjectList.Items.Clear();
        foreach (var project in shown) ProjectList.Items.Add(Row(project));
    }

    private FrameworkElement Row(ClaudeProject project)
    {
        var token = project.Id.Length > 8 ? project.Id[..8] : project.Id;
        var secondary = Themed.Brush("TextFillColorSecondaryBrush");
        var grid = new Grid { ColumnSpacing = 6, Padding = new Thickness(0, 4, 0, 4), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });

        var icon = new FontIcon
        {
            Glyph = project.IsStarred ? "" : "", FontSize = 16, Width = 18, Margin = new Thickness(0, 0, 4, 0),
            Foreground = project.IsStarred ? new SolidColorBrush(Microsoft.UI.Colors.Goldenrod) : secondary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetAccessibilityView(icon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        grid.Children.Add(icon);

        var parts = new List<string> { _center.Accounts.NameOf(project.Account) };
        if (project.IsExample) parts.Add("Example");
        if (project.IsBeta) parts.Add("Beta");
        if (project.Updated is { } updated) parts.Add(ArtifactsPage.When(updated));
        var subtitle = string.Join(" · ", parts);
        var words = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = project.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        words.Children.Add(new TextBlock { Text = subtitle, TextTrimming = TextTrimming.CharacterEllipsis,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = secondary });
        if (project.Summary.Length > 0) ToolTipService.SetToolTip(words, project.Summary);
        AutomationProperties.SetName(words, $"{project.Name}, {subtitle}");
        AutomationProperties.SetAutomationId(words, $"project.{token}");
        Grid.SetColumn(words, 1);
        grid.Children.Add(words);

        if (!project.IsPrivate)
        {
            var shared = new FontIcon { Glyph = "", FontSize = 14, Foreground = secondary, Margin = new Thickness(6), VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(shared, "Shared with your organization");
            AutomationProperties.SetAccessibilityView(shared, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            Grid.SetColumn(shared, 2);
            grid.Children.Add(shared);
        }

        var open = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 14 }, Padding = new Thickness(6), VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
        };
        open.Click += (_, _) => Open(project);
        ToolTipService.SetToolTip(open, project.IsBeta ? "Open, signed in as its account" : "Open in the browser");
        AutomationProperties.SetName(open, $"Open {project.Name}");
        AutomationProperties.SetAutomationId(open, $"project.{token}.open");
        Grid.SetColumn(open, 3);
        grid.Children.Add(open);

        grid.DoubleTapped += (_, _) => Open(project);
        return grid;
    }

    /// A Beta project's page hands itself to the desktop app, signed in as
    /// someone else, so it opens in a window signed in as its own account.
    private static void Open(ClaudeProject project)
    {
        if (project.IsBeta) ClaudePageWindow.Show(project.Account, project.OpenUrl, project.Name);
        else _ = Windows.System.Launcher.LaunchUriAsync(new Uri(project.OpenUrl));
    }
}
