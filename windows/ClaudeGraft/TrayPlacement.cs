using System.Drawing;

namespace ClaudeGraft;

internal enum FlyoutEdge { Bottom, Top, Left, Right }

internal static class TrayPlacement
{
    // All coordinates are physical pixels on the monitor that owns the icon.
    internal static Point Place(Rectangle screen, Rectangle work, Size size, int margin,
        Rectangle? overflow, out FlyoutEdge edge)
    {
        edge = work.Top > screen.Top ? FlyoutEdge.Top
            : work.Left > screen.Left ? FlyoutEdge.Left
            : work.Right < screen.Right ? FlyoutEdge.Right
            : FlyoutEdge.Bottom;

        Point Clamp(Point p, int inset)
        {
            int maxX = Math.Max(work.Left, work.Right - size.Width);
            int maxY = Math.Max(work.Top, work.Bottom - size.Height);
            int padX = Math.Min(inset, (maxX - work.Left) / 2);
            int padY = Math.Min(inset, (maxY - work.Top) / 2);
            return new Point(Math.Clamp(p.X, work.Left + padX, maxX - padX),
                Math.Clamp(p.Y, work.Top + padY, maxY - padY));
        }

        var corner = new Point(edge == FlyoutEdge.Left ? work.Left : work.Right,
            edge == FlyoutEdge.Top ? work.Top : work.Bottom);
        if (overflow is not { } panel) return Clamp(corner, margin);

        int x = panel.Left + (panel.Width - size.Width) / 2;
        int y = panel.Top + (panel.Height - size.Height) / 2;
        var above = new Point(x, panel.Top - margin - size.Height);
        var below = new Point(x, panel.Bottom + margin);
        var left = new Point(panel.Left - margin - size.Width, y);
        var right = new Point(panel.Right + margin, y);
        Point[] candidates = edge switch
        {
            FlyoutEdge.Top => [below, left, right, above],
            FlyoutEdge.Left => [right, above, below, left],
            FlyoutEdge.Right => [left, above, below, right],
            _ => [above, left, right, below],
        };

        // Prefer the side facing away from the taskbar. Give up the monitor
        // margin before covering any of the overflow on a small display.
        var best = Clamp(candidates[0], margin);
        long leastOverlap = long.MaxValue;
        foreach (int inset in new[] { margin, 0 })
        {
            foreach (var candidate in candidates)
            {
                var at = Clamp(candidate, inset);
                var intersection = Rectangle.Intersect(new Rectangle(at, size), panel);
                long overlap = (long)intersection.Width * intersection.Height;
                if (overlap == 0) return at;
                if (overlap < leastOverlap)
                {
                    leastOverlap = overlap;
                    best = at;
                }
            }
        }
        return best;
    }
}
