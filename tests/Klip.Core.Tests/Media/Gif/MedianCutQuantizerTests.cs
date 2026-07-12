using Klip.Core.Media.Gif;

namespace Klip.Core.Tests.Media.Gif;

/// <summary>Median cut variante palettegen (RF-F4.07).</summary>
public class MedianCutQuantizerTests
{
    [Fact]
    public void BuildPalette_FitsInMax_ReturnsExactColors()
    {
        var histogram = new Dictionary<uint, long>
        {
            [0xFF0000] = 10,
            [0x00FF00] = 5,
            [0x0000FF] = 1,
            [0x123456] = 100,
        };

        var palette = MedianCutQuantizer.BuildPalette(histogram, 256);

        Assert.Equal(4, palette.Length);
        Assert.Equal([0x0000FFu, 0x00FF00u, 0x123456u, 0xFF0000u], palette);
    }

    [Fact]
    public void BuildPalette_1000DistinctColors_ReducesTo256WithBoundedError()
    {
        var random = new Random(1234);
        var histogram = new Dictionary<uint, long>();
        while (histogram.Count < 1000)
            histogram[(uint)random.Next(0x1000000)] = random.Next(1, 100);

        var palette = MedianCutQuantizer.BuildPalette(histogram, 256);

        Assert.True(palette.Length <= 256);
        Assert.True(palette.Length > 200, $"Paleta subdividiu pouco: {palette.Length} cores");

        // Toda cor original deve ter um representante razoavelmente proximo
        // (erro medio por canal <= 64 seria pessimo; o median cut fica bem
        // abaixo, mas o teste tolera variacao de seed).
        var worst = 0;
        foreach (var color in histogram.Keys)
        {
            var nearest = GifTestUtil.NearestPaletteColor(color, palette);
            worst = Math.Max(worst, GifTestUtil.DistanceSquared(color, nearest));
        }
        Assert.True(worst <= 3 * 64 * 64, $"Pior distancia^2 da quantizacao: {worst}");
    }

    [Fact]
    public void BuildPalette_DominantColorsArePreserved()
    {
        uint[] dominant = [0x1E1E1E, 0xFFFFFF, 0x0078D4, 0xC42B1C, 0x107C10, 0xF2C811, 0x881798, 0x00B7C3];

        var histogram = new Dictionary<uint, long>();
        foreach (var color in dominant)
            histogram[color] = 1_000_000;

        var random = new Random(99);
        while (histogram.Count < 1000)
        {
            var color = (uint)random.Next(0x1000000);
            histogram.TryAdd(color, 1);
        }

        var palette = MedianCutQuantizer.BuildPalette(histogram, 256);

        // Cores dominantes (peso 1M vs 1) devem sobreviver quase exatas: a
        // media ponderada do box delas e puxada para o valor dominante.
        foreach (var color in dominant)
        {
            var nearest = GifTestUtil.NearestPaletteColor(color, palette);
            var distance = GifTestUtil.DistanceSquared(color, nearest);
            Assert.True(distance <= 3 * 4 * 4, $"Cor dominante {color:X6} perdida (dist^2={distance}, nearest={nearest:X6})");
        }
    }

    [Fact]
    public void BuildPalette_SingleColor_ReturnsIt()
    {
        var palette = MedianCutQuantizer.BuildPalette(new Dictionary<uint, long> { [0xABCDEF] = 42 }, 256);
        Assert.Equal([0xABCDEFu], palette);
    }

    [Fact]
    public void BuildPalette_EmptyHistogram_Throws()
    {
        Assert.Throws<ArgumentException>(() => MedianCutQuantizer.BuildPalette(new Dictionary<uint, long>(), 256));
    }

    [Fact]
    public void BuildPalette_IsDeterministic()
    {
        var random = new Random(5);
        var histogram = new Dictionary<uint, long>();
        while (histogram.Count < 500)
            histogram[(uint)random.Next(0x1000000)] = random.Next(1, 50);

        var first = MedianCutQuantizer.BuildPalette(histogram, 128);
        var second = MedianCutQuantizer.BuildPalette(new Dictionary<uint, long>(histogram), 128);
        Assert.Equal(first, second);
    }
}
