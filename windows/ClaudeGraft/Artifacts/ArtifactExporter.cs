using System.IO.Compression;
using ClaudeGraft.Core;
using Microsoft.Web.WebView2.Core;

namespace ClaudeGraft;

/// <summary>
/// Turns an artifact's files, already read onto this PC, into the file a person
/// asked for. The page is drawn in a private browser of its own that holds no
/// sign-in, keeps nothing and follows no link out of the folder.
/// </summary>
public static class ArtifactExporter
{
    public enum Problem { Gone, Refused, TooLarge, Unrenderable, Unwritable }

    public sealed class ExportFailure(Problem problem, int status = 0) : Exception(problem.ToString())
    {
        public Problem Problem { get; } = problem;
        public int Status { get; } = status;

        public string Text => Problem switch
        {
            Problem.Gone => "claude.ai no longer has that artifact",
            Problem.Refused => $"claude.ai wouldn't hand over its files ({Status})",
            Problem.TooLarge => "That artifact is too large to export",
            Problem.Unrenderable => "That artifact couldn't be drawn for export",
            _ => "The export couldn't be saved there",
        };
    }

    public static async Task Write(string folder, ArtifactExportFormat format, bool singleFile, string destination)
    {
        byte[] data;
        switch (format)
        {
            case ArtifactExportFormat.Html when singleFile:
                try { data = await File.ReadAllBytesAsync(Path.Combine(folder, "index.html")); }
                catch (IOException) { throw new ExportFailure(Problem.Unwritable); }
                break;
            case ArtifactExportFormat.Html:
                // Off the UI thread: an archive of many files held the window still.
                await Task.Run(() =>
                {
                    try
                    {
                        File.Delete(destination);
                        ZipFile.CreateFromDirectory(folder, destination);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ExportFailure(Problem.Unwritable); }
                });
                return;
            default:
                data = await Render(folder, format);
                break;
        }
        try { await File.WriteAllBytesAsync(destination, data); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ExportFailure(Problem.Unwritable); }
    }

    private const string Host = "artifact.graft.example";
    private static Task<CoreWebView2Environment>? _environment;

    private static Task<CoreWebView2Environment> Environment() => _environment ??= CoreWebView2Environment.CreateWithOptionsAsync(null,
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "ClaudeGraft", "export-renderer"),
        new CoreWebView2EnvironmentOptions()).AsTask();

    private static async Task<byte[]> Render(string folder, ArtifactExportFormat format)
    {
        CoreWebView2Controller? controller = null;
        try
        {
            var environment = await Environment();
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.IsInPrivateModeEnabled = true;
            controller = await environment.CreateCoreWebView2ControllerAsync(
                CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)ClaudeWebSession.HiddenParent.Handle), options);
            controller.IsVisible = false;
            // Letter at 96 to the inch, so the page lays out at the width it prints at.
            controller.Bounds = new Windows.Foundation.Rect(0, 0, 816, 1056);
            var web = controller.CoreWebView2;
            web.Settings.AreDevToolsEnabled = false;
            web.SetVirtualHostNameToFolderMapping(Host, folder, CoreWebView2HostResourceAccessKind.DenyCors);
            web.NavigationStarting += (_, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var to) || to.Host != Host) e.Cancel = true;
            };
            web.NewWindowRequested += (_, e) => e.Handled = true;
            var loaded = new TaskCompletionSource<bool>();
            web.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
            web.Navigate($"https://{Host}/index.html");
            var done = await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            if (done != loaded.Task || !loaded.Task.Result) throw new ExportFailure(Problem.Unrenderable);
            // A page that draws itself with script has usually finished a moment
            // after it loads; this is the moment.
            await Task.Delay(600);

            switch (format)
            {
                case ArtifactExportFormat.Markdown:
                    return System.Text.Encoding.UTF8.GetBytes(await Text(web, MarkdownScript));
                case ArtifactExportFormat.Word:
                    return WordDocument.FromHtml(await Text(web, DocumentScript));
                case ArtifactExportFormat.Pdf:
                    var file = Path.Combine(Path.GetTempPath(), $"graft-export-{Guid.NewGuid():N}.pdf");
                    try
                    {
                        var settings = environment.CreatePrintSettings();
                        settings.ShouldPrintBackgrounds = true;
                        if (!await web.PrintToPdfAsync(file, settings)) throw new ExportFailure(Problem.Unrenderable);
                        return await File.ReadAllBytesAsync(file);
                    }
                    finally { File.Delete(file); }
                default:
                    throw new ExportFailure(Problem.Unrenderable);
            }
        }
        catch (Exception e) when (e is ClaudeWebSession.WebFailure or System.Runtime.InteropServices.COMException or IOException)
        {
            throw new ExportFailure(Problem.Unrenderable);
        }
        finally { controller?.Close(); }
    }

    private static async Task<string> Text(CoreWebView2 web, string body)
    {
        var answer = await ClaudeWebSession.Evaluate(web, "async () => ({text: await (async () => {" + body + "})()})", "");
        return (string?)answer["text"] ?? throw new ExportFailure(Problem.Unrenderable);
    }

    /// Walks the page as drawn, so an artifact that builds itself with script
    /// exports what a person sees. What claude.ai's own Markdown export leaves out
    /// is left out here too: scripts, styles, drawings, controls and anything hidden.
    private const string MarkdownScript = """
        const SKIP = new Set(["SCRIPT", "STYLE", "SVG", "BUTTON", "IFRAME", "CANVAS", "NOSCRIPT", "TEMPLATE",
                              "HEAD", "INPUT", "SELECT", "TEXTAREA", "VIDEO", "AUDIO", "OBJECT", "EMBED", "MATH"]);
        const BLOCK = new Set(["DIV", "SECTION", "ARTICLE", "MAIN", "HEADER", "FOOTER", "NAV", "ASIDE", "FORM",
                               "FIGURE", "FIGCAPTION", "DETAILS", "SUMMARY", "DL", "DT", "DD", "ADDRESS",
                               "FIELDSET", "BODY", "CENTER"]);
        const tagOf = (node) => node.nodeType === 1 ? node.tagName.toUpperCase() : "";
        const hidden = (node) => {
            const style = getComputedStyle(node);
            return style.display === "none" || style.visibility === "hidden";
        };
        const escape = (text) => text.replace(/([\\`*_~<\[\]])/g, "\\$1");
        const words = (text) => escape(text.replace(/\s+/g, " "));
        const line = (text) => text.replace(/^([#>+-])/, "\\$1").replace(/^(\d+)\./, "$1\\.");

        function inline(node) {
            let out = "";
            for (const child of node.childNodes) out += inlineNode(child);
            return out;
        }

        function inlineNode(child) {
            if (child.nodeType === 3) return words(child.nodeValue);
            if (child.nodeType !== 1) return "";
            const tag = tagOf(child);
            if (SKIP.has(tag) || hidden(child)) return "";
            const wrap = (mark) => {
                const text = inline(child).trim();
                return text ? mark + text + mark : "";
            };
            switch (tag) {
            case "BR": return "  \n";
            case "STRONG": case "B": return wrap("**");
            case "EM": case "I": return wrap("*");
            case "DEL": case "S": case "STRIKE": return wrap("~~");
            case "CODE": case "KBD": case "SAMP": {
                const text = child.textContent;
                if (!text) return "";
                const fence = text.includes("`") ? "`` " : "`";
                return fence + text + fence.split("").reverse().join("");
            }
            case "A": {
                const text = inline(child).trim();
                const href = child.getAttribute("href") || "";
                if (!href || href.startsWith("#") || /^javascript:/i.test(href)) return text;
                return "[" + (text || escape(href)) + "](" + href.replace(/ /g, "%20").replace(/\)/g, "%29") + ")";
            }
            case "IMG": {
                const alt = child.getAttribute("alt") || "";
                const src = child.getAttribute("src") || "";
                if (src && !src.startsWith("data:")) return "![" + escape(alt) + "](" + src.replace(/ /g, "%20") + ")";
                return escape(alt);
            }
            default: return inline(child);
            }
        }

        function list(element) {
            const ordered = tagOf(element) === "OL";
            let number = parseInt(element.getAttribute("start") || "1", 10) || 1;
            const lines = [];
            for (const item of element.children) {
                if (tagOf(item) !== "LI" || hidden(item)) continue;
                const box = [...item.children].find((c) => tagOf(c) === "INPUT" && c.type === "checkbox");
                let text = "";
                const nested = [];
                for (const child of item.childNodes) {
                    const tag = tagOf(child);
                    if (tag === "UL" || tag === "OL") nested.push(list(child));
                    else text += inlineNode(child);
                }
                const marker = ordered ? (number++) + "." : "-";
                const check = box ? (box.checked ? "[x] " : "[ ] ") : "";
                lines.push(marker + " " + check + text.replace(/\s*\n\s*/g, " ").trim());
                const indent = " ".repeat(marker.length + 1);
                for (const block of nested) lines.push(block.split("\n").map((l) => indent + l).join("\n"));
            }
            return lines.join("\n");
        }

        function table(element) {
            const rows = [...element.querySelectorAll("tr")].map((row) =>
                [...row.children].filter((cell) => ["TD", "TH"].includes(tagOf(cell)))
                    .map((cell) => inline(cell).replace(/\s+/g, " ").trim().replace(/\|/g, "\\|")));
            const width = Math.max(0, ...rows.map((row) => row.length));
            if (!width) return "";
            const pad = (row) => "| " + [...row, ...Array(width - row.length).fill("")].join(" | ") + " |";
            const lines = [pad(rows[0]), "|" + " --- |".repeat(width)];
            for (const row of rows.slice(1)) lines.push(pad(row));
            return lines.join("\n");
        }

        function blocks(node, out) {
            let paragraph = "";
            const flush = () => {
                const text = paragraph.replace(/[ \t]+/g, " ").replace(/ *\n */g, "\n").trim();
                if (text) out.push(line(text));
                paragraph = "";
            };
            for (const child of node.childNodes) {
                if (child.nodeType === 3) { paragraph += words(child.nodeValue); continue; }
                if (child.nodeType !== 1) continue;
                const tag = tagOf(child);
                if (SKIP.has(tag) || hidden(child)) continue;
                if (/^H[1-6]$/.test(tag)) {
                    flush();
                    const text = inline(child).replace(/\s+/g, " ").trim();
                    if (text) out.push("#".repeat(Number(tag[1])) + " " + text);
                } else if (tag === "P") {
                    flush();
                    paragraph = inline(child);
                    flush();
                } else if (tag === "PRE") {
                    flush();
                    const text = child.textContent.replace(/\n$/, "");
                    const fence = text.includes("```") ? "~~~~" : "```";
                    out.push(fence + "\n" + text + "\n" + fence);
                } else if (tag === "UL" || tag === "OL") {
                    flush();
                    const text = list(child);
                    if (text) out.push(text);
                } else if (tag === "BLOCKQUOTE") {
                    flush();
                    const inner = [];
                    blocks(child, inner);
                    if (inner.length) out.push(inner.join("\n\n").split("\n").map((l) => l ? "> " + l : ">").join("\n"));
                } else if (tag === "HR") {
                    flush();
                    out.push("---");
                } else if (tag === "TABLE") {
                    flush();
                    const text = table(child);
                    if (text) out.push(text);
                } else if (BLOCK.has(tag)) {
                    flush();
                    blocks(child, out);
                } else {
                    paragraph += inlineNode(child);
                }
            }
            flush();
        }

        const out = [];
        if (document.body) blocks(document.body, out);
        return out.join("\n\n").replace(/\n{3,}/g, "\n\n").trim() + "\n";
        """;

    /// The page as drawn, without what would run or reach out when Word reads it.
    /// Word cannot see the folder, so the artifact's own pictures and style
    /// sheets go in by value; anything from the web goes.
    private const string DocumentScript = """
        const copy = document.documentElement.cloneNode(true);
        copy.querySelectorAll("script, noscript, iframe, object, embed, template").forEach((e) => e.remove());
        const remote = (address) => /^[a-z][a-z0-9+.-]*:/i.test(address) && !/^data:/i.test(address);
        const asData = async (address) => {
            const blob = await (await fetch(address)).blob();
            return await new Promise((done) => {
                const reader = new FileReader();
                reader.onload = () => done(reader.result);
                reader.onerror = () => done("");
                reader.readAsDataURL(blob);
            });
        };
        for (const e of copy.querySelectorAll("img, link, source, video, audio")) {
            const address = e.getAttribute("src") || e.getAttribute("href") || "";
            if (!address || remote(address)) { e.remove(); continue; }
            try {
                if (e.tagName === "IMG" && !address.startsWith("data:")) e.setAttribute("src", await asData(address));
                else if (e.tagName === "LINK" && /stylesheet/i.test(e.getAttribute("rel") || "")) {
                    const style = document.createElement("style");
                    style.textContent = await (await fetch(address)).text();
                    e.replaceWith(style);
                } else if (e.tagName !== "IMG") e.remove();
            } catch { e.remove(); }
        }
        return "<!doctype html>" + copy.outerHTML;
        """;
}
