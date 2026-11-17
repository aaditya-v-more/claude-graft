using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClaudeGraft;

/// <summary>
/// Groups on the left; the chosen one's name, and each person in it with what
/// they may do, on the right. Every change is saved as it is made.
/// </summary>
public sealed class GroupsEditor
{
    private readonly ArtifactCenter _center;
    private readonly ContentDialog _dialog;
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly ContentControl _detail = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly Button _delete;
    private Guid? _selection;

    public static string RoleSummary(ArtifactGroup group) => group.CommonRole?.Label() ?? "Mixed";

    private static readonly ArtifactRole[] Roles = Enum.GetValues<ArtifactRole>();

    public GroupsEditor(ArtifactCenter center, XamlRoot root)
    {
        _center = center;
        AutomationProperties.SetName(_list, "Groups");
        AutomationProperties.SetAutomationId(_list, "groups.list");
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is ListViewItem { Tag: Guid id } && id != _selection)
            {
                _selection = id;
                ShowDetail();
            }
        };

        var add = SmallButton("", "New Group", "groups.add");
        add.Click += (_, _) =>
        {
            _selection = _center.AddGroup("New Group").Id;
            Render();
        };
        _delete = SmallButton("", "Delete Group", "groups.delete");
        _delete.Click += (_, _) =>
        {
            if (_selection is { } id) _center.DeleteGroup(id);
            _selection = _center.Groups.FirstOrDefault()?.Id;
            Render();
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(add);
        buttons.Children.Add(_delete);

        var left = new Grid { Width = 180 };
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.Children.Add(_list);
        Grid.SetRow(buttons, 1);
        left.Children.Add(buttons);

        var body = new Grid { ColumnSpacing = 16, Width = 620, Height = 380 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.Children.Add(left);
        Grid.SetColumn(_detail, 1);
        body.Children.Add(_detail);
        var note = new TextBlock
        {
            Text = "Each person is invited with the role beside them", Margin = new Thickness(0, 10, 0, 0),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = Themed.Brush("TextFillColorSecondaryBrush"),
        };
        ToolTipService.SetToolTip(note, ArtifactsPage.Note);
        AutomationProperties.SetAutomationId(note, "groups.note");
        Grid.SetRow(note, 1);
        Grid.SetColumnSpan(note, 2);
        body.Children.Add(note);

        _dialog = new ContentDialog
        {
            XamlRoot = root, Title = "Groups", Content = body,
            CloseButtonText = "Done", DefaultButton = ContentDialogButton.Close,
        };
        Dialogs.Prepare(_dialog, root);
        _dialog.Resources["ContentDialogMaxWidth"] = 720.0;
        _selection = _center.Groups.FirstOrDefault()?.Id;
        Render();
    }

    public async Task ShowAsync() => await _dialog.ShowAsync();

    private static Button SmallButton(string glyph, string name, string id)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 12 }, Padding = new Thickness(8, 5, 8, 5) };
        ToolTipService.SetToolTip(button, name);
        AutomationProperties.SetName(button, name);
        AutomationProperties.SetAutomationId(button, id);
        return button;
    }

    private void Render()
    {
        _list.Items.Clear();
        foreach (var group in _center.Groups)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = group.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            var count = new TextBlock { Text = group.Emails.Count.ToString(), Foreground = Themed.Brush("TextFillColorSecondaryBrush") };
            Grid.SetColumn(count, 1);
            row.Children.Add(count);
            var item = new ListViewItem { Content = row, Tag = group.Id };
            AutomationProperties.SetName(item, $"{group.Name}, {group.Emails.Count} people, {RoleSummary(group)}");
            _list.Items.Add(item);
            if (group.Id == _selection) _list.SelectedItem = item;
        }
        _delete.IsEnabled = _selection is not null;
        ShowDetail();
    }

    /// The name in the list follows the name being typed, without rebuilding the
    /// detail under the caret.
    private void RenameInList(ArtifactGroup group)
    {
        foreach (var item in _list.Items.OfType<ListViewItem>())
            if (item.Tag is Guid id && id == group.Id && item.Content is Grid { Children: [TextBlock name, ..] })
                name.Text = group.Name;
    }

    private void ShowDetail()
    {
        _delete.IsEnabled = _selection is not null;
        if (_center.Groups.FirstOrDefault(g => g.Id == _selection) is not { } group)
        {
            _detail.Content = new TextBlock
            {
                Text = _center.Groups.Count == 0 ? "No groups yet" : "Select a group",
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Foreground = Themed.Brush("TextFillColorSecondaryBrush"),
            };
            return;
        }

        var panel = new StackPanel { Spacing = 10 };
        var name = new TextBox { Header = "Name", Text = group.Name };
        AutomationProperties.SetAutomationId(name, "groups.name");
        name.TextChanged += (_, _) =>
        {
            group.Name = name.Text;
            _center.GroupsChanged();
            RenameInList(group);
        };
        panel.Children.Add(name);

        var everyone = new ComboBox { Header = "Everyone", IsEnabled = group.Emails.Count > 0, MinWidth = 160 };
        foreach (var role in Roles) everyone.Items.Add(role.Label());
        if (group.CommonRole is null) everyone.Items.Add("Mixed");
        everyone.SelectedIndex = group.CommonRole is { } common ? Array.IndexOf(Roles, common) : Roles.Length;
        AutomationProperties.SetAutomationId(everyone, "groups.everyone");
        everyone.SelectionChanged += (_, _) =>
        {
            if (everyone.SelectedIndex < 0 || everyone.SelectedIndex >= Roles.Length || Roles[everyone.SelectedIndex] == group.CommonRole) return;
            group.SetRoleForEveryone(Roles[everyone.SelectedIndex]);
            _center.GroupsChanged();
            Render();
        };
        panel.Children.Add(everyone);

        panel.Children.Add(new TextBlock { Text = $"People · {group.Emails.Count}", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        var position = 0;
        foreach (var email in group.Emails.ToList()) panel.Children.Add(Member(group, email, position++));

        var adding = new TextBox { PlaceholderText = "Add addresses" };
        AutomationProperties.SetName(adding, "Add addresses");
        AutomationProperties.SetAutomationId(adding, "groups.newAddresses");
        var addButton = new Button { Content = "Add", IsEnabled = false };
        AutomationProperties.SetAutomationId(addButton, "groups.addAddresses");
        adding.TextChanged += (_, _) => addButton.IsEnabled = GroupAddresses.Parse(adding.Text).Count > 0;
        // Pasted addresses join with the role everyone already has, so adding to
        // a group of editors adds editors.
        void Add()
        {
            var addresses = GroupAddresses.Parse(adding.Text);
            if (addresses.Count == 0) return;
            var role = group.CommonRole ?? ArtifactRole.Reader;
            foreach (var address in addresses.Where(a => !group.Emails.Contains(a)))
            {
                group.Emails.Add(address);
                group.SetRole(role, address);
            }
            _center.GroupsChanged();
            Render();
        }
        addButton.Click += (_, _) => Add();
        adding.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            Add();
        };
        var addRow = new Grid { ColumnSpacing = 6 };
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        addRow.Children.Add(adding);
        Grid.SetColumn(addButton, 1);
        addRow.Children.Add(addButton);
        panel.Children.Add(addRow);

        _detail.Content = new ScrollViewer { Content = panel, Padding = new Thickness(0, 0, 12, 0) };
    }

    private FrameworkElement Member(ArtifactGroup group, string email, int position)
    {
        var row = new Grid { ColumnSpacing = 6 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var address = new TextBlock { Text = email, FontFamily = new FontFamily("Cascadia Mono, Consolas"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(address, email);
        row.Children.Add(address);

        var role = new ComboBox { MinWidth = 120 };
        foreach (var r in Roles) role.Items.Add(r.Label());
        role.SelectedIndex = Array.IndexOf(Roles, group.RoleOf(email));
        AutomationProperties.SetName(role, $"Role for {email}");
        AutomationProperties.SetAutomationId(role, $"groups.member.{position}.role");
        role.SelectionChanged += (_, _) =>
        {
            if (role.SelectedIndex < 0 || Roles[role.SelectedIndex] == group.RoleOf(email)) return;
            group.SetRole(Roles[role.SelectedIndex], email);
            _center.GroupsChanged();
            Render();
        };
        Grid.SetColumn(role, 1);
        row.Children.Add(role);

        var remove = SmallButton("", $"Remove {email}", $"groups.member.{position}.remove");
        ToolTipService.SetToolTip(remove, "Remove from the group");
        remove.Click += (_, _) =>
        {
            group.Emails.Remove(email);
            group.Roles.Remove(email);
            _center.GroupsChanged();
            Render();
        };
        Grid.SetColumn(remove, 2);
        row.Children.Add(remove);
        return row;
    }
}
