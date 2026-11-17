using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClaudeGraft;

/// <summary>
/// Claude Desktop's version and what to do about it, at the top of the flyout
/// and the window. Red only for something waiting on a person.
/// </summary>
public sealed partial class ClaudeUpdateLine : UserControl
{
    /// Raised for Restart Claudes, which closes work and so needs a warning the
    /// host window shows.
    public event Action? RestartRequested;

    private bool _stacked;

    /// The sidebar's form: a sync symbol, the name over the version, and on the
    /// right the button that checks again or the one that acts.
    public bool Stacked
    {
        get => _stacked;
        set
        {
            _stacked = value;
            Glyph.FontSize = value ? 16 : 14;
            // A fixed box, since the state glyphs differ in width and would nudge the words.
            Glyph.Width = Glyph.Height = value ? 18 : 16;
            Words.Style = value ? null : (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        }
    }

    private void Check_Click(object sender, RoutedEventArgs e) => _ = ClaudeUpdateCenter.CheckNowAsync();
    public ClaudeUpdateLine()
    {
        InitializeComponent();
        Loaded += (_, _) => { ClaudeUpdateCenter.Changed += Draw; Draw(); };
        Unloaded += (_, _) => ClaudeUpdateCenter.Changed -= Draw;
    }

    private void Draw()
    {
        var phase = ClaudeUpdateCenter.Now;
        Root.Visibility = phase == ClaudeUpdateCenter.Phase.Hidden ? Visibility.Collapsed : Visibility.Visible;
        var line = ClaudeUpdateCenter.Line;
        Words.Inlines.Clear();
        const string name = "Claude Desktop";
        // The name leads in its own weight, as on the Mac: on a line of its own in the sidebar,
        // and the hold on updates is the tooltip's to say, so the line keeps to the version.
        if (line.StartsWith(name + " ", StringComparison.Ordinal))
        {
            Words.MaxLines = Stacked ? 3 : 2;
            Words.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            if (Stacked) Words.Inlines.Add(new Microsoft.UI.Xaml.Documents.LineBreak());
            var rest = line[(name.Length + 1)..].TrimStart('·', ' ');
            // Two short lines beside the button rather than a sentence wrapped under the name; the tooltip keeps it whole.
            if (Stacked) rest = phase switch
            {
                ClaudeUpdateCenter.Phase.Available => $"{ClaudeUpdateCenter.Latest} available",
                ClaudeUpdateCenter.Phase.NeedsRestart => $"{ClaudeUpdateCenter.Installed} waits on a restart",
                _ => rest,
            };
            var held = rest.IndexOf(" · updates when you ask", StringComparison.Ordinal);
            var version = new Microsoft.UI.Xaml.Documents.Run { Text = (Stacked ? "" : " ") + (held < 0 ? rest : rest[..held]) };
            // Left unset when alarming so it takes the line's red: a null brush draws nothing.
            if (!ClaudeUpdateCenter.Alarming) version.Foreground = Themed.Brush("TextFillColorSecondaryBrush");
            Words.Inlines.Add(version);
        }
        else Words.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = line });
        ToolTipService.SetToolTip(Words, line);
        Words.Foreground = Themed.Brush(ClaudeUpdateCenter.Alarming
            ? "SystemFillColorCriticalBrush" : "TextFillColorPrimaryBrush");
        var busy = phase is ClaudeUpdateCenter.Phase.Installing or ClaudeUpdateCenter.Phase.Restarting or ClaudeUpdateCenter.Phase.Checking;
        // The spinner takes the check button's place rather than pushing the words over.
        CheckBusy.IsActive = busy;
        CheckBusy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        var action = ClaudeUpdateCenter.Action;
        // Checking again says nothing new while an action waits, and beside its button the check fell out of line with Graft's.
        CheckButton.Visibility = !busy && action is null && phase is not ClaudeUpdateCenter.Phase.Store ? Visibility.Visible : Visibility.Collapsed;
        Glyph.Glyph = phase switch
        {
            ClaudeUpdateCenter.Phase.UpToDate => "",
            ClaudeUpdateCenter.Phase.Available => "",
            ClaudeUpdateCenter.Phase.NeedsRestart => "",
            ClaudeUpdateCenter.Phase.Failed => "",
            ClaudeUpdateCenter.Phase.Store => "",
            _ => "",
        };
        Glyph.Foreground = Themed.Brush(ClaudeUpdateCenter.Alarming
            ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");
        // The sidebar is narrow enough that "Restart Claudes…" beside the name squeezed it to three lines.
        ActionButton.Content = Stacked && action is not null
            ? phase switch { ClaudeUpdateCenter.Phase.NeedsRestart => "Restart…", ClaudeUpdateCenter.Phase.Failed => "Retry", _ => action }
            : action;
        ActionButton.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(ActionButton, action is null ? "" : action.TrimEnd('…') + " Claude Desktop");
    }

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        if (ClaudeUpdateCenter.Staged) return;
        switch (ClaudeUpdateCenter.Now)
        {
            case ClaudeUpdateCenter.Phase.Available: _ = ClaudeUpdateCenter.UpdateAsync(); break;
            case ClaudeUpdateCenter.Phase.NeedsRestart: RestartRequested?.Invoke(); break;
            case ClaudeUpdateCenter.Phase.Failed: _ = ClaudeUpdateCenter.RetryAsync(); break;
        }
    }
}
