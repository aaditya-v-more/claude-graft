using ClaudeGraft.Core;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.UI.Notifications;

namespace ClaudeGraft;

/// Every update notification shares one tag, so each replaces the last in
/// Action Center: "available" gives way to the download's progress bar, which
/// gives way to "installing" or to the failure — one entry, never a pile.
internal static class UpdateToast
{
    private const string Tag = "update", Group = "update";
    private static uint _sequence;

    public static void Available(Updater.Release release) => Show(new ToastContentBuilder()
        .AddText(L10n.Text("Claude Graft update available"))
        .AddText(L10n.Format("Version {0} is ready to install.", release.Version))
        .AddButton(new ToastButton().SetContent(L10n.Text("Install")).AddArgument("action", "install"))
        .AddButton(new ToastButton().SetContent(L10n.Text("Dismiss")).SetDismissActivation()));

    /// The bar's value is bound rather than baked in, so progress can move it
    /// with an in-place update instead of a new toast popping up each percent.
    public static void Downloading(Updater.Release release)
    {
        _sequence = 0;
        Show(new ToastContentBuilder()
            .AddText(L10n.Text("Updating Claude Graft"))
            .AddVisualChild(new AdaptiveProgressBar
            {
                Title = L10n.Format("Version {0}", release.Version),
                Value = new BindableProgressBarValue("value"),
                ValueStringOverride = new BindableString("percent"),
                Status = new BindableString("status"),
            }),
            data: Progress(0, L10n.Text("Downloading…")));
    }

    public static void Report(double fraction)
    {
        try { ToastNotificationManagerCompat.CreateToastNotifier().Update(Progress(fraction, L10n.Text("Downloading…")), Tag, Group); }
        catch { }
    }

    public static void Installing() => Show(new ToastContentBuilder()
        .AddText(L10n.Text("Updating Claude Graft"))
        .AddText(L10n.Text("Installing. Claude Graft will reopen when it is done.")));

    public static void Failed(Updater.Release release) => Show(new ToastContentBuilder()
        .AddText(L10n.Text("Claude Graft could not download the update"))
        .AddText(L10n.Format("Version {0} is still available. Check your connection and try again.", release.Version))
        .AddButton(new ToastButton().SetContent(L10n.Text("Try Again")).AddArgument("action", "install"))
        .AddButton(new ToastButton().SetContent(L10n.Text("Dismiss")).SetDismissActivation()));

    private static NotificationData Progress(double fraction, string status)
    {
        var data = new NotificationData { SequenceNumber = ++_sequence };
        data.Values["value"] = fraction.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        data.Values["percent"] = $"{(int)Math.Round(fraction * 100)}%";
        data.Values["status"] = status;
        return data;
    }

    /// Claude restarted to update and opened the main profile in a shortcut's
    /// place. Its own tag, so it never replaces Graft's update notice.
    public static void StrayClaude(GracefulQuit.Target stray, string shortcut)
    {
        try
        {
            new ToastContentBuilder()
                .AddText(L10n.Format("{0} was opened again after Claude updated", shortcut))
                .AddText(L10n.Text("Claude's update also opened your main Claude. Close it?"))
                .AddButton(new ToastButton().SetContent(L10n.Text("Close It"))
                    .AddArgument("action", "closeStray")
                    .AddArgument("pid", stray.Pid)
                    .AddArgument("started", stray.Started.Ticks.ToString()))
                .AddButton(new ToastButton().SetContent(L10n.Text("Keep It")).SetDismissActivation())
                .Show(toast => { toast.Tag = "stray"; toast.Group = "claude-update"; });
        }
        catch (Exception e) { Diagnostics.Note("notify.failed", new Dictionary<string, object?> { ["error"] = e.Message }); }
    }

    private static void Show(ToastContentBuilder builder, NotificationData? data = null)
    {
        try
        {
            builder.Show(toast =>
            {
                toast.Tag = Tag;
                toast.Group = Group;
                if (data is not null) toast.Data = data;
            });
        }
        catch (Exception e)
        {
            Diagnostics.Note("notify.show", new Dictionary<string, object?> { ["error"] = e.GetType().Name + ": " + e.Message });
        }
    }
}
