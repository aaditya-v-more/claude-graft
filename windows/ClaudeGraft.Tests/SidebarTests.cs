using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

[Collection("GlobalState")]
public sealed class SidebarTests
{
    [Fact(DisplayName = "an unpin survives the source's old copy and unrelated pins retain their positions")]
    public void PinAgreement()
    {
        var shared = new HashSet<string> { "local_a", "local_b" };
        var original = new SidebarSync.Snapshot(["local_a", "local_b"], ["local_b", "local_a"], "recency");
        var edited = original with { Pins = ["local_b"], Order = ["local_b"], Sort = "alpha" };
        var merged = SidebarSync.Merge(edited, original, original);
        Assert.True(merged.SameChoices(edited));
        var withPrivateChat = original with { Pins = ["local_a", "local_b", "private"], Order = ["private", "local_a", "local_b"] };
        Assert.Equal(new[] { "private", "local_b" }, withPrivateChat.Replacing(shared, merged).Order);
    }

    private sealed class NativeSidebarFact : FactAttribute
    {
        public NativeSidebarFact()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GRAFT_SIDEBAR_TEST_RUNTIME")))
                Skip = "Run windows/test-sidebar.ps1 with the published runtime.";
        }
    }

    [NativeSidebarFact(DisplayName = "disposable Chromium databases sync missing pin records, unpins, ordering and unrelated records across restarts")]
    public void WindowsBrowserRoundTrip()
    {
        var runtime = Environment.GetEnvironmentVariable("GRAFT_SIDEBAR_TEST_RUNTIME")!;
        var seeder = Environment.GetEnvironmentVariable("GRAFT_SIDEBAR_TEST_SEED")!;
        using var temp = new TempDir();
        var oldProcesses = ClaudeProcesses.Enumerate;
        try
        {
            GraftPaths.ProfilesRootOverride = temp.Path;
            ClaudeProcesses.Enumerate = () => [];
            var a = temp.Dir("A");
            var b = temp.Dir("B");
            var c = temp.Dir("C");
            var ids = new[] { "local_a", "local_b", "local_c" };
            foreach (var (profile, account, pins, time) in new[] {
                (a, "account-a", new[] { "local_c", "local_a" }, 10),
                (b, "account-b", new[] { "local_b" }, 20), (c, "account-c", Array.Empty<string>(), 30) })
            {
                File.WriteAllText(Path.Combine(profile, "config.json"), JsonSerializer.Serialize(new { lastKnownAccountUuid = account }));
                File.WriteAllText(Path.Combine(profile, "claude_desktop_config.json"),
                    """{"preferences":{"permissionChoice":"keep"},"mcpServers":{"own":{"command":"keep"}}}""");
                var store = Directory.CreateDirectory(Path.Combine(profile, "claude-code-sessions", account, "org")).FullName;
                foreach (var id in ids) File.WriteAllText(Path.Combine(store, id + ".json"),
                    JsonSerializer.Serialize(new { sessionId = id, title = "Unchanged", isArchived = false }));
                Seed(profile, account, pins, time, noPinRecord: profile == c);
            }
            var key = Graft.PairKey(Path.Combine(a, "claude-code-sessions/account-a/org"), Path.Combine(b, "claude-code-sessions/account-b/org"));
            Graft.SaveMirrorState(new MirrorState { Pairs = { [key] = new() } });
            SidebarSync.BeforeOpening(a, () => runtime);
            var result = SidebarBrowser.ReadOrWrite(runtime, [a, b]);
            Assert.Equal(new[] { "local_b", "local_c", "local_a" }, result[a].Order);
            Assert.True(result[a].SameChoices(result[b]));

            Edit(a, result[a], ["local_c", "local_b"]);
            SidebarSync.BeforeOpening(b, () => runtime);
            result = SidebarBrowser.ReadOrWrite(runtime, [a, b]);
            Assert.Equal(new[] { "local_c", "local_b" }, result[b].Order);
            Assert.Equal("alpha", result[b].Sort);
            Assert.True(result[a].SameChoices(result[b]));

            Edit(b, result[b], []);
            SidebarSync.BeforeOpening(a, () => runtime);
            result = SidebarBrowser.ReadOrWrite(runtime, [a, b]);
            Assert.All(result.Values, snapshot => { Assert.Empty(snapshot.Pins); Assert.Empty(snapshot.Order); });

            var unpinned = SidebarBrowser.ReadOrWrite(runtime, [c])[c];
            Assert.Empty(unpinned.Pins);
            Assert.Empty(unpinned.Order);
            var settingsPath = Path.Combine(c, "claude_desktop_config.json");
            var settingsBefore = File.ReadAllBytes(settingsPath);
            // Let the helper read its original snapshot, then refuse the file
            // replacement after Chromium commits the pin. Rollback must delete
            // the absent key; storing null makes the next read unsupported.
            using (var held = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.Throws<IOException>(() => Edit(c, unpinned, ["local_a"]));
            var restored = SidebarBrowser.ReadOrWrite(runtime, [c])[c];
            Assert.Empty(restored.Pins);
            Assert.Equal(unpinned.Fingerprint, restored.Fingerprint);
            Assert.Equal(settingsBefore, File.ReadAllBytes(settingsPath));
            Edit(c, restored, ["local_a"]);
            Assert.Equal(new[] { "local_a" }, SidebarBrowser.ReadOrWrite(runtime, [c])[c].Pins);
            var freshPair = Graft.PairKey(Path.Combine(a, "claude-code-sessions/account-a/org"),
                Path.Combine(c, "claude-code-sessions/account-c/org"));
            Graft.SaveMirrorState(new MirrorState { Pairs = { [key] = new(), [freshPair] = new() } });
            SidebarSync.BeforeOpening(a, () => runtime);
            result = SidebarBrowser.ReadOrWrite(runtime, [a, b, c]);
            Assert.Equal("Pinned chats and Code sort order are synced.", SidebarSync.StatusFor(c));
            Assert.All(result.Values, snapshot => {
                Assert.Equal(new[] { "local_a" }, snapshot.Pins);
                Assert.Equal(new[] { "local_a" }, snapshot.Order);
                Assert.Equal("alpha", snapshot.Sort);
            });
            foreach (var (profile, account) in new[] { (a, "account-a"), (b, "account-b") })
            {
                var prefs = JsonNode.Parse(File.ReadAllText(Path.Combine(profile, "claude_desktop_config.json")))!;
                Assert.Equal("keep", (string?)prefs["preferences"]!["permissionChoice"]);
                Assert.Equal("keep", (string?)prefs["mcpServers"]!["own"]!["command"]);
                Seed(profile, account, [], 0, verify: true);
                foreach (var id in ids)
                {
                    var record = JsonNode.Parse(File.ReadAllText(Path.Combine(profile,
                        "claude-code-sessions", account, "org", id + ".json")))!;
                    Assert.Equal(id, (string?)record["sessionId"]);
                    Assert.Equal("Unchanged", (string?)record["title"]);
                    Assert.False((bool)record["isArchived"]!);
                    Assert.Equal(id == "local_a", (bool)record["isStarred"]!);
                }
            }

            void Edit(string path, SidebarSync.Snapshot snapshot, string[] order) =>
                SidebarBrowser.ReadOrWrite(runtime, [path], new() {
                    [path] = new { expected = snapshot.Fingerprint, shared = ids, pins = order, order, sort = "alpha" }
                });

            void Seed(string profile, string account, string[] pins, int time, bool verify = false, bool noPinRecord = false)
            {
                var scratch = temp.Dir(Guid.NewGuid().ToString("N"));
                var input = Path.Combine(scratch, "request.json");
                var output = Path.Combine(scratch, "result.json");
                File.WriteAllText(input, JsonSerializer.Serialize(new { profile, pins, sort = "recency", time,
                    scope = account + "/org", scratch, output, verify, noPinRecord }));
                var start = new ProcessStartInfo(Path.Combine(runtime, "electron.exe")) {
                    WorkingDirectory = scratch, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
                };
                start.Environment.Remove("ELECTRON_RUN_AS_NODE");
                start.Environment.Remove("NODE_OPTIONS");
                start.ArgumentList.Add("--user-data-dir=" + scratch);
                start.ArgumentList.Add(seeder);
                start.ArgumentList.Add(input);
                using var process = Process.Start(start)!;
                if (!process.WaitForExit(25_000))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                    throw new TimeoutException("Fixture browser timed out.");
                }
                Assert.Equal(0, process.ExitCode);
                Assert.Equal("true", File.ReadAllText(output));
            }
        }
        finally { GraftPaths.ProfilesRootOverride = null; ClaudeProcesses.Enumerate = oldProcesses; }
    }
}
