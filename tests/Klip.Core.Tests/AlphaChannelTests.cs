using Klip.Core.Imaging;

namespace Klip.Core.Tests;

public class AlphaChannelTests
{
    [Fact]
    public void IsFullyTransparent_ColorsWithZeroAlpha_True()
    {
        byte[] bgra = [10, 20, 30, 0, 200, 100, 50, 0, 1, 2, 3, 0];
        Assert.True(AlphaChannel.IsFullyTransparent(bgra));
    }

    [Fact]
    public void IsFullyTransparent_OneVisiblePixel_False()
    {
        byte[] bgra = [10, 20, 30, 0, 200, 100, 50, 0, 1, 2, 3, 1];
        Assert.False(AlphaChannel.IsFullyTransparent(bgra));
    }

    [Fact]
    public void IsFullyTransparent_OpaqueImage_False()
    {
        var bgra = new byte[64 * 4];
        for (var i = 3; i < bgra.Length; i += 4)
            bgra[i] = 255;
        Assert.False(AlphaChannel.IsFullyTransparent(bgra));
    }

    [Fact]
    public void IsFullyTransparent_EmptyOrPartialPixel_False()
    {
        Assert.False(AlphaChannel.IsFullyTransparent([]));
        Assert.False(AlphaChannel.IsFullyTransparent([0, 0, 0]));
    }
}
