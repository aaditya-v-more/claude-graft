using System.Text.Json;

namespace ClaudeGraft.Core;

/// A state file that is there but will not read. Never the same as one that is
/// absent: read as empty, a mirror baseline makes the next pass a first pass and
/// a session state forgets every withdrawal.
public sealed class UnreadableStateException : IOException
{
    public string Path { get; }
    public UnreadableStateException(string path, Exception? inner)
        : base($"{System.IO.Path.GetFileName(path)} could not be read", inner) => Path = path;
}

/// Another pass held the state lock past <see cref="StateLock.Patience"/>, so
/// this one did nothing.
public sealed class StateBusyException : IOException
{
    public StateBusyException() : base("Graft is still finishing another change. Try again in a moment.") { }
}

public static class StateFile
{
    /// The file decoded, a fresh value when there is no file at all, and
    /// <see cref="UnreadableStateException"/> for anything else.
    public static T Read<T>(string path, JsonSerializerOptions options) where T : class, new()
    {
        if (!File.Exists(path)) return new T();
        byte[] data;
        try { data = ReadShared(path); }
        catch (FileNotFoundException) { return new T(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new UnreadableStateException(path, e);
        }
        try
        {
            return JsonSerializer.Deserialize<T>(data, options) ?? throw new JsonException("null document");
        }
        catch (JsonException e) { throw new UnreadableStateException(path, e); }
    }

    // A writer replaces these files by rename; a reader landing on that moment
    // gets a sharing violation, which a few short waits see through.
    private static byte[] ReadShared(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return File.ReadAllBytes(path); }
            catch (IOException e) when (attempt < 5 && e is not FileNotFoundException and not DirectoryNotFoundException)
            {
                Thread.Sleep(40);
            }
        }
    }
}
