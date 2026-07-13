using Klip.Core.Media.Gif;

namespace Klip.Core.Tests.Media.Gif;

/// <summary>Round-trip da compressao LZW propria (RF-F4.08, RF-F4.11).</summary>
public class GifLzwEncoderTests
{
    private static byte[] RoundTrip(byte[] indices, int minCodeSize)
    {
        using var stream = new MemoryStream();
        GifLzwEncoder.Encode(stream, indices, minCodeSize);
        return GifReferenceDecoder.DecodeLzwBlock(stream.ToArray(), indices.Length);
    }

    [Fact]
    public void RoundTrip_SingleIndex()
    {
        byte[] indices = [5];
        Assert.Equal(indices, RoundTrip(indices, 4));
    }

    [Fact]
    public void RoundTrip_LongRun_HighlyCompressible()
    {
        // Run longo forca crescimento de codigo e o caso KwKwK no decoder.
        var indices = new byte[20_000];
        var output = RoundTrip(indices, 2);
        Assert.Equal(indices, output);
    }

    [Fact]
    public void RoundTrip_Alternating()
    {
        var indices = new byte[10_000];
        for (var i = 0; i < indices.Length; i++)
            indices[i] = (byte)(i % 2);
        Assert.Equal(indices, RoundTrip(indices, 2));
    }

    [Fact]
    public void RoundTrip_RandomBytes_ForcesDictionaryReset()
    {
        // 200k bytes aleatorios estouram as 4096 entradas varias vezes:
        // exercita clear code + reset da largura de codigo.
        var random = new Random(42);
        var indices = new byte[200_000];
        random.NextBytes(indices);
        Assert.Equal(indices, RoundTrip(indices, 8));
    }

    [Fact]
    public void RoundTrip_SmallAlphabet_LongInput()
    {
        var random = new Random(7);
        var indices = new byte[50_000];
        for (var i = 0; i < indices.Length; i++)
            indices[i] = (byte)random.Next(4);
        Assert.Equal(indices, RoundTrip(indices, 2));
    }

    [Fact]
    public void RoundTrip_RunsAndSteps_MixedPattern()
    {
        var indices = new List<byte>();
        for (var run = 1; run <= 300; run++)
        {
            for (var i = 0; i < run; i++)
                indices.Add((byte)(run % 16));
        }
        Assert.Equal(indices.ToArray(), RoundTrip([.. indices], 4));
    }

    [Fact]
    public void Encode_CompressesLongRuns()
    {
        var indices = new byte[10_000];
        using var stream = new MemoryStream();
        GifLzwEncoder.Encode(stream, indices, 2);
        // Sanidade de compressao: run de 10k pixels iguais deve encolher muito.
        Assert.True(stream.Length < 1_000, $"LZW nao comprimiu: {stream.Length} bytes");
    }

    [Fact]
    public void Encode_EmptyInput_Throws()
    {
        using var stream = new MemoryStream();
        Assert.Throws<ArgumentException>(() => GifLzwEncoder.Encode(stream, [], 2));
    }
}
