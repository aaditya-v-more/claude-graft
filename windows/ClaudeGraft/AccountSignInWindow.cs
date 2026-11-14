using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;

namespace ClaudeGraft;

internal sealed class AccountSignInWindow : Window
{
    public const string Hint = "Choose Continue with email on claude.ai and enter the emailed code.";
    private readonly Guid _id;
    private readonly WebView2 _view = new();
    private readonly TextBlock _hint = new() { Text = Hint, TextWrapping = TextWrapping.Wrap, Margin = new(12, 8, 12, 8), FontSize = 12 };
    private readonly TaskCompletionSource<WebAccount?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<(Window Window, WebView2 View)> _popups = [];
    private CoreWebView2Environment? _environment;
    private bool _closed, _checking, _again;
    private string? _pendingIdentity;

    public AccountSignInWindow(Guid id)
    {
        _id = id;
        Title = "Sign in to claude.ai";
        var content = new Grid();
        content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        content.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(_hint);
        Grid.SetRow(_view, 1);
        content.Children.Add(_view);
        Content = content;
        AutomationProperties.SetName(_view, "claude.ai sign in");
        AutomationProperties.SetAutomationId(_hint, "AccountSignInHint");
        AppWindow.ResizeClient(new SizeInt32(540, 700));
        Closed += (_, _) => Finish(null);
    }

    public async Task<WebAccount?> ShowAsync()
    {
        Activate();
        try
        {
            _environment = await AccountWebSession.EnvironmentAsync(_id);
            if (_closed) return null;
            await _view.EnsureCoreWebView2Async(_environment);
            if (_closed) return null;
            var core = _view.CoreWebView2;
            AccountWebSession.Configure(core);
            core.SourceChanged += (_, _) => CheckIdentity();
            core.NavigationCompleted += (_, _) => CheckIdentity();
            core.ProcessFailed += (_, _) => _hint.Text = "The sign-in page stopped responding. Close this window and try Add Account again.";
            core.WebMessageReceived += IdentityReceived;
            core.NavigationStarting += (_, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) e.Cancel = true;
            };
            core.NewWindowRequested += NewWindow;
            core.Navigate("https://claude.ai/login");
        }
        catch
        {
            _hint.Text = "The sign-in browser could not start. Install Microsoft Edge WebView2 Runtime, then close this window and try again.";
        }
        return await _result.Task;
    }

    private async void CheckIdentity()
    {
        if (_closed || _view.CoreWebView2 is not { } core || !WebAccountPolicy.IsClaudeOrigin(core.Source)) return;
        if (_checking) { _again = true; return; }
        _checking = true;
        var nonce = _pendingIdentity = Guid.NewGuid().ToString("N");
        try
        {
            await core.ExecuteScriptAsync("(async()=>{try{const response=await fetch('/api/account',{credentials:'same-origin',cache:'no-store',redirect:'error',signal:AbortSignal.timeout(10000)});window.chrome.webview.postMessage({graftIdentity:"
                + JsonSerializer.Serialize(nonce) + ",status:response.status,body:await response.text()});}catch{window.chrome.webview.postMessage({graftIdentity:"
                + JsonSerializer.Serialize(nonce) + ",status:0});}})();");
            // A navigation can discard an outstanding script without a message.
            await Task.Delay(TimeSpan.FromSeconds(12));
        }
        catch { }
        finally
        {
            if (_pendingIdentity == nonce) { _pendingIdentity = null; _checking = false; }
            if (_again && !_closed) { _again = false; CheckIdentity(); }
        }
    }

    private void IdentityReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_closed || !WebAccountPolicy.IsClaudeOrigin(e.Source)) return;
        try
        {
            var answer = JsonNode.Parse(e.WebMessageAsJson);
            if (answer?["graftIdentity"]?.GetValue<string>() != _pendingIdentity || _pendingIdentity is null) return;
            _pendingIdentity = null;
            _checking = false;
            if (answer?["status"]?.GetValue<int>() == 200)
            {
                var identity = WebAccountPolicy.Identity(_id, JsonNode.Parse(answer["body"]!.GetValue<string>()));
                if (identity is not null) { Finish(identity); return; }
                _hint.Text = "Signed in, but the account details could not be read. Try navigating to claude.ai again.";
            }
            if (_again) { _again = false; CheckIdentity(); }
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
    }

    private async void NewWindow(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        e.Handled = true;
        Window? popup = null;
        try
        {
            if (_closed || _environment is null || !Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps) return;
            popup = new Window { Title = "Sign in to claude.ai" };
            var view = new WebView2();
            popup.Content = view;
            popup.AppWindow.ResizeClient(new SizeInt32(520, 660));
            _popups.Add((popup, view));
            popup.Closed += (_, _) => { _popups.RemoveAll(p => ReferenceEquals(p.Window, popup)); view.Close(); CheckIdentity(); };
            popup.Activate();
            await view.EnsureCoreWebView2Async(_environment);
            if (_closed) { view.Close(); popup.Close(); return; }
            AccountWebSession.Configure(view.CoreWebView2);
            view.CoreWebView2.NewWindowRequested += (_, args) => args.Handled = true;
            view.CoreWebView2.WindowCloseRequested += (_, _) => popup.Close();
            view.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var target) || target.Scheme != Uri.UriSchemeHttps) args.Cancel = true;
            };
            e.NewWindow = view.CoreWebView2;
        }
        catch { popup?.Close(); _hint.Text = Hint; }
        finally { deferral.Complete(); }
    }

    private void Finish(WebAccount? identity)
    {
        if (_closed) return;
        _closed = true;
        foreach (var popup in _popups.ToArray()) { popup.View.Close(); popup.Window.Close(); }
        _popups.Clear();
        _view.Close();
        Close();
        _result.TrySetResult(identity);
    }

    public void CloseSafely() => Finish(null);
}
