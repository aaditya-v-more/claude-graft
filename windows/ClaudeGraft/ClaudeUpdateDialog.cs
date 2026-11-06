using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClaudeGraft;

internal static class ClaudeUpdateDialog
{
    public static async Task ShowAsync(XamlRoot root)
    {
        ContentDialog Dialog(string message) => new()
        {
            XamlRoot = root, Title = "Claude Desktop updates", Content = new TextBlock
            { Text = message, TextWrapping = TextWrapping.Wrap }, CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };

        try
        {
            if (GraftPaths.ProfilesRootOverride is not null)
                throw new IOException("The isolated test manager cannot update Claude Desktop.");
            var status = "";
            for (;;)
            {
                var installed = InstalledClaude.Read();
                var policy = ClaudeUpdatePolicy.Read();
                var description = policy switch
                {
                    ClaudePolicyState.Manual => "Manual updates are enabled for all Claude profiles on this Windows account.",
                    ClaudePolicyState.Automatic => "Claude currently manages its own updates. Manual mode takes effect when each Claude next starts.",
                    _ => "An existing Windows policy controls updates. Your administrator manages installation.",
                };
                var menu = Dialog($"Installed version {installed.Version}.\n\n{description}" +
                    (status.Length == 0 ? "" : "\n\n" + status));
                menu.PrimaryButtonText = "Check for updates";
                menu.SecondaryButtonText = policy switch
                {
                    ClaudePolicyState.Manual => "Restore automatic updates",
                    ClaudePolicyState.Automatic => "Enable manual updates",
                    _ => "",
                };
                var action = await menu.ShowAsync();
                if (action == ContentDialogResult.None) return;
                if (action == ContentDialogResult.Secondary)
                {
                    try
                    {
                        ClaudeUpdatePolicy.SetManual(policy != ClaudePolicyState.Manual);
                        status = "The policy takes effect when Claude next starts.";
                    }
                    catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        status = "Windows protects this account’s update policy. An administrator can configure automatic updates. You can still check for a release here.";
                    }
                    continue;
                }
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var release = await ClaudeDesktopUpdate.Check(installed, deadline.Token);
                if (release.Version <= installed.Version) { status = "This Claude installation is up to date."; continue; }
                if (policy == ClaudePolicyState.Managed)
                {
                    status = $"Version {release.Version} is available. Contact your administrator to update.";
                    continue;
                }
                var approved = await Task.Run(() => ClaudeDesktopUpdate.Running(installed));
                var names = approved.Count == 0 ? "No Claude profiles are running." :
                    $"Claude will close these profiles and stop their active workflows:\n{string.Join("\n", approved.Select(p => $"• {p.Profile} (PID {p.Pid})"))}";
                var confirm = Dialog($"Install Claude Desktop {release.Version}?\n\n{names}\n\nGraft will leave Claude closed after installation."
                    + (policy == ClaudePolicyState.Automatic ? " Claude’s automatic update setting will still apply next time." : ""));
                confirm.PrimaryButtonText = approved.Count == 0 ? "Install update" : "Close profiles and install";
                confirm.CloseButtonText = "Cancel";
                if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
                var busy = Dialog("Preparing the update…");
                busy.CloseButtonText = "Cancel";
                using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(20));
                var completed = false;
                busy.CloseButtonClick += (_, e) => { e.Cancel = true; stop.Cancel(); };
                busy.Closing += (_, e) => { if (!completed) { e.Cancel = true; stop.Cancel(); } };
                var showing = busy.ShowAsync();
                try
                {
                    var progress = new Progress<string>(text => ((TextBlock)busy.Content).Text = text);
                    await ClaudeDesktopUpdate.Install(installed, release, approved, policy, progress, stop.Token);
                }
                finally { completed = true; busy.Hide(); await showing; }
                status = $"Installed and verified Claude Desktop {release.Version}.";
            }
        }
        catch (OperationCanceledException) { await Dialog("The update request was canceled or timed out.").ShowAsync(); }
        catch (Exception e) { await Dialog(e.Message).ShowAsync(); }
    }
}
