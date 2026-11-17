using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeGraft.Core;
using Microsoft.Web.WebView2.Core;

namespace ClaudeGraft;

/// <summary>
/// Asks claude.ai for one account's things from inside a page of claude.ai.
/// The sign-in is a cookie in a WebView2 data folder of the account's own, and
/// requests go out from a claude.ai page loaded into it, as the site's own pages
/// make them. Graft never reads the cookie and has nowhere to send it but
/// claude.ai: nothing else is ever loaded into that folder but claude.ai.
/// </summary>
public sealed class ClaudeWebSession
{
    public enum Failure { SignedOut, Unreachable, Unreadable }

    public sealed class WebFailure(Failure reason) : Exception(reason.ToString())
    {
        public Failure Reason { get; } = reason;
    }

    public Guid Account { get; }
    private CoreWebView2Controller? _controller;
    private Task? _loading;
    private bool _closed;
    private int _inFlight;
    private TaskCompletionSource? _drained;

    public ClaudeWebSession(Guid account) => Account = account;

    public static string StoresRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeGraft", "artifact-stores");

    public static string StoreFolder(Guid account) => Path.Combine(StoresRoot, account.ToString("D"));

    private static readonly Dictionary<Guid, Task<CoreWebView2Environment>> Environments = new();

    /// One environment per folder: WebView2 refuses a second one on the same
    /// folder with different options, and the sign-in window shares this one.
    public static Task<CoreWebView2Environment> EnvironmentFor(Guid account)
    {
        if (Environments.TryGetValue(account, out var existing)) return existing;
        var created = CoreWebView2Environment.CreateWithOptionsAsync(null, StoreFolder(account), new CoreWebView2EnvironmentOptions()).AsTask();
        Environments[account] = created;
        return created;
    }

    /// The folder can only go once its browser process has exited, which is
    /// after every page on it has closed; a refusal is written down and tried
    /// again, and a folder that still will not go is left to the next launch.
    public static async Task Forget(Guid account)
    {
        if (Environments.Remove(account, out var environment))
        {
            try
            {
                var env = await environment;
                var exited = new TaskCompletionSource();
                env.BrowserProcessExited += (_, _) => exited.TrySetResult();
                await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            }
            catch (Exception) { }
        }
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (Directory.Exists(StoreFolder(account))) Directory.Delete(StoreFolder(account), recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Diagnostics.Note("artifacts.store removal failed", new Dictionary<string, object?> { ["attempt"] = attempt, ["error"] = e.Message });
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
            }
        }
    }

    public static IReadOnlyList<Guid> StoreIdentifiers() =>
        Directory.Exists(StoresRoot)
            ? Directory.GetDirectories(StoresRoot).Select(d => Guid.TryParse(Path.GetFileName(d), out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList()
            : [];

    public async Task<(int Status, string Body)> Send(string method, string path, string? body = null,
        IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        var controller = await Ready();
        // The artifact service answers 404 without its own headers; the rest of
        // claude.ai is asked as its pages ask it, without them.
        var headers = new Dictionary<string, string>(path.StartsWith("/api/frame/", StringComparison.Ordinal) ? ArtifactAPI.Headers : new Dictionary<string, string>());
        if (body is not null) headers["Content-Type"] = "application/json";
        foreach (var (name, value) in extraHeaders ?? new Dictionary<string, string>()) headers[name] = value;
        var args = JsonSerializer.Serialize(new { path, method, headers, body = body ?? "" });
        const string script = """
            async (a) => {
              const response = await fetch(a.path, {method: a.method, credentials: "same-origin", headers: a.headers,
                                                    body: a.body === "" ? undefined : a.body});
              return {status: response.status, body: await response.text()};
            }
            """;
        JsonObject answer;
        _inFlight++;
        try { answer = await Evaluate(controller.CoreWebView2, script, args); }
        finally { RequestEnded(); }
        if (answer["status"] is not JsonValue s || !s.TryGetValue<int>(out var status) || answer["body"] is not JsonValue b || !b.TryGetValue<string>(out var text))
            throw new WebFailure(Failure.Unreadable);
        if (ArtifactAPI.MeansSignedOut(status, path)) throw new WebFailure(Failure.SignedOut);
        if (ArtifactAPI.NeedsAccountCheck(status, path)) await Send("GET", ArtifactAPI.AccountPath);
        return (status, text);
    }

    /// A file from the artifact's file host, which takes its token in the address
    /// and no cookie. `limit` is held to in the page as the file streams in: one
    /// read whole and measured after can take the page's process down with it.
    public async Task<(int Status, byte[] Data)> Download(string url, long limit)
    {
        var controller = await Ready();
        const string script = """
            async (a) => {
              const response = await fetch(a.url, {credentials: "omit", cache: "no-store", referrerPolicy: "no-referrer"});
              if (!response.ok) return {status: response.status, body: ""};
              if (Number(response.headers.get("Content-Length") || 0) > a.limit) return {status: response.status, body: "", tooLarge: true};
              const reader = response.body.getReader();
              const chunks = [];
              let size = 0;
              for (;;) {
                const {done, value} = await reader.read();
                if (done) break;
                size += value.length;
                if (size > a.limit) { await reader.cancel(); return {status: response.status, body: "", tooLarge: true}; }
                chunks.push(value);
              }
              const bytes = new Uint8Array(size);
              let offset = 0;
              for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; }
              let binary = "";
              for (let i = 0; i < bytes.length; i += 0x8000) binary += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
              return {status: response.status, body: btoa(binary)};
            }
            """;
        JsonObject answer;
        _inFlight++;
        try { answer = await Evaluate(controller.CoreWebView2, script, JsonSerializer.Serialize(new { url, limit = Math.Max(0, limit) })); }
        finally { RequestEnded(); }
        if (answer["tooLarge"] is JsonValue large && large.TryGetValue<bool>(out var tooLarge) && tooLarge)
            throw new ArtifactExporter.ExportFailure(ArtifactExporter.Problem.TooLarge);
        if (answer["status"] is not JsonValue s || !s.TryGetValue<int>(out var status) || answer["body"] is not JsonValue b || !b.TryGetValue<string>(out var text))
            throw new WebFailure(Failure.Unreadable);
        try { return (status, Convert.FromBase64String(text)); }
        catch (FormatException) { throw new WebFailure(Failure.Unreadable); }
    }

    public async Task<ArtifactAPI.Identity> Identity()
    {
        var (status, body) = await Send("GET", ArtifactAPI.AccountPath);
        if (status != 200) throw new WebFailure(Failure.SignedOut);
        return ArtifactAPI.IdentityFrom(body) ?? throw new WebFailure(Failure.Unreadable);
    }

    /// Runs an async function in the page and waits for its promise, which
    /// ExecuteScriptAsync does not; the arguments go in as JSON, never as script.
    public static async Task<JsonObject> Evaluate(CoreWebView2 web, string function, string argumentsJson)
    {
        var expression = $"({function})({argumentsJson})";
        string result;
        try
        {
            result = await web.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                JsonSerializer.Serialize(new { expression, awaitPromise = true, returnByValue = true }));
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException)
        {
            throw new WebFailure(Failure.Unreachable);
        }
        var node = JsonNode.Parse(result) as JsonObject;
        if (node?["exceptionDetails"] is not null) throw new WebFailure(Failure.Unreachable);
        return node?["result"]?["value"] as JsonObject ?? throw new WebFailure(Failure.Unreadable);
    }

    /// Drops the page, so the next request loads a fresh one: a sign-in made
    /// since is only seen by a page loaded after it.
    public void Reset()
    {
        _controller?.Close();
        _controller = null;
        _loading = null;
    }

    /// For good: nothing asked of this session afterwards loads a page.
    public void Close()
    {
        _closed = true;
        Reset();
    }

    /// Once every request already sent has come back.
    public Task Released()
    {
        if (_inFlight == 0) return Task.CompletedTask;
        _drained ??= new TaskCompletionSource();
        return _drained.Task;
    }

    private void RequestEnded()
    {
        if (--_inFlight > 0) return;
        _drained?.TrySetResult();
        _drained = null;
    }

    public static readonly TimeSpan LoadLimit = TimeSpan.FromSeconds(30);

    private async Task<CoreWebView2Controller> Ready()
    {
        if (_closed) throw new WebFailure(Failure.SignedOut);
        _loading ??= Load();
        var loading = _loading;
        try { await loading; }
        catch
        {
            if (_loading == loading) Reset();
            throw;
        }
        if (_closed) throw new WebFailure(Failure.SignedOut);
        // A page dropped while this waited: the next one is loaded.
        if (_controller is null) return await Ready();
        return _controller;
    }

    private async Task Load()
    {
        var environment = await EnvironmentFor(Account);
        var controller = await environment.CreateCoreWebView2ControllerAsync(CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)HiddenParent.Handle));
        controller.IsVisible = false;
        if (_closed) { controller.Close(); throw new WebFailure(Failure.SignedOut); }
        _controller = controller;
        var web = controller.CoreWebView2;
        web.Settings.AreDevToolsEnabled = false;
        web.Settings.AreDefaultContextMenusEnabled = false;
        // A page whose process died still reads as loaded; drop it so the next
        // request loads another.
        web.ProcessFailed += (_, _) =>
        {
            if (_controller != controller) return;
            Diagnostics.Note("artifacts.page ended");
            Reset();
        };
        var finished = new TaskCompletionSource<bool>();
        web.NavigationCompleted += (_, e) => finished.TrySetResult(e.IsSuccess);
        web.Navigate(ArtifactAPI.Origin + ArtifactAPI.AccountPath);
        var done = await Task.WhenAny(finished.Task, Task.Delay(LoadLimit));
        if (_controller != controller) return;
        if (done != finished.Task || !finished.Task.Result) { Reset(); throw new WebFailure(Failure.Unreachable); }
        // A challenge page or a redirect elsewhere is no claude.ai page to ask from.
        if (!Uri.TryCreate(web.Source, UriKind.Absolute, out var at) || at.Host != new Uri(ArtifactAPI.Origin).Host)
        {
            Reset();
            throw new WebFailure(Failure.SignedOut);
        }
    }

    /// A window never shown, for the hidden pages to belong to.
    internal static class HiddenParent
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateWindowExW(int exStyle, string className, string? windowName, int style,
            int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

        private static nint _handle;
        public static nint Handle => _handle != 0 ? _handle : _handle = CreateWindowExW(0, "STATIC", "Claude Graft pages", unchecked((int)0x80000000), 0, 0, 800, 600, 0, 0, 0, 0);
    }
}
