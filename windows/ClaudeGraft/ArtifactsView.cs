using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace ClaudeGraft;

public sealed class ArtifactsView : UserControl, IDisposable
{
    private readonly AccountCenter _accounts;
    private readonly ArtifactCenter _center;
    private readonly StackPanel _list = new() { Spacing = 8 };
    private readonly TextBox _search = Named(new TextBox { PlaceholderText = "Filter artifacts", MinWidth = 180 }, "Filter artifacts by title");
    private readonly ComboBox _filter = Named(new ComboBox { ItemsSource = new[] { "All artifacts", "My artifacts", "Shared with me" }, SelectedIndex = 0 }, "Artifact ownership filter");
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _refresh;
    private bool _dialogOpen;

    public ArtifactsView(AccountCenter accounts)
    {
        _accounts = accounts;
        _center = new ArtifactCenter(accounts);
        var page = new Grid { Padding = new Thickness(22), RowSpacing = 12 };
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new StackPanel { Spacing = 8 };
        heading.Children.Add(new TextBlock { Text = "Artifacts", FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = "Artifacts from your signed-in claude.ai accounts. Sharing with a group sends an invitation to each address.", TextWrapping = TextWrapping.Wrap });
        heading.Children.Add(new AccountsBar(accounts)); page.Children.Add(heading);
        var filters = new Grid { ColumnSpacing = 8 };
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filters.Children.Add(_search); Grid.SetColumn(_filter, 1); filters.Children.Add(_filter);
        Grid.SetRow(filters, 1); page.Children.Add(filters);
        var scroll = new ScrollViewer { Content = _list, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 2); page.Children.Add(scroll);
        var bottom = new StackPanel { Spacing = 8 };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _refresh = Button("Refresh artifacts", async () => await _center.RefreshAsync());
        actions.Children.Add(_refresh);
        actions.Children.Add(Button("Manage groups…", ManageGroupsAsync));
        bottom.Children.Add(actions); bottom.Children.Add(_status);
        Grid.SetRow(bottom, 3); page.Children.Add(bottom); Content = page;
        _search.TextChanged += (_, _) => Render(); _filter.SelectionChanged += (_, _) => Render();
        _center.Changed += (_, _) => Render();
        Loaded += async (_, _) => { await accounts.InitializeAsync(); await _center.RefreshAsync(); };
        Render();
    }

    public void Dispose() => _center.Dispose();

    private static T Named<T>(T element, string name) where T : DependencyObject
    { AutomationProperties.SetName(element, name); return element; }
    private static Button Button(string title, Func<Task> action)
    {
        var button = Named(new Button { Content = title }, title);
        button.Click += async (_, _) => await action();
        return button;
    }
    private void Render()
    {
        _list.Children.Clear(); _refresh.IsEnabled = !_center.Loading;
        _status.Text = _center.Status ?? (_center.Loading ? "Loading artifacts…" : "");
        var shown = _center.Artifacts.Where(a => (_filter.SelectedIndex != 1 || a.IsOwn) && (_filter.SelectedIndex != 2 || !a.IsOwn)
            && a.Title.Contains(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (shown.Count == 0)
            _list.Children.Add(new TextBlock { Text = _accounts.Accounts.Count == 0 ? "Add an account to see its artifacts. Use Continue with email and the code claude.ai sends you."
                : _center.Loading ? "Loading artifacts…" : "No artifacts match this view.", TextWrapping = TextWrapping.Wrap });
        foreach (var artifact in shown) _list.Children.Add(Row(artifact));
    }
    private FrameworkElement Row(Artifact artifact)
    {
        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(12) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 4 };
        text.Children.Add(new TextBlock { Text = artifact.Title, FontSize = 16, TextWrapping = TextWrapping.Wrap });
        var account = _accounts.Accounts.FirstOrDefault(a => a.Id == artifact.Account);
        text.Children.Add(new TextBlock { Text = $"{account?.DisplayName ?? "Account"} · {ArtifactApi.AccessLabel(artifact.Access)}", FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap });
        row.Children.Add(text);
        var menu = new MenuFlyout();
        void Item(string title, Func<Task> action, bool enabled = true)
        {
            var item = Named(new MenuFlyoutItem { Text = title, IsEnabled = enabled }, title + " — " + artifact.Title);
            item.Click += async (_, _) => await action(); menu.Items.Add(item);
        }
        Item("Open in browser", () => { if (artifact.OpenUrl is not null) Links.Open(artifact.OpenUrl.AbsoluteUri); return Task.CompletedTask; }, artifact.OpenUrl is not null);
        Item("Copy link", () =>
        {
            if (artifact.OpenUrl is null) return Task.CompletedTask;
            var data = new DataPackage(); data.SetText(artifact.OpenUrl.AbsoluteUri); Clipboard.SetContent(data);
            _center.Report(artifact.Access is ArtifactAccess.OnlyYou or ArtifactAccess.Invited ? "Link copied. Only people with access can open it." : "Link copied.");
            return Task.CompletedTask;
        }, artifact.OpenUrl is not null);
        if (artifact.IsOwn)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var access = new MenuFlyoutSubItem { Text = "Who can open" };
            void Choice(string title, bool selected, Func<Task> action, bool enabled = true)
            {
                var choice = Named(new ToggleMenuFlyoutItem { Text = title, IsChecked = selected, IsEnabled = enabled }, title);
                choice.Click += async (_, _) => { if (selected) choice.IsChecked = true; else await action(); }; access.Items.Add(choice);
            }
            Choice("Only people invited", artifact.Access is ArtifactAccess.OnlyYou or ArtifactAccess.Invited, () => _center.SetPublicAsync(artifact, false));
            if (artifact.Access == ArtifactAccess.Organization) Choice("Your organization", true, () => Task.CompletedTask, false);
            Choice("Anyone with the link", artifact.Access == ArtifactAccess.AnyoneWithLink, () => _center.SetPublicAsync(artifact, true));
            menu.Items.Add(access);
            var share = new MenuFlyoutSubItem { Text = "Share with group", IsEnabled = _center.GroupStore.Groups.Any(g => g.Emails.Count > 0) };
            foreach (var group in _center.GroupStore.Groups.Where(g => g.Emails.Count > 0))
            {
                var item = Named(new MenuFlyoutItem { Text = group.Name }, "Share with " + group.Name);
                item.Click += async (_, _) =>
                {
                    var confirmation = new TextBlock { Text = $"Invite {group.Emails.Count} people to “{artifact.Title}”? claude.ai emails each person. If the artifact does not support their role, they receive the closest lower role.\n\n"
                        + string.Join("\n", group.Emails.Select(e => $"{e} · {ArtifactApi.RoleLabel(group.RoleOf(e))}")), TextWrapping = TextWrapping.Wrap };
                    if (await Ask("Share with " + group.Name, confirmation, "Send invitations")) await _center.ShareAsync(artifact, group);
                };
                share.Items.Add(item);
            }
            menu.Items.Add(share);
            Item("Rename…", async () =>
            {
                var input = Named(new TextBox { Text = artifact.Title }, "Artifact title");
                if (await Ask("Rename artifact", input, "Rename")) await _center.RenameAsync(artifact, input.Text);
            }, artifact.Renamable);
        }
        Item("Duplicate", () => _center.DuplicateAsync(artifact), artifact.Duplicable && artifact.DuplicateAvailable);
        var exports = new MenuFlyoutSubItem { Text = "Export" };
        foreach (var format in Enum.GetValues<ArtifactExportFormat>())
        {
            var item = Named(new MenuFlyoutItem { Text = ArtifactExport.Label(format) }, "Export as " + ArtifactExport.Label(format));
            item.Click += async (_, _) => await _center.ExportAsync(artifact, format, async single =>
            {
                var picker = new FileSavePicker { SuggestedFileName = ArtifactExport.FileName(artifact.Title, format, single) };
                picker.FileTypeChoices.Add(ArtifactExport.Label(format), new List<string> { "." + ArtifactExport.Extension(format, single) });
                WinRT.Interop.InitializeWithWindow.Initialize(picker, Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
                return (await picker.PickSaveFileAsync())?.Path;
            });
            exports.Items.Add(item);
        }
        menu.Items.Add(exports);
        if (artifact.IsOwn) Item("Delete…", async () =>
        {
            if (await Ask("Delete artifact?", new TextBlock { Text = $"Delete “{artifact.Title}” permanently? claude.ai keeps no bin or copy to restore.", TextWrapping = TextWrapping.Wrap }, "Delete"))
                await _center.DeleteAsync(artifact);
        });
        var actions = Named(new Button { Content = _center.IsBusy(artifact) ? "Working…" : "Actions", Flyout = menu,
            IsEnabled = !_center.IsBusy(artifact), VerticalAlignment = VerticalAlignment.Center }, "Actions for " + artifact.Title);
        Grid.SetColumn(actions, 1); row.Children.Add(actions);
        return new Border { Child = row, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] };
    }

    private async Task<bool> Ask(string title, FrameworkElement content, string accept)
    {
        if (_dialogOpen) return false;
        _dialogOpen = true;
        try
        {
            var dialog = new ContentDialog { Title = title, Content = new ScrollViewer { Content = content, MaxHeight = 420 },
                PrimaryButtonText = accept, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally { _dialogOpen = false; }
    }

    private async Task ManageGroupsAsync()
    {
        if (!_center.GroupStore.CanWrite) { _center.Report(_center.GroupStore.Notice ?? "Groups cannot be saved."); return; }
        var groups = _center.GroupStore.Groups.Select(g => new ArtifactGroup { Id = g.Id, Name = g.Name,
            Emails = g.Emails.ToList(), Roles = new(g.Roles, StringComparer.OrdinalIgnoreCase) }).ToList();
        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        var picker = Named(new ComboBox { PlaceholderText = "Choose a group", DisplayMemberPath = "Name", HorizontalAlignment = HorizontalAlignment.Stretch }, "Sharing group");
        var name = Named(new TextBox { Header = "Group name" }, "Group name");
        var members = new StackPanel { Spacing = 6 };
        var addresses = Named(new TextBox { Header = "Email addresses", PlaceholderText = "Paste addresses or a mail header", AcceptsReturn = true, MinHeight = 60 }, "Email addresses to add");
        var allRoles = Named(new ComboBox { Header = "Role for everyone", ItemsSource = Enum.GetValues<ArtifactRole>(), SelectedIndex = 0 }, "Role for everyone");
        ArtifactGroup? selected = null;
        bool loading = false;
        void Members()
        {
            members.Children.Clear();
            foreach (var email in selected?.Emails.ToArray() ?? [])
            {
                var line = new Grid { ColumnSpacing = 6 };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.Children.Add(new TextBlock { Text = email, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                var role = Named(new ComboBox { ItemsSource = Enum.GetValues<ArtifactRole>(), SelectedItem = selected!.RoleOf(email) }, "Role for " + email);
                role.SelectionChanged += (_, _) => { if (selected is not null && role.SelectedItem is ArtifactRole value) selected.Roles[email] = value; };
                Grid.SetColumn(role, 1); line.Children.Add(role);
                var remove = Named(Button("Remove", () => { selected?.Emails.Remove(email); selected?.Roles.Remove(email); Members(); return Task.CompletedTask; }), "Remove " + email);
                Grid.SetColumn(remove, 2); line.Children.Add(remove);
                members.Children.Add(line);
            }
        }
        void Pick(ArtifactGroup? group)
        {
            loading = true; selected = group; name.Text = group?.Name ?? "";
            name.IsEnabled = addresses.IsEnabled = allRoles.IsEnabled = group is not null;
            allRoles.SelectedItem = group?.CommonRole; Members(); loading = false;
        }
        void Populate(ArtifactGroup? chosen)
        { picker.ItemsSource = null; picker.ItemsSource = groups; picker.SelectedItem = chosen; Pick(chosen); }
        picker.SelectionChanged += (_, _) => Pick(picker.SelectedItem as ArtifactGroup);
        name.TextChanged += (_, _) => { if (!loading && selected is not null) selected.Name = name.Text; };
        allRoles.SelectionChanged += (_, _) => { if (!loading && selected is not null && allRoles.SelectedItem is ArtifactRole role) { selected.SetRoleForEveryone(role); Members(); } };
        panel.Children.Add(picker);
        var manage = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        manage.Children.Add(Button("Add group", () => { var group = new ArtifactGroup { Name = "New group" }; groups.Add(group); Populate(group); return Task.CompletedTask; }));
        manage.Children.Add(Button("Delete group", () => { if (selected is not null) groups.Remove(selected); Populate(groups.FirstOrDefault()); return Task.CompletedTask; }));
        panel.Children.Add(manage); panel.Children.Add(name); panel.Children.Add(allRoles); panel.Children.Add(members); panel.Children.Add(addresses);
        panel.Children.Add(Button("Add addresses", () =>
        {
            if (selected is null) return Task.CompletedTask;
            var role = selected.CommonRole ?? ArtifactRole.Reader;
            foreach (var email in ArtifactApi.Addresses(addresses.Text))
                if (!selected.Emails.Contains(email, StringComparer.OrdinalIgnoreCase)) { selected.Emails.Add(email); selected.Roles[email] = role; }
            addresses.Text = ""; Members(); return Task.CompletedTask;
        }));
        Populate(groups.FirstOrDefault());
        if (!await Ask("Sharing groups", panel, "Save groups")) return;
        try
        {
            if (groups.Any(g => string.IsNullOrWhiteSpace(g.Name))) throw new IOException("Give each group a name before saving.");
            _center.GroupStore.Groups.Clear(); _center.GroupStore.Groups.AddRange(groups); _center.GroupStore.Save();
            _center.Report("Sharing groups saved.");
        }
        catch (Exception e) { _center.Report(e.Message); }
    }
}
