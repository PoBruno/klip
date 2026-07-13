using Klip.Core.Media.Gif;

namespace Klip.Core.Tests.Media.Gif;

/// <summary>
/// Round-trip do encoder completo (RF-F4.07, RF-F4.08, RF-F4.11): encoda,
/// decoda com o decoder de referencia e compara pixels, delays,
/// sub-retangulos e loop.
/// </summary>
public class GifEncoderRoundTripTests
{
    private static readonly GifEncoder Encoder = new();

    private static GifReferenceDecoder.DecodedGif EncodeAndDecode(
        IReadOnlyList<GifFrameSource> frames, GifEncodeOptions? options = null)
    {
        using var stream = new MemoryStream();
        Encoder.Encode(stream, frames, options ?? new GifEncodeOptions());
        return GifReferenceDecoder.Decode(stream.ToArray());
    }

    [Fact]
    public void SingleFrame_FewColors_IsLosslessAndFullRect()
    {
        var bgra = GifTestUtil.MakeBgra(64, 48, (x, y) => ((byte)(x / 16 * 60), (byte)(y / 16 * 80), 200));
        var decoded = EncodeAndDecode([new GifFrameSource(bgra, 64, 48, 100)]);

        Assert.Equal(64, decoded.Width);
        Assert.Equal(48, decoded.Height);
        var frame = Assert.Single(decoded.Frames);
        Assert.Equal(GifTestUtil.ToRgb(bgra), frame.Canvas); // CA-F4.3: bit a bit
        Assert.Equal(new DirtyRect(0, 0, 64, 48), new DirtyRect(frame.X, frame.Y, frame.Width, frame.Height));
        Assert.False(frame.HasTransparency); // primeiro frame sem transparencia
        Assert.Equal(10, frame.DelayCs);
    }

    [Fact]
    public void SingleFrame_Exactly256Colors_IsLossless()
    {
        // 16x16 = 256 pixels, todos distintos: limite exato do caminho lossless.
        var bgra = GifTestUtil.MakeBgra(16, 16, (x, y) => ((byte)(x * 16), (byte)(y * 16), (byte)(x + y)));
        var decoded = EncodeAndDecode([new GifFrameSource(bgra, 16, 16, 50)]);

        var frame = Assert.Single(decoded.Frames);
        Assert.Equal(GifTestUtil.ToRgb(bgra), frame.Canvas);
    }

    [Fact]
    public void MultiFrame_MovingSquare_ReconstructsEveryFramePixelPerfect()
    {
        byte[] MakeFrame(int squareX) => GifTestUtil.MakeBgra(80, 60, (x, y) =>
            x >= squareX && x < squareX + 10 && y is >= 10 and < 20
                ? ((byte)220, (byte)40, (byte)40)
                : ((x + y) % 2 == 0 ? ((byte)30, (byte)30, (byte)30) : ((byte)45, (byte)45, (byte)45)));

        byte[][] sources = [MakeFrame(10), MakeFrame(14), MakeFrame(20)];
        var frames = sources.Select(s => new GifFrameSource(s, 80, 60, 100)).ToArray();

        var decoded = EncodeAndDecode(frames);

        Assert.Equal(3, decoded.Frames.Count);
        for (var i = 0; i < 3; i++)
            Assert.Equal(GifTestUtil.ToRgb(sources[i]), decoded.Frames[i].Canvas);

        // Delta frames usam transparencia + sub-retangulo (trio do screencast).
        Assert.True(decoded.Frames[1].HasTransparency);
        Assert.Equal(new DirtyRect(10, 10, 14, 10),
            new DirtyRect(decoded.Frames[1].X, decoded.Frames[1].Y, decoded.Frames[1].Width, decoded.Frames[1].Height));
    }

    [Fact]
    public void DirtyRect_10x10Change_EmitsSmallImageDescriptor()
    {
        var first = GifTestUtil.MakeBgra(100, 100, (_, _) => (10, 10, 10));
        var second = GifTestUtil.MakeBgra(100, 100, (x, y) =>
            x is >= 20 and < 30 && y is >= 30 and < 40 ? ((byte)250, (byte)250, (byte)0) : ((byte)10, (byte)10, (byte)10));

        var decoded = EncodeAndDecode(
        [
            new GifFrameSource(first, 100, 100, 100),
            new GifFrameSource(second, 100, 100, 100),
        ]);

        Assert.Equal(2, decoded.Frames.Count);
        Assert.Equal(new DirtyRect(20, 30, 10, 10),
            new DirtyRect(decoded.Frames[1].X, decoded.Frames[1].Y, decoded.Frames[1].Width, decoded.Frames[1].Height));
        Assert.Equal(GifTestUtil.ToRgb(second), decoded.Frames[1].Canvas);
    }

    [Fact]
    public void Over256Colors_EveryPixelMapsToNearestPaletteColor()
    {
        // 64x64 com 4096 cores distintas -> median cut para 256 exatas
        // (frame unico: sem slot transparente, GCT de 256 sem padding).
        var bgra = GifTestUtil.MakeBgra(64, 64, (x, y) => ((byte)(x * 4), (byte)(y * 4), (byte)(x + y)));
        var decoded = EncodeAndDecode([new GifFrameSource(bgra, 64, 64, 100)]);

        Assert.Equal(256, decoded.GlobalPalette.Length);
        var frame = Assert.Single(decoded.Frames);

        var source = GifTestUtil.ToRgb(bgra);
        for (var i = 0; i < source.Length; i++)
        {
            var expected = GifTestUtil.NearestPaletteColor(source[i], decoded.GlobalPalette);
            Assert.Equal(expected, frame.Canvas[i]);
        }
    }

    [Fact]
    public void IdenticalFramesInList_AreFoldedWithAccumulatedDelay()
    {
        var a = GifTestUtil.MakeBgra(20, 20, (_, _) => (1, 2, 3));
        var b = GifTestUtil.MakeBgra(20, 20, (_, _) => (200, 100, 50));

        var decoded = EncodeAndDecode(
        [
            new GifFrameSource(a, 20, 20, 100),
            new GifFrameSource((byte[])a.Clone(), 20, 20, 50),
            new GifFrameSource(b, 20, 20, 70),
        ]);

        // CA-F4.2: frame parado nao e reemitido e a duracao total se mantem.
        Assert.Equal(2, decoded.Frames.Count);
        Assert.Equal(15, decoded.Frames[0].DelayCs); // 100 + 50 ms
        Assert.Equal(7, decoded.Frames[1].DelayCs);
    }

    [Fact]
    public void DiffTolerance_FoldsCodecNoiseFrames()
    {
        // RF-F4.10: ruido de +/-1 por canal nao pode quebrar o delta encoding.
        var clean = GifTestUtil.MakeBgra(24, 24, (_, _) => (100, 100, 100));
        var noisy = GifTestUtil.MakeBgra(24, 24, (x, y) => ((byte)(100 + (x + y) % 2), 100, (byte)(101 - x % 2)));

        var options = new GifEncodeOptions { DiffTolerance = 5 };
        var decoded = EncodeAndDecode(
        [
            new GifFrameSource(clean, 24, 24, 100),
            new GifFrameSource(noisy, 24, 24, 50),
        ], options);

        var frame = Assert.Single(decoded.Frames);
        Assert.Equal(15, frame.DelayCs);
        Assert.Equal(GifTestUtil.ToRgb(clean), frame.Canvas);
    }

    [Fact]
    public void Delay_15FpsBecomes7Centiseconds()
    {
        // 15 fps nao existe em GIF: 1000/15 = 66,7 ms -> 7 cs (14,3 fps efetivo).
        var a = GifTestUtil.MakeBgra(8, 8, (_, _) => (0, 0, 0));
        var b = GifTestUtil.MakeBgra(8, 8, (_, _) => (255, 255, 255));

        var decoded = EncodeAndDecode(
        [
            new GifFrameSource(a, 8, 8, 67),
            new GifFrameSource(b, 8, 8, 66),
        ]);

        Assert.Equal(7, decoded.Frames[0].DelayCs);
        Assert.Equal(7, decoded.Frames[1].DelayCs);
    }

    [Fact]
    public void Delay_NeverBelow2Centiseconds()
    {
        var a = GifTestUtil.MakeBgra(8, 8, (_, _) => (0, 0, 0));
        var b = GifTestUtil.MakeBgra(8, 8, (_, _) => (255, 255, 255));

        var decoded = EncodeAndDecode(
        [
            new GifFrameSource(a, 8, 8, 0),
            new GifFrameSource(b, 8, 8, 5),
        ]);

        // Players clampam delay < 2 cs; o encoder nunca emite abaixo disso.
        Assert.All(decoded.Frames, f => Assert.True(f.DelayCs >= 2, $"Delay {f.DelayCs} cs < 2"));
    }

    [Fact]
    public void LoopForever_EmitsNetscapeExtensionWithLoopZero()
    {
        var bgra = GifTestUtil.MakeBgra(8, 8, (_, _) => (9, 9, 9));
        var frames = new[] { new GifFrameSource(bgra, 8, 8, 100) };

        var looping = EncodeAndDecode(frames, new GifEncodeOptions { LoopForever = true });
        Assert.Equal(0, looping.LoopCount);

        var single = EncodeAndDecode(frames, new GifEncodeOptions { LoopForever = false });
        Assert.Null(single.LoopCount);
    }

    [Fact]
    public void BayerDithering_OnQuantizedContent_DecodesToValidGif()
    {
        var gradient = GifTestUtil.MakeBgra(64, 64, (x, y) => ((byte)(x * 4), (byte)(y * 4), (byte)(255 - x * 2)));
        var second = GifTestUtil.MakeBgra(64, 64, (x, y) => ((byte)(y * 4), (byte)(x * 4), (byte)(255 - y * 2)));

        var decoded = EncodeAndDecode(
        [
            new GifFrameSource(gradient, 64, 64, 100),
            new GifFrameSource(second, 64, 64, 100),
        ], new GifEncodeOptions { Dithering = GifDithering.Bayer8x8 });

        Assert.Equal(2, decoded.Frames.Count);
        // Dithering muda os indices escolhidos, mas todo pixel decodado deve
        // vir da paleta global.
        Assert.All(decoded.Frames[0].Canvas, c => Assert.Contains(c, decoded.GlobalPalette));
    }

    [Fact]
    public void FrameBufferToEncoder_EndToEnd_WithSpill()
    {
        var spillDirectory = Path.Combine(Path.GetTempPath(), "klip-gif-e2e-" + Guid.NewGuid().ToString("N"));
        try
        {
            const int frameBytes = 32 * 32 * 4;
            byte[][] sources =
            [
                GifTestUtil.MakeBgra(32, 32, (_, _) => (10, 20, 30)),
                GifTestUtil.MakeBgra(32, 32, (x, _) => (x < 8 ? ((byte)200, (byte)10, (byte)10) : ((byte)10, (byte)20, (byte)30))),
                GifTestUtil.MakeBgra(32, 32, (_, y) => (y < 8 ? ((byte)10, (byte)200, (byte)10) : ((byte)10, (byte)20, (byte)30))),
            ];

            using var buffer = new GifFrameBuffer(frameBytes, spillDirectory); // so 1 frame em RAM
            foreach (var source in sources)
            {
                Assert.True(buffer.Add(source, 32, 32, 100));
                Assert.False(buffer.Add(source, 32, 32, 20)); // dedupe na ingestao
            }

            var decoded = EncodeAndDecode(buffer.Snapshot());

            Assert.Equal(3, decoded.Frames.Count);
            for (var i = 0; i < 3; i++)
            {
                Assert.Equal(GifTestUtil.ToRgb(sources[i]), decoded.Frames[i].Canvas);
                Assert.Equal(12, decoded.Frames[i].DelayCs); // 100 + 20 ms
            }
        }
        finally
        {
            if (Directory.Exists(spillDirectory))
                Directory.Delete(spillDirectory, recursive: true);
        }
    }

    [Fact]
    public void Encode_EmptyFrameList_Throws()
    {
        using var stream = new MemoryStream();
        Assert.Throws<ArgumentException>(() => Encoder.Encode(stream, [], new GifEncodeOptions()));
    }

    [Fact]
    public void Encode_MismatchedDimensions_Throws()
    {
        using var stream = new MemoryStream();
        var frames = new[]
        {
            new GifFrameSource(new byte[8 * 8 * 4], 8, 8, 10),
            new GifFrameSource(new byte[4 * 4 * 4], 4, 4, 10),
        };
        Assert.Throws<ArgumentException>(() => Encoder.Encode(stream, frames, new GifEncodeOptions()));
    }
}
