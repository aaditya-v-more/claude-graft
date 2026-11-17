using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClaudeGraft;

public sealed partial class ArtifactsPage : Page
{
    public const string Note =
        "Graft signs in to claude.ai once for each account, in a window of its own, and keeps that sign-in in a "
        + "browser store of its own for that account. One sign-in serves Artifacts, Projects and Search Chats alike. "
        + "It is used only by claude.ai's own pages and is never read by Graft or sent anywhere else. Signing out deletes it.\n\n"
        + "Sign in with an emailed code: enter the account's address on claude.ai's page, choose Continue with email, "
        + "and type the code claude.ai sends. That is the one way of signing in Graft supports.\n\n"
        + "Groups are kept by Graft on this PC. Sharing with a group invites each person with the role the group gives "
        + "them, and claude.ai emails each one, as it does when you invite them from the artifact's own Share panel. "
        + "Where an artifact takes no commenters or editors, they are invited with the most it does take, never more.";

    /// Under every Sign In button, since a sign-in that starts another way is
    /// one Graft does not support.
    public const string SignInHintText = "Use Continue with email and the code claude.ai sends you";

    private readonly ArtifactCenter _center = ArtifactCenter.Shared;
    private string _shownSignature = "";

    public ArtifactsPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        NoteText.Text = Note;
        SignInHint.Text = SignInHintText;
        Loaded += (_, _) =>
        {
            _center.Changed += Render;
            Render();
            _center.Refresh();
        };
        Unloaded += (_, _) => _center.Changed -= Render;
    }

    /// Shown again from the sidebar: asked afresh, as on the Mac.
    public void OnShown() => _center.Refresh();

    private void AddAccount_Click(object sender, RoutedEventArgs e) => _center.SignInNewAccount();
    private void Refresh_Click(object sender, RoutedEventArgs e) => _center.Refresh();
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) { if (IsLoaded) Render(); }
    private void Search_Changed(object sender, TextChangedEventArgs e) => Render();
    private async void Groups_Click(object sender, RoutedEventArgs e) => await new GroupsEditor(_center, XamlRoot).ShowAsync();

    private List<Artifact> Shown()
    {
        var query = SearchBox.Text.Trim();
        return _center.Artifacts.Where(a => FilterBox.SelectedIndex switch
        {
            1 => a.IsOwn,
            2 => !a.IsOwn,
            _ => true,
        } && (query.Length == 0 || a.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase))).ToList();
    }

    private void Render()
    {
        var signedIn = _center.Accounts.Count > 0;
        AddAccountText.Text = signedIn ? "Add Account…" : "Sign In…";
        AutomationProperties.SetName(AddAccountButton, signedIn ? "Add Account" : "Sign In");
        RefreshIcon.Visibility = _center.AnyLoading ? Visibility.Collapsed : Visibility.Visible;
        RefreshRing.Visibility = _center.AnyLoading ? Visibility.Visible : Visibility.Collapsed;
        RefreshRing.IsActive = _center.AnyLoading;
        RefreshButton.IsEnabled = signedIn && !_center.AnyLoading;
        StatusLine.Text = _center.Status ?? "";
        ToolTipService.SetToolTip(StatusLine, _center.Status);
        GroupsText.Text = $"Groups ({_center.Groups.Count})";
        AutomationProperties.SetName(GroupsButton, $"Groups, {_center.Groups.Count}");

        ArtifactsPage.ShowChips(AccountChips, _center, "artifacts");

        FilterBar.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        SignedOutPanel.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        var shown = signedIn ? Shown() : [];
        CountText.Text = shown.Count == 1 ? "1 artifact" : $"{shown.Count} artifacts";
        EmptyText.Visibility = signedIn && shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _center.AnyLoading ? "Asking claude.ai…" : SearchBox.Text.Trim().Length == 0 ? "No artifacts" : "Nothing matches";
        ArtifactList.Visibility = shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Rebuilt only when what it shows has changed: a rebuild closes any menu
        // open on a row, and a load raises Changed several times over.
        var signature = string.Join("\n", shown.Select(a =>
            $"{a.Id}|{a.Title}|{a.Audience}|{_center.NameOf(a.Account)}|{a.Duplicable}|{a.DuplicateAvailable}"))
            + "\n" + string.Join(",", _center.Busy.Order()) + "\n" + string.Join(",", _center.Groups.Select(g => $"{g.Name}{g.Emails.Count}{g.CommonRole}"));
        if (signature == _shownSignature) return;
        _shownSignature = signature;
        ArtifactList.Items.Clear();
        foreach (var artifact in shown) ArtifactList.Items.Add(new ArtifactRow(_center, artifact, XamlRoot).Element);
    }

    /// The account chips, rebuilt only when the accounts themselves change; a refresh moves
    /// each through loading and back, and a rebuilt chip drew its fill from blank, a blink.
    public static void ShowChips(Panel chips, ArtifactCenter center, string scope)
    {
        var signature = string.Join("\n", center.Accounts.Select(a => $"{a.Id}|{center.NameOf(a.Id)}|{a.Email}"));
        if (chips.Tag as string != signature)
        {
            chips.Tag = signature;
            chips.Children.Clear();
            foreach (var account in center.Accounts) chips.Children.Add(AccountChip(center, account, scope));
            return;
        }
        foreach (var (chip, account) in chips.Children.OfType<DropDownButton>().Zip(center.Accounts))
        {
            var (color, help) = Look(center.StateOf(account.Id));
            if (chip.Content is Panel { Children: [Microsoft.UI.Xaml.Shapes.Ellipse dot, ..] }) dot.Fill = new SolidColorBrush(color);
            ToolTipService.SetToolTip(chip, help);
            AutomationProperties.SetName(chip, $"{center.NameOf(account.Id)}, {help}");
        }
    }

    private static (Windows.UI.Color, string) Look(ArtifactCenter.AccountState state) => state switch
    {
        ArtifactCenter.AccountState.Ready => (Microsoft.UI.Colors.SeaGreen, "Signed in"),
        ArtifactCenter.AccountState.SignedOut => (Microsoft.UI.Colors.DarkOrange, "Signed out · sign in again"),
        ArtifactCenter.AccountState.Failed => (Microsoft.UI.Colors.DarkOrange, "claude.ai didn't answer"),
        ArtifactCenter.AccountState.Loading => (Microsoft.UI.Colors.Gray, "Asking claude.ai…"),
        _ => (Microsoft.UI.Colors.Gray, "Not asked yet"),
    };

    /// One signed-in account, with its way to sign in again or out. The same chip
    /// heads Artifacts, Projects and Search Chats, since the sign-in behind it is
    /// one sign-in for all three.
    public static FrameworkElement AccountChip(ArtifactCenter center, ArtifactAccount account, string scope)
    {
        var state = center.StateOf(account.Id);
        var name = center.NameOf(account.Id);
        var (color, help) = Look(state);
        var menu = new MenuFlyout();
        if (account.Email is { } email && email != name) menu.Items.Add(new MenuFlyoutItem { Text = email, IsEnabled = false });
        var again = new MenuFlyoutItem { Text = "Sign In Again…" };
        again.Click += (_, _) => center.SignInAgain(account.Id);
        var signOut = new MenuFlyoutItem { Text = "Sign Out" };
        signOut.Click += (_, _) => center.SignOut(account.Id);
        menu.Items.Add(again);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(signOut);

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAccessibilityView(dot, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        content.Children.Add(dot);
        content.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
        var chip = new DropDownButton { Content = content, Flyout = menu, Padding = new Thickness(8, 3, 8, 4) };
        ToolTipService.SetToolTip(chip, help);
        AutomationProperties.SetName(chip, $"{name}, {help}");
        AutomationProperties.SetAutomationId(chip, $"{scope}.account.{account.Id.ToString()[..8]}");
        return chip;
    }

    public static string Glyph(ArtifactAccess access) => access switch
    {
        ArtifactAccess.OnlyYou => "",
        ArtifactAccess.Invited => "",
        ArtifactAccess.Organization => "",
        _ => "",
    };

    public static string When(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("t") : local.ToString("d MMM yyyy");
    }
}

/// One artifact, with who can open it and what its owner may do with it.
internal sealed class ArtifactRow
{
    public FrameworkElement Element { get; }
    private readonly ArtifactCenter _center;
    private readonly Artifact _artifact;
    private readonly XamlRoot _root;
    private string Token => _artifact.Id.Length > 8 ? _artifact.Id[..8] : _artifact.Id;

    public ArtifactRow(ArtifactCenter center, Artifact artifact, XamlRoot root)
    {
        _center = center;
        _artifact = artifact;
        _root = root;

        var grid = new Grid { ColumnSpacing = 6, Padding = new Thickness(0, 4, 0, 4) };
        foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });

        var icon = new FontIcon { Glyph = artifact.Kind == "page" ? "" : "", FontSize = 16, Width = 18, Margin = new Thickness(0, 0, 4, 0),
            Foreground = Secondary };
        AutomationProperties.SetAccessibilityView(icon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        grid.Children.Add(icon);

        var subtitle = Subtitle();
        var words = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = artifact.Title, TextTrimming = TextTrimming.CharacterEllipsis });
        words.Children.Add(new TextBlock { Text = subtitle, TextTrimming = TextTrimming.CharacterEllipsis,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = Secondary });
        AutomationProperties.SetName(words, $"{artifact.Title}, {subtitle}");
        AutomationProperties.SetAutomationId(words, $"artifact.{Token}");
        Place(grid, words, 1);

        Place(grid, Access(), 2);
        Place(grid, ShareMenu(), 3);
        Place(grid, IconButton("", "Copy link", $"Copy Link to {artifact.Title}", "copy-link", artifact.OpenUrl is not null,
            () => center.CopyLink(artifact)), 4);
        Place(grid, IconButton("", "Open in the browser", $"Open {artifact.Title}", "open", artifact.OpenUrl is not null,
            () => { if (Uri.TryCreate(artifact.OpenUrl, UriKind.Absolute, out var url)) _ = Windows.System.Launcher.LaunchUriAsync(url); }), 5);
        Place(grid, Actions(), 6);
        Element = grid;
    }

    private static Brush Secondary => Themed.Brush("TextFillColorSecondaryBrush");

    private static void Place(Grid grid, FrameworkElement element, int column)
    {
        Grid.SetColumn(element, column);
        element.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(element);
    }

    private string Subtitle()
    {
        var who = _center.NameOf(_artifact.Account);
        var owner = _artifact.IsOwn ? who : $"Shared with {who}";
        return _artifact.SortTime is { } time ? $"{owner} · {ArtifactsPage.When(time)}" : owner;
    }

    private bool IsBusy(params string[] actions) => actions.Any(a => _center.Busy.Contains($"{a}-{_artifact.Id}"));

    private Button IconButton(string glyph, string help, string name, string id, bool enabled, Action click)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 14 }, Padding = new Thickness(6),
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"], IsEnabled = enabled };
        button.Click += (_, _) => click();
        ToolTipService.SetToolTip(button, help);
        AutomationProperties.SetName(button, name);
        AutomationProperties.SetAutomationId(button, $"artifact.{Token}.{id}");
        return button;
    }

    private static FrameworkElement Working() => new ProgressRing { Width = 16, Height = 16, IsActive = true, Margin = new Thickness(6) };

    private DropDownButton MenuButton(FrameworkElement content, string help, string name, string id, MenuFlyout menu, bool enabled)
    {
        var button = new DropDownButton { Content = content, Flyout = menu, Padding = new Thickness(6, 4, 6, 4), IsEnabled = enabled,
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"] };
        ToolTipService.SetToolTip(button, help);
        AutomationProperties.SetName(button, name);
        AutomationProperties.SetAutomationId(button, $"artifact.{Token}.{id}");
        return button;
    }

    /// Only the owner can change who opens it; for anyone else it is said, not
    /// offered.
    private FrameworkElement Access()
    {
        var current = _artifact.Access;
        var glyph = new FontIcon { Glyph = ArtifactsPage.Glyph(current), FontSize = 14 };
        if (!_artifact.IsOwn)
        {
            glyph.Foreground = Secondary;
            glyph.Margin = new Thickness(6);
            ToolTipService.SetToolTip(glyph, current.Label());
            AutomationProperties.SetAccessibilityView(glyph, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            return glyph;
        }
        if (IsBusy("access")) return Working();
        var menu = new MenuFlyout();
        void Choice(string text, ArtifactAccess access, bool? makePublic)
        {
            var item = new RadioMenuFlyoutItem { Text = text, GroupName = $"access-{_artifact.Id}", IsChecked = current.MenuChoice() == access };
            // The organization entry only says what it is; Graft does not share
            // with an organization.
            if (makePublic is { } isPublic) item.Click += (_, _) => _center.SetPublic(_artifact, isPublic);
            else item.IsEnabled = false;
            menu.Items.Add(item);
        }
        Choice("Only People Invited", ArtifactAccess.Invited, false);
        if (current == ArtifactAccess.Organization) Choice("Your Organization", ArtifactAccess.Organization, null);
        Choice("Anyone with the Link", ArtifactAccess.AnyoneWithLink, true);
        return MenuButton(glyph, current.Label(), $"Who Can Open {_artifact.Title}, {current.Label()}", "access", menu, true);
    }

    private FrameworkElement ShareMenu()
    {
        if (IsBusy("share")) return Working();
        var menu = new MenuFlyout();
        if (_center.Groups.Count == 0) menu.Items.Add(new MenuFlyoutItem { Text = "No groups yet", IsEnabled = false });
        foreach (var group in _center.Groups)
        {
            var item = new MenuFlyoutItem { Text = $"{group.Name} ({group.Emails.Count}) · {GroupsEditor.RoleSummary(group)}", IsEnabled = group.Emails.Count > 0 };
            item.Click += (_, _) => _center.Share(_artifact, group);
            menu.Items.Add(item);
        }
        return MenuButton(new FontIcon { Glyph = "", FontSize = 14 },
            _artifact.IsOwn ? "Share with a group" : "Only its owner can share it",
            $"Share {_artifact.Title} with a Group", "share", menu, _artifact.IsOwn);
    }

    /// What claude.ai's own menu offers for an artifact, each only where the list
    /// says claude.ai would allow it.
    private FrameworkElement Actions()
    {
        if (IsBusy("rename", "duplicate", "delete", "export")) return Working();
        var menu = new MenuFlyout();
        if (_artifact.IsOwn && _artifact.Renamable)
        {
            var rename = new MenuFlyoutItem { Text = "Rename…" };
            rename.Click += async (_, _) => await AskRename();
            menu.Items.Add(rename);
        }
        if (_artifact.Duplicable)
        {
            var duplicate = new MenuFlyoutItem { Text = "Duplicate", IsEnabled = _artifact.DuplicateAvailable };
            duplicate.Click += (_, _) => _center.Duplicate(_artifact);
            menu.Items.Add(duplicate);
        }
        var export = new MenuFlyoutSubItem { Text = "Export" };
        foreach (var format in Enum.GetValues<ArtifactExportFormat>())
        {
            var item = new MenuFlyoutItem { Text = format.Label() + "…" };
            item.Click += (_, _) => _center.Export(_artifact, format, name => AskWhere(name, format));
            export.Items.Add(item);
        }
        menu.Items.Add(export);
        if (_artifact.IsOwn)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var delete = new MenuFlyoutItem { Text = "Delete…" };
            delete.Click += async (_, _) => await AskDelete();
            menu.Items.Add(delete);
        }
        return MenuButton(new FontIcon { Glyph = "", FontSize = 14 }, "Rename, duplicate, export or delete",
            $"More for {_artifact.Title}", "more", menu, true);
    }

    private async Task<string?> AskWhere(string fileName, ArtifactExportFormat format)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(fileName) };
        var extension = Path.GetExtension(fileName);
        picker.FileTypeChoices.Add(format == ArtifactExportFormat.Html && extension == ".zip" ? "Zip archive" : format.Label(), [extension]);
        var window = Microsoft.UI.Win32Interop.GetWindowFromWindowId(_root.ContentIslandEnvironment.AppWindowId);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, window);
        return (await picker.PickSaveFileAsync())?.Path;
    }

    private async Task AskRename()
    {
        var box = new TextBox { Text = _artifact.Title, SelectionStart = 0, SelectionLength = _artifact.Title.Length };
        AutomationProperties.SetName(box, "Title");
        AutomationProperties.SetAutomationId(box, $"artifact.{Token}.newTitle");
        var dialog = new ContentDialog
        {
            XamlRoot = _root, Title = "Rename Artifact", Content = box,
            PrimaryButtonText = "Rename", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };
        Dialogs.Prepare(dialog, _root);
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) _center.Rename(_artifact, box.Text);
    }

    private async Task AskDelete()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = _root, Title = $"Delete “{_artifact.Title}”?",
            Content = "claude.ai keeps no copy, and everyone it was shared with loses it too.",
            PrimaryButtonText = "Delete", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
        };
        Dialogs.Prepare(dialog, _root);
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) _center.Delete(_artifact);
    }
}
