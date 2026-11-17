using System.Text;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class ChatFocusTests
{
    private static readonly DateTime T0 = new(2026, 11, 11, 3, 0, 0);

    private static ChatFocus.Record R(string session, long? focused, string? title = null, bool archived = false, int writtenSecond = 0) =>
        new(session, title ?? session, focused, archived, T0.AddSeconds(writtenSecond));

    private static IReadOnlyDictionary<string, IReadOnlyList<ChatFocus.Record>> Stores(
        params (string Store, ChatFocus.Record[] Records)[] stores) =>
        stores.ToDictionary(s => s.Store, s => (IReadOnlyList<ChatFocus.Record>)s.Records);

    private static string? Look(ChatFocus focus, string store, params (string Store, ChatFocus.Record[] Records)[] stores)
    {
        var all = Stores(stores);
        focus.Look(all);
        return focus.OnScreen(store, all[store]);
    }

    [Fact(DisplayName = "the conversation on screen is the one its Claude focused last")]
    public void NewestFocus() =>
        Assert.Equal("b", Look(new ChatFocus(), "main", ("main", new[] { R("a", 100), R("b", 200), R("c", null) })));

    [Fact(DisplayName = "a focus the mirror copied into another Claude's store is not that Claude's")]
    public void MirroredFocusIsNotOwned()
    {
        var focus = new ChatFocus();
        Look(focus, "two", ("main", new[] { R("a", 100) }), ("two", new[] { R("b", 150) }));
        // main focuses a; the mirror then carries a's record into two's store.
        Assert.Equal("b", Look(focus, "two",
            ("main", new[] { R("a", 300, writtenSecond: 1) }),
            ("two", new[] { R("b", 150), R("a", 300, writtenSecond: 3) })));
    }

    [Fact(DisplayName = "on the first look every store holding a focus owns it")]
    public void FirstLookOwnsAll()
    {
        var focus = new ChatFocus();
        Assert.Equal("a", Look(focus, "two", ("main", new[] { R("a", 300) }), ("two", new[] { R("a", 300) })));
        Assert.Equal("a", focus.OnScreen("main", new[] { R("a", 300) }));
    }

    [Fact(DisplayName = "an archived conversation leaves rather than handing over to an older one")]
    public void ArchivedLeaves() =>
        Assert.Null(Look(new ChatFocus(), "main", ("main", new[] { R("a", 100), R("b", 200, archived: true) })));

    [Fact(DisplayName = "a deleted conversation stays named for one look, then nothing is")]
    public void DeletedGoes()
    {
        var focus = new ChatFocus();
        Assert.Equal("b", Look(focus, "main", ("main", new[] { R("a", 100), R("b", 200) })));
        Assert.Equal("b", Look(focus, "main", ("main", new[] { R("a", 100) })));
        Assert.Null(Look(focus, "main", ("main", new[] { R("a", 100) })));
    }

    [Fact(DisplayName = "a newer focus after a deletion is named at once")]
    public void NewerFocusAfterDelete()
    {
        var focus = new ChatFocus();
        Look(focus, "main", ("main", new[] { R("a", 100), R("b", 200) }));
        Assert.Equal("a", Look(focus, "main", ("main", new[] { R("a", 300) })));
    }

    [Fact(DisplayName = "a record's focus, title and archive state are read from Claude's own file")]
    public void Parses()
    {
        var r = ChatFocus.Parse(Encoding.UTF8.GetBytes("""{"sessionId":"local_1","title":"Fix it","lastFocusedAt":1794382461388,"isArchived":false}"""), T0);
        Assert.Equal(("local_1", "Fix it", 1794382461388L, false), (r!.Session, r.Title, r.FocusedAt!.Value, r.Archived));
        Assert.Null(ChatFocus.Parse(Encoding.UTF8.GetBytes("{ broken"), T0));
    }

    private static ChatFocus.Turn TurnOf(string json) =>
        ChatFocus.TurnOf(ChatFocus.Parse(Encoding.UTF8.GetBytes("{\"sessionId\":\"local_1\"," + json + "}"), T0)!);

    [Fact(DisplayName = "a prompt newer than the last turn's end is still being answered")]
    public void Working() =>
        Assert.Equal(ChatFocus.Turn.Working, TurnOf("\"lastTurnEdgeAt\":100,\"latestUserFrameAt\":200"));

    [Fact(DisplayName = "a turn that ended after the prompt is done")]
    public void Done() =>
        Assert.Equal(ChatFocus.Turn.Done, TurnOf("\"lastTurnEdgeAt\":200,\"latestUserFrameAt\":100,\"turnWrapUp\":{\"description\":\"x\"}"));

    [Fact(DisplayName = "a turn that ended on a question waits on the person")]
    public void Asks() =>
        Assert.Equal(ChatFocus.Turn.NeedsPerson, TurnOf("\"lastTurnEdgeAt\":200,\"latestUserFrameAt\":100,\"turnWrapUp\":{\"description\":\"x\",\"needsUser\":\"Which one?\"}"));

    [Fact(DisplayName = "a turn that broke after the prompt waits on the person")]
    public void Broke() =>
        Assert.Equal(ChatFocus.Turn.NeedsPerson, TurnOf("\"lastTurnEdgeAt\":200,\"latestUserFrameAt\":100,\"errorAt\":150"));

    [Fact(DisplayName = "a conversation never answered has no turn to report")]
    public void Untouched() => Assert.Equal(ChatFocus.Turn.None, TurnOf("\"title\":\"x\""));
}
