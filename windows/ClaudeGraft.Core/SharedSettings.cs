using System.Text.Json;

namespace ClaudeGraft.Core;

public static class SharedSettings
{
    private sealed record State(string Source, string Digest, bool HadOwn);
    private static string StatePath(string path) => Graft.StashPath(path) + ".share.json";

    // Junctions cannot represent files, and file symlinks need Developer Mode.
    // Copies keep atomic replacements working; the baseline carries edits back
    // to the source on the next launch while the original stays in its stash.
    public static bool Share(string source, string destination)
    {
        using var stateLock = StateLock.Acquire();
        if (Junction.IsLink(source) || Junction.IsLink(destination))
            throw new IOException("Linked settings files must be restored before sharing them.");
        var sourceBytes = File.ReadAllBytes(source);
        var stateFile = StatePath(destination);
        State? state = File.Exists(stateFile)
            ? JsonSerializer.Deserialize<State>(File.ReadAllBytes(stateFile)) ?? throw new IOException("Unreadable settings baseline.")
            : null;
        if (state is not null && !Fs.SamePath(state.Source, source))
        {
            Restore(destination);
            if (File.Exists(stateFile)) throw new IOException("The previous settings source is unavailable.");
            state = null;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (state is null)
        {
            var hadOwn = File.Exists(destination);
            var stash = Graft.StashPath(destination);
            if (Fs.Exists(stash)) throw new IOException("An earlier settings stash needs to be restored first.");
            if (hadOwn) File.Copy(destination, stash);
            state = new(source, Graft.Digest(sourceBytes), hadOwn);
            AtomicWrite.Bytes(stateFile, JsonSerializer.SerializeToUtf8Bytes(state));
            AtomicWrite.Bytes(destination, sourceBytes);
            return true;
        }
        var own = File.Exists(destination) ? File.ReadAllBytes(destination) : null;
        var sourceDigest = Graft.Digest(sourceBytes);
        var ownDigest = own is null ? null : Graft.Digest(own);
        if (own is not null && ownDigest != state.Digest && ownDigest != sourceDigest)
        {
            if (sourceDigest != state.Digest)
            {
                // Keep both versions when both sides changed; selecting the
                // newer one must not make the other edit unrecoverable.
                var sourceWins = File.GetLastWriteTimeUtc(source) >= File.GetLastWriteTimeUtc(destination);
                AtomicWrite.Bytes(destination + ".graft-conflict-" + Guid.NewGuid().ToString("N"), sourceWins ? own : sourceBytes);
                if (!sourceWins) sourceBytes = own;
            }
            else sourceBytes = own;
            AtomicWrite.Bytes(source, sourceBytes);
        }
        if (own is null || !own.AsSpan().SequenceEqual(sourceBytes)) AtomicWrite.Bytes(destination, sourceBytes);
        AtomicWrite.Bytes(stateFile, JsonSerializer.SerializeToUtf8Bytes(state with { Digest = Graft.Digest(sourceBytes) }));
        return true;
    }

    public static bool Restore(string destination)
    {
        using var stateLock = StateLock.Acquire();
        var stateFile = StatePath(destination);
        if (!File.Exists(stateFile)) return false;
        var state = JsonSerializer.Deserialize<State>(File.ReadAllBytes(stateFile))
            ?? throw new IOException("Unreadable settings baseline.");
        if (!File.Exists(state.Source)) return true;
        Share(state.Source, destination);
        if (state.HadOwn)
        {
            var stash = Graft.StashPath(destination);
            if (!File.Exists(stash)) throw new IOException("The original settings stash is missing.");
            File.Move(stash, destination, overwrite: true);
        }
        else File.Delete(destination);
        File.Delete(stateFile);
        return true;
    }
}
