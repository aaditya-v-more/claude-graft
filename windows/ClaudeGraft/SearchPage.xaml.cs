using ClaudeGraft.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace ClaudeGraft;

/// <summary>
/// Every signed-in account's chats and Code sessions searched at once, the way
/// claude.ai's own box searches one account's, then narrowed by kind, status,
/// where a session ran, age, account, project and star.
/// </summary>
public sealed partial class SearchPage : Page
{
    public static string Note => L10n.Text(
        "Searches every account signed in under Artifacts, with the same sign-in: each account's chats as claude.ai's own search box finds them, and its Code sessions by title and folder.\n\nA Code session is placed by its transcript on this PC: run here, on another computer, in the cloud, or gone from claude.ai when a transcript here belongs to a session claude.ai no longer lists. Gone is only said once the account's whole session list has been read.\n\nclaude.ai is asked only when you press Return or Search. The filters narrow what came back and ask nothing.");

    private readonly ProjectCenter _center = ProjectCenter.Shared;
    private string _shownSignature = "";

    public SearchPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        NoteText.Text = Note;
        SignInHint.Text = ArtifactsPage.SignInHintText;
        Loaded += (_, _) =>
        {
            _center.Changed += Render;
            if (_center.Searched is { } query) QueryBox.Text = query;
            Render();
        };
        Unloaded += (_, _) => _center.Changed -= Render;
    }

    private void AddAccount_Click(object sender, RoutedEventArgs e) => _center.Accounts.SignInNewAccount();
    private void Search_Click(object sender, RoutedEventArgs e) => Go();

    private void Query_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        Go();
    }

    /// Return does nothing while a search runs, as the button does: a held key
    /// would otherwise start one walk of every account after another.
    private void Go()
    {
        if (_center.Searching) return;
        _center.Search(QueryBox.Text);
    }

    private void Render()
    {
        var accounts = _center.Accounts;
        var signedIn = accounts.Accounts.Count > 0;
        AddAccountText.Text = signedIn ? L10n.Text("Add Account…") : L10n.Text("Sign In…");
        AutomationProperties.SetName(AddAccountButton, signedIn ? L10n.Text("Add Account") : L10n.Text("Sign In"));
        ArtifactsPage.ShowChips(AccountChips, accounts, "search");

        SearchText.Visibility = _center.Searching ? Visibility.Collapsed : Visibility.Visible;
        SearchRing.Visibility = _center.Searching ? Visibility.Visible : Visibility.Collapsed;
        SearchRing.IsActive = _center.Searching;
        SearchButton.IsEnabled = !_center.Searching;
        FilterButton.Flyout = FilterMenu();
        FilterIcon.Glyph = _center.Filters.IsNarrowed ? "" : "";
        ToolTipService.SetToolTip(FilterButton, _center.Filters.IsNarrowed ? L10n.Text("Filters are narrowing the results") : L10n.Text("Filter the results"));
        AutomationProperties.SetName(FilterButton, _center.Filters.IsNarrowed ? L10n.Text("Filters, narrowed") : L10n.Text("Filters"));

        var trouble = _center.SearchTrouble();
        TroubleText.Text = string.Join(" · ", trouble);
        TroubleBar.Visibility = trouble.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        QueryBar.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        SignedOutPanel.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        var shown = signedIn ? _center.Shown() : [];
        var hidden = _center.Results.Count - shown.Count;
        CountText.Text = _center.Searched is null ? "" : L10n.Plural(shown.Count, "{0} result", "{0} results");
        EmptyText.Visibility = signedIn && shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _center.Searching ? L10n.Text("Asking claude.ai…")
            : _center.Searched is null ? L10n.Text("Type what to look for, then press Return")
            : hidden > 0 ? L10n.Format("Nothing the filters let through · {0} hidden", hidden)
            : L10n.Text("Nothing matches");
        HitList.Visibility = shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var signature = string.Join("\n", shown.Select(h => $"{h.Id}|{h.Account}|{accounts.NameOf(h.Account)}|{_center.ProjectName(h.Project, h.Account)}"));
        if (signature == _shownSignature) return;
        _shownSignature = signature;
        HitList.Items.Clear();
        foreach (var hit in shown) HitList.Items.Add(Row(hit));
    }

    private MenuFlyout FilterMenu()
    {
        var menu = new MenuFlyout();
        var filters = _center.Filters;
        void Set(SearchFilters next) { _center.Filters = next; Render(); }

        MenuFlyoutSubItem Choices<T>(string title, (string Label, T Value)[] options, T current, Func<T, SearchFilters> with)
        {
            var sub = new MenuFlyoutSubItem { Text = title };
            foreach (var (label, value) in options)
            {
                var item = new RadioMenuFlyoutItem { Text = label, GroupName = title, IsChecked = Equals(value, current) };
                item.Click += (_, _) => Set(with(value));
                sub.Items.Add(item);
            }
            return sub;
        }

        menu.Items.Add(Choices(L10n.Text("Kind"), [(L10n.Text("Chats and Code"), SearchFilters.Kinds.All), (L10n.Text("Chats"), SearchFilters.Kinds.Chats), (L10n.Text("Code Sessions"), SearchFilters.Kinds.Code)],
            filters.Kind, v => filters with { Kind = v }));
        menu.Items.Add(Choices(L10n.Text("Status"), [(L10n.Text("Active"), SearchFilters.Statuses.Active), (L10n.Text("Archived"), SearchFilters.Statuses.Archived), (L10n.Text("Active and Archived"), SearchFilters.Statuses.Any)],
            filters.Status, v => filters with { Status = v }));
        menu.Items.Add(Choices(L10n.Text("Where"), [(L10n.Text("Anywhere"), SearchFilters.Places.Anywhere), (L10n.Text("On This PC"), SearchFilters.Places.ThisPC),
            (L10n.Text("On Another Computer"), SearchFilters.Places.OtherPC), (L10n.Text("In the Cloud"), SearchFilters.Places.Cloud), (L10n.Text("Gone from claude.ai"), SearchFilters.Places.GoneFromClaude)],
            filters.Place, v => filters with { Place = v }));
        menu.Items.Add(Choices(L10n.Text("Age"), [(L10n.Text("Any Time"), SearchFilters.Ages.Any), (L10n.Text("Past Day"), SearchFilters.Ages.Day), (L10n.Text("Past Week"), SearchFilters.Ages.Week),
            (L10n.Text("Past Month"), SearchFilters.Ages.Month), (L10n.Text("Past Year"), SearchFilters.Ages.Year)],
            filters.Age, v => filters with { Age = v }));
        menu.Items.Add(Choices(L10n.Text("Account"), [(L10n.Text("Every Account"), (Guid?)null), .. _center.Accounts.Accounts.Select(a => (_center.Accounts.NameOf(a.Id), (Guid?)a.Id))],
            filters.Account, v => filters with { Account = v }));
        menu.Items.Add(Choices(L10n.Text("Project"), [(L10n.Text("Any Project"), (string?)null), (L10n.Text("No Project"), ""), .. _center.Projects.Where(p => !p.IsBeta).Select(p => (p.Name, (string?)p.Id))],
            filters.Project, v => filters with { Project = v }));
        var starred = new ToggleMenuFlyoutItem { Text = L10n.Text("Starred Only"), IsChecked = filters.StarredOnly };
        starred.Click += (_, _) => Set(filters with { StarredOnly = starred.IsChecked });
        menu.Items.Add(starred);
        menu.Items.Add(new MenuFlyoutSeparator());
        var reset = new MenuFlyoutItem { Text = L10n.Text("Reset Filters"), IsEnabled = filters.IsNarrowed };
        reset.Click += (_, _) => Set(new SearchFilters());
        menu.Items.Add(reset);
        return menu;
    }

    private static TextBlock Highlighted(string text, IEnumerable<(int Start, int End)> ranges, Style? style = null)
    {
        var block = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        if (style is not null) block.Style = style;
        var at = 0;
        foreach (var (start, end) in SearchAPI.Fitting(text, ranges))
        {
            if (start < at) continue;
            if (start > at) block.Inlines.Add(new Run { Text = text[at..start] });
            block.Inlines.Add(new Run { Text = text[start..end], FontWeight = FontWeights.Bold });
            at = end;
        }
        if (at < text.Length) block.Inlines.Add(new Run { Text = text[at..] });
        return block;
    }

    private string Subtitle(SearchHit hit)
    {
        var parts = new List<string> { _center.Accounts.NameOf(hit.Account) };
        parts.Add(hit.Kind == SearchHit.Kinds.Chat ? L10n.Text("Chat") : hit.Place switch
        {
            SearchHit.Places.ThisPC => L10n.Text("Code · on this PC"),
            SearchHit.Places.OtherPC => L10n.Text("Code · on another computer"),
            SearchHit.Places.Cloud => L10n.Text("Code · in the cloud"),
            SearchHit.Places.GoneFromClaude => L10n.Text("Code · gone from claude.ai"),
            // Code is the product's own name, which stays the same in every language.
            _ => "Code",
        });
        if (_center.ProjectName(hit.Project, hit.Account) is { } project) parts.Add(project);
        if (hit.IsArchived) parts.Add(L10n.Text("Archived"));
        if (hit.Updated is { } updated) parts.Add(ArtifactsPage.When(updated));
        return string.Join(" · ", parts);
    }

    private FrameworkElement Row(SearchHit hit)
    {
        var token = (hit.Id.Length > 8 ? hit.Id[^8..] : hit.Id).Replace('\\', '-');
        var secondary = Themed.Brush("TextFillColorSecondaryBrush");
        var caption = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        var grid = new Grid { ColumnSpacing = 6, Padding = new Thickness(0, 4, 0, 4), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });

        var icon = new FontIcon
        {
            Glyph = hit.Kind == SearchHit.Kinds.Chat ? "" : "", FontSize = 16, Width = 18, Margin = new Thickness(0, 0, 4, 0),
            Foreground = hit.IsStarred ? new SolidColorBrush(Microsoft.UI.Colors.Goldenrod) : secondary, VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetAccessibilityView(icon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        grid.Children.Add(icon);

        var subtitle = Subtitle(hit);
        var words = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(Highlighted(hit.Title, hit.TitleMatches));
        if (hit.Snippet.Length > 0)
        {
            var snippet = Highlighted(hit.Snippet.ReplaceLineEndings(" "), hit.SnippetMatches, caption);
            words.Children.Add(snippet);
        }
        words.Children.Add(new TextBlock { Text = subtitle, Style = caption, Foreground = secondary, TextTrimming = TextTrimming.CharacterEllipsis });
        AutomationProperties.SetName(words, $"{hit.Title}, {subtitle}");
        AutomationProperties.SetAutomationId(words, $"hit.{token}");
        Grid.SetColumn(words, 1);
        grid.Children.Add(words);

        var gone = hit.OpenUrl is null;
        var open = new Button
        {
            Content = new FontIcon { Glyph = gone ? "" : "", FontSize = 14 }, Padding = new Thickness(6), VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            IsEnabled = !gone || hit.Transcript is not null,
        };
        open.Click += (_, _) => Open(hit);
        var help = gone ? L10n.Text("Show the transcript in File Explorer") : hit.Kind == SearchHit.Kinds.Code ? L10n.Text("Open, signed in as its account") : L10n.Text("Open in the browser");
        ToolTipService.SetToolTip(open, help);
        AutomationProperties.SetName(open, gone ? L10n.Format("Show the transcript of {0}", hit.Title) : L10n.Format("Open {0}", hit.Title));
        AutomationProperties.SetAutomationId(open, $"hit.{token}.open");
        Grid.SetColumn(open, 2);
        grid.Children.Add(open);
        grid.DoubleTapped += (_, _) => Open(hit);
        return grid;
    }

    /// A Code session's page hands itself to the desktop app, as a Beta project's
    /// does, so it opens signed in as its own account; a session claude.ai no
    /// longer has is shown where its transcript is.
    private static void Open(SearchHit hit)
    {
        if (hit.OpenUrl is { } url)
        {
            if (hit.Kind == SearchHit.Kinds.Code) ClaudePageWindow.Show(hit.Account, url, hit.Title);
            else _ = Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        }
        else if (hit.Transcript is { } transcript && File.Exists(transcript))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{transcript}\"") { UseShellExecute = false });
        }
    }
}
