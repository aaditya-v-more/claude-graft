using System.IO.Compression;
using System.Text;

namespace ClaudeGraft.Core;

/// <summary>
/// A .docx holding a page as an HTML part, which Word turns into its own
/// paragraphs, tables and pictures when it opens the file.
/// </summary>
public static class WordDocument
{
    private const string ChunkType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/aFChunk";

    public static byte[] FromHtml(string html)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
                  <Override PartName="/word/page.html" ContentType="text/html"/>
                </Types>
                """);
            Add(zip, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="document" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
                </Relationships>
                """);
            Add(zip, "word/_rels/document.xml.rels", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="page" Type="{ChunkType}" Target="page.html"/>
                </Relationships>
                """);
            Add(zip, "word/document.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <w:body><w:altChunk r:id="page"/><w:sectPr/></w:body>
                </w:document>
                """);
            // Word takes the part's encoding from the page itself, and guesses a
            // legacy code page for one that names none.
            Add(zip, "word/page.html", html.Contains("charset", StringComparison.OrdinalIgnoreCase)
                ? html
                : html.Replace("<head>", "<head><meta charset=\"utf-8\">", StringComparison.OrdinalIgnoreCase));
        }
        return stream.ToArray();
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }
}
