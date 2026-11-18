using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class LocalizationTests
{
    [Fact(DisplayName = "every English string the app puts on screen has a Russian entry")]
    public void EveryKeyIsTranslated()
    {
        var missing = Keys().Keys.Where(k => !L10n.RussianEntries.ContainsKey(k)).Order().ToList();
        Assert.True(missing.Count == 0, "No Russian for:\n" + string.Join("\n", missing));
    }

    [Fact(DisplayName = "the Russian catalog holds nothing the app no longer shows")]
    public void NoStaleEntries()
    {
        var keys = Keys();
        var stale = L10n.RussianEntries.Keys.Where(k => !keys.ContainsKey(k)).Order().ToList();
        Assert.True(stale.Count == 0, "Not on screen any more:\n" + string.Join("\n", stale));
    }

    [Fact(DisplayName = "every translation keeps the placeholders of its English, in each of a count's three forms")]
    public void PlaceholdersSurvive()
    {
        var keys = Keys();
        var broken = new List<string>();
        foreach (var (english, russian) in L10n.RussianEntries)
        {
            var plural = keys.TryGetValue(english, out var kind) && kind == Kind.Plural;
            var forms = plural ? russian.Split('|') : [russian];
            if (plural && forms.Length != 3) broken.Add($"{english} has {forms.Length} forms, not one|few|many");
            var wanted = Placeholders(english);
            foreach (var form in forms)
            {
                if (!Placeholders(form).SetEquals(wanted)) broken.Add($"{english} → {form}");
                try { _ = string.Format(CultureInfo.InvariantCulture, form, new object[10]); }
                catch (FormatException) { broken.Add($"{form} does not format"); }
            }
        }
        Assert.True(broken.Count == 0, string.Join("\n", broken));
    }

    [Fact(DisplayName = "the catalog names no English twice, since only one of the two would ever show")]
    public void NoDuplicateKeys()
    {
        using var stream = typeof(L10n).Assembly.GetManifestResourceStream("ClaudeGraft.Core.Localization.ru.json");
        Assert.NotNull(stream);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var reader = new Utf8JsonReader(memory.ToArray(), new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        var seen = new HashSet<string>();
        var twice = new List<string>();
        while (reader.Read())
            if (reader.TokenType == JsonTokenType.PropertyName && !seen.Add(reader.GetString()!)) twice.Add(reader.GetString()!);
        Assert.Empty(twice);
        Assert.Equal(seen.Count, L10n.RussianEntries.Count);
    }

    [Fact(DisplayName = "a Russian display language picks Russian and one there is no catalog for falls back to English")]
    public void CultureSelectsLanguage()
    {
        Assert.Equal(L10n.Russian, L10n.LanguageFor(new CultureInfo("ru-RU")));
        Assert.Equal(L10n.Russian, L10n.LanguageFor(new CultureInfo("ru")));
        Assert.Equal(L10n.English, L10n.LanguageFor(new CultureInfo("en-GB")));
        Assert.Equal(L10n.English, L10n.LanguageFor(new CultureInfo("de-DE")));
        Assert.Equal(L10n.English, L10n.LanguageFor(new CultureInfo("uk-UA")));
        Assert.Equal(L10n.English, L10n.LanguageFor(CultureInfo.InvariantCulture));
    }

    [Fact(DisplayName = "speaking Russian shows the catalog's words, and a string it lacks stays English")]
    public void RussianShowsAndFallsBack()
    {
        Assert.Equal("Sign Out", L10n.Text("Sign Out"));
        using (L10n.Speaking(L10n.Russian))
        {
            Assert.Equal("Выйти", L10n.Text("Sign Out"));
            Assert.Equal("Удалить «Отчёт»?", L10n.Format("Delete “{0}”?", "Отчёт"));
            Assert.Equal("Not a string anywhere in the app", L10n.Text("Not a string anywhere in the app"));
            Assert.Equal("Работа, Дом и Учёба", L10n.List(["Работа", "Дом", "Учёба"]));
        }
        Assert.Equal("Sign Out", L10n.Text("Sign Out"));
        Assert.Equal("A, B and C", L10n.List(["A", "B", "C"]));
    }

    [Theory(DisplayName = "Russian picks one, few or many by the count's last digits")]
    [InlineData(0, L10n.Form.Many)]
    [InlineData(1, L10n.Form.One)]
    [InlineData(2, L10n.Form.Few)]
    [InlineData(4, L10n.Form.Few)]
    [InlineData(5, L10n.Form.Many)]
    [InlineData(11, L10n.Form.Many)]
    [InlineData(12, L10n.Form.Many)]
    [InlineData(14, L10n.Form.Many)]
    [InlineData(21, L10n.Form.One)]
    [InlineData(22, L10n.Form.Few)]
    [InlineData(25, L10n.Form.Many)]
    [InlineData(111, L10n.Form.Many)]
    [InlineData(1001, L10n.Form.One)]
    public void RussianForms(int count, L10n.Form form) => Assert.Equal(form, L10n.RussianForm(count));

    [Fact(DisplayName = "a count reads as a sentence in either language, never a word glued to a number")]
    public void PluralsReadRight()
    {
        Assert.Equal("1 project", L10n.Plural(1, "{0} project", "{0} projects"));
        Assert.Equal("3 projects", L10n.Plural(3, "{0} project", "{0} projects"));
        using var russian = L10n.Speaking(L10n.Russian);
        Assert.Equal("1 проект", L10n.Plural(1, "{0} project", "{0} projects"));
        Assert.Equal("3 проекта", L10n.Plural(3, "{0} project", "{0} projects"));
        Assert.Equal("11 проектов", L10n.Plural(11, "{0} project", "{0} projects"));
        Assert.Equal("Восстановлены 2 сессии, закрытые без записей",
            L10n.Plural(2, "Recovered {0} session that closed without a record", "Recovered {0} sessions that closed without records"));
    }

    [Fact(DisplayName = "no XAML puts English on screen that skips the catalog")]
    public void XamlHasNoBareText()
    {
        var bare = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(WindowsRoot(), "ClaudeGraft"), "*.xaml", SearchOption.AllDirectories))
        {
            if (Ignored(file)) continue;
            foreach (var value in Bare(File.ReadAllText(file)))
                bare.Add($"{Path.GetFileName(file)}: {value}");
        }
        Assert.True(bare.Count == 0, "Use local:Localized for:\n" + string.Join("\n", bare));
    }

    [Fact(DisplayName = "the XAML audit catches a bare word and lets glyphs, bindings and product names by")]
    public void AuditCatches()
    {
        Assert.Equal(new[] { "Save", "Pick one", "Close it", "Cancel" }, Bare("""
            <Button Content="Save" /><TextBox PlaceholderText="Pick one" ToolTipService.ToolTip="Close it" />
            <TextBlock Text="Cancel" />
            """));
        Assert.Empty(Bare("""
            <TextBlock Text="{x:Bind Name}" /><FontIcon Glyph="&#xE713;" /><TextBlock Text="5h" Header="·" />
            <TextBlock Text="Claude" /><TextBlock local:Localized.Text="Settings" /><TextBlock Text="&#xE10C;" />
            """));
    }

    private static readonly string[] ProductNames = ["Claude", "Claude Graft", "Claude Code", "aadityavmore@gmail.com"];

    private static readonly Regex BareAttribute = new(
        @"\s(?:Text|Content|Header|Description|PlaceholderText|ToolTipService\.ToolTip|AutomationProperties\.Name|Title|OnContent|OffContent)=""([^""]*)""");

    private static List<string> Bare(string xaml) => BareAttribute.Matches(xaml)
        .Select(m => WebUtility.HtmlDecode(m.Groups[1].Value))
        .Where(v => !v.StartsWith('{') && Regex.IsMatch(v, @"\p{L}{2,}") && !ProductNames.Contains(v))
        .ToList();

    private enum Kind { Text, Plural }

    private static readonly string Literal = @"""((?:[^""\\]|\\.)*)""";

    /// Every English the app hands the catalog: the C# calls with a literal, the
    /// plural English a count's Russian sits under, and the XAML attached properties.
    private static Dictionary<string, Kind> Keys()
    {
        var keys = new Dictionary<string, Kind>();
        var text = new Regex(@"L10n\.(?:Text|Format)\(\s*" + Literal);
        var plural = new Regex(@"L10n\.Plural\([^,""]+,\s*" + Literal + @"\s*,\s*" + Literal);
        var xaml = new Regex(@"local:Localized\.\w+=""([^""]*)""");
        foreach (var project in new[] { "ClaudeGraft", "ClaudeGraft.Core" })
        foreach (var file in Directory.GetFiles(Path.Combine(WindowsRoot(), project), "*", SearchOption.AllDirectories))
        {
            if (Ignored(file)) continue;
            var source = file.EndsWith(".cs") || file.EndsWith(".xaml") ? File.ReadAllText(file) : null;
            if (source is null) continue;
            if (file.EndsWith(".cs"))
            {
                foreach (Match m in text.Matches(source)) keys.TryAdd(Unescape(m.Groups[1].Value), Kind.Text);
                foreach (Match m in plural.Matches(source)) keys[Unescape(m.Groups[2].Value)] = Kind.Plural;
            }
            else
                foreach (Match m in xaml.Matches(source)) keys.TryAdd(WebUtility.HtmlDecode(m.Groups[1].Value), Kind.Text);
        }
        Assert.True(keys.Count > 400, $"only {keys.Count} strings found; the scan has lost its way");
        return keys;
    }

    private static bool Ignored(string file)
    {
        var parts = file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Contains("obj") || parts.Contains("bin");
    }

    private static string Unescape(string literal)
    {
        var text = new StringBuilder();
        for (var i = 0; i < literal.Length; i++)
        {
            if (literal[i] != '\\' || i + 1 == literal.Length) { text.Append(literal[i]); continue; }
            var next = literal[++i];
            switch (next)
            {
                case 'n': text.Append('\n'); break;
                case 't': text.Append('\t'); break;
                case 'r': text.Append('\r'); break;
                case '0': text.Append('\0'); break;
                case 'u':
                    text.Append((char)Convert.ToInt32(literal.Substring(i + 1, 4), 16));
                    i += 4;
                    break;
                default: text.Append(next); break;
            }
        }
        return text.ToString();
    }

    private static HashSet<string> Placeholders(string text) =>
        Regex.Matches(text, @"(?<!\{)\{(\d+)[^{}]*\}").Select(m => m.Groups[1].Value).ToHashSet();

    private static string WindowsRoot([CallerFilePath] string here = "") =>
        Directory.GetParent(here)!.Parent!.FullName;
}
