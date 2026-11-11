using System.IO.Compression;
using System.Text;

namespace ClaudeGraft.Core;

public static class ArtifactExport
{
    public static string MarkdownScript => Script("ArtifactMarkdown.js");
    public static string DocumentScript => Script("ArtifactDocument.js");
    private static string Script(string name)
    {
        using var stream = typeof(ArtifactExport).Assembly.GetManifestResourceStream("ClaudeGraft." + name)
            ?? throw new InvalidOperationException("The export renderer is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static string Extension(ArtifactExportFormat format, bool singleFile) => format switch
    { ArtifactExportFormat.Markdown => "md", ArtifactExportFormat.Pdf => "pdf", ArtifactExportFormat.Word => "docx", _ => singleFile ? "html" : "zip" };
    public static string Label(ArtifactExportFormat format) => format switch
    { ArtifactExportFormat.Markdown => "Markdown", ArtifactExportFormat.Pdf => "PDF", ArtifactExportFormat.Word => "Word document", _ => "Page source" };
    public static string FileName(string title, ArtifactExportFormat format, bool singleFile)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var name = new string(title.Select(c => invalid.Contains(c) ? '-' : c).Take(120).ToArray()).Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(name) || Graft.ValidateWindowsName(name) is not null) name = "Artifact";
        return name + "." + Extension(format, singleFile);
    }

    public static Task WriteSourceAsync(string folder, bool singleFile, string destination, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (Fs.IsInside(destination, folder)) throw new IOException("Choose a destination outside the temporary export folder.");
        var temporary = destination + ".graft-" + Guid.NewGuid().ToString("N");
        try
        {
            if (singleFile) File.Copy(Path.Combine(folder, "index.html"), temporary);
            else ZipFile.CreateFromDirectory(folder, temporary, CompressionLevel.Optimal, includeBaseDirectory: false);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }, ct);

    // A real Word package. Word imports the already sanitized rendered HTML
    // inside it, rather than reaching out to the account or any external page.
    public static void WriteWord(string destination, string sanitizedHtml)
    {
        var temporary = destination + ".graft-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                void Entry(string name, string text)
                { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(text); }
                Entry("[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/><Override PartName="/word/content.html" ContentType="text/html"/></Types>""");
                Entry("_rels/.rels", """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="document" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>""");
                Entry("word/document.xml", """<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><w:body><w:altChunk r:id="rendered"/><w:sectPr><w:pgSz w:w="12240" w:h="15840"/><w:pgMar w:top="1080" w:right="1080" w:bottom="1080" w:left="1080"/></w:sectPr></w:body></w:document>""");
                Entry("word/_rels/document.xml.rels", """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rendered" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/aFChunk" Target="content.html"/></Relationships>""");
                Entry("word/content.html", sanitizedHtml);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
