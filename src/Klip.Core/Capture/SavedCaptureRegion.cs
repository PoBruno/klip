using System.Globalization;

namespace Klip.Core.Capture;

/// <summary>Rectangle in physical pixels of the virtual desktop.</summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public bool Contains(ScreenRect other) =>
        other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;
}

/// <summary>A monitor as seen right now: physical bounds and effective DPI.</summary>
public readonly record struct MonitorSnapshot(ScreenRect Bounds, uint Dpi);

/// <summary>
/// Last rectangle captured, kept to repeat the shot without the overlay.
/// Stored with the monitor it was on and that monitor's DPI: if either changed
/// the pixels no longer mean the same thing, and the caller falls back to the overlay.
/// </summary>
/// <remarks>Settings format: "monitorX,monitorY,monitorW,monitorH|x,y,w,h|dpi".</remarks>
public readonly record struct SavedCaptureRegion(ScreenRect Region, ScreenRect Monitor, uint Dpi)
{
    public string Serialize() => string.Create(CultureInfo.InvariantCulture,
        $"{Monitor.X},{Monitor.Y},{Monitor.Width},{Monitor.Height}|{Region.X},{Region.Y},{Region.Width},{Region.Height}|{Dpi}");

    public static bool TryParse(string? text, out SavedCaptureRegion region)
    {
        region = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Split('|');
        if (parts.Length != 3
            || !TryParseRect(parts[0], out var monitor)
            || !TryParseRect(parts[1], out var rect)
            || !uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var dpi)
            || dpi == 0)
            return false;

        if (rect.Width < 1 || rect.Height < 1 || monitor.Width < 1 || monitor.Height < 1)
            return false;

        region = new SavedCaptureRegion(rect, monitor, dpi);
        return true;
    }

    /// <summary>
    /// True when the same monitor (same bounds) is still there with the same DPI and
    /// the region still fits inside it.
    /// </summary>
    public bool IsStillValid(IEnumerable<MonitorSnapshot> monitors)
    {
        foreach (var m in monitors)
        {
            if (m.Bounds == Monitor && m.Dpi == Dpi)
                return Monitor.Contains(Region);
        }
        return false;
    }

    private static bool TryParseRect(string text, out ScreenRect rect)
    {
        rect = default;
        var n = text.Split(',');
        if (n.Length != 4)
            return false;
        var values = new int[4];
        for (var i = 0; i < 4; i++)
        {
            if (!int.TryParse(n[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out values[i]))
                return false;
        }
        rect = new ScreenRect(values[0], values[1], values[2], values[3]);
        return true;
    }
}
