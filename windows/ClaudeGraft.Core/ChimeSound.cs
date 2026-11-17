namespace ClaudeGraft.Core;

/// <summary>
/// Graft's own sounds, drawn as soft mallet tones rather than shipped as files, in
/// the register and quiet of Windows' own notifications: a rising pair for a
/// finished turn, and three notes for one that waits on the person.
/// </summary>
public static class ChimeSound
{
    private const int Rate = 44100;

    public static readonly byte[] Done = Wave([(587.33, 0.00), (880.00, 0.13)], 0.75, 0.13);
    public static readonly byte[] Ask = Wave([(523.25, 0.00), (659.25, 0.11), (783.99, 0.22)], 0.85, 0.15);

    // Each note nearly a pure sine with a faint octave, eased in over 12 ms and
    // dying away quickly: a rounded mallet rather than a bell's ring or a beep.
    private static byte[] Wave((double Hz, double At)[] notes, double seconds, double level)
    {
        var count = (int)(seconds * Rate);
        var mix = new double[count];
        foreach (var (hz, at) in notes)
        {
            var start = (int)(at * Rate);
            for (var i = start; i < count; i++)
            {
                var t = (i - start) / (double)Rate;
                var envelope = Math.Sin(Math.PI / 2 * Math.Min(1, t / 0.012)) * Math.Exp(-t * 8);
                var tone = Math.Sin(2 * Math.PI * hz * t)
                    + 0.1 * Math.Sin(2 * Math.PI * hz * 2 * t) * Math.Exp(-t * 14);
                mix[i] += tone * envelope;
            }
        }
        var peak = mix.Max(Math.Abs);
        var data = new byte[count * 2];
        for (var i = 0; i < count; i++)
        {
            // The last 20 ms fade to nothing, so the sound ends without a click.
            var tail = Math.Min(1, (count - i) / (0.02 * Rate));
            var sample = (short)Math.Round(mix[i] / peak * level * tail * short.MaxValue);
            data[i * 2] = (byte)sample;
            data[i * 2 + 1] = (byte)(sample >> 8);
        }

        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write("RIFF"u8); w.Write(36 + data.Length); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(data.Length); w.Write(data);
        w.Flush();
        return stream.ToArray();
    }
}
