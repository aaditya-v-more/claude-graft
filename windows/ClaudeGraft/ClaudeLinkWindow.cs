// Built after ClaudeLinkWindow in James's Windows port (aaditya-v-more/claude-graft#9).

using System.Threading.Tasks;
using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ClaudeGraft;

/// <summary>
/// Asks which running Claude started a sign-in, in a process of its own that
/// exits when this closes. The link may carry a one-time code, so it stays in
/// this window: never in the manager, its diagnostics or its saved state. The
/// diagnostics log only what became of it, so a sign-in that never landed can be traced.
/// </summary>
internal sealed class ClaudeLinkWindow : Window
{
    private readonly ComboBox _profiles = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        PlaceholderText = L10n.Text("Choose a Claude"),
    };
    private readonly Button _continue = new()
    {
        Content = L10n.Text("Continue Sign-In"),
        IsEnabled = false,
        Style = (Style)Application.Current.Resources["AccentButtonStyle"],
    };
    private readonly Button _close = new() { Content = L10n.Text("Close") };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private IReadOnlyList<SignInTarget> _targets = [];
    private string? _link;
    private readonly IReadOnlyList<SignInTarget>? _staged;

    /// <paramref name="staged"/> stands in for the open Claudes, from the debug
    /// window; Continue then sends nothing anywhere.
    internal ClaudeLinkWindow(string link, IReadOnlyList<SignInTarget>? staged = null)
    {
        _link = link;
        _staged = staged;
        Title = "Claude Graft";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(520, 300));

        var panel = new StackPanel { Padding = new Thickness(24), Spacing = 12, Background = Themed.Brush("SolidBackgroundFillColorBaseBrush") };
        panel.Children.Add(new TextBlock
        {
            Text = L10n.Text("Finish Signing In"),
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
        });
        panel.Children.Add(_message);
        panel.Children.Add(_profiles);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_continue);
        buttons.Children.Add(_close);
        panel.Children.Add(buttons);
        Content = panel;

        Identify(_profiles, L10n.Text("Claude that started sign-in"), "signIn.profiles");
        Identify(_continue, L10n.Text("Continue Sign-In"), "signIn.continue");
        Identify(_close, L10n.Text("Close"), "signIn.close");

        Closed += (_, _) => { if (_link is not null) Note("dismissed"); _link = null; };
        _close.Click += (_, _) => Close();
        _profiles.SelectionChanged += (_, _) => _continue.IsEnabled = _profiles.SelectedIndex >= 0;
        _continue.Click += async (_, _) => await ContinueAsync();
    }

    private void Note(string outcome, string? profile = null)
    {
        if (_staged is not null) return;
        Diagnostics.Note("link." + outcome, profile is null ? null
            : new Dictionary<string, object?> { ["to"] = Path.GetFileName(profile) });
    }

    private static void Identify(FrameworkElement control, string name, string id)
    {
        AutomationProperties.SetName(control, name);
        AutomationProperties.SetAutomationId(control, id);
    }

    /// An ordinary link goes straight on and the window never shows; a sign-in
    /// asks first, with nothing chosen, since a wrong guess signs in the wrong account.
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
                Note("opened");
                Close();
                return;
            }
            if (kind == ClaudeLinkKind.Invalid)
            {
                Note("invalid");
                Refuse(L10n.Text("This Claude link could not be opened."));
                return;
            }
            var targets = _staged ?? await Task.Run(ClaudeLinks.RunningTargets);
            if (targets.Count == 0)
            {
                Note("noClaudeOpen");
                Refuse(L10n.Text("No Claude is open. Open the one you want to sign in to and start sign-in from its window."));
                return;
            }
            _message.Text = L10n.Text("Which Claude did you start this sign-in from?");
            _targets = targets;
            var names = targets.ToLookup(t => t.Name, StringComparer.OrdinalIgnoreCase);
            _profiles.ItemsSource = targets
                .Select(t => names[t.Name].Count() == 1 ? t.Name : $"{t.Name} ({Path.GetFileName(t.Profile)})")
                .ToArray();
            _profiles.SelectedIndex = -1;
            Note("signInAsked");
            Activate();
        }
        catch (Exception)
        {
            Note("unreadable");
            Refuse(L10n.Text("Graft could not read which Claudes are open. Make sure Claude Desktop is installed and try again."));
        }
    }

    private async Task ContinueAsync()
    {
        var index = _profiles.SelectedIndex;
        if (_link is not { } link || index < 0 || index >= _targets.Count) return;
        var target = _targets[index];
        _link = null;
        _continue.IsEnabled = _profiles.IsEnabled = false;
        if (_staged is not null)
        {
            Refuse(L10n.Format("Staged from the debug window: nothing was sent to {0}.", target.Name));
            return;
        }
        try
        {
            await Task.Run(() => ClaudeLinks.Forward(link, target));
            Note("signInSent", target.Profile);
            Close();
        }
        catch (Exception)
        {
            Note("targetGone", target.Profile);
            Refuse(L10n.Text("That Claude is no longer open. Go back to it and start sign-in again."));
        }
    }

    private void Refuse(string text)
    {
        _link = null;
        _profiles.Visibility = _continue.Visibility = Visibility.Collapsed;
        _message.Text = text;
        Activate();
    }
}
