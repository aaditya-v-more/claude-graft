using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ClaudeGraft;

public sealed class SharedUsageView : Page
{
    private UsagePoolController? controller;
    private string? selectedBorrower;
    private readonly StackPanel content = new() { Spacing = 16, Margin = new Thickness(24), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly InfoBar feedback = new() { IsClosable = true };
    public SharedUsageView()
    {
        Content = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Unloaded += (_, _) => { if (controller is not null) controller.Changed -= Render; };
        Loaded += (_, _) => { if (controller is not null) { controller.Changed -= Render; controller.Changed += Render; Render(); } };
    }
    public SharedUsageView(UsagePoolController controller) : this()
    {
        this.controller = controller;
        Render();
        _ = controller.RefreshAsync();
    }
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (controller is not null) controller.Changed -= Render;
        controller = e.Parameter as UsagePoolController;
        if (controller is not null) { controller.Changed += Render; _ = controller.RefreshAsync(); }
        Render();
    }
    private static TextBlock Text(string value, double size = 14) => new() { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap };
    private static Button Button(string label, string id, RoutedEventHandler handler)
    {
        var button = new Button { Content = label }; AutomationProperties.SetAutomationId(button, id); button.Click += handler; return button;
    }
    private async Task Run(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        { feedback.Title = "Shared Usage"; feedback.Message = e.Message; feedback.Severity = InfoBarSeverity.Error; feedback.IsOpen = true; }
    }
    private void Render()
    {
        content.Children.Clear();
        content.Children.Add(Text("Shared Usage", 26));
        content.Children.Add(Text("Choose the accounts each Claude’s Code tab may borrow from. Requests use available allowance whose week resets soonest; conversations stay with their cached account while it has room. Chat, artifacts, connectors, and Claude in Chrome keep the borrowing Claude’s own account."));
        content.Children.Add(Text("A model request carries its conversation to the lending account. Review the terms of your Claude plans before combining subscriptions."));
        content.Children.Add(feedback);
        if (controller is null) return;
        if (controller.Problem is string problem) content.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Title = "Shared Usage needs attention", Message = problem });
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        toolbar.Children.Add(Button("Refresh", "SharedUsageRefresh", async (_, _) => await Run(controller.RefreshAsync)));
        toolbar.Children.Add(Button("Stop all sharing", "SharedUsageStop", async (_, _) => await Run(controller.StopSharingAsync)));
        content.Children.Add(toolbar);
        content.Children.Add(Text("Borrowing Claude", 18));
        var chooser = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, DisplayMemberPath = "Name", ItemsSource = controller.Borrowers };
        AutomationProperties.SetAutomationId(chooser, "SharedUsageBorrower");
        var selected = controller.Borrowers.FirstOrDefault(b => b.Id == selectedBorrower) ?? controller.Borrowers.FirstOrDefault();
        chooser.SelectedItem = selected; selectedBorrower = selected?.Id;
        chooser.SelectionChanged += (_, _) => { if (chooser.SelectedItem is PoolBorrowerRow item && item.Id != selectedBorrower) { selectedBorrower = item.Id; Render(); } };
        content.Children.Add(chooser);
        if (selected is not null)
        {
            if (selected.Note is string note) content.Children.Add(Text(note));
            if (selected.Lenders.Length > 0) content.Children.Add(Text(selected.Connected ? "Connected to the local usage relay" : "Sharing starts after this Claude is reopened and its Code tab sends a request."));
            content.Children.Add(Text("Accounts available to this Claude", 18));
            foreach (var account in controller.Accounts.Where(a => a.Id != selected.Id))
            {
                var row = new StackPanel { Spacing = 4 };
                var same = account.Account is not null && account.Account == controller.Accounts.FirstOrDefault(a => a.Id == selected.Id)?.Account;
                var check = new CheckBox { Content = account.Name, IsChecked = selected.Lenders.Contains(account.Id), IsEnabled = !same };
                AutomationProperties.SetAutomationId(check, "SharedUsageLender-" + account.Id);
                check.Checked += async (_, _) => await Run(() => controller.SetLenderAsync(selected.Id, account.Id, true));
                check.Unchecked += async (_, _) => await Run(() => controller.SetLenderAsync(selected.Id, account.Id, false));
                row.Children.Add(check);
                row.Children.Add(Text(same ? "This is the same account, so it has the same allowance." : account.State));
                if (account.Limits is { } limits) row.Children.Add(Text($"5 hours: {limits.FiveHour:P0} used · Week: {limits.Week:P0} used"));
                if (account.SavedAccount) row.Children.Add(Button("Forget saved account", "SharedUsageForget-" + account.Id,
                    async (_, _) => await Run(() => controller.ForgetAccountAsync(account.Id))));
                content.Children.Add(row);
            }
            if (controller.SigningIn)
            {
                content.Children.Add(Text("Finish signing in in your browser."));
                content.Children.Add(Button("Cancel sign-in", "SharedUsageCancelSignIn", (_, _) => controller.CancelSignIn()));
            }
            else content.Children.Add(Button("Sign in to another account…", "SharedUsageSignIn", async (_, _) => await Run(() => controller.SignInAsync(selected.Id))));
            content.Children.Add(Text("Open Graft at login so the relay is ready after restarting Windows. A running relay stays available if you close Graft; Stop all sharing restores closed profiles and finishes restoring open profiles after they close."));
            if (!AutoStart.IsEnabled()) content.Children.Add(Button("Open Graft at login", "SharedUsageOpenAtLogin", async (_, _) => await Run(() =>
            { AutoStart.Set(true); Render(); return Task.CompletedTask; })));
        }
        content.Children.Add(Text("Recent Code conversations", 18));
        if (controller.Conversations.Count == 0) content.Children.Add(Text("Conversations appear here after the relay answers a Code request."));
        foreach (var chat in controller.Conversations)
        {
            var row = new StackPanel { Spacing = 5 };
            row.Children.Add(Text(chat.Title)); row.Children.Add(Text(controller.Name(chat.From) + " · Answering account: " + controller.Name(chat.Answering)));
            if (chat.Note is not null) row.Children.Add(Text(chat.Note));
            var choices = new List<KeyValuePair<string?, string>> { new(null, chat.Assigned is null && chat.ServedBy is not null ? "Automatic · now " + controller.Name(chat.ServedBy) : "Automatic") };
            choices.AddRange(chat.Choices.Select(id => new KeyValuePair<string?, string>(id, controller.Name(id))));
            var destination = new ComboBox { ItemsSource = choices, DisplayMemberPath = "Value", SelectedValuePath = "Key", HorizontalAlignment = HorizontalAlignment.Stretch };
            destination.SelectedIndex = Math.Max(0, choices.FindIndex(c => c.Key == chat.Assigned));
            AutomationProperties.SetAutomationId(destination, "SharedUsageConversation-" + chat.Id);
            destination.SelectionChanged += async (_, _) =>
            {
                if (destination.SelectedItem is KeyValuePair<string?, string> choice) await Run(() => controller.AssignAsync(chat.Id, choice.Key));
            };
            row.Children.Add(destination); content.Children.Add(row);
        }
    }
}
