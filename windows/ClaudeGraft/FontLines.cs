using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace ClaudeGraft;

/// A font's own baseline and letter heights, as DirectWrite reads them from the
/// font file, in fractions of an em. WinUI's text measures only its box.
public static class FontLines
{
    public readonly record struct Metrics(double Ascent, double CapHeight, double XHeight);

    private static readonly ConcurrentDictionary<(string, int, int), Metrics?> Known = new();

    public static Metrics? Of(string family, int weight, int style) =>
        Known.GetOrAdd((family, weight, style), key => Read(key.Item1, key.Item2, key.Item3));

    [StructLayout(LayoutKind.Sequential)]
    private struct FontMetrics
    {
        public ushort DesignUnitsPerEm, Ascent, Descent;
        public short LineGap;
        public ushort CapHeight, XHeight;
        public short UnderlinePosition;
        public ushort UnderlineThickness;
        public short StrikethroughPosition;
        public ushort StrikethroughThickness;
    }

    [DllImport("dwrite.dll")]
    private static extern int DWriteCreateFactory(int type, ref Guid iid, out IntPtr factory);

    private delegate int GetSystemFontCollection(IntPtr self, out IntPtr collection, int checkForUpdates);
    private delegate int FindFamilyName(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string name, out uint index, out int exists);
    private delegate int GetFontFamily(IntPtr self, uint index, out IntPtr family);
    private delegate int GetFirstMatchingFont(IntPtr self, int weight, int stretch, int style, out IntPtr font);
    private delegate void GetMetrics(IntPtr self, out FontMetrics metrics);
    private delegate int CreateFontFace(IntPtr self, out IntPtr face);
    private delegate int GetGlyphIndices(IntPtr self, ref uint codePoint, uint count, out ushort index);
    private delegate int GetDesignGlyphMetrics(IntPtr self, ref ushort index, uint count, out GlyphMetrics metrics, int sideways);

    [StructLayout(LayoutKind.Sequential)]
    private struct GlyphMetrics
    {
        public int LeftSideBearing;
        public uint AdvanceWidth;
        public int RightSideBearing, TopSideBearing;
        public uint AdvanceHeight;
        public int BottomSideBearing, VerticalOriginY;
    }

    private static readonly ConcurrentDictionary<(string, int), double?> Inks = new();

    /// How far above the baseline the middle of one glyph's ink sits, in ems: an
    /// icon font's glyph need not sit where its letters would.
    public static double? InkMiddle(string family, int codePoint) =>
        Inks.GetOrAdd((family, codePoint), key => ReadInk(key.Item1, key.Item2));

    private static T Slot<T>(IntPtr com, int index) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(com), index * IntPtr.Size));

    private static Metrics? Read(string family, int weight, int style) => WithFont(family, weight, style, font =>
    {
        Slot<GetMetrics>(font, 11)(font, out var m);
        double em = m.DesignUnitsPerEm;
        return em == 0 ? (Metrics?)null : new Metrics(m.Ascent / em, m.CapHeight / em, m.XHeight / em);
    });

    private static double? ReadInk(string family, int codePoint) => WithFont(family, 400, 0, font =>
    {
        Slot<GetMetrics>(font, 11)(font, out var m);
        if (Slot<CreateFontFace>(font, 13)(font, out var face) < 0) return (double?)null;
        try
        {
            var cp = (uint)codePoint;
            if (Slot<GetGlyphIndices>(face, 11)(face, ref cp, 1, out var index) < 0 || index == 0) return null;
            if (Slot<GetDesignGlyphMetrics>(face, 10)(face, ref index, 1, out var g, 0) < 0) return null;
            double em = m.DesignUnitsPerEm;
            var top = g.VerticalOriginY - g.TopSideBearing;
            var bottom = g.VerticalOriginY - (int)g.AdvanceHeight + g.BottomSideBearing;
            return em == 0 ? null : (top + bottom) / 2.0 / em;
        }
        finally { Marshal.Release(face); }
    });

    private static T? WithFont<T>(string family, int weight, int style, Func<IntPtr, T?> use)
    {
        family = family.Split(',')[0].Trim();
        IntPtr factory = 0, collection = 0, fontFamily = 0, font = 0;
        try
        {
            var iid = new Guid("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");
            if (DWriteCreateFactory(0, ref iid, out factory) < 0) return default;
            if (Slot<GetSystemFontCollection>(factory, 3)(factory, out collection, 0) < 0) return default;
            var find = Slot<FindFamilyName>(collection, 5);
            uint index = 0; int exists = 0;
            // WinUI names its default "XamlAutoFontFamily", and a variable family can go by
            // its base name; both draw as Segoe UI's proportions.
            foreach (var name in new[] { family, "Segoe UI Variable Text", "Segoe UI" })
                if (find(collection, name, out index, out exists) >= 0 && exists != 0) break;
            if (exists == 0) return default;
            if (Slot<GetFontFamily>(collection, 4)(collection, index, out fontFamily) < 0) return default;
            if (Slot<GetFirstMatchingFont>(fontFamily, 7)(fontFamily, weight, 5, style, out font) < 0) return default;
            return use(font);
        }
        catch (Exception) { return default; }
        finally
        {
            foreach (var com in new[] { font, fontFamily, collection, factory })
                if (com != 0) Marshal.Release(com);
        }
    }
}
