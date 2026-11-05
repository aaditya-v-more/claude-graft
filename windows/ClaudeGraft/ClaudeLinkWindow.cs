using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ClaudeGraft;

// Browser callbacks can contain a one-use code. Keep it in this short-lived
// process, outside the manager, its diagnostics, and its saved window state.
internal sealed class ClaudeLinkWindow : Window
{
    private readonly ComboBox _profiles = new() { HorizontalAlignment = HorizontalAlignment.Stretch,
        PlaceholderText = "Choose the profile that started sign-in" };
    private readonly Button _continue = new() { Content = "Continue sign-in", IsEnabled = false };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private IReadOnlyList<SignInTarget> _targets = [];
    private string? _link;

    internal ClaudeLinkWindow(string? link)
    {
        Title = "Claude Graft";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(480, 280));
        var panel = new StackPanel { Padding = new Thickness(24), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "Finish signing in to Claude", FontSize = 20 });
        panel.Children.Add(_message);
        panel.Children.Add(_profiles);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var cancel = new Button { Content = "Close" };
        AutomationProperties.SetName(_profiles, "Profile that started sign-in");
        AutomationProperties.SetAutomationId(_profiles, "SignInProfiles");
        AutomationProperties.SetAutomationId(_continue, "ContinueSignIn");
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(_continue);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;
        Closed += (_, _) => _link = null;
        _profiles.SelectionChanged += (_, _) => _continue.IsEnabled = _profiles.SelectedIndex >= 0;
        _continue.Click += async (_, _) =>
        {
            var index = _profiles.SelectedIndex;
            if (_link is not string callback || index < 0 || index >= _targets.Count) return;
            var target = _targets[index];
            _link = null;
            _continue.IsEnabled = _profiles.IsEnabled = false;
            try { await Task.Run(() => ClaudeLinks.Forward(callback, target)); Close(); }
            catch { ShowError("Graft could not reach that running profile. Return to it and start sign-in again."); }
        };
        _link = link;
    }

    internal async Task OpenAsync()
    {
        try
        {
            var kind = _link is null ? ClaudeLinkKind.Invalid : ClaudeLinks.Classify(_link);
            if (kind == ClaudeLinkKind.OpenClaude)
            {
                var link = _link!;
                _link = null;
                await Task.Run(() => ClaudeLinks.Forward(link, null));
                Close();
                return;
            }
            if (kind == ClaudeLinkKind.Invalid)
            {
                ShowError("This Claude link could not be opened.");
                return;
            }
            var targets = await Task.Run(ClaudeLinks.RunningTargets);
            if (targets.Count == 0)
            {
                ShowError("Open the profile you want to sign in to, then start sign-in from that window.");
                return;
            }
            _message.Text = "Which Claude profile did you start this sign-in from?";
            _targets = targets;
            var names = targets.ToLookup(target => target.Name, StringComparer.OrdinalIgnoreCase);
            _profiles.ItemsSource = targets.Select(target => names[target.Name].Count() == 1
                ? target.Name : $"{target.Name} ({Path.GetFileName(target.Profile)})").ToArray();
            _profiles.SelectedIndex = -1;
            Activate();
        }
        catch { ShowError("Graft could not verify your Claude profiles. Check that Claude Desktop is installed and try again."); }
    }

    private void ShowError(string text)
    {
        _link = null;
        _profiles.Visibility = _continue.Visibility = Visibility.Collapsed;
        _message.Text = text;
        Activate();
    }
}
