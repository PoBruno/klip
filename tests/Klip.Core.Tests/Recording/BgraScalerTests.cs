using Klip.Core.Recording;

namespace Klip.Core.Tests.Recording;

/// <summary>RF-F4.03: downscale box do frame BGRA antes da retencao.</summary>
public class BgraScalerTests
{
    [Fact]
    public void Downscale_100Percent_ReturnsIdenticalCopy()
    {
        var src = new byte[2 * 2 * 4];
        for (int i = 0; i < src.Length; i++)
            src[i] = (byte)i;

        var (dst, w, h) = BgraScaler.Downscale(src, 2, 2, 100);

        Assert.Equal(2, w);
        Assert.Equal(2, h);
        Assert.Equal(src, dst);
        Assert.NotSame(src, dst); // copia: o chamador pode reter sem alias
    }

    [Fact]
    public void Downscale_50Percent_AveragesBox()
    {
        // 2x2 -> 1x1: media dos 4 pixels, canal a canal
        var src = new byte[]
        {
            0, 0, 0, 255,      100, 100, 100, 255,
            200, 200, 200, 255, 60, 60, 60, 255,
        };

        var (dst, w, h) = BgraScaler.Downscale(src, 2, 2, 50);

        Assert.Equal(1, w);
        Assert.Equal(1, h);
        Assert.Equal((byte)90, dst[0]); // (0+100+200+60)/4
        Assert.Equal((byte)90, dst[1]);
        Assert.Equal((byte)90, dst[2]);
        Assert.Equal((byte)255, dst[3]);
    }

    [Fact]
    public void Downscale_75Percent_ProducesExpectedDimensions()
    {
        var src = new byte[8 * 4 * 4];
        var (_, w, h) = BgraScaler.Downscale(src, 8, 4, 75);
        Assert.Equal(6, w);
        Assert.Equal(3, h);
    }

    [Fact]
    public void Downscale_NeverCollapsesBelow1Px()
    {
        var src = new byte[1 * 1 * 4];
        var (_, w, h) = BgraScaler.Downscale(src, 1, 1, 50);
        Assert.Equal(1, w);
        Assert.Equal(1, h);
    }

    [Fact]
    public void Downscale_RejectsWrongBufferSize() =>
        Assert.Throws<ArgumentException>(() => BgraScaler.Downscale(new byte[3], 1, 1, 50));
}
