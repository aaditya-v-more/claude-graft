using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ClaudeGraft.Core;

/// <summary>
/// A plan's live limits from Anthropic's own usage endpoint, the one Claude Code
/// uses — better than the figures a profile writes to disk in every way that
/// matters: exact reset times, and current values even for a profile that is not
/// running.
/// </summary>
public static class UsageApi
{
    public const string Endpoint = "https://api.anthropic.com/api/oauth/usage";

    public sealed record Reading
    {
        /// Null when the account has no five-hour limit.
        public required int? FiveHour { get; init; }
        public required int Week { get; init; }
        public DateTimeOffset? FiveHourReset { get; init; }
        public DateTimeOffset? WeekReset { get; init; }
        public string? Plan { get; init; }
        public int? Fable { get; init; }
        public DateTimeOffset? FableReset { get; init; }
    }

    public sealed class Failure : Exception
    {
        public int StatusCode { get; }
        public TimeSpan? RetryAfter { get; }
        public Failure(int status, TimeSpan? retryAfter) : base($"usage endpoint answered {status}")
        {
            StatusCode = status;
            RetryAfter = retryAfter;
        }
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task<Reading> FetchAsync(string token, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        if ((int)response.StatusCode != 200)
        {
            TimeSpan? retry = response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date is DateTimeOffset d ? d - DateTimeOffset.UtcNow : null);
            throw new Failure((int)response.StatusCode, retry);
        }

        var body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        return ReadingFrom(doc.RootElement)
            ?? throw new Failure(0, null);
    }

    /// Separated from the request so the shape of the answer can be tested.
    public static Reading? ReadingFrom(JsonElement body)
    {
        // An account with no five-hour limit is sent five_hour null or no key at
        // all; a limited one gets the window even idle, at zero. Anything else
        // there is unreadable, and taking it for no limit would hide a real one.
        var sent = body.TryGetProperty("five_hour", out var raw) && raw.ValueKind != JsonValueKind.Null;
        var session = Window(body, "five_hour");
        if (sent && session is null) return null;
        var weekly = Window(body, "seven_day");
        if (session is null && weekly is null) return null;
        var fable = FableWindow(body);
        return new Reading
        {
            FiveHour = session?.used,
            Week = weekly?.used ?? 0,
            FiveHourReset = session?.resets,
            WeekReset = weekly?.resets,
            Plan = body.TryGetProperty("subscription_type", out var s) && s.ValueKind == JsonValueKind.String
                ? Capitalize(s.GetString()) : null,
            Fable = fable?.used,
            FableReset = fable?.resets,
        };
    }

    private static (int used, DateTimeOffset? resets)? Window(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object) return null;
        if (Percentage(window, "utilization") is not int used) return null;
        DateTimeOffset? resets = window.TryGetProperty("resets_at", out var r) ? ParseDate(r) : null;
        return (used, resets);
    }

    // Only some accounts carry a Fable limit, and it sits among the per-model
    // weekly windows rather than beside five_hour and seven_day.
    private static (int used, DateTimeOffset? resets)? FableWindow(JsonElement body)
    {
        if (!body.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Array) return null;
        foreach (var limit in limits.EnumerateArray())
        {
            if (limit.ValueKind != JsonValueKind.Object
                || !limit.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String
                || kind.GetString() != "weekly_scoped"
                || !limit.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.Object
                || !scope.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.Object
                || !model.TryGetProperty("display_name", out var name) || name.ValueKind != JsonValueKind.String
                || name.GetString()?.Contains("fable", StringComparison.OrdinalIgnoreCase) != true)
                continue;
            if ((Percentage(limit, "percent") ?? Percentage(limit, "utilization")) is not int used) return null;
            return (used, limit.TryGetProperty("resets_at", out var r) ? ParseDate(r) : null);
        }
        return null;
    }

    private static int? Percentage(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? (int)Math.Round(value.GetDouble()) : null;

    private static DateTimeOffset? ParseDate(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
            return date;
        if (value.ValueKind == JsonValueKind.Number)
            return DateTimeOffset.FromUnixTimeSeconds((long)value.GetDouble());
        return null;
    }

    private static string? Capitalize(string? s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
