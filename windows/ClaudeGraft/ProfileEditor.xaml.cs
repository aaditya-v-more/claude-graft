using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ClaudeGraft;

/// One icon in the picker, named as a screen reader reads it.
public sealed class IconChoice(string preset, string? previewFile)
{
    public string Preset { get; } = preset;
    public string Title { get; } = IconPreset.Title(preset);
    public BitmapImage? Preview { get; } = previewFile is null ? null : new BitmapImage(new Uri(previewFile));
    public override string ToString() => Title;
}

/// One selectable source, with the label a person reads.
public sealed class SourceOption
{
    public required string Label { get; init; }
    public required ShortcutSource Source { get; init; }
}

/// <summary>
/// A Claude's name, icon, folder and where it reads chats, edited on its page.
/// A new one is a draft until Create; an existing one changes only on Save.
/// Saving an invalid folder is refused, and choosing a source warns first that
/// merging two histories cannot be undone.
/// </summary>
public sealed partial class ProfileEditor : UserControl
{
    /// The edited copy and whether it is new, once Save or Create is pressed.
    public event Action<Shortcut, Shortcut, bool>? Saved;
    public event Action<Shortcut>? DeleteRequested;
    public event Action? Discarded;

    private Shortcut? _original;
    private Shortcut _shortcut = Shortcut.New("");
    private bool _isNew;
    private bool _folderEdited;
    private bool _filling;

    public ProfileEditor()
    {
        InitializeComponent();
        // Once: the page around it is moved into a new host on each switch, which loads it again.
        var shown = false;
        Loaded += async (_, _) => { if (shown) return; shown = true; await ShowIcons(); };
    }

    /// An existing Claude, or a new draft when null.
    public void Edit(Shortcut? existing)
    {
        _original = existing;
        _isNew = existing is null;
        _shortcut = existing?.Copy() ?? Shortcut.New(App.Store.UniqueName());
        if (_isNew) _shortcut.IconPreset = App.Store.NextIconPreset();
        _folderEdited = !_isNew;
        // Something outside Graft can take the link away; saving unchanged writes it back.
        _linkMissing = existing is not null && Installer.InstalledLink(existing) is null;
        Fill();
    }

    private bool _linkMissing;

    private void Fill()
    {
        _filling = true;
        NameBox.Text = _shortcut.Name;
        FolderBox.Text = _shortcut.Folder;
        var options = App.Store.AvailableSources(_shortcut)
            .Select(s => new SourceOption { Label = App.Store.Label(s), Source = s }).ToList();
        // The same choices keep the same list, so the box changes its selection rather than emptying and refilling.
        if (SourceBox.ItemsSource is List<SourceOption> shown && shown.Select(o => (o.Label, o.Source)).SequenceEqual(options.Select(o => (o.Label, o.Source))))
            options = shown;
        else SourceBox.ItemsSource = options;
        SourceBox.SelectedItem = options.FirstOrDefault(o => o.Source.Equals(_shortcut.Source)) ?? options.FirstOrDefault();
        ShowSource();
        Problem.IsOpen = false;
        _filling = false;
        if (IconGrid.ItemsSource is IReadOnlyList<IconChoice> choices) ShowIcon(choices);
        Changed();
    }

    private void ShowSource()
    {
        var own = _shortcut.Source.Kind == SourceKind.Own;
        var label = App.Store.Label(_shortcut.Source);
        MergeNote.Visibility = own ? Visibility.Collapsed : Visibility.Visible;
        MergeText.Text = $"Shared both ways with {label}";
        SourceInfoText.Text = own
            ? "This profile keeps its own Claude Code history, connectors and preferences. Nothing is shared."
            : $"Merges this profile's Claude Code chats with {label}'s, and shares extensions and window state. "
              + "Missing local MCP servers are copied; existing server settings are kept. Logins and permission choices stay separate.";
    }

    /// Raised whenever what the page's buttons may do has changed.
    public event Action? StateChanged;
    public bool IsNew => _isNew;
    public bool IsDirty { get; private set; }

    private bool _busy;

    /// While a save moves files and makes the shortcut, which can take seconds;
    /// the fields hold still, since what is being written was read from them.
    public bool Busy
    {
        get => _busy;
        set
        {
            _busy = value;
            NameBox.IsEnabled = FolderBox.IsEnabled = SourceBox.IsEnabled = IconButton.IsEnabled = !value;
            Changed();
        }
    }

    private void Changed()
    {
        IsDirty = _isNew || _original is null || _linkMissing
            || NameBox.Text.Trim() != _original.Name || FolderBox.Text.Trim() != _original.Folder
            || !_shortcut.Source.Equals(_original.Source) || _shortcut.IconPreset != _original.IconPreset;
        StateChanged?.Invoke();
    }
    private static IReadOnlyDictionary<string, string?>? _previews;

    private async Task ShowIcons()
    {
        _previews ??= await Task.Run(ShortcutIcons.Previews);
        var choices = IconPreset.All.Select(p => new IconChoice(p, _previews.GetValueOrDefault(p))).ToList();
        IconGrid.ItemsSource = choices;
        ShowIcon(choices);
    }

    private void ShowIcon(IReadOnlyList<IconChoice> choices)
    {
        var current = choices.FirstOrDefault(c => c.Preset == IconPreset.Normalize(_shortcut.IconPreset));
        IconGrid.SelectedItem = current;
        IconImage.Source = current?.Preview;
        AutomationProperties.SetItemStatus(IconButton, current?.Title ?? "");
    }

    private void IconGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not IconChoice choice) return;
        _shortcut.IconPreset = choice.Preset;
        ShowIcon((IReadOnlyList<IconChoice>)IconGrid.ItemsSource);
        IconFlyout.Hide();
        Changed();
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_filling) return;
        // Until the folder is typed by hand, a new Claude's name keeps rewriting it.
        if (_isNew && !_folderEdited)
        {
            _filling = true;
            FolderBox.Text = Shortcut.FolderName(NameBox.Text);
            _filling = false;
        }
        Changed();
    }

    private void FolderBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_filling) return;
        _folderEdited = true;
        Changed();
    }

    private void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceBox.SelectedItem is not SourceOption option) return;
        _shortcut.Source = option.Source;
        ShowSource();
        if (!_filling) Changed();
    }

    private void ShowFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = GraftPaths.Profile(_original?.Folder ?? FolderBox.Text.Trim());
        if (Directory.Exists(folder))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = false })?.Dispose();
    }

    public void Save()
    {
        if (_busy) return;
        var name = NameBox.Text.Trim();
        var folder = FolderBox.Text.Trim();
        if (name.Length == 0) { Reject("The Claude needs a name."); return; }
        if (Graft.ValidateFolder(folder) is string problem) { Reject(problem); return; }
        Problem.IsOpen = false;
        var result = _shortcut.Copy();
        result.Name = name;
        result.Folder = folder;
        Saved?.Invoke(_original ?? result, result, _isNew);
    }

    /// A draft goes; an existing Claude's fields go back to what is saved.
    public void Revert()
    {
        if (_isNew) Discarded?.Invoke();
        else Edit(_original);
    }

    public void RequestDelete()
    {
        if (_original is not null) DeleteRequested?.Invoke(_original);
    }
    private void Reject(string message)
    {
        Problem.Message = message;
        Problem.IsOpen = true;
    }
}
