using Klip.Core.Capture;

namespace Klip.Core.Tests;

public class PanoramicStitcherTests
{
    private const int W = 60;

    /// <summary>Tall image with unique content per row (distinct gradients).</summary>
    private static byte[] MakeTallImage(int height)
    {
        var img = new byte[height * W * 4];
        var rng = new Random(42);
        var rowNoise = new byte[height];
        rng.NextBytes(rowNoise);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < W; x++)
        {
            var o = (y * W + x) * 4;
            img[o] = (byte)((y * 3 + x) % 251);
            img[o + 1] = (byte)((y * 7 + rowNoise[y]) % 251);
            img[o + 2] = (byte)((y * 13) % 251);
            img[o + 3] = 255;
        }
        return img;
    }

    private static byte[] Slice(byte[] tall, int startRow, int rows, int noise = 0, Random? rng = null)
    {
        var stride = W * 4;
        var slice = new byte[rows * stride];
        Buffer.BlockCopy(tall, startRow * stride, slice, 0, rows * stride);
        if (noise > 0 && rng is not null)
        {
            // light per-pixel noise (antialiasing/compression) needs some tolerance
            for (var i = 0; i < slice.Length; i++)
            {
                if (i % 4 == 3)
                    continue; // alpha
                slice[i] = (byte)Math.Clamp(slice[i] + rng.Next(-noise, noise + 1), 0, 255);
            }
        }
        return slice;
    }

    [Fact]
    public void AddFrame_VariableDeltas_ReconstructsFullHeight()
    {
        // manual scroll: irregular deltas, user scrolls at their own pace
        var tall = MakeTallImage(300);
        var stitcher = new PanoramicStitcher(W, 80);

        var position = 0;
        stitcher.AddFrame(Slice(tall, 0, 80));
        foreach (var delta in new[] { 13, 27, 5, 30, 22, 18, 25, 30, 30, 20 })
        {
            position += delta;
            var result = stitcher.AddFrame(Slice(tall, position, 80));
            Assert.Equal(PanoramicFrameStatus.Appended, result.Status);
            Assert.Equal(delta, result.AppendedRows);
        }

        var (bgra, height) = stitcher.GetResult();
        Assert.Equal(position + 80, height);
        Assert.True(bgra.AsSpan(0, height * W * 4).SequenceEqual(tall.AsSpan(0, height * W * 4)));
    }

    [Fact]
    public void AddFrame_WithPixelNoise_StillAligns()
    {
        // must tolerate antialiasing/ClearType, exact hash matching glued garbage before
        var tall = MakeTallImage(200);
        var rng = new Random(7);
        var stitcher = new PanoramicStitcher(W, 80);

        stitcher.AddFrame(Slice(tall, 0, 80, noise: 3, rng));
        var r1 = stitcher.AddFrame(Slice(tall, 25, 80, noise: 3, rng));
        var r2 = stitcher.AddFrame(Slice(tall, 50, 80, noise: 3, rng));

        Assert.Equal(PanoramicFrameStatus.Appended, r1.Status);
        Assert.Equal(25, r1.AppendedRows);
        Assert.Equal(PanoramicFrameStatus.Appended, r2.Status);
        Assert.Equal(25, r2.AppendedRows);
    }

    [Fact]
    public void AddFrame_NoScroll_ReturnsNoMovement()
    {
        var tall = MakeTallImage(160);
        var stitcher = new PanoramicStitcher(W, 80);

        stitcher.AddFrame(Slice(tall, 0, 80));
        var result = stitcher.AddFrame(Slice(tall, 0, 80));

        Assert.Equal(PanoramicFrameStatus.NoMovement, result.Status);
        Assert.Equal(80, stitcher.TotalRows);
    }

    [Fact]
    public void AddFrame_GarbageFrame_IsDiscarded_NeverGlued()
    {
        // regression: an unrelated frame must never get glued
        var tall = MakeTallImage(160);
        var stitcher = new PanoramicStitcher(W, 80);
        stitcher.AddFrame(Slice(tall, 0, 80));

        var garbage = new byte[80 * W * 4];
        new Random(99).NextBytes(garbage);
        var result = stitcher.AddFrame(garbage);

        Assert.Equal(PanoramicFrameStatus.LowConfidence, result.Status);
        Assert.Equal(80, stitcher.TotalRows); // nothing appended
        Assert.Equal(1, stitcher.FramesDiscarded);
    }

    [Fact]
    public void AddFrame_ScrollTooFast_IsDiscarded()
    {
        // delta bigger than the search window (half frame): discard, don't guess
        var tall = MakeTallImage(400);
        var stitcher = new PanoramicStitcher(W, 80);
        stitcher.AddFrame(Slice(tall, 0, 80));

        var result = stitcher.AddFrame(Slice(tall, 79, 80)); // rolou quase uma tela inteira

        Assert.Equal(PanoramicFrameStatus.LowConfidence, result.Status);
        Assert.Equal(80, stitcher.TotalRows);
    }

    [Fact]
    public void AddFrame_StickyFooter_AppearsOnce()
    {
        var tall = MakeTallImage(240);
        var stitcher = new PanoramicStitcher(W, 90);

        byte[] WithFooter(int start)
        {
            var frame = Slice(tall, start, 90);
            for (var y = 80; y < 90; y++)
            for (var x = 0; x < W; x++)
            {
                var o = (y * W + x) * 4;
                frame[o] = 180;
                frame[o + 1] = 180;
                frame[o + 2] = 180;
                frame[o + 3] = 255;
            }
            return frame;
        }

        stitcher.AddFrame(WithFooter(0));
        stitcher.AddFrame(WithFooter(20));
        stitcher.AddFrame(WithFooter(40));

        var (result, height) = stitcher.GetResult();
        var stride = W * 4;
        var grayRows = 0;
        for (var y = 0; y < height; y++)
        {
            if (result[y * stride] == 180 && result[y * stride + 1] == 180)
                grayRows++;
        }
        Assert.Equal(10, grayRows);                      // footer once (10 rows)
        Assert.Equal(180, result[(height - 1) * stride]); // at the very end
    }

    [Fact]
    public void AddFrame_RecoversAfterDiscardedFrame()
    {
        // user scrolled too fast (lost a frame), then settled and kept going
        var tall = MakeTallImage(400);
        var stitcher = new PanoramicStitcher(W, 80);

        stitcher.AddFrame(Slice(tall, 0, 80));
        Assert.Equal(PanoramicFrameStatus.LowConfidence, stitcher.AddFrame(Slice(tall, 79, 80)).Status);
        // next frame aligns with the previously discarded one (kept as reference)
        var result = stitcher.AddFrame(Slice(tall, 99, 80));
        Assert.Equal(PanoramicFrameStatus.Appended, result.Status);
        Assert.Equal(20, result.AppendedRows);
    }

    [Fact]
    public void AddFrame_MemoryLimit_ReturnsLimitReached_ContentPreserved()
    {
        // sem teto artificial: o guarda de memoria conclui em vez de descartar
        var tall = MakeTallImage(400);
        // limit around 100 rows: 100 * W * 4 bytes
        var stitcher = new PanoramicStitcher(W, 80, maxMemoryBytes: 100L * W * 4);

        stitcher.AddFrame(Slice(tall, 0, 80));
        stitcher.AddFrame(Slice(tall, 30, 80)); // 110 rows > 100

        var result = stitcher.AddFrame(Slice(tall, 60, 80));
        Assert.Equal(PanoramicFrameStatus.LimitReached, result.Status);

        var (_, height) = stitcher.GetResult();
        Assert.Equal(110, height); // content up to the limit is preserved
    }
}
