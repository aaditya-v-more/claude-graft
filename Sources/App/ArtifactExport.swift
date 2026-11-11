import AppKit
import UniformTypeIdentifiers
import WebKit

/// Turns an artifact's files, already read onto this Mac, into the file a
/// person asked for.
@MainActor
enum ArtifactExport {
    enum Failure: Error, Equatable {
        case gone
        case refused(Int)
        case tooLarge
        case unrenderable
        case unwritable

        var message: String {
            switch self {
            case .gone: return L10n.text("claude.ai no longer has that artifact")
            case .refused(let status): return L10n.format("claude.ai wouldn't hand over its files (%ld)", status)
            case .tooLarge: return L10n.text("That artifact is too large to export")
            case .unrenderable: return L10n.text("That artifact couldn't be drawn for export")
            case .unwritable: return L10n.text("The export couldn't be saved there")
            }
        }
    }

    static func askWhere(title: String, format: ArtifactExportFormat, singleFile: Bool) -> URL? {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = ArtifactExportFormat.fileName(for: title, format: format, singleFile: singleFile)
        if let type = UTType(filenameExtension: format.fileExtension(singleFile: singleFile)) {
            panel.allowedContentTypes = [type]
        }
        panel.canCreateDirectories = true
        NSApp.activate(ignoringOtherApps: true)
        return panel.runModal() == .OK ? panel.url : nil
    }

    static func write(folder: URL, format: ArtifactExportFormat, singleFile: Bool, to destination: URL) async throws {
        let index = folder.appending(path: "index.html")
        let data: Data
        switch format {
        case .html where singleFile:
            guard let page = try? Data(contentsOf: index) else { throw Failure.unwritable }
            data = page
        case .html:
            try await zip(folder, to: destination)
            return
        case .markdown, .pdf, .word:
            data = try await ArtifactRenderer.render(index, in: folder, as: format)
        }
        do { try data.write(to: destination, options: .atomic) } catch { throw Failure.unwritable }
    }

    /// `ditto` makes the archive Finder makes, which is the one a person
    /// double-clicks expecting it to open. It is waited on off the main
    /// thread, where an archive of many files held the whole window still.
    private static func zip(_ folder: URL, to destination: URL) async throws {
        try await Task.detached(priority: .userInitiated) {
            try? FileManager.default.removeItem(at: destination)
            let process = Process()
            process.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
            process.arguments = ["-c", "-k", "--norsrc", folder.path, destination.path]
            do { try process.run() } catch { throw Failure.unwritable }
            process.waitUntilExit()
            guard process.terminationStatus == 0 else { throw Failure.unwritable }
        }.value
    }

    /// Walks the page as drawn, so an artifact that builds itself with
    /// script exports what a person sees rather than an empty shell. What
    /// claude.ai's own Markdown export leaves out is left out here too:
    /// scripts, styles, drawings, controls and anything hidden.
    static let markdownScript = #"""
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
        """#

    /// The page as drawn, without what would run or reach out when Word's
    /// importer reads it: scripts and frames go, and so does any picture or
    /// style sheet from the web, which the importer would fetch while
    /// everything waited.
    static let documentScript = #"""
        const copy = document.documentElement.cloneNode(true);
        copy.querySelectorAll("script, noscript, iframe, object, embed, template").forEach((e) => e.remove());
        copy.querySelectorAll("img, link, source, video, audio").forEach((e) => {
            const address = e.getAttribute("src") || e.getAttribute("href") || "";
            if (/^[a-z][a-z0-9+.-]*:/i.test(address) && !/^(data|file):/i.test(address)) e.remove();
        });
        return "<!doctype html>" + copy.outerHTML;
        """#
}

/// Draws an artifact's own page from the folder it was read into, in a web
/// view that keeps nothing and holds no sign-in, and reads back what it drew.
/// It is never shown, and it follows no link out of the folder.
@MainActor
final class ArtifactRenderer: NSObject, WKNavigationDelegate {
    private let folder: URL
    private var webView: WKWebView?
    private var waiting: CheckedContinuation<Void, Error>?

    /// Letter at 96 points to the inch, so the PDF reads at the width it
    /// would print at.
    static let pageWidth: CGFloat = 816
    static let pageHeight: CGFloat = 1056

    private init(folder: URL) {
        self.folder = folder.standardizedFileURL
    }

    static func render(_ index: URL, in folder: URL, as format: ArtifactExportFormat) async throws -> Data {
        let renderer = ArtifactRenderer(folder: folder)
        defer { renderer.webView = nil }
        return try await renderer.run(index, format)
    }

    private func run(_ index: URL, _ format: ArtifactExportFormat) async throws -> Data {
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = .nonPersistent()
        let view = WKWebView(frame: NSRect(x: 0, y: 0, width: Self.pageWidth, height: Self.pageHeight),
                             configuration: configuration)
        view.navigationDelegate = self
        webView = view
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            waiting = continuation
            view.loadFileURL(index, allowingReadAccessTo: folder)
            Task {
                try? await Task.sleep(nanoseconds: 20_000_000_000)
                self.finish(ArtifactExport.Failure.unrenderable)
            }
        }
        // A page that draws itself with script has usually finished a moment
        // after it loads; this is the moment.
        try? await Task.sleep(nanoseconds: 600_000_000)
        switch format {
        case .markdown:
            guard let text = try? await view.callAsyncJavaScript(ArtifactExport.markdownScript, arguments: [:],
                                                                 contentWorld: .defaultClient) as? String
            else { throw ArtifactExport.Failure.unrenderable }
            return Data(text.utf8)
        case .pdf:
            let height = (try? await view.callAsyncJavaScript(
                "return Math.max(document.documentElement.scrollHeight, document.body ? document.body.scrollHeight : 0);",
                arguments: [:], contentWorld: .defaultClient) as? NSNumber)?.doubleValue ?? 0
            view.setFrameSize(NSSize(width: Self.pageWidth, height: max(Self.pageHeight, CGFloat(height))))
            try? await Task.sleep(nanoseconds: 200_000_000)
            return try await pdf(of: view)
        case .word:
            guard let html = try? await view.callAsyncJavaScript(ArtifactExport.documentScript, arguments: [:],
                                                                 contentWorld: .defaultClient) as? String,
                  let document = NSAttributedString(html: Data(html.utf8), baseURL: folder, documentAttributes: nil)
            else { throw ArtifactExport.Failure.unrenderable }
            do {
                return try document.data(from: NSRange(location: 0, length: document.length),
                                         documentAttributes: [.documentType: NSAttributedString.DocumentType.officeOpenXML])
            } catch {
                throw ArtifactExport.Failure.unrenderable
            }
        case .html:
            throw ArtifactExport.Failure.unrenderable
        }
    }

    private func pdf(of view: WKWebView) async throws -> Data {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Data, Error>) in
            view.createPDF(configuration: WKPDFConfiguration()) { result in
                switch result {
                case .success(let data): continuation.resume(returning: data)
                case .failure: continuation.resume(throwing: ArtifactExport.Failure.unrenderable)
                }
            }
        }
    }

    private func finish(_ error: Error?) {
        guard let waiting else { return }
        self.waiting = nil
        if let error { waiting.resume(throwing: error) } else { waiting.resume() }
    }

    private func isInside(_ url: URL?) -> Bool {
        guard let url, url.isFileURL else { return url?.scheme == "about" }
        return url.standardizedFileURL.path.hasPrefix(folder.path + "/")
    }

    nonisolated func webView(_ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction,
                             decisionHandler: @escaping @MainActor @Sendable (WKNavigationActionPolicy) -> Void) {
        MainActor.assumeIsolated {
            decisionHandler(isInside(navigationAction.request.url) ? .allow : .cancel)
        }
    }

    nonisolated func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        Task { @MainActor in self.finish(nil) }
    }

    nonisolated func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) {
        Task { @MainActor in self.finish(ArtifactExport.Failure.unrenderable) }
    }

    nonisolated func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!,
                             withError error: Error) {
        Task { @MainActor in self.finish(ArtifactExport.Failure.unrenderable) }
    }
}
