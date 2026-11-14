using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace ClaudeGraft;

public sealed class AccountsBar : UserControl
{
    private readonly AccountCenter _center;
    private readonly StackPanel _accounts = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly TextBlock _status = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly Button _add = new() { Content = "Add Account…" };
    public AccountsBar(AccountCenter center)
    {
        _center = center;
        var panel = new StackPanel { Spacing = 6 };
        var controls = new Grid { ColumnSpacing = 8 };
        controls.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        controls.Children.Add(_add);
        var scroll = new ScrollViewer { Content = _accounts, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(scroll, 1);
        controls.Children.Add(scroll);
        panel.Children.Add(controls);
        panel.Children.Add(new TextBlock { Text = AccountSignInWindow.Hint, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_status);
        Content = panel;
        AutomationProperties.SetAutomationId(_add, "AddWebAccount");
        _add.Click += async (_, _) => await RunAsync(() => _center.SignInAsync());
        Loaded += async (_, _) => { _center.Changed += Refresh; _center.StateChanged += Refresh; Refresh(this, EventArgs.Empty); await RunAsync(_center.InitializeAsync); };
        Unloaded += (_, _) => { _center.Changed -= Refresh; _center.StateChanged -= Refresh; };
    }
    private void Refresh(object? sender, EventArgs e)
    {
        _accounts.Children.Clear();
        foreach (var account in _center.Accounts)
        {
            var name = account.DisplayName + (_center.State(account.Id) == false ? " · Sign in again" : "");
            var button = new Button { Content = name };
            AutomationProperties.SetName(button, name);
            var menu = new MenuFlyout();
            var signIn = new MenuFlyoutItem { Text = "Sign In Again…" };
            signIn.Click += async (_, _) => await RunAsync(() => _center.SignInAsync(account));
            var signOut = new MenuFlyoutItem { Text = "Sign Out" };
            signOut.Click += async (_, _) => await RunAsync(() => _center.SignOutAsync(account));
            menu.Items.Add(signIn); menu.Items.Add(signOut); button.Flyout = menu;
            _accounts.Children.Add(button);
        }
        _status.Text = _center.Notice ?? (_center.Accounts.Count == 0 ? "Add an account to use Artifacts, Projects and Search Chats." : "");
        _status.Visibility = string.IsNullOrEmpty(_status.Text) ? Visibility.Collapsed : Visibility.Visible;
    }
    private async Task RunAsync(Func<Task> operation)
    {
        _add.IsEnabled = false;
        try { await operation(); Refresh(this, EventArgs.Empty); }
        catch (Exception e) { _status.Text = e is WebAccountException or IOException ? e.Message : "The account action could not be completed."; _status.Visibility = Visibility.Visible; }
        finally { _add.IsEnabled = true; }
    }
}
