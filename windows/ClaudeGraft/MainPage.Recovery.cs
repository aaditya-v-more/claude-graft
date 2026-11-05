using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClaudeGraft;

public sealed partial class MainPage
{
    private ChatRecovery.Offer? _recoveryOffer;
    private int _recoveryRequest;

    private async Task FindRecoverableChats()
    {
        if (RecoverySection is null) return;
        var request = ++_recoveryRequest;
        _recoveryOffer = null;
        RecoverySection.Visibility = Visibility.Collapsed;
        if (!_loaded || _original is not { InstalledName: not null, Source.Kind: SourceKind.Own } shortcut ||
            SourceBox.SelectedItem is not SourceOption { Source.Kind: SourceKind.Own }) return;
        try
        {
            var offer = await Task.Run(() => ChatRecovery.Find(shortcut.ProfileDir));
            if (!_loaded || request != _recoveryRequest || offer is null) return;
            _recoveryOffer = offer;
            var kind = offer.Count == 1 ? "chat" : "chats";
            RecoverySummary.Text = $"{Path.GetFileName(offer.Source)} holds {offer.Count} more {kind} for this account.";
            RecoveryButton.Visibility = Visibility.Visible;
            RecoverySection.Visibility = Visibility.Visible;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            if (_loaded && request == _recoveryRequest) ShowProblem("Could not check for this account's earlier chats. " + e.Message);
        }
    }

    private async void RecoverChats_Click(object sender, RoutedEventArgs e)
    {
        var offer = _recoveryOffer;
        if (offer is null || _original is not { } shortcut || _busy) return;
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = RecoverySummary.Text, TextWrapping = TextWrapping.Wrap });
        foreach (var chat in offer.Recent)
            body.Children.Add(new TextBlock { Text = $"{chat.LastActive.ToLocalTime():d}   {chat.Title}",
                TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = "Quit every Claude Desktop instance first. Graft copies missing chat records. Each profile keeps its existing history and stays independent.", TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog { Title = "Chats found elsewhere", Content = body,
            PrimaryButtonText = offer.HasOwnChats ? "Merge them here" : "Copy them here", CloseButtonText = "Cancel" };
        PrepareDialog(dialog);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _busy = true;
        RecoveryButton.IsEnabled = false;
        try
        {
            var count = await Task.Run(() => ChatRecovery.Copy(shortcut.ProfileDir, offer));
            if (!_loaded || _original?.Id != shortcut.Id) return;
            await FindRecoverableChats();
            if (_recoveryOffer is null)
            {
                RecoverySummary.Text = count == 1 ? "Copied one chat. Open this profile to see it." :
                    count > 0 ? $"Copied {count} chats. Open this profile to see them." : "This profile already has these chats.";
                RecoverySection.Visibility = Visibility.Visible;
                RecoveryButton.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex) { ShowProblem(ex.Message); }
        finally { _busy = false; RecoveryButton.IsEnabled = true; }
    }
}
