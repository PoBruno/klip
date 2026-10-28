namespace Klip.Core.Capture;

/// <summary>Where the overlay magnifier sits relative to the cursor.</summary>
public static class MagnifierLayout
{
    /// <summary>
    /// Bottom-right of the cursor by default; flips left and/or up when it would
    /// leave the area, so it never hides under the screen edge.
    /// </summary>
    public static (double X, double Y) Place(
        double cursorX, double cursorY, double width, double height,
        double areaWidth, double areaHeight, double offset = 20)
    {
        var x = cursorX + offset;
        if (x + width > areaWidth)
            x = cursorX - offset - width;
        var y = cursorY + offset;
        if (y + height > areaHeight)
            y = cursorY - offset - height;
        return (Math.Max(0, x), Math.Max(0, y));
    }

    /// <summary>Source rect (in frame pixels) centered on the pixel under the cursor.</summary>
    public static (int X, int Y) SourceOrigin(int pixelX, int pixelY, int cells) =>
        (pixelX - cells / 2, pixelY - cells / 2);
}
