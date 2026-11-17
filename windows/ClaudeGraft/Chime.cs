using System.Runtime.InteropServices;
using ClaudeGraft.Core;
using Microsoft.UI.Dispatching;

namespace ClaudeGraft;

/// <summary>
/// Takes what Claude Code's hook reported through the launcher: a permission
/// waiting marks the conversation as needing the person until its transcript
/// moves on, and a turn's end or a waiting permission chimes when that is wanted
/// and its Claude is not the app in front.
/// </summary>
internal static class Chime
{
    private static DispatcherQueue? _ui;
    private static FileSystemWatcher? _watch;
    private static Timer? _poll;
    private static readonly object Gate = new();
    // Sessions waiting on a permission: their transcript and its size when it was asked.
    private static readonly Dictionary<string, (string File, long Length)> Waiting = new();

    // Pinned for good: an asynchronous sound reads its bytes after the call returns.
    private static readonly nint DoneSound = GCHandle.Alloc(ChimeSound.Done, GCHandleType.Pinned).AddrOfPinnedObject();
    private static readonly nint AskSound = GCHandle.Alloc(ChimeSound.Ask, GCHandleType.Pinned).AddrOfPinnedObject();

    public static void Start(DispatcherQueue ui)
    {
        _ui = ui;
        var folder = ChimeHook.SignalsDirectory;
        try
        {
            Directory.CreateDirectory(folder);
            // Left from while Graft was not running, they would chime for turns long over.
            foreach (var stale in Directory.EnumerateFiles(folder))
                try { File.Delete(stale); } catch (IOException) { }
            _watch = new FileSystemWatcher(folder, "*.json");
            _watch.Created += (_, e) => Take(e.FullPath);
            _watch.Renamed += (_, e) => Take(e.FullPath);
            _watch.EnableRaisingEvents = true;
        }
        catch (Exception e) { Diagnostics.Note("chime.watch-failed", new Dictionary<string, object?> { ["error"] = e.Message }); }
        _poll = new Timer(_ => Poll(), null, 1000, 1000);
    }

    private static void Take(string file)
    {
        var signal = HookSignal.Read(file);
        try { File.Delete(file); } catch (IOException) { }
        if (signal is not null) _ui?.TryEnqueue(() => Handle(signal));
    }

    private static void Handle(HookSignal signal)
    {
        if (signal.What == HookSignal.Kind.AwaitingPermission)
        {
            ChatOnScreen.SetAsking(signal.Session, signal.At);
            if (signal.Transcript is string t)
                lock (Gate) Waiting[signal.Session] = (t, Length(t));
            Play(AskSound, signal.Session);
            return;
        }
        ChatOnScreen.SetAsking(signal.Session, null);
        lock (Gate) Waiting.Remove(signal.Session);
        // Claude writes whether the turn ended on a question a moment after the hook runs.
        _ = Task.Delay(1500).ContinueWith(_ => _ui?.TryEnqueue(() =>
        {
            var (_, turn) = ChatOnScreen.SessionState(signal.Session);
            Play(turn == ChatFocus.Turn.NeedsPerson ? AskSound : DoneSound, signal.Session);
        }));
    }

    // Answered either way, a permission puts the tool's result in the transcript.
    private static void Poll()
    {
        List<string> answered;
        lock (Gate)
            answered = Waiting.Where(w => Length(w.Value.File) is var now && now > w.Value.Length && LastLineIsToolResult(w.Value.File))
                .Select(w => w.Key).ToList();
        foreach (var session in answered)
        {
            lock (Gate) Waiting.Remove(session);
            ChatOnScreen.SetAsking(session, null);
        }
    }

    private static long Length(string file)
    {
        try { return new FileInfo(file).Length; }
        catch (IOException) { return 0; }
    }

    private static bool LastLineIsToolResult(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var tail = (int)Math.Min(stream.Length, 256 * 1024);
            stream.Seek(-tail, SeekOrigin.End);
            var buffer = new byte[tail];
            stream.ReadExactly(buffer);
            var lines = System.Text.Encoding.UTF8.GetString(buffer).TrimEnd().Split('\n');
            return lines[^1].Contains("\"tool_result\"", StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
    }

    private static void Play(nint sound, string session)
    {
        if (!App.Settings.Chime) return;
        var (profile, _) = ChatOnScreen.SessionState(session);
        // Its Claude in front, the person is already looking. A session no record names
        // yet is most likely the Claude in front's newest.
        if (FrontClaude.Profile is string front && (profile is null || Fs.SamePath(front, profile))) return;
        PlaySound(sound, 0, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
    }

    private const uint SND_ASYNC = 0x1, SND_NODEFAULT = 0x2, SND_MEMORY = 0x4;
    [DllImport("winmm.dll")] private static extern bool PlaySound(nint sound, nint module, uint flags);
}
