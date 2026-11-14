using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeGraft.Core;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Streams;

namespace ClaudeGraft;

// The marker-gated entry point uses only disposable stores and intercepts every browser request.
internal static class WebAccountChecks
{
    public static async Task RunAsync()
    {
        var checks = new List<string>();
        var streams = new List<InMemoryRandomAccessStream>();
        var output = Path.Combine(GraftPaths.OwnData, "web-account-checks.json");
        Directory.CreateDirectory(GraftPaths.OwnData);
        void Check(bool condition, string name)
        { if (!condition) throw new IOException(name); checks.Add(name); }
        var signedOut = false;
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accountChecks = 0;
        var external = 0;
        AccountWebSession.ConfigureFixture = core =>
        {
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += async (_, e) =>
            {
                var deferral = e.GetDeferral();
                try
                {
                    var uri = new Uri(e.Request.Uri);
                    var status = 200;
                    string content, headers = "Content-Type: application/json\r\nCache-Control: no-store";
                    if (uri.Host.EndsWith(".frame.claudeusercontent.com"))
                    { content = "fixture artifact"; headers += "\r\nAccess-Control-Allow-Origin: https://claude.ai"; }
                    else if (uri.Host != "claude.ai") { external++; status = 403; content = "{}"; }
                    else if (uri.AbsolutePath == "/api/slow")
                    { slowStarted.TrySetResult(); await Task.Delay(1200); content = "{\"late\":true}"; }
                    else if (uri.AbsolutePath == "/api/account")
                    {
                        accountChecks++;
                        status = signedOut ? 401 : 200;
                        content = "{\"uuid\":\"fixture-account\",\"email_address\":\"fixture@example.invalid\",\"memberships\":[{\"organization\":{\"uuid\":\"fixture-org\",\"capabilities\":[\"chat\"]}}]}";
                    }
                    else if (uri.AbsolutePath == "/api/forbidden") { status = 403; content = "{\"error\":\"fixture permission\"}"; }
                    else if (uri.AbsolutePath == "/api/set-cookie")
                    { content = "{}"; headers += "\r\nSet-Cookie: graft_fixture=first; Path=/; Secure; SameSite=Strict; Max-Age=3600"; }
                    else
                    {
                        var cookie = e.Request.Headers.Contains("Cookie") ? e.Request.Headers.GetHeader("Cookie") : "";
                        content = JsonSerializer.Serialize(new { hasFixtureCookie = cookie.Contains("graft_fixture=first"), method = e.Request.Method });
                    }
                    var stream = new InMemoryRandomAccessStream(); streams.Add(stream);
                    using (var writer = new DataWriter(stream))
                    { writer.WriteBytes(System.Text.Encoding.UTF8.GetBytes(content)); await writer.StoreAsync(); writer.DetachStream(); }
                    stream.Seek(0);
                    e.Response = core.Environment.CreateWebResourceResponse(stream, status, status == 200 ? "OK" : "Refused", headers);
                }
                catch (Exception) { }
                finally { deferral.Complete(); }
            };
        };
        try
        {
            var first = Guid.NewGuid(); var second = Guid.NewGuid();
            var retryId = Guid.NewGuid();
            var linked = Path.Combine(GraftPaths.OwnData, "browser-retry-target"); Directory.CreateDirectory(linked);
            Directory.CreateDirectory(AccountWebSession.StoresRoot);
            Junction.Create(AccountWebSession.Folder(retryId), linked);
            using (var retry = new AccountWebSession(retryId))
            {
                var refused = false;
                try { await retry.SendAsync("GET", "/api/cookie", null, null, default); }
                catch (IOException) { refused = true; }
                Directory.Delete(AccountWebSession.Folder(retryId));
                var response = await retry.SendAsync("GET", "/api/cookie", null, null, default);
                Check(refused && response.StatusCode == 200, "a failed browser initialization can retry after its folder is repaired");
            }
            using (var a = new AccountWebSession(first))
            using (var b = new AccountWebSession(second))
            {
                await a.SendAsync("GET", "/api/set-cookie", null, null, default);
                var own = await a.SendAsync("GET", "/api/cookie", null, null, default);
                var other = await b.SendAsync("GET", "/api/cookie", null, null, default);
                Check(own.Json?["hasFixtureCookie"]?.GetValue<bool>() == true, "the browser keeps its own fixture cookie");
                Check(other.Json?["hasFixtureCookie"]?.GetValue<bool>() == false, "another account browser does not inherit that cookie");
                var bytes = await a.DownloadAsync(new Uri("https://11111111-1111-1111-1111-111111111111.frame.claudeusercontent.com/_f/file"), 1024, default);
                Check(System.Text.Encoding.UTF8.GetString(bytes) == "fixture artifact", "artifact downloads cross only the approved file origin");
            }
            using (var reopened = new AccountWebSession(first))
            {
                var own = await reopened.SendAsync("GET", "/api/cookie", null, null, default);
                Check(own.Json?["hasFixtureCookie"]?.GetValue<bool>() == true, "an account browser retains its sign-in across controller recreation");
                await reopened.RetireAsync();
            }
            using (var cleared = new AccountWebSession(first))
            {
                var own = await cleared.SendAsync("GET", "/api/cookie", null, null, default);
                Check(own.Json?["hasFixtureCookie"]?.GetValue<bool>() == false, "sign-out clears the browser session before retiring its files");
            }
            var record = new WebAccount { Id = second, AccountUuid = "fixture-account", Email = "fixture@example.invalid", OrganizationId = "fixture-org" };
            new WebAccountStore().Save([record]);
            using (var center = new AccountCenter())
            {
                await center.SendAsync(record, "GET", "/api/cookie");
                var before = accountChecks;
                var permission = await center.SendAsync(record, "GET", "/api/forbidden");
                Check(permission.StatusCode == 403 && center.State(record.Id) != false && accountChecks == before + 1,
                    "a permission refusal checks identity once without signing out a working account");
                signedOut = true;
                var rejected = false;
                try { await center.SendAsync(record, "GET", "/api/forbidden"); }
                catch (WebAccountException e) { rejected = e.IsSignedOut; }
                Check(rejected && center.State(record.Id) == false, "a signed-out identity marks only its own account");
                signedOut = false;
                var pending = center.SendAsync(record, "GET", "/api/slow");
                await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var removed = center.SignOutAsync(record);
                rejected = false;
                try { await pending; } catch (WebAccountException) { rejected = true; }
                await removed;
                Check(rejected && !center.IsCurrent(record) && center.Accounts.Count == 0,
                    "a late response cannot restore an account removed during its request");
            }
            Check(external == 0, "browser fixtures never request a live account or outside origin");

            var flyout = new FlyoutView();
            ((Microsoft.UI.Xaml.Controls.Border)flyout.FindName("PoolPanel")).Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            ((Microsoft.UI.Xaml.Controls.TextBlock)flyout.FindName("PoolTitle")).Text = "A current conversation with a long title that spans two lines";
            for (var index = 0; index < 12; index++) flyout.Rows.Add(ShortcutRow.ForShortcut(Shortcut.New("Fixture " + index, source: ShortcutSource.Own)));
            flyout.ConstrainHeight(480);
            flyout.Measure(new Windows.Foundation.Size(320, double.PositiveInfinity));
            Check(flyout.DesiredSize.Height <= 480 && flyout.DesiredSize.Height > 200,
                "a long tray list and its current conversation fit inside a short monitor work area");

            var source = Path.Combine(GraftPaths.OwnData, "export-fixture"); Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "render.js"), "document.getElementById('result').textContent='Rendered fixture'; document.addEventListener('securitypolicyviolation',e=>{const p=document.createElement('p');p.textContent='Blocked '+e.effectiveDirective;document.body.append(p)});fetch('https://example.invalid/blocked').catch(()=>{});try{new WebSocket('wss://example.invalid/blocked')}catch{};try{new Worker('render.js')}catch{};");
            await File.WriteAllTextAsync(Path.Combine(source, "index.html"), "<!doctype html><html><head><meta charset='utf-8'></head><body><h1 id='result'>Waiting</h1><p>Local export</p><script src='render.js'></script></body></html>");
            var markdown = Path.Combine(GraftPaths.OwnData, "fixture.md");
            await ArtifactRenderer.WriteAsync(source, ArtifactExportFormat.Markdown, markdown);
            var text = await File.ReadAllTextAsync(markdown);
            Check(text.Contains("Rendered fixture") && !text.Contains("Waiting"), "Markdown exports render downloaded JavaScript before reading the document");
            Check(text.Contains("Blocked connect-src") && text.Contains("Blocked worker-src"), "offline exports block fetch, WebSocket and worker connections with CSP");
            var pdf = Path.Combine(GraftPaths.OwnData, "fixture.pdf");
            await ArtifactRenderer.WriteAsync(source, ArtifactExportFormat.Pdf, pdf);
            Check((await File.ReadAllBytesAsync(pdf)).AsSpan(0, 5).SequenceEqual("%PDF-"u8), "PDF export writes a real rendered PDF");
            var word = Path.Combine(GraftPaths.OwnData, "fixture.docx");
            await ArtifactRenderer.WriteAsync(source, ArtifactExportFormat.Word, word);
            using (var zip = ZipFile.OpenRead(word))
            using (var reader = new StreamReader(zip.GetEntry("word/content.html")!.Open()))
            {
                var document = await reader.ReadToEndAsync();
                Check(document.Contains("Rendered fixture") && !document.Contains("<script", StringComparison.OrdinalIgnoreCase),
                    "Word export contains the rendered document without executable scripts");
            }
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { passed = checks.Count, failed = 0, checks }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { passed = checks.Count, failed = 1, checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            AccountWebSession.ConfigureFixture = null;
            foreach (var stream in streams) stream.Dispose();
        }
    }
}
