using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ClaudeGraft.Core;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ClaudeGraft;

public sealed class IconChoice(string name, BitmapImage preview)
{
    public string Name { get; set; } = name;
    public BitmapImage Preview { get; set; } = preview;
    public override string ToString() => Name;
}

public static class ShortcutIcons
{
    public static readonly string[] Names = { "Original", "Blue", "Violet", "Green", "Red", "Pink", "Teal", "Graphite", "Work", "Personal", "Code", "Research" };
    private const int Pixels = 256;
    private const string RendererVersion = "mac-presets-v3";
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string file, int index, int width, int height, out IntPtr icon, out uint id, uint count, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

    private static string Source => Launcher.ClaudeExe() ?? Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
    private static string CacheDirectory
    {
        get
        {
            var identity = Source + "|" + File.GetLastWriteTimeUtc(Source).Ticks;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
            return Path.Combine(GraftPaths.OwnData, "icons", RendererVersion, hash);
        }
    }

    public static IReadOnlyList<(string Name, string Path)> PreviewPaths() =>
        Names.Select(name => (name, Ensure(name, true))).ToList();

    public static string IconFor(Shortcut shortcut) =>
        Ensure(Names.Contains(shortcut.IconPreset) ? shortcut.IconPreset : "Original", false);

    private static Bitmap Stock()
    {
        var count = PrivateExtractIcons(Source, 0, Pixels, Pixels, out var handle, out _, 1, 0);
        if (count == 1 && handle != IntPtr.Zero)
        {
            try { using var icon = Icon.FromHandle(handle); return icon.ToBitmap(); }
            finally { DestroyIcon(handle); }
        }
        using var fallback = new Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"), Pixels, Pixels);
        return fallback.ToBitmap();
    }

    private static string Ensure(string name, bool preview)
    {
        var directory = CacheDirectory;
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, name + (preview ? ".png" : ".ico"));
        if (File.Exists(file)) return file;
        using var original = Stock();
        using var bitmap = new Bitmap(Pixels, Pixels, PixelFormat.Format32bppArgb);
        using (var canvas = Graphics.FromImage(bitmap))
        {
            canvas.InterpolationMode = InterpolationMode.HighQualityBicubic;
            canvas.DrawImage(original, new Rectangle(0, 0, Pixels, Pixels));
        }
        // These are the macOS presets' hue angles and saturation, in the same
        // order. Tint the full stock icon, retaining its shading and transparency.
        var angle = name switch { "Blue" or "Work" => -2.8, "Violet" or "Personal" => -1.75,
            "Green" or "Code" => 2.25, "Red" => -0.45, "Pink" or "Research" => -0.95, "Teal" => 2.8, _ => 0.0 };
        if (angle != 0 || name == "Graphite")
            for (var y = 0; y < Pixels; y++)
                for (var x = 0; x < Pixels; x++)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    if (pixel.A == 0) continue;
                    bitmap.SetPixel(x, y, Hsl(pixel.A, (pixel.GetHue() + angle * 180 / Math.PI + 360) % 360,
                        name == "Graphite" ? 0 : pixel.GetSaturation(), pixel.GetBrightness()));
                }
        if (name is "Work" or "Personal" or "Code" or "Research") Badge(bitmap, name);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (preview) bitmap.Save(temporary, ImageFormat.Png);
            else WriteIcon(bitmap, temporary);
            File.Move(temporary, file, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return file;
    }

    private static void Badge(Bitmap bitmap, string name)
    {
        using var canvas = Graphics.FromImage(bitmap);
        canvas.SmoothingMode = SmoothingMode.AntiAlias;
        canvas.TranslateTransform(Pixels * .63f, Pixels * .635f);
        canvas.ScaleTransform(Pixels * .29f / 100, Pixels * .29f / 100);
        using var rim = new SolidBrush(Color.FromArgb(245, Color.White));
        canvas.FillEllipse(rim, 0, 0, 100, 100);
        using var accent = new SolidBrush(name switch {
            "Work" => Color.FromArgb(0,122,255), "Personal" => Color.FromArgb(175,82,222),
            "Code" => Color.FromArgb(52,199,89), _ => Color.FromArgb(255,45,85) });
        canvas.FillEllipse(accent, 5.5f, 5.5f, 89, 89);
        using var line = new Pen(Color.White, 4.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        switch (name)
        {
            case "Work":
                canvas.DrawRectangle(line, 40, 28, 20, 11);
                canvas.FillRectangle(Brushes.White, 27, 38, 46, 31);
                using (var seam = new Pen(accent.Color, 3)) canvas.DrawLine(seam, 27, 49, 73, 49);
                canvas.FillRectangle(Brushes.White, 46, 46, 8, 10);
                break;
            case "Personal":
                canvas.FillEllipse(Brushes.White, 41, 26, 18, 18);
                using (var shoulders = new GraphicsPath())
                {
                    shoulders.AddArc(29, 47, 42, 34, 180, 180);
                    shoulders.AddLine(71, 64, 71, 70);
                    shoulders.AddLine(71, 70, 29, 70);
                    shoulders.CloseFigure();
                    canvas.FillPath(Brushes.White, shoulders);
                }
                break;
            case "Code":
                canvas.DrawRectangle(line, 25, 30, 50, 40);
                canvas.DrawLines(line, new[] { new PointF(34,42), new PointF(43,50), new PointF(34,58) });
                canvas.DrawLine(line, 52, 59, 63, 59);
                break;
            case "Research":
                canvas.DrawEllipse(line, 29, 26, 30, 30);
                canvas.DrawLine(line, 55, 53, 72, 70);
                break;
        }
    }

    // Windows chooses the nearest embedded size for the desktop, taskbar and
    // Explorer. Supplying each size prevents a 32-pixel extraction being enlarged.
    private static void WriteIcon(Bitmap image, string file)
    {
        var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
        var frames = sizes.Select(size =>
        {
            using var scaled = new Bitmap(size, size);
            using (var canvas = Graphics.FromImage(scaled))
            {
                canvas.InterpolationMode = InterpolationMode.HighQualityBicubic;
                canvas.DrawImage(image, new Rectangle(0, 0, size, size));
            }
            using var memory = new MemoryStream();
            scaled.Save(memory, ImageFormat.Png);
            return memory.ToArray();
        }).ToArray();
        using var writer = new BinaryWriter(File.Create(file));
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
            writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
            writer.Write((byte)0); writer.Write((byte)0);
            writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(frames[i].Length); writer.Write(offset); offset += frames[i].Length;
        }
        foreach (var frame in frames) writer.Write(frame);
    }

    private static Color Hsl(int alpha, double hue, double saturation, double lightness)
    {
        var c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = lightness - c / 2;
        var (r, g, b) = hue switch { <60 => (c,x,0.0), <120 => (x,c,0.0), <180 => (0.0,c,x),
            <240 => (0.0,x,c), <300 => (x,0.0,c), _ => (c,0.0,x) };
        return Color.FromArgb(alpha, (int)Math.Clamp((r+m)*255,0,255),
            (int)Math.Clamp((g+m)*255,0,255), (int)Math.Clamp((b+m)*255,0,255));
    }
}
