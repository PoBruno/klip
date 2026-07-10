using Klip.Core.Imaging;

namespace Klip.Core.Tests;

public class PixelatorTests
{
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

    private static (byte b, byte g, byte r, byte a) PixelAt(byte[] buf, int w, int x, int y)
    {
        var o = (y * w + x) * 4;
        return (buf[o], buf[o + 1], buf[o + 2], buf[o + 3]);
    }

    [Fact]
    public void Pixelate_SolidImage_StaysTheSame()
    {
        var img = Solid(16, 16, 120, 60, 30);
        Pixelator.Pixelate(img, 16, 16, 4);

        // averaging a flat color gives back the same color
        var (b, g, r, a) = PixelAt(img, 16, 7, 7);
        Assert.Equal(30, b);
        Assert.Equal(60, g);
        Assert.Equal(120, r);
        Assert.Equal(255, a);
    }

    [Fact]
    public void Pixelate_BlockBecomesFlatAverage()
    {
        // 4x4 image, one block. half black, half white -> whole block is mid gray
        var img = new byte[4 * 4 * 4];
        for (var y = 0; y < 4; y++)
        for (var x = 0; x < 4; x++)
        {
            var o = (y * 4 + x) * 4;
            byte v = (byte)(x < 2 ? 0 : 255);
            img[o] = v; img[o + 1] = v; img[o + 2] = v; img[o + 3] = 255;
        }

        Pixelator.Pixelate(img, 4, 4, 4);

        // every pixel should now be the same averaged value (about 127)
        var first = PixelAt(img, 4, 0, 0);
        for (var y = 0; y < 4; y++)
        for (var x = 0; x < 4; x++)
            Assert.Equal(first, PixelAt(img, 4, x, y));
        Assert.InRange(first.b, 126, 128);
    }

    [Fact]
    public void Pixelate_Redaction_DestroysOriginalDetail()
    {
        // a lone bright pixel in a dark block must not survive as itself
        var img = Solid(8, 8, 0, 0, 0);
        var o = (2 * 8 + 2) * 4;
        img[o] = 255; img[o + 1] = 255; img[o + 2] = 255;

        Pixelator.Pixelate(img, 8, 8, 8);

        var (b, _, _, _) = PixelAt(img, 8, 2, 2);
        Assert.NotEqual(255, b); // the white pixel got averaged away
    }

    [Fact]
    public void Pixelate_BlockClampedToOne_DoesNotThrow()
    {
        var img = Solid(4, 4, 10, 20, 30);
        Pixelator.Pixelate(img, 4, 4, 0); // block 0 clamps to 1, image unchanged
        Assert.Equal((30, 20, 10, (byte)255), PixelAt(img, 4, 1, 1));
    }

    [Fact]
    public void Pixelate_BufferTooSmall_Throws()
    {
        Assert.Throws<ArgumentException>(() => Pixelator.Pixelate(new byte[10], 10, 10, 4));
    }
}
