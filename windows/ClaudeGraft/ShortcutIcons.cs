using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using ClaudeGraft.Core;

namespace ClaudeGraft;

/// <summary>
/// Claude's own icon, recoloured or badged for a shortcut. Each preset is drawn
/// once into a file of its own whose path never changes, so a link pinned to the
/// taskbar keeps its icon across a Claude update that moves the exe it came from.
/// </summary>
public static class ShortcutIcons
{
    private const int Pixels = 256;
    private const int PreviewPixels = 96;
    // Raised whenever the drawing changes, so files drawn by an older build go.
    private const string Renderer = "2";

    private static readonly object Gate = new();
    private static string? _checkedStamp;

    private static string Folder => Path.Combine(GraftPaths.OwnData, "icons");

    /// The icon a shortcut's link wears, as a file and index; Claude's exe when
    /// nothing could be drawn, and empty when Claude is not installed either.
    public static (string File, int Index) LinkIcon(Shortcut shortcut)
    {
        try
        {
            if (Ensure(IconPreset.Normalize(shortcut.IconPreset), preview: false) is string file) return (file, 0);
        }
        catch (Exception e)
        {
            Diagnostics.Note("icon.failed", new Dictionary<string, object?> { ["preset"] = shortcut.IconPreset, ["error"] = e.Message });
        }
        return (Launcher.ClaudeExe() ?? "", 0);
    }

    /// A small PNG of every preset, for the picker; null where none could be drawn.
    public static IReadOnlyDictionary<string, string?> Previews()
    {
        var result = new Dictionary<string, string?>();
        foreach (var preset in IconPreset.All)
        {
            try { result[preset] = Ensure(preset, preview: true); }
            catch { result[preset] = null; }
        }
        return result;
    }

    /// One preset's small PNG, or null where none could be drawn.
    public static string? Preview(string preset)
    {
        try { return Ensure(IconPreset.Normalize(preset), preview: true); }
        catch { return null; }
    }

    /// Redraws, in place, the icons in use once Claude's own has changed, so
    /// links already made follow it.
    public static void Refresh(IEnumerable<Shortcut> shortcuts)
    {
        foreach (var preset in shortcuts.Select(s => IconPreset.Normalize(s.IconPreset)).Distinct())
            try { Ensure(preset, preview: false); } catch { }
    }

    private static string? Ensure(string preset, bool preview)
    {
        lock (Gate)
        {
            var source = Launcher.ClaudeExe();
            if (source is null || !File.Exists(source)) return null;
            Directory.CreateDirectory(Folder);
            ForgetIfSourceChanged(source);

            var file = Path.Combine(Folder, preset + (preview ? ".png" : ".ico"));
            if (File.Exists(file)) return file;

            using var stock = Stock(source);
            if (stock is null) return null;
            using var drawn = Draw(stock, preset);
            var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                if (preview)
                {
                    using var small = Scaled(drawn, PreviewPixels);
                    small.Save(temporary, ImageFormat.Png);
                }
                else WriteIco(drawn, temporary);
                File.Move(temporary, file, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return file;
        }
    }

    // A Claude update brings a new exe and perhaps a new icon; what was drawn
    // from the old one is redrawn at the same paths.
    private static void ForgetIfSourceChanged(string source)
    {
        var stamp = Renderer + "|" + source + "|" + File.GetLastWriteTimeUtc(source).Ticks;
        if (_checkedStamp == stamp) return;
        var stampFile = Path.Combine(Folder, "source.txt");
        string? previous = null;
        try { previous = File.ReadAllText(stampFile); } catch { }
        if (previous != stamp)
        {
            foreach (var old in Directory.EnumerateFiles(Folder).Where(f => f.EndsWith(".ico") || f.EndsWith(".png")))
                try { File.Delete(old); } catch { }
            AtomicWrite.Bytes(stampFile, System.Text.Encoding.UTF8.GetBytes(stamp));
        }
        _checkedStamp = stamp;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string file, int index, int cx, int cy,
        out IntPtr icon, out uint id, uint count, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

    private static Bitmap? Stock(string exe)
    {
        if (PrivateExtractIcons(exe, 0, Pixels, Pixels, out var handle, out _, 1, 0) != 1 || handle == IntPtr.Zero)
            return null;
        try
        {
            using var icon = Icon.FromHandle(handle);
            using var bitmap = icon.ToBitmap();
            return Scaled(bitmap, Pixels);
        }
        finally { DestroyIcon(handle); }
    }

    internal static Bitmap Draw(Bitmap stock, string preset)
    {
        var (hue, saturation, badge) = IconPreset.Style(preset);
        var bitmap = Scaled(stock, Pixels);
        if (hue is not null || saturation != 1) Recolour(bitmap, hue ?? 0, saturation);
        if (badge is not null) Badge(bitmap, badge);
        return bitmap;
    }

    // A rotation about the grey axis, the same one CSS hue-rotate uses, so the
    // shading and the highlights keep their brightness; saturation 0 is a
    // luminance grey.
    private static void Recolour(Bitmap bitmap, double angle, double saturation)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        var m = new[]
        {
            .213 + c * .787 - s * .213, .715 - c * .715 - s * .715, .072 - c * .072 + s * .928,
            .213 - c * .213 + s * .143, .715 + c * .285 + s * .140, .072 - c * .072 - s * .283,
            .213 - c * .213 - s * .787, .715 - c * .715 + s * .715, .072 + c * .928 + s * .072,
        };
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * data.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            for (var i = 0; i < bytes.Length; i += 4)
            {
                if (bytes[i + 3] == 0) continue;
                double b = bytes[i], g = bytes[i + 1], r = bytes[i + 2];
                double nr = m[0] * r + m[1] * g + m[2] * b;
                double ng = m[3] * r + m[4] * g + m[5] * b;
                double nb = m[6] * r + m[7] * g + m[8] * b;
                if (saturation != 1)
                {
                    var luma = .2126 * nr + .7152 * ng + .0722 * nb;
                    nr = luma + (nr - luma) * saturation;
                    ng = luma + (ng - luma) * saturation;
                    nb = luma + (nb - luma) * saturation;
                }
                bytes[i] = (byte)Math.Clamp(nb, 0, 255);
                bytes[i + 1] = (byte)Math.Clamp(ng, 0, 255);
                bytes[i + 2] = (byte)Math.Clamp(nr, 0, 255);
            }
            Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
        }
        finally { bitmap.UnlockBits(data); }
    }

    private static void Badge(Bitmap bitmap, string badge)
    {
        float side = bitmap.Width;
        var disk = new RectangleF(side * .63f, side * .635f, side * .29f, side * .29f);
        using var canvas = Graphics.FromImage(bitmap);
        canvas.SmoothingMode = SmoothingMode.AntiAlias;
        canvas.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using (var rim = new SolidBrush(Color.FromArgb(245, Color.White))) canvas.FillEllipse(rim, disk);
        disk.Inflate(-side * .016f, -side * .016f);
        var accent = badge switch
        {
            "work" => Color.FromArgb(0, 122, 255),
            "personal" => Color.FromArgb(175, 82, 222),
            "code" => Color.FromArgb(52, 199, 89),
            _ => Color.FromArgb(255, 45, 85),
        };
        using (var fill = new SolidBrush(accent)) canvas.FillEllipse(fill, disk);

        var glyph = badge switch { "work" => "", "personal" => "", "code" => "", _ => "" };
        using var font = GlyphFont(side * .135f);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        canvas.DrawString(glyph, font, Brushes.White, disk, format);
    }

    // Segoe Fluent Icons ships with Windows 11; Windows 10 has the same glyphs
    // at the same code points in Segoe MDL2 Assets.
    private static Font GlyphFont(float pixels)
    {
        using var installed = new InstalledFontCollection();
        var family = installed.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
        return new Font(family, pixels, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    private static Bitmap Scaled(Image image, int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var canvas = Graphics.FromImage(bitmap);
        canvas.InterpolationMode = InterpolationMode.HighQualityBicubic;
        canvas.PixelOffsetMode = PixelOffsetMode.HighQuality;
        canvas.CompositingMode = CompositingMode.SourceCopy;
        canvas.DrawImage(image, new Rectangle(0, 0, size, size));
        return bitmap;
    }

    // Every size Explorer and the taskbar ask for, so none is a 32-pixel frame
    // blown up. Only the 256 frame is PNG; the small ones are plain bitmaps,
    // which every reader of .ico takes.
    private static void WriteIco(Bitmap image, string file)
    {
        var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };
        var frames = sizes.Select(size =>
        {
            using var scaled = Scaled(image, size);
            return size == 256 ? Png(scaled) : Dib(scaled);
        }).ToArray();

        using var writer = new BinaryWriter(File.Create(file));
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)sizes.Length);
        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            var edge = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
            writer.Write(edge);
            writer.Write(edge);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(frames[i].Length);
            writer.Write(offset);
            offset += frames[i].Length;
        }
        foreach (var frame in frames) writer.Write(frame);
    }

    private static byte[] Png(Bitmap bitmap)
    {
        using var memory = new MemoryStream();
        bitmap.Save(memory, ImageFormat.Png);
        return memory.ToArray();
    }

    // An icon bitmap is stored bottom-up at twice its height, the colour rows
    // followed by a one-bit mask that the alpha channel makes redundant.
    private static byte[] Dib(Bitmap bitmap)
    {
        int size = bitmap.Width, maskStride = (size + 31) / 32 * 4;
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);
        writer.Write(40);
        writer.Write(size);
        writer.Write(size * 2);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(size * size * 4 + maskStride * size);
        writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);

        var data = bitmap.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[size * 4];
            for (var y = size - 1; y >= 0; y--)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                writer.Write(row);
            }
        }
        finally { bitmap.UnlockBits(data); }
        writer.Write(new byte[maskStride * size]);
        return memory.ToArray();
    }
}
