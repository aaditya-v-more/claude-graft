using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;

namespace ClaudeGraft;

/// <summary>
/// A claude.ai page shown in a window of Graft's, signed in as the account it
/// belongs to. It never hands a link to the desktop app, which is signed in as
/// someone else, and anything off claude.ai goes to the browser instead, so
/// the account's store holds claude.ai alone.
/// </summary>
public sealed class ClaudePageWindow
{
    private static readonly List<ClaudePageWindow> Open = [];

    private readonly Guid _account;
    private readonly string _url;
    private readonly Window _window;
    private WebView2? _web;

    private ClaudePageWindow(Guid account, string url, string title)
    {
        _account = account;
        _url = url;
        _window = new Window { Title = title };
        _window.AppWindow.Resize(new SizeInt32(1200, 820));
        _window.Closed += (_, _) =>
        {
            _web?.Close();
            _web = null;
            Open.Remove(this);
        };
    }

    /// The same page already open is brought forward rather than opened twice.
    public static void Show(Guid account, string url, string title)
    {
        if (Open.FirstOrDefault(w => w._account == account && w._url == url) is { } existing)
        {
            existing._window.Activate();
            return;
        }
        var window = new ClaudePageWindow(account, url, title);
        Open.Add(window);
        _ = window.Load();
    }

    /// Every window on an account's store, before the store is removed: the
    /// folder cannot go while a page on it is alive.
    public static void CloseAll(Guid account)
    {
        foreach (var window in Open.Where(w => w._account == account).ToList()) window._window.Close();
    }

    private async Task Load()
    {
        var web = new WebView2();
        _web = web;
        _window.Content = web;
        _window.Activate();
        try { await web.EnsureCoreWebView2Async(await ClaudeWebSession.EnvironmentFor(_account)); }
        catch (Exception e)
        {
            Diagnostics.Note("projects.window failed", new Dictionary<string, object?> { ["error"] = e.Message });
            _window.Close();
            return;
        }
        if (_web != web) return;
        var core = web.CoreWebView2;
        core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
        core.NavigationStarting += (_, e) =>
        {
            if (IsClaude(e.Uri)) return;
            e.Cancel = true;
            OpenOutside(e.Uri);
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (IsClaude(e.Uri)) core.Navigate(e.Uri);
            else OpenOutside(e.Uri);
        };
        core.DocumentTitleChanged += (_, _) => { if (core.DocumentTitle.Length > 0) _window.Title = core.DocumentTitle; };
        core.Navigate(_url);
    }

    private static bool IsClaude(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var to) && to.Scheme == "https" && to.Host == new Uri(ArtifactAPI.Origin).Host;

    /// Only web addresses: a scheme that would start an app is never passed on.
    private static void OpenOutside(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var to) && (to.Scheme == "https" || to.Scheme == "http"))
            _ = Windows.System.Launcher.LaunchUriAsync(to);
    }
}
