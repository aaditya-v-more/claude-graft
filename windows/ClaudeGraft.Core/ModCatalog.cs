using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeGraft.Core;

/// <summary>
/// The featured mods of the community list at awesome-claude-code-mods, joined
/// to its scan for what each can reach. Asked only when the Browse view is shown
/// or refreshed, never on a timer. A mod is installed at the commit the scan
/// read, so what was reported is what arrives.
/// </summary>
public static partial class ModCatalog
{
    public const string ListRepository = "karanb192/awesome-claude-code-mods";
    public const string ListPage = "https://github.com/" + ListRepository;
    private const string Raw = "https://raw.githubusercontent.com/" + ListRepository + "/main/";

    public static string CacheDirectory => Path.Combine(GraftPaths.OwnData, "catalogue");
    public const string SourceFile = "graft-catalogue.json";
    private const long MaxDownload = 50L * 1024 * 1024;

    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(60) };

    public sealed record Featured(string Name, string Url, string Description);

    public sealed record Scanned(string Id, string Repo, string Path, string Commit, string Name, string Description,
        IReadOnlyList<string> Reach, string Validation, int Stars = 0, string Kind = "", string Url = "",
        int? Level = null, DateTimeOffset? Updated = null);

    public sealed record Entry(Featured Featured, Scanned? Scan);

    public sealed record Source(string Repo, string Path, string Commit);

    /// The bullets under the README's Featured heading, each `- [name](url) - text`.
    public static List<Featured> ParseFeatured(string readme)
    {
        var start = readme.IndexOf("\n## Featured", StringComparison.Ordinal);
        if (start < 0) return new();
        var end = readme.IndexOf("\n## ", start + 4, StringComparison.Ordinal);
        var section = end < 0 ? readme[start..] : readme[start..end];
        return FeaturedLine().Matches(section)
            .Select(m => new Featured(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value.Trim()))
            .ToList();
    }

    [GeneratedRegex(@"^- \[([^\]]+)\]\((https://github\.com/[^)\s]+)\)\s+-\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex FeaturedLine();

    public static List<Scanned> ParseScan(Stream json)
    {
        var found = new List<Scanned>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("mods", out var mods) || mods.ValueKind != JsonValueKind.Array) return found;
        foreach (var m in mods.EnumerateArray())
        {
            string S(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            var reach = m.TryGetProperty("reach", out var r) && r.ValueKind == JsonValueKind.Object && r.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array
                ? labels.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.String).Select(l => l.GetString()!).ToList()
                : new List<string>();
            var validation = m.TryGetProperty("validate", out var v) && v.ValueKind == JsonValueKind.Object ? S(v, "status") : "";
            var stars = m.TryGetProperty("stars", out var st) && st.ValueKind == JsonValueKind.Number && st.TryGetInt32(out var n) ? n : 0;
            int? level = m.TryGetProperty("reach", out var rl) && rl.ValueKind == JsonValueKind.Object && rl.TryGetProperty("level", out var lv)
                && lv.ValueKind == JsonValueKind.Number && lv.TryGetInt32(out var l) ? l : null;
            DateTimeOffset? updated = DateTimeOffset.TryParse(S(m, "pushedAt"), out var at) ? at : null;
            found.Add(new Scanned(S(m, "id"), S(m, "repo"), S(m, "path"), S(m, "sourceCommit"), S(m, "name"), S(m, "description"),
                reach, validation, stars, S(m, "kind"), S(m, "url"), level, updated));
        }
        return found;
    }

    /// A featured link names a repository and sometimes a folder in it; the
    /// scan's entry for that folder, or the repository's only one, is its match.
    public static Scanned? Match(Featured featured, IReadOnlyList<Scanned> scan)
    {
        var parts = new Uri(featured.Url).AbsolutePath.Trim('/').Split('/');
        if (parts.Length < 2) return null;
        var repo = $"{parts[0]}/{parts[1]}";
        var folder = parts.Length > 4 && parts[2] == "tree" ? string.Join('/', parts[4..]) : null;
        var inRepo = scan.Where(s => s.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase)).ToList();
        if (folder is not null) return inRepo.FirstOrDefault(s => s.Path.Equals(folder, StringComparison.OrdinalIgnoreCase));
        return inRepo.FirstOrDefault(s => s.Name.Equals(featured.Name, StringComparison.OrdinalIgnoreCase))
            ?? (inRepo.Count == 1 ? inRepo[0] : null);
    }

    public sealed record Catalogue(List<Entry> Featured, List<Scanned> All);

    public static async Task<Catalogue> Load(CancellationToken cancel = default)
    {
        var readme = await Http.GetStringAsync(Raw + "README.md", cancel);
        var featured = ParseFeatured(readme);
        await using var scanFile = await Cached("data/mods.json", "mods.json", cancel);
        var scan = ParseScan(scanFile);
        return new Catalogue(featured.Select(f => new Entry(f, Match(f, scan))).ToList(), scan);
    }

    public enum Sort { Stars, Updated, Name }

    /// The most a mod may reach, by the scan's levels: draws and remembers, reads,
    /// writes or runs, network. Null allows anything, an unscanned level included.
    public sealed record Filter(string Query = "", int? MostReach = null, bool PassedOnly = false, Sort Order = Sort.Stars,
        bool DesktopOnly = false);

    /// Only what the author wrote: a mod that names the desktop app or its Code
    /// tab, and does not go on to say it does nothing there. Nothing is read of
    /// its code before it is downloaded.
    public static bool MentionsDesktop(string description) =>
        DesktopWords().IsMatch(description) && !NotOnDesktop().IsMatch(description);

    [GeneratedRegex(@"\bdesktop\b|\bcode tab\b|デスクトップ|桌面", RegexOptions.IgnoreCase)]
    private static partial Regex DesktopWords();

    [GeneratedRegex(@"terminal[- ]only|only (works )?in (the |a )?terminal|(does|do) nothing in (claude )?desktop|not (work|supported) in (claude )?desktop|不做任何事|只在终端", RegexOptions.IgnoreCase)]
    private static partial Regex NotOnDesktop();

    /// A mod that signs in, switches accounts or moves sessions does what Graft
    /// does, and the two may each undo the other.
    public static bool ManagesAccountsOrSessions(Scanned mod) =>
        mod.Reach.Contains("reads your sign-in") || AccountsOrSessions().IsMatch(mod.Description);

    [GeneratedRegex(@"switch(es|ing)? (between )?(claude |your )?accounts?|accounts? switch|multiple (claude )?accounts|(move|moves|moving|transfer|sync|syncs) (claude |desktop |code )*sessions?|sessions? between|session manager|manage[sd]? (claude |desktop )+sessions|usage (pool|sharing)|share (your )?usage", RegexOptions.IgnoreCase)]
    private static partial Regex AccountsOrSessions();

    /// Every mod the list publishes: built-in mods ship with Claude Code, and
    /// the list's fixtures, catalogues and duplicates are not mods to install.
    /// Each word of the query must appear in the name, description or repository.
    public static List<Scanned> Browse(IEnumerable<Scanned> scan, Filter filter)
    {
        var words = filter.Query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var found = scan.Where(m => m.Kind == "mod" && m.Commit.Length > 0)
            .Where(m => filter.MostReach is not { } most || m.Level is { } level && level <= most)
            .Where(m => !filter.PassedOnly || m.Validation is "passed" or "warnings")
            .Where(m => !filter.DesktopOnly || MentionsDesktop(m.Description))
            .Where(m => words.All(w => m.Name.Contains(w, StringComparison.OrdinalIgnoreCase)
                || m.Description.Contains(w, StringComparison.OrdinalIgnoreCase) || m.Repo.Contains(w, StringComparison.OrdinalIgnoreCase)));
        return (filter.Order switch
        {
            Sort.Updated => found.OrderByDescending(m => m.Updated ?? DateTimeOffset.MinValue),
            Sort.Name => found.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase),
            _ => found.OrderByDescending(m => m.Stars),
        }).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();
    }

    /// The scan is tens of megabytes, so it is asked for again only with the
    /// ETag of the copy kept, and a 304 reads that copy.
    private static async Task<Stream> Cached(string remote, string name, CancellationToken cancel)
    {
        Directory.CreateDirectory(CacheDirectory);
        var file = Path.Combine(CacheDirectory, name);
        var tagFile = file + ".etag";
        using var request = new HttpRequestMessage(HttpMethod.Get, Raw + remote);
        if (File.Exists(file) && File.Exists(tagFile) && EntityTagHeaderValueOrNull(File.ReadAllText(tagFile)) is { } tag)
            request.Headers.IfNoneMatch.Add(tag);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        if (response.StatusCode == HttpStatusCode.NotModified) return File.OpenRead(file);
        response.EnsureSuccessStatusCode();
        var temp = file + ".part";
        await using (var target = File.Create(temp)) await response.Content.CopyToAsync(target, cancel);
        File.Move(temp, file, overwrite: true);
        if (response.Headers.ETag is { } etag) File.WriteAllText(tagFile, etag.ToString());
        return File.OpenRead(file);
    }

    private static System.Net.Http.Headers.EntityTagHeaderValue? EntityTagHeaderValueOrNull(string text) =>
        System.Net.Http.Headers.EntityTagHeaderValue.TryParse(text.Trim(), out var tag) ? tag : null;

    /// Downloads the scanned commit, takes the mod's folder out of it, checks it
    /// with Claude Code's own validator and adds it to the library. Answers the
    /// mod's name, or why nothing was added.
    public static async Task<(string? Name, string? Problem)> Install(Scanned mod, Func<string, Task<bool>>? goOn = null, CancellationToken cancel = default)
    {
        if (mod.Commit.Length == 0 || !RepoName().IsMatch(mod.Repo)) return (null, "The list gives no commit to install from.");
        var work = Path.Combine(Path.GetTempPath(), $"graft-mod-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(work);
            var zip = Path.Combine(work, "source.zip");
            using (var response = await Http.GetAsync($"https://codeload.github.com/{mod.Repo}/zip/{mod.Commit}", HttpCompletionOption.ResponseHeadersRead, cancel))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxDownload) return (null, "The mod's repository is too large to download.");
                await using var target = File.Create(zip);
                await using var body = await response.Content.ReadAsStreamAsync(cancel);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await body.ReadAsync(buffer, cancel)) > 0)
                {
                    if ((total += read) > MaxDownload) return (null, "The mod's repository is too large to download.");
                    await target.WriteAsync(buffer.AsMemory(0, read), cancel);
                }
            }
            var unpacked = Path.Combine(work, "source");
            ZipFile.ExtractToDirectory(zip, unpacked);
            // GitHub wraps the tree in one folder named for the repository and commit.
            var root = Directory.GetDirectories(unpacked).SingleOrDefault();
            if (root is null) return (null, "The download held no folder.");
            var folder = mod.Path.Length == 0 ? root : Path.GetFullPath(Path.Combine(root, mod.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!folder.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) || !Directory.Exists(folder))
                return (null, $"The download has no folder {mod.Path}.");
            if (Validate(folder) is string refused) return (null, refused);
            if (TerminalOnly(folder) && goOn is not null
                && !await goOn("This mod's code draws only in the terminal, so it is likely to do nothing in Claude Desktop."))
                return (null, null);
            File.WriteAllText(Path.Combine(folder, SourceFile), JsonSerializer.Serialize(new Source(mod.Repo, mod.Path, mod.Commit)));
            return ModLibrary.Add(folder);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or TaskCanceledException)
        {
            return (null, "The mod could not be downloaded: " + e.Message);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    /// A guess from the code, never a refusal: every drawing hook that names a
    /// surface turns away all but the terminal, and nothing names the desktop.
    public static bool TerminalOnly(string folder)
    {
        var sources = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".ts") || f.EndsWith(".tsx") || f.EndsWith(".js") || f.EndsWith(".mjs"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}") && !f.EndsWith(".d.ts") && !f.Contains(".test."))
            .Select(File.ReadAllText).ToList();
        if (!sources.Any(t => t.Contains("ui.render"))) return false;
        if (sources.Any(t => t.Contains("'desktop'") || t.Contains("\"desktop\""))) return false;
        return sources.Any(t => TerminalGate().IsMatch(t));
    }

    [GeneratedRegex(@"surface\s*!==?\s*['""]terminal['""]")]
    private static partial Regex TerminalGate();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepoName();

    /// Redirected by the test suite, which has no Claude Code to ask.
    public static Func<string, string?>? ValidateOverride { get; set; }

    /// Null when the validator passes the folder, with or without warnings.
    public static string? Validate(string folder)
    {
        if (ValidateOverride is { } check) return check(folder);
        if (ClaudeCodeProgram() is not { } program) return "Claude Code was not found to check the mod with.";
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("plugin");
        start.ArgumentList.Add("validate");
        start.ArgumentList.Add(folder);
        using var process = Process.Start(start);
        if (process is null) return "Claude Code could not be started to check the mod.";
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return "Checking the mod took too long.";
        }
        if (process.ExitCode == 0) return null;
        var said = (output.Result + errors.Result).Trim();
        return "Claude Code's validator refused the mod." + (said.Length > 0 ? "\n\n" + said[^Math.Min(said.Length, 800)..] : "");
    }

    /// The newest copy the desktop app keeps for the main profile, which is the
    /// version its sessions run.
    private static string? ClaudeCodeProgram()
    {
        var root = Path.Combine(GraftPaths.DefaultProfile, "claude-code");
        if (!Directory.Exists(root)) return null;
        return Directory.GetDirectories(root)
            .Select(d => (Dir: d, Version: Version.TryParse(Path.GetFileName(d), out var v) ? v : null))
            .Where(d => d.Version is not null)
            .OrderByDescending(d => d.Version)
            .SelectMany(d => Directory.GetFiles(d.Dir, "claude.exe", SearchOption.AllDirectories))
            .FirstOrDefault();
    }

    public static Source? SourceOf(string modFolder)
    {
        try
        {
            var file = Path.Combine(modFolder, SourceFile);
            return File.Exists(file) ? JsonSerializer.Deserialize<Source>(File.ReadAllText(file)) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
}
