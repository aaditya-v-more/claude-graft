using System.Text;
using System.Text.Json;
using ClaudeGraft.Core;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace ClaudeGraft;

public static class ArtifactRenderer
{
    public static async Task WriteAsync(string folder, ArtifactExportFormat format, string destination, CancellationToken ct = default)
    {
        if (format == ArtifactExportFormat.Source) throw new ArgumentException("Source export does not need a renderer.");
        folder = Path.GetFullPath(folder);
        if (!File.Exists(Path.Combine(folder, "index.html")) || Fs.IsInside(destination, folder))
            throw new IOException("The artifact cannot be drawn or saved at that location.");
        RejectLinks(folder);
        var renderRoot = Path.Combine(GraftPaths.OwnData, "export-renderers");
        Directory.CreateDirectory(renderRoot);
        if ((File.GetAttributes(renderRoot) & FileAttributes.ReparsePoint) != 0) throw new IOException("The render folder must not be a link.");
        var browserFolder = Path.Combine(renderRoot, Guid.NewGuid().ToString("N"));
        var temporary = Path.GetFullPath(destination) + ".graft-" + Guid.NewGuid().ToString("N");
        CoreWebView2Controller? controller = null;
        var responseBodies = new List<InMemoryRandomAccessStream>();
        try
        {
            ct.ThrowIfCancellationRequested();
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, browserFolder, new CoreWebView2EnvironmentOptions());
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.IsInPrivateModeEnabled = true;
            controller = await environment.CreateCoreWebView2ControllerAsync(
                CoreWebView2ControllerWindowReference.CreateFromWindowHandle(unchecked((ulong)-3L)), options);
            controller.Bounds = new Rect(0, 0, 816, 1056);
            controller.IsVisible = false;
            var core = controller.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.PermissionRequested += (_, e) => { e.State = CoreWebView2PermissionState.Deny; e.Handled = true; };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.DownloadStarting += (_, e) => { e.Cancel = true; e.Handled = true; };
            const string origin = "https://graft-export.invalid/";
            bool Local(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https"
                && uri.Host == "graft-export.invalid" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo);
            core.NavigationStarting += (_, e) => { if (!Local(e.Uri) || new Uri(e.Uri).AbsolutePath != "/index.html") e.Cancel = true; };
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += async (_, e) =>
            {
                var deferral = e.GetDeferral();
                try
                {
                    if (!Local(e.Request.Uri)) throw new IOException();
                    var relative = Uri.UnescapeDataString(new Uri(e.Request.Uri).AbsolutePath).TrimStart('/');
                    if (relative.Contains(':') || relative.Contains('\\')) throw new IOException();
                    var source = Path.GetFullPath(Path.Combine(folder, relative));
                    if (!Fs.IsInside(source, folder) || !File.Exists(source)) throw new IOException();
                    var body = new InMemoryRandomAccessStream();
                    responseBodies.Add(body);
                    using (var writer = new DataWriter(body))
                    { writer.WriteBytes(await File.ReadAllBytesAsync(source, ct)); await writer.StoreAsync(); writer.DetachStream(); }
                    body.Seek(0);
                    // Fetch interception does not cover WebSockets or workers; CSP closes those paths too.
                    const string policy = "default-src 'none'; script-src 'self' 'unsafe-inline' 'unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; connect-src 'none'; frame-src 'none'; worker-src 'none'; media-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'";
                    e.Response = environment.CreateWebResourceResponse(body, 200, "OK", "Content-Type: " + ContentType(source) + "\r\nContent-Security-Policy: " + policy + "\r\nCache-Control: no-store");
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
                    or OperationCanceledException or ObjectDisposedException or System.Runtime.InteropServices.COMException)
                {
                    try { e.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", "Content-Type: text/plain"); }
                    catch (Exception closed) when (closed is ObjectDisposedException or System.Runtime.InteropServices.COMException) { }
                }
                finally
                {
                    try { deferral.Complete(); }
                    catch (Exception closed) when (closed is ObjectDisposedException or System.Runtime.InteropServices.COMException) { }
                }
            };
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            core.NavigationCompleted += (_, e) =>
            {
                if (e.IsSuccess) loaded.TrySetResult();
                else loaded.TrySetException(new IOException("The artifact could not be drawn for export."));
            };
            core.ProcessFailed += (_, _) => loaded.TrySetException(new IOException("The artifact renderer stopped responding."));
            core.Navigate(origin + "index.html");
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(25), ct);
            await Task.Delay(600, ct);
            if (format == ArtifactExportFormat.Pdf)
            {
                var settings = environment.CreatePrintSettings();
                settings.PageWidth = 8.5;
                settings.PageHeight = 11;
                settings.ShouldPrintBackgrounds = true;
                settings.ShouldPrintHeaderAndFooter = false;
                if (!await core.PrintToPdfAsync(temporary, settings)) throw new IOException("The PDF could not be written.");
            }
            else
            {
                var script = format == ArtifactExportFormat.Markdown ? ArtifactExport.MarkdownScript : ArtifactExport.DocumentScript;
                var answer = await core.ExecuteScriptAsync("(()=>{" + script + "})()");
                var text = JsonSerializer.Deserialize<string>(answer) ?? throw new IOException("The artifact could not be drawn for export.");
                ct.ThrowIfCancellationRequested();
                if (format == ArtifactExportFormat.Word) ArtifactExport.WriteWord(temporary, text);
                else await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(false), ct);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            controller?.Close();
            foreach (var body in responseBodies) body.Dispose();
            if (File.Exists(temporary)) File.Delete(temporary);
            try { if (Directory.Exists(browserFolder)) Directory.Delete(browserFolder, true); } catch (IOException) { }
        }
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8", ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8", ".json" => "application/json", ".svg" => "image/svg+xml",
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp",
        ".woff" => "font/woff", ".woff2" => "font/woff2", ".ttf" => "font/ttf", _ => "application/octet-stream"
    };

    private static void RejectLinks(string folder)
    {
        var pending = new Stack<string>(); pending.Push(folder);
        while (pending.TryPop(out var path))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("An artifact export cannot contain filesystem links.");
            if ((attributes & FileAttributes.Directory) != 0)
                foreach (var child in Directory.EnumerateFileSystemEntries(path)) pending.Push(child);
        }
    }
}
