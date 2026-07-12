using Klip.Core.Media.Gif;

namespace Klip.Core.Tests.Media.Gif;

/// <summary>Retencao com dedupe e spill em disco (RF-F4.04, RF-F4.06).</summary>
public sealed class GifFrameBufferTests : IDisposable
{
    private readonly string _spillDirectory = Path.Combine(Path.GetTempPath(), "klip-gif-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_spillDirectory))
            Directory.Delete(_spillDirectory, recursive: true);
    }

    private static byte[] SolidFrame(int width, int height, byte value)
        => GifTestUtil.MakeBgra(width, height, (_, _) => (value, value, value));

    [Fact]
    public void Add_IdenticalFrame_DedupesAndAccumulatesDelay()
    {
        using var buffer = new GifFrameBuffer(long.MaxValue, _spillDirectory);
        var frame = SolidFrame(16, 16, 50);

        Assert.True(buffer.Add(frame, 16, 16, 100));
        Assert.False(buffer.Add(frame, 16, 16, 50));
        Assert.False(buffer.Add(frame, 16, 16, 25));

        Assert.Equal(1, buffer.Count);
        var snapshot = buffer.Snapshot();
        // CA-F4.2: duracao total preservada no frame retido.
        Assert.Equal(175, snapshot[0].DelayMs);
    }

    [Fact]
    public void Add_DistinctFrames_AllKeptInOrder()
    {
        using var buffer = new GifFrameBuffer(long.MaxValue, _spillDirectory);
        buffer.Add(SolidFrame(8, 8, 1), 8, 8, 10);
        buffer.Add(SolidFrame(8, 8, 2), 8, 8, 20);
        buffer.Add(SolidFrame(8, 8, 3), 8, 8, 30);

        Assert.Equal(3, buffer.Count);
        var snapshot = buffer.Snapshot();
        Assert.Equal(10, snapshot[0].DelayMs);
        Assert.Equal(30, snapshot[2].DelayMs);
        Assert.Equal(SolidFrame(8, 8, 2), snapshot[1].Bgra);
    }

    [Fact]
    public void Add_AboveRamCeiling_SpillsToDisk()
    {
        const int frameBytes = 16 * 16 * 4;
        // Teto para exatamente 2 frames em RAM; o resto vai para o disco.
        using var buffer = new GifFrameBuffer(frameBytes * 2, _spillDirectory);

        for (byte i = 1; i <= 5; i++)
            buffer.Add(SolidFrame(16, 16, i), 16, 16, 33);

        Assert.Equal(5, buffer.Count);
        Assert.True(buffer.EstimatedBytes <= frameBytes * 2, $"RAM acima do teto: {buffer.EstimatedBytes}");
        Assert.Equal(3, Directory.GetFiles(_spillDirectory).Length);

        // Snapshot reproduz todos os frames, inclusive os do spill.
        var snapshot = buffer.Snapshot();
        for (byte i = 1; i <= 5; i++)
            Assert.Equal(SolidFrame(16, 16, i), snapshot[i - 1].Bgra);
    }

    [Fact]
    public void Dispose_DeletesSpillFiles()
    {
        const int frameBytes = 8 * 8 * 4;
        var buffer = new GifFrameBuffer(frameBytes, _spillDirectory);
        buffer.Add(SolidFrame(8, 8, 1), 8, 8, 10);
        buffer.Add(SolidFrame(8, 8, 2), 8, 8, 10);
        buffer.Add(SolidFrame(8, 8, 3), 8, 8, 10);
        Assert.NotEmpty(Directory.GetFiles(_spillDirectory));

        buffer.Dispose();
        Assert.Empty(Directory.GetFiles(_spillDirectory));
    }

    [Fact]
    public void Add_DedupeAfterSpill_StillWorks()
    {
        const int frameBytes = 8 * 8 * 4;
        using var buffer = new GifFrameBuffer(frameBytes, _spillDirectory);
        buffer.Add(SolidFrame(8, 8, 1), 8, 8, 10);
        buffer.Add(SolidFrame(8, 8, 2), 8, 8, 10); // este vai para o spill
        Assert.False(buffer.Add(SolidFrame(8, 8, 2), 8, 8, 15));

        Assert.Equal(2, buffer.Count);
        Assert.Equal(25, buffer.Snapshot()[1].DelayMs);
    }

    [Fact]
    public void Add_AccumulatedDelay_SaturatesAtCap()
    {
        using var buffer = new GifFrameBuffer(long.MaxValue, _spillDirectory);
        var frame = SolidFrame(4, 4, 9);
        buffer.Add(frame, 4, 4, 600_000);
        buffer.Add(frame, 4, 4, 600_000);

        // Cap de 65535 centesimos = 655350 ms (limite do campo de delay).
        Assert.Equal(655_350, buffer.Snapshot()[0].DelayMs);
    }

    [Fact]
    public void Add_MismatchedDimensions_Throws()
    {
        using var buffer = new GifFrameBuffer(long.MaxValue, _spillDirectory);
        buffer.Add(SolidFrame(8, 8, 1), 8, 8, 10);
        Assert.Throws<ArgumentException>(() => buffer.Add(SolidFrame(16, 16, 1), 16, 16, 10));
    }

    [Fact]
    public void Add_WrongBufferLength_Throws()
    {
        using var buffer = new GifFrameBuffer(long.MaxValue, _spillDirectory);
        Assert.Throws<ArgumentException>(() => buffer.Add(new byte[10], 8, 8, 10));
    }
}
