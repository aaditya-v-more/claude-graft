using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;

namespace ClaudeGraft;

/// <summary>
/// claude.ai's own sign-in page, in a window of Graft's, writing into the data
/// folder of the account being signed in. It closes itself once the page says
/// who is signed in.
/// </summary>
public sealed class ArtifactSignInWindow
{
    public const string Hint = "Choose Continue with email, then type the code claude.ai sends you.";

    public Guid Account { get; }
    private readonly Action<ArtifactAPI.Identity> _done;
    private readonly Action _cancelled;
    private Window? _window;
    private WebView2? _web;
    private readonly List<Window> _popups = [];
    private bool _finished, _checking, _askAgain;

    public ArtifactSignInWindow(Guid account, Action<ArtifactAPI.Identity> done, Action cancelled)
    {
        Account = account;
        _done = done;
        _cancelled = cancelled;
    }

    public async void Show()
    {
        var web = new WebView2();
        var hint = new TextBlock
        {
            Text = Hint, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 8, 12, 8),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(hint, "signin.hint");
        var root = new Grid { RowDefinitions = { new() { Height = GridLength.Auto }, new() { Height = new GridLength(1, GridUnitType.Star) } } };
        root.Children.Add(hint);
        Grid.SetRow(web, 1);
        root.Children.Add(web);
        root.Background = Themed.Brush("ApplicationPageBackgroundThemeBrush");
        var window = new Window { Title = "Sign In to claude.ai", Content = root };
        window.AppWindow.Resize(new SizeInt32(560, 760));
        window.Closed += (_, _) =>
        {
            if (_window != window) return;
            Release();
            if (!_finished) _cancelled();
        };
        _window = window;
        _web = web;
        window.Activate();

        try { await web.EnsureCoreWebView2Async(await ClaudeWebSession.EnvironmentFor(Account)); }
        catch (Exception e)
        {
            Diagnostics.Note("artifacts.signin failed", new Dictionary<string, object?> { ["error"] = e.Message });
            Close();
            return;
        }
        if (_web != web) return;
        var core = web.CoreWebView2;
        // claude.ai ends a sign-in by moving inside the page already loaded,
        // which finishes no navigation; the address changing is what every way
        // of signing in does, so that is what asks.
        core.SourceChanged += (_, _) => _ = Check(core);
        core.NavigationCompleted += (_, _) => _ = Check(core);
        core.NewWindowRequested += OnNewWindow;
        core.Navigate(ArtifactAPI.SignInUrl);
    }

    /// A provider's sign-in hands its answer back to the page that opened it,
    /// so it gets a real window, in the same folder; over claude.ai, the answer
    /// had nowhere to go.
    private async void OnNewWindow(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var popupWeb = new WebView2();
            var popup = new Window { Title = "Sign In to claude.ai", Content = popupWeb };
            popup.AppWindow.Resize(new SizeInt32(520, 680));
            _popups.Add(popup);
            popup.Closed += (_, _) => _popups.Remove(popup);
            popup.Activate();
            await popupWeb.EnsureCoreWebView2Async(await ClaudeWebSession.EnvironmentFor(Account));
            popupWeb.CoreWebView2.WindowCloseRequested += (_, _) =>
            {
                popup.Close();
                // Its answer has reached the page by now, which may not move again.
                if (_web?.CoreWebView2 is { } main) _ = Check(main);
            };
            args.NewWindow = popupWeb.CoreWebView2;
            args.Handled = true;
        }
        catch (Exception) { args.Handled = true; }
        finally { deferral.Complete(); }
    }

    public void Close()
    {
        var window = _window;
        Release();
        window?.Close();
    }

    /// Every page this window made goes at once, since the folder they write
    /// into cannot be removed while one of them is alive.
    private void Release()
    {
        foreach (var popup in _popups.ToList()) popup.Close();
        _popups.Clear();
        _web?.Close();
        _web = null;
        _window = null;
    }

    /// One question at a time: a move made while one is out is asked about
    /// once it comes back.
    private async Task Check(CoreWebView2 core)
    {
        if (_finished || _web?.CoreWebView2 != core) return;
        if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var at) || at.Host != new Uri(ArtifactAPI.Origin).Host) return;
        if (_checking) { _askAgain = true; return; }
        _checking = true;
        try
        {
            const string script = """
                async (a) => {
                  const response = await fetch(a.path, {credentials: "same-origin"});
                  return {status: response.status, body: await response.text()};
                }
                """;
            System.Text.Json.Nodes.JsonObject answer;
            try { answer = await ClaudeWebSession.Evaluate(core, script, "{\"path\":\"" + ArtifactAPI.AccountPath + "\"}"); }
            // The page moving on mid-question throws, and that move asks again.
            catch (ClaudeWebSession.WebFailure) { return; }
            if ((int?)answer["status"] != 200 || (string?)answer["body"] is not { } body) return;
            if (ArtifactAPI.IdentityFrom(body) is not { } identity)
            {
                // Signed in and still not read is the one failure nothing on
                // screen shows; the shape is written down, never the body.
                var keys = (System.Text.Json.Nodes.JsonNode.Parse(body) as System.Text.Json.Nodes.JsonObject)?.Select(p => p.Key).Order().ToList();
                Diagnostics.Note("artifacts.signin unreadable", new Dictionary<string, object?> { ["keys"] = keys is null ? "" : string.Join(",", keys) });
                return;
            }
            _finished = true;
            // The pages go first; handed on before, a folder removed because of
            // this sign-in was still in use and stayed.
            Close();
            _done(identity);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException) { }
        finally
        {
            _checking = false;
            if (_askAgain && !_finished)
            {
                _askAgain = false;
                _ = Check(core);
            }
        }
    }
}
