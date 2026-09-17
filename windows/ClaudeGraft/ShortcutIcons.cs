using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ClaudeGraft.Core;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ClaudeGraft;

public sealed record IconChoice(string Name, BitmapImage Preview);

public static class ShortcutIcons
{
    public static readonly string[] Names = { "Original", "Blue", "Violet", "Green", "Red", "Pink", "Teal", "Graphite", "Work", "Personal", "Code", "Research" };
    private static string DirectoryPath => Path.Combine(GraftPaths.OwnData, "icons");
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

    public static IReadOnlyList<IconChoice> Choices()
    {
        try
        {
            return Names.Select(name => new IconChoice(name, new BitmapImage(new Uri(Ensure(name, preview: true))))).ToList();
        }
        catch
        {
            return Names.Select(name => new IconChoice(name, new BitmapImage(new Uri("ms-appx:///Assets/AppIcon.ico")))).ToList();
        }
    }

    public static string IconFor(Shortcut shortcut) => Ensure(Names.Contains(shortcut.IconPreset) ? shortcut.IconPreset : "Original", false);

    private static string Ensure(string name, bool preview)
    {
        Directory.CreateDirectory(DirectoryPath);
        var file = Path.Combine(DirectoryPath, name + (preview ? ".png" : ".ico"));
        if (File.Exists(file)) return file;
        using var source = Icon.ExtractAssociatedIcon(Launcher.ClaudeExe() ?? Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"))
            ?? new Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        using var original = source.ToBitmap();
        using var bitmap = new Bitmap(128, 128);
        using (var canvas = Graphics.FromImage(bitmap))
        {
            canvas.InterpolationMode = InterpolationMode.HighQualityBicubic;
            canvas.DrawImage(original, new Rectangle(0, 0, 128, 128));
        }
        var angle = name switch { "Blue" or "Work" => -2.8, "Violet" or "Personal" => -1.75,
            "Green" or "Code" => 2.25, "Red" => -0.45, "Pink" or "Research" => -0.95, "Teal" => 2.8, _ => 0.0 };
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.A == 0) continue;
                var saturation = name == "Graphite" ? 0 : pixel.GetSaturation();
                bitmap.SetPixel(x, y, Hsl(pixel.A, (pixel.GetHue() + angle * 180 / Math.PI + 360) % 360, saturation, pixel.GetBrightness()));
            }
        if (name is "Work" or "Personal" or "Code" or "Research")
        {
            using var canvas = Graphics.FromImage(bitmap);
            canvas.SmoothingMode = SmoothingMode.AntiAlias;
            canvas.FillEllipse(Brushes.White, 80, 80, 40, 40);
            using var accent = new SolidBrush(name switch { "Work" => Color.RoyalBlue, "Personal" => Color.MediumPurple,
                "Code" => Color.ForestGreen, _ => Color.DeepPink });
            canvas.FillEllipse(accent, 82, 82, 36, 36);
            using var font = new Font("Segoe Fluent Icons", 15, FontStyle.Regular, GraphicsUnit.Pixel);
            var glyph = name switch { "Work" => "\uE821", "Personal" => "\uE77B", "Code" => "\uE756", _ => "\uE721" };
            canvas.DrawString(glyph, font, Brushes.White, new RectangleF(82, 82, 36, 36),
                new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
        }
        if (preview) bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        else
        {
            var handle = bitmap.GetHicon();
            try { using var icon = Icon.FromHandle(handle); using var output = File.Create(file); icon.Save(output); }
            finally { DestroyIcon(handle); }
        }
        return file;
    }

    private static Color Hsl(int alpha, double hue, double saturation, double lightness)
    {
        var c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = lightness - c / 2;
        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x)
        };
        return Color.FromArgb(alpha, (int)Math.Clamp((r + m) * 255, 0, 255),
            (int)Math.Clamp((g + m) * 255, 0, 255), (int)Math.Clamp((b + m) * 255, 0, 255));
    }
}
