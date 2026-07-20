using System.Drawing;
using System.Drawing.Drawing2D;

namespace VolumeLock.Native;

/// <summary>
/// Generates the tray icon in code instead of shipping a binary .ico. Also produces a small
/// "locked" badge variant so the tray icon itself can reflect whether any entry is currently
/// locked (see <see cref="MainWindow"/> for where that's wired up).
/// </summary>
internal static class TrayIconFactory
{
    /// <summary>Creates the icon and returns both the GDI+ wrapper and its native handle so
    /// the caller can free the handle on shutdown via <see cref="NativeMethods.DestroyIconHandle"/>.</summary>
    public static (Icon Icon, nint Handle) Create(bool lockedBadge = false)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var background = new SolidBrush(Color.FromArgb(255, 32, 32, 34));
            using var backgroundPath = RoundedRect(new Rectangle(1, 1, 30, 30), 8);
            g.FillPath(background, backgroundPath);

            using var accent = new SolidBrush(Color.FromArgb(255, 0x60, 0xCD, 0xFF));
            // Simple speaker glyph: a body rectangle + a triangular cone.
            var body = new[]
            {
                new Point(11, 12), new Point(15, 12), new Point(15, 20), new Point(11, 20)
            };
            var cone = new[]
            {
                new Point(15, 12), new Point(21, 7), new Point(21, 25), new Point(15, 20)
            };
            g.FillPolygon(accent, body);
            g.FillPolygon(accent, cone);

            if (lockedBadge)
            {
                using var badgeBrush = new SolidBrush(Color.FromArgb(255, 0xFF, 0x8A, 0x80));
                g.FillEllipse(badgeBrush, 18, 18, 12, 12);
                using var pen = new Pen(Color.FromArgb(255, 32, 32, 34), 1.5f);
                g.DrawEllipse(pen, 18, 18, 12, 12);
            }
        }

        nint hIcon = bitmap.GetHicon();
        Icon icon = Icon.FromHandle(hIcon);
        return (icon, hIcon);
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
