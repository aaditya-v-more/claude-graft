using System.Reflection;
using ClaudeGraft.Core;
using Microsoft.UI.Xaml.Controls;

namespace ClaudeGraft;

/// <summary>
/// The app's preferences: how it looks, and how it starts. Reads the current
/// settings in and, once Done is pressed, hands back what was chosen for the
/// caller to apply and persist. Auto-start is read from and written to the
/// Startup folder rather than the settings file, so this shows the real state.
/// </summary>
public sealed partial class SettingsDialog : ContentDialog
{
    public SettingsDialog(GraftSettings current)
    {
        InitializeComponent();

        // The combo order matches the enum order, so the choice round-trips
        // through the selected index without a lookup table.
        ThemeBox.SelectedIndex = (int)current.Theme;
        BackdropBox.SelectedIndex = (int)current.Backdrop;
        AutoStartSwitch.IsOn = AutoStart.IsEnabled();
        StartHiddenSwitch.IsOn = current.StartHidden;

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null
            ? "Claude Graft"
            : $"Claude Graft {version.Major}.{version.Minor}.{version.Build}";
    }

    /// The settings as chosen. Read after the dialog closes on Done.
    public GraftSettings Result => new()
    {
        Theme = (AppTheme)ThemeBox.SelectedIndex,
        Backdrop = (BackdropMaterial)BackdropBox.SelectedIndex,
        StartHidden = StartHiddenSwitch.IsOn,
    };

    /// Whether the person asked to start with Windows — applied to the Startup
    /// folder separately from the settings file.
    public bool AutoStartEnabled => AutoStartSwitch.IsOn;
}
