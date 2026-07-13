using Klip.Core.Media.Gif;

namespace Klip.Core.Tests.Media.Gif;

/// <summary>Dirty-rect entre frames BGRA (RF-F4.06, RF-F4.08, RF-F4.10).</summary>
public class DirtyRectDetectorTests
{
    [Fact]
    public void Compute_IdenticalFrames_ReturnsEmpty()
    {
        var frame = GifTestUtil.MakeBgra(32, 32, (x, y) => ((byte)x, (byte)y, 100));
        var rect = DirtyRectDetector.Compute(frame, frame, 32, 32, 0);
        Assert.True(rect.IsEmpty);
    }

    [Fact]
    public void Compute_SinglePixelChange_Returns1x1AtPosition()
    {
        var a = GifTestUtil.MakeBgra(32, 32, (_, _) => (10, 20, 30));
        var b = (byte[])a.Clone();
        var offset = (17 * 32 + 5) * 4;
        b[offset + 2] = 200; // canal R do pixel (5,17)

        var rect = DirtyRectDetector.Compute(b, a, 32, 32, 0);
        Assert.Equal(new DirtyRect(5, 17, 1, 1), rect);
    }

    [Fact]
    public void Compute_10x10Block_ReturnsExactBoundingBox()
    {
        var a = GifTestUtil.MakeBgra(100, 100, (_, _) => (0, 0, 0));
        var b = GifTestUtil.MakeBgra(100, 100, (x, y) =>
            x is >= 20 and < 30 && y is >= 30 and < 40 ? ((byte)255, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)0));

        var rect = DirtyRectDetector.Compute(b, a, 100, 100, 0);
        Assert.Equal(new DirtyRect(20, 30, 10, 10), rect);
    }

    [Fact]
    public void Compute_NoiseWithinTolerance_ReturnsEmpty()
    {
        // Ruido de codec (RF-F4.10): +/-1 por canal soma no maximo 3.
        var a = GifTestUtil.MakeBgra(16, 16, (_, _) => (100, 100, 100));
        var b = GifTestUtil.MakeBgra(16, 16, (x, _) => ((byte)(100 + x % 2), (byte)(100 - x % 2), 101));

        Assert.False(DirtyRectDetector.Compute(b, a, 16, 16, 0).IsEmpty);
        Assert.True(DirtyRectDetector.Compute(b, a, 16, 16, 3).IsEmpty);
    }

    [Fact]
    public void Compute_AlphaOnlyChange_IsIgnored()
    {
        var a = GifTestUtil.MakeBgra(8, 8, (_, _) => (1, 2, 3));
        var b = (byte[])a.Clone();
        for (var i = 3; i < b.Length; i += 4)
            b[i] = 128;

        Assert.True(DirtyRectDetector.Compute(b, a, 8, 8, 0).IsEmpty);
    }

    [Fact]
    public void Compute_ChangesInCorners_CoversWholeFrame()
    {
        var a = GifTestUtil.MakeBgra(50, 40, (_, _) => (0, 0, 0));
        var b = (byte[])a.Clone();
        b[2] = 255;                          // (0,0)
        b[(39 * 50 + 49) * 4 + 2] = 255;     // (49,39)

        var rect = DirtyRectDetector.Compute(b, a, 50, 40, 0);
        Assert.Equal(new DirtyRect(0, 0, 50, 40), rect);
    }
}
