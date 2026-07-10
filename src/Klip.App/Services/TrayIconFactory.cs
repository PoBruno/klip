using System.Drawing;
using System.Drawing.Drawing2D;

namespace Klip.App.Services;

/// <summary>
/// Tray icon drawn at runtime (placeholder until we ship the real .ico).
/// Draws a rounded clipboard with a clip, monochrome Fluent style.
/// </summary>
public static class TrayIconFactory
{
    public static Icon Create()
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var body = RoundedRect(new RectangleF(6, 4, 20, 25), 4);
        using var bodyPen = new Pen(Color.White, 2.4f);
        g.DrawPath(bodyPen, body);

        // clipboard clip
        using var clipBrush = new SolidBrush(Color.White);
        g.FillRectangle(clipBrush, 12, 2, 8, 5);

        // content lines
        using var linePen = new Pen(Color.White, 2f);
        g.DrawLine(linePen, 11, 13, 21, 13);
        g.DrawLine(linePen, 11, 18, 21, 18);
        g.DrawLine(linePen, 11, 23, 17, 23);

        var hIcon = bmp.GetHicon();
        // clone it so we don't hold the GDI handle (that one needs DestroyIcon)
        using var tmp = Icon.FromHandle(hIcon);
        var icon = (Icon)tmp.Clone();
        Interop.NativeMethods.DestroyIcon(hIcon);
        return icon;
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
