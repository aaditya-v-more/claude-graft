using System.Diagnostics;
using System.Threading.Tasks;
using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClaudeGraft;

/// What the manager and the flyout show in place of the profile cards until
/// Claude Desktop is installed and signed in somewhere.
public sealed class SetupPrompt : StackPanel
{
    private const string DownloadUrl = "https://claude.ai/download";

    private readonly TextBlock _title = new()
    {
        Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
        TextWrapping = TextWrapping.Wrap,
        HorizontalAlignment = HorizontalAlignment.Center,
        TextAlignment = TextAlignment.Center,
    };
    private readonly TextBlock _body = new()
    {
        TextWrapping = TextWrapping.Wrap,
        HorizontalAlignment = HorizontalAlignment.Center,
        TextAlignment = TextAlignment.Center,
        Foreground = Themed.Brush("TextFillColorSecondaryBrush"),
    };
    private readonly Button _action = new()
    {
        Style = (Style)Application.Current.Resources["AccentButtonStyle"],
    };
    private readonly Button _recheck = new() { Content = "Check Again" };
    private SetupState _state;

    /// Raised when the person asks for the state to be read again, and after
    /// Open Claude, so a sign-in finished in that window is picked up.
    public event Action? RecheckRequested;
    /// Raised once something has been started outside this window.
    public event Action? Acted;

    public SetupPrompt()
    {
        Spacing = 12;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        MaxWidth = 340;
        Children.Add(new FontIcon
        {
            Glyph = "",
            FontSize = 36,
            Foreground = Themed.Brush("AccentTextFillColorPrimaryBrush"),
        });
        Children.Add(_title);
        Children.Add(_body);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        };
        buttons.Children.Add(_action);
        buttons.Children.Add(_recheck);
        Children.Add(buttons);

        _action.Click += (_, _) => Act();
        _recheck.Click += (_, _) => RecheckRequested?.Invoke();
    }

    public void Show(SetupState state)
    {
        _state = state;
        Visibility = state == SetupState.Ready ? Visibility.Collapsed : Visibility.Visible;
        if (state == SetupState.NotInstalled)
        {
            _title.Text = "Install Claude Desktop";
            _body.Text = "Claude Graft runs extra profiles of Claude Desktop, so it needs Claude Desktop "
                         + "installed first. Install it, sign in, then come back here.";
            _action.Content = "Download Claude";
        }
        else
        {
            _title.Text = "Sign in to Claude";
            _body.Text = "Open Claude Desktop and sign in to your account. Your profile and its usage "
                         + "show up here once you have.";
            _action.Content = "Open Claude";
        }
    }

    private void Act()
    {
        if (_state == SetupState.NotInstalled)
        {
            try { Process.Start(new ProcessStartInfo(DownloadUrl) { UseShellExecute = true }); }
            catch { }
        }
        else
        {
            Task.Run(() => Launcher.Open(new GraftConfig { ProfileDir = GraftPaths.DefaultProfile, SourceDir = null }));
        }
        Acted?.Invoke();
    }
}
