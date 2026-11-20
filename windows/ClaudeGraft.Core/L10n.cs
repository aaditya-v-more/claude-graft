using System.Globalization;
using System.Text.Json;

namespace ClaudeGraft.Core;

/// <summary>
/// The words on screen, in English or Russian. As on the Mac, the English text
/// is the key: a string missing from the Russian catalog shows in English
/// rather than as a name nobody can read. The catalog is `Localization/ru.json`,
/// built into this assembly so the app and the suite read the same one.
/// </summary>
public static class L10n
{
    public const string English = "en";
    public const string Russian = "ru";

    /// The languages there is a catalog for, besides English.
    public static IReadOnlyList<string> Translated { get; } = [Russian];

    private static readonly Lazy<IReadOnlyDictionary<string, string>> RussianCatalog = new(() => Load(Russian));

    /// Every Russian entry, keyed by its English.
    public static IReadOnlyDictionary<string, string> RussianEntries => RussianCatalog.Value;

    // Process-wide once Choose has run; a scope from Speaking wins inside it, so
    // tests in parallel can each speak their own language.
    private static string? _chosen;
    private static readonly AsyncLocal<string?> Scoped = new();

    public static string Language => Scoped.Value ?? _chosen ?? LanguageFor(CultureInfo.CurrentUICulture);

    /// Russian for any Russian UI culture, English for everything else.
    public static string LanguageFor(CultureInfo culture) =>
        Translated.Contains(culture.TwoLetterISOLanguageName) ? culture.TwoLetterISOLanguageName : English;

    /// Fixes the language for the process: <paramref name="requested"/> when it
    /// names one there is a catalog for, Windows' display language otherwise.
    public static void Choose(string? requested)
    {
        var wanted = requested?.Trim().ToLowerInvariant();
        _chosen = wanted == English || (wanted is not null && Translated.Contains(wanted)) ? wanted : null;
    }

    /// For the suite: speaks <paramref name="language"/> until disposed, on this flow alone.
    public static IDisposable Speaking(string language)
    {
        var was = Scoped.Value;
        Scoped.Value = language;
        return new Restore(() => Scoped.Value = was);
    }

    public static string Text(string english) =>
        Language == Russian && RussianEntries.TryGetValue(english, out var russian) ? russian : english;

    public static string Format(string english, params object?[] arguments) => Apply(english, Text(english), arguments);

    /// A count in words: English picks <paramref name="one"/> or <paramref name="other"/>;
    /// Russian keys its three forms, one|few|many, by <paramref name="other"/>.
    /// The count is {0}, and any further arguments follow it.
    public static string Plural(long count, string one, string other, params object?[] arguments)
    {
        object?[] all = [count, .. arguments];
        var english = count == 1 ? one : other;
        if (Language != Russian || !RussianEntries.TryGetValue(other, out var forms)) return Apply(english, english, all);
        var split = forms.Split('|');
        return split.Length == 3 ? Apply(english, split[(int)RussianForm(count)], all) : Apply(english, english, all);
    }

    public enum Form { One, Few, Many }

    public static Form RussianForm(long count)
    {
        var n = Math.Abs(count);
        if (n % 10 == 1 && n % 100 != 11) return Form.One;
        if (n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14) return Form.Few;
        return Form.Many;
    }

    /// "A", "A and B", "A, B and C", in the language on screen.
    public static string List(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => L10n.Format("{0} and {1}", string.Join(", ", items.Take(items.Count - 1)), items[^1]),
    };

    // A translation whose placeholders were broken by hand would throw; English is better than a crash.
    private static string Apply(string english, string text, object?[] arguments)
    {
        try { return string.Format(CultureInfo.CurrentCulture, text, arguments); }
        catch (FormatException) { return string.Format(CultureInfo.CurrentCulture, english, arguments); }
    }

    private static IReadOnlyDictionary<string, string> Load(string language)
    {
        using var stream = typeof(L10n).Assembly.GetManifestResourceStream($"ClaudeGraft.Core.Localization.{language}.json");
        if (stream is null) return new Dictionary<string, string>();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream, CatalogOptions) ?? new Dictionary<string, string>();
    }

    public static readonly JsonSerializerOptions CatalogOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
