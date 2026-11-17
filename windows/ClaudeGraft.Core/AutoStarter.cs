using System.Text.Json;

namespace ClaudeGraft.Core;

/// <summary>
/// Keeps an opted-in account's five-hour window open without a press. This is the
/// one place the app starts a session that a person did not ask for, and the rest
/// of the app leans hard the other way — the button is the only other caller, and
/// the Mac build forbids a session on a timer outright — so it is drawn as narrow
/// as it can be, and pinned as the sole automated caller by StartCallerAuditTests.
///
/// It fires for a profile only when its owner turned this on, only when a *live*
/// reading says no window is open, and never twice inside the cooldown. That last
/// guard is the one that matters most: SessionStarter has never been proven to
/// open a window, and a start that quietly opens nothing leaves the window reading
/// closed for ever — so without the cooldown a broken start would re-fire on every
/// sweep, once for every account, for as long as the app ran.
///
/// So the cooldown is written to disk before the attempt, not held in memory. This
/// app has a startup crash to its name, and an in-memory cooldown would be empty on
/// every relaunch — a crash-loop, with the sweep firing seconds after each launch,
/// is exactly the "over and over" the rule exists to stop, and only a cooldown that
/// outlives the process holds it off.
///
/// Where the button stays enabled on an uncertain reading — greying out the one
/// useful action on a guess is worse than leaving it — the background does the
/// opposite: a reading that is missing, off disk, or refused says nothing about
/// whether a window is open, and starting a session on that guess is worse than
/// waiting for a reading that can answer. Uncertainty here is silence.
/// </summary>
public static class AutoStarter
{
    /// Long enough that a start which opened nothing is not retried in a tight
    /// loop, short enough that a genuinely closed window is picked up the same
    /// hour. A start that works opens a five-hour window, so the cooldown is spent
    /// only on the failing case it exists for.
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(30);

    /// Whether a background start should go out for one profile now. Pure, so the
    /// rule can be checked without a network or a clock.
    public static bool ShouldStart(bool optedIn, UsageEntry? reading, DateTimeOffset now, DateTimeOffset cooldownUntil)
    {
        if (!optedIn) return false;
        if (now < cooldownUntil) return false;
        // Only a live reading is trusted to say a window is open — the same rule
        // the row's button gates on, and the reason disk and refusals are treated
        // as "unknown" rather than "closed".
        if (reading is not { IsLive: true, Usage: { } usage }) return false;
        // No five-hour limit, no window for a session to open.
        if (usage.FiveHour is null) return false;
        var open = (usage.FiveHourReset is DateTimeOffset reset && reset > now) || usage.FiveHour > 0;
        return !open;
    }

    private static int _sweeping;

    /// Runs one pass over the profiles their owners asked to keep warm, starting a
    /// session on each one whose window is confirmed closed. The caller hands in
    /// only the opted-in profiles, so this decides the window and the cooldown and
    /// nothing about who opted in.
    public static async Task SweepAsync(IEnumerable<string> profiles)
    {
        // A pass that runs long — a profile timing out on the network — must not
        // have the next tick start a second one over the top of it: two passes
        // could both read the same window closed and both fire before either wrote
        // the cooldown down.
        if (Interlocked.Exchange(ref _sweeping, 1) == 1) return;
        try
        {
            foreach (var profile in profiles)
            {
                try
                {
                    // A cheap pre-check off disk, so a profile still in its cooldown
                    // does not even cost a usage read.
                    if (DateTimeOffset.UtcNow < CooldownUntil(profile)) continue;

                    var reading = await UsageMonitor.ReadAsync(profile).ConfigureAwait(false);
                    if (!ShouldStart(true, reading, DateTimeOffset.UtcNow, CooldownUntil(profile))) continue;

                    ArmCooldown(profile);

                    var problem = await SessionStarter.StartAsync(profile).ConfigureAwait(false);
                    // The window this may have opened is what the stored reading
                    // predates, so the next read is made to refetch rather than
                    // trust it.
                    UsageMonitor.Invalidate(profile);
                    Diagnostics.Note("autostart.attempt", new Dictionary<string, object?>
                    {
                        ["profile"] = profile,
                        ["result"] = problem ?? "window opened",
                    });
                }
                catch (Exception e)
                {
                    Diagnostics.Note("autostart.failed", new Dictionary<string, object?>
                    {
                        ["profile"] = profile,
                        ["error"] = e.GetType().Name + ": " + e.Message,
                    });
                }
            }
        }
        finally { Interlocked.Exchange(ref _sweeping, 0); }
    }

    // MARK: - The cooldown, kept on disk

    /// Beside the rest of this app's state. A profile-keyed map of when each one's
    /// cooldown lifts — the whole guard against a crash-loop re-firing, so it has
    /// to be the filesystem and not a static.
    public static string StatePath => Path.Combine(GraftPaths.OwnData, "autostart-cooldowns.json");

    private static readonly object Lock = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    /// When this profile's cooldown lifts, read fresh from disk so a restart does
    /// not forget it; the epoch when it has never been tried.
    public static DateTimeOffset CooldownUntil(string profile)
    {
        var key = Path.GetFullPath(profile);
        lock (Lock) return Load().TryGetValue(key, out var until) ? until : default;
    }

    /// Holds this profile off for the cooldown and writes it down at once, before
    /// the attempt, so a start that opens nothing — or a crash mid-start — still
    /// counts against the next pass rather than being retried immediately.
    public static void ArmCooldown(string profile)
    {
        var key = Path.GetFullPath(profile);
        lock (Lock)
        {
            var map = Load();
            map[key] = DateTimeOffset.UtcNow + Cooldown;
            Save(map);
        }
    }

    private static Dictionary<string, DateTimeOffset> Load()
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(
                File.ReadAllBytes(StatePath), Options) ?? new();
        }
        catch { return new(); }
    }

    private static void Save(Dictionary<string, DateTimeOffset> map)
    {
        // A lapsed cooldown reads the same as one never set, so it is dropped
        // rather than left to grow the file for a profile that may be long gone.
        var now = DateTimeOffset.UtcNow;
        var live = map.Where(e => e.Value > now).ToDictionary(e => e.Key, e => e.Value);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            AtomicWrite.Bytes(StatePath, JsonSerializer.SerializeToUtf8Bytes(live, Options));
        }
        catch { }
    }
}
