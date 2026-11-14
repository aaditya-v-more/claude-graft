using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeGraft.Core;
using Microsoft.Web.WebView2.Core;

namespace ClaudeGraft;

public sealed record WebResponse(int StatusCode, JsonNode? Json, string Text);
public sealed class WebAccountException : Exception
{
    public int StatusCode { get; }
    public bool IsSignedOut { get; }
    public WebAccountException(string message, int statusCode = 0, bool isSignedOut = false) : base(message)
    { StatusCode = statusCode; IsSignedOut = isSignedOut; }
}

internal sealed class AccountWebSession(Guid id) : IDisposable
{
    private CoreWebView2Environment? _environment;
    private CoreWebView2Controller? _controller;
    private Task? _loading;
    private TaskCompletionSource? _navigation;
    private readonly Dictionary<string, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly SemaphoreSlim _requests = new(1, 1);
    private bool _closed;
    private int _generation;
    internal static Action<CoreWebView2>? ConfigureFixture { get; set; }
    internal static string StoresRoot => Path.Combine(GraftPaths.OwnData, "web-accounts");
    internal static string Folder(Guid id) => Path.Combine(StoresRoot, id.ToString("N"));

    internal static async Task<CoreWebView2Environment> EnvironmentAsync(Guid id)
    {
        var folder = Folder(id);
        Directory.CreateDirectory(StoresRoot);
        if ((File.GetAttributes(StoresRoot) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The account browser folder must not be a link.");
        Directory.CreateDirectory(folder);
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The account browser folder must not be a link.");
        return await CoreWebView2Environment.CreateWithOptionsAsync(null, folder, new CoreWebView2EnvironmentOptions());
    }

    internal static void Configure(CoreWebView2 core)
    {
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.PermissionRequested += (_, e) => { e.State = CoreWebView2PermissionState.Deny; e.Handled = true; };
        core.DownloadStarting += (_, e) => { e.Cancel = true; e.Handled = true; };
        ConfigureFixture?.Invoke(core);
    }

    private Task ReadyAsync()
    {
        if (_closed) throw SignedOut();
        if (_loading is { IsFaulted: true } or { IsCanceled: true }) _loading = null;
        return _loading ??= LoadAsync();
    }

    private async Task LoadAsync()
    {
        var generation = _generation;
        CoreWebView2Controller? creating = null;
        try
        {
            _environment = await EnvironmentAsync(id);
            creating = await _environment.CreateCoreWebView2ControllerAsync(
                CoreWebView2ControllerWindowReference.CreateFromWindowHandle(unchecked((ulong)-3L)));
            if (_closed || generation != _generation) { creating.Close(); throw SignedOut(); }
            _controller = creating;
            creating.IsVisible = false;
            var core = creating.CoreWebView2;
            Configure(core);
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.NavigationStarting += (_, e) =>
            {
                if (!WebAccountPolicy.IsClaudeOrigin(e.Uri)) e.Cancel = true;
            };
            core.WebMessageReceived += Received;
            core.ProcessFailed += (_, _) => { if (!_closed && generation == _generation && ReferenceEquals(_controller, creating)) Reset(); };
            var navigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _navigation = navigation;
            core.NavigationCompleted += (_, e) =>
            {
                if (_closed || generation != _generation || !ReferenceEquals(_controller, creating)) return;
                if (e.IsSuccess && WebAccountPolicy.IsClaudeOrigin(core.Source)) navigation.TrySetResult();
                else navigation.TrySetException(new WebAccountException("claude.ai could not be reached. Try Refresh."));
            };
            core.Navigate("https://claude.ai/api/account");
            await navigation.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (_closed || generation != _generation) throw SignedOut();
        }
        catch
        {
            creating?.Close();
            if (generation == _generation) { _controller = null; _loading = null; }
            throw;
        }
    }

    private void Received(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_closed || !WebAccountPolicy.IsClaudeOrigin(e.Source)) return;
        try
        {
            if (JsonNode.Parse(e.WebMessageAsJson) is JsonObject message
                && message["graftRequest"]?.GetValue<string>() is string request
                && _pending.Remove(request, out var completion)) completion.TrySetResult(message);
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
    }

    private async Task<JsonObject> ScriptAsync(string operation, CancellationToken cancellationToken, int timeoutSeconds = 40)
    {
        await ReadyAsync().WaitAsync(cancellationToken);
        if (_closed || _controller is null) throw SignedOut();
        var request = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending.Add(request, completion);
        var requestLiteral = JsonSerializer.Serialize(request);
        var script = "(async()=>{const graftRequest=" + requestLiteral + ";try{const result=await(async()=>{" + operation
            + "})();window.chrome.webview.postMessage({graftRequest,...result});}catch{window.chrome.webview.postMessage({graftRequest,failed:true});}})();";
        try
        {
            await _controller.CoreWebView2.ExecuteScriptAsync(script);
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken);
            if (_closed) throw SignedOut();
            if (result["failed"]?.GetValue<bool>() == true) throw new WebAccountException("claude.ai could not complete the request. Try again.");
            return result;
        }
        catch (TimeoutException)
        {
            Reset();
            throw new WebAccountException("claude.ai took too long to answer. Try Refresh.");
        }
        finally { _pending.Remove(request); }
    }

    public async Task<WebResponse> SendAsync(string method, string path, JsonNode? body,
        IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
    {
        if (method is not ("GET" or "POST" or "PATCH" or "PUT" or "DELETE")) throw new ArgumentException("Unsupported request method.");
        var allHeaders = WebAccountPolicy.Headers(path, body is not null, headers);
        await _requests.WaitAsync(cancellationToken);
        try
        {
            var result = await ScriptAsync("const response=await fetch(" + JsonSerializer.Serialize(path)
                + ",{method:" + JsonSerializer.Serialize(method) + ",credentials:'same-origin',cache:'no-store',redirect:'error',signal:AbortSignal.timeout(30000),headers:"
                + JsonSerializer.Serialize(allHeaders) + ",body:" + (body is null ? "undefined" : JsonSerializer.Serialize(body.ToJsonString()))
                + "});const reader=response.body?.getReader();let text='';const decoder=new TextDecoder();let size=0;if(reader){for(;;){const {done,value}=await reader.read();if(done)break;size+=value.length;if(size>16777216){await reader.cancel();throw Error('limit');}text+=decoder.decode(value,{stream:true});}text+=decoder.decode();}return {status:response.status,body:text};", cancellationToken);
            var status = result["status"]!.GetValue<int>();
            var text = result["body"]?.GetValue<string>() ?? "";
            JsonNode? json = null;
            if (!string.IsNullOrWhiteSpace(text)) { try { json = JsonNode.Parse(text); } catch (JsonException) { } }
            return new(status, json, text);
        }
        finally { _requests.Release(); }
    }

    public async Task<byte[]> DownloadAsync(Uri uri, int limit, CancellationToken cancellationToken)
    {
        if (!WebAccountPolicy.IsArtifactFile(uri) || limit < 1 || limit > 200 * 1024 * 1024)
            throw new ArgumentException("The artifact file address or size is not supported.");
        await _requests.WaitAsync(cancellationToken);
        try
        {
            var operation = "const response=await fetch(" + JsonSerializer.Serialize(uri.AbsoluteUri)
                + ",{credentials:'omit',cache:'no-store',redirect:'error',referrerPolicy:'no-referrer',signal:AbortSignal.timeout(180000)});"
                + "if(!response.ok)return {status:response.status,body:''};const limit=" + limit
                + ";if(Number(response.headers.get('Content-Length')||0)>limit)return {tooLarge:true};"
                + "const reader=response.body.getReader(),chunks=[];let size=0;for(;;){const {done,value}=await reader.read();if(done)break;size+=value.length;if(size>limit){await reader.cancel();return {tooLarge:true};}chunks.push(value);}"
                + "const bytes=new Uint8Array(size);let offset=0;for(const chunk of chunks){bytes.set(chunk,offset);offset+=chunk.length;}let binary='';for(let i=0;i<bytes.length;i+=32768)binary+=String.fromCharCode(...bytes.subarray(i,i+32768));return {status:response.status,body:btoa(binary)};";
            var result = await ScriptAsync(operation, cancellationToken, 190);
            if (result["tooLarge"]?.GetValue<bool>() == true) throw new IOException("The artifact exceeds the 200 MB export limit.");
            if (result["status"]?.GetValue<int>() is not >= 200 or >= 300) throw new IOException("The artifact file could not be downloaded.");
            return Convert.FromBase64String(result["body"]!.GetValue<string>());
        }
        finally { _requests.Release(); }
    }

    private static WebAccountException SignedOut() => new("Sign in to this claude.ai account again.", 401, true);
    private void Reset()
    {
        _generation++;
        _navigation?.TrySetException(new WebAccountException("The account page was restarted. Try Refresh."));
        foreach (var pending in _pending.Values) pending.TrySetException(new WebAccountException("The account page was restarted. Try Refresh."));
        _pending.Clear();
        _controller?.Close();
        _controller = null;
        _loading = null;
    }

    public async Task RetireAsync()
    {
        _closed = true;
        Reset();
        await _requests.WaitAsync();
        try
        {
            var environment = _environment ?? await EnvironmentAsync(id);
            var eraser = await environment.CreateCoreWebView2ControllerAsync(
                CoreWebView2ControllerWindowReference.CreateFromWindowHandle(unchecked((ulong)-3L)));
            try { await eraser.CoreWebView2.Profile.ClearBrowsingDataAsync(); }
            finally { eraser.Close(); }
        }
        finally { _requests.Release(); }
    }

    public void Dispose() { _closed = true; Reset(); }
}
