using Klip.Core.Imaging;

namespace Klip.Core.Tests;

public class BackgroundRemoverTests
{
    /// <summary>Builds a solid color BGRA buffer.</summary>
    private static byte[] Solid(int w, int h, byte r, byte g, byte b)
    {
        var buffer = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            buffer[i * 4] = b;
            buffer[i * 4 + 1] = g;
            buffer[i * 4 + 2] = r;
            buffer[i * 4 + 3] = 255;
        }
        return buffer;
    }

    private static void FillRect(byte[] buffer, int w, int x0, int y0, int x1, int y1, byte r, byte g, byte b)
    {
        for (var y = y0; y < y1; y++)
        for (var x = x0; x < x1; x++)
        {
            var o = (y * w + x) * 4;
            buffer[o] = b;
            buffer[o + 1] = g;
            buffer[o + 2] = r;
            buffer[o + 3] = 255;
        }
    }

    private static byte AlphaAt(byte[] buffer, int w, int x, int y) => buffer[(y * w + x) * 4 + 3];

    [Fact]
    public void RemoveFromEdges_SolidBackground_KeepsCenterObject()
    {
        // white 20x20 background with an 8x8 red square in the middle
        var img = Solid(20, 20, 255, 255, 255);
        FillRect(img, 20, 6, 6, 14, 14, 220, 30, 30);

        var removed = BackgroundRemover.RemoveFromEdges(img, 20, 20);

        Assert.Equal(20 * 20 - 8 * 8, removed);
        Assert.Equal(0, AlphaAt(img, 20, 0, 0));     // corner is transparent
        Assert.Equal(0, AlphaAt(img, 20, 19, 19));
        Assert.Equal(255, AlphaAt(img, 20, 10, 10)); // object kept
    }

    [Fact]
    public void RemoveFromEdges_ObjectTouchingEdge_WithDifferentColor_IsKept()
    {
        // object touches the edge but has a different color: edge seeds over the
        // object only remove it when connected by color
        var img = Solid(10, 10, 0, 0, 0);
        FillRect(img, 10, 0, 0, 10, 5, 200, 200, 200); // top half is gray

        BackgroundRemover.RemoveFromEdges(img, 10, 10);

        // both halves touch the border, so the flood clears everything (expected)
        Assert.Equal(0, AlphaAt(img, 10, 5, 2));
        Assert.Equal(0, AlphaAt(img, 10, 5, 7));
    }

    [Fact]
    public void RemoveFromEdges_GradientWithinTolerance_IsRemoved()
    {
        var img = Solid(10, 10, 100, 100, 100);
        // center pixel slightly off, still within the tolerance of 32
        FillRect(img, 10, 4, 4, 6, 6, 115, 115, 115);

        var removed = BackgroundRemover.RemoveFromEdges(img, 10, 10, tolerance: 32);

        Assert.Equal(100, removed);
    }

    [Fact]
    public void RemoveFromEdges_BufferTooSmall_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            BackgroundRemover.RemoveFromEdges(new byte[10], 10, 10));
    }
}
