using Klip.Core.Media.Gif;
using Klip.Core.Recording;

namespace Klip.Core.Tests.Media.Gif;

/// <summary>
/// Pipeline de ingestao da gravacao GIF (RF-F4.03/RF-F4.04): copia pooled no
/// produtor, worker dedicado e delay real derivado do timestamp CFR.
/// </summary>
public sealed class GifFramePipelineTests : IDisposable
{
    private readonly string _spillDirectory = Path.Combine(Path.GetTempPath(), "klip-gif-pipeline-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_spillDirectory))
            Directory.Delete(_spillDirectory, recursive: true);
    }

    private static byte[] SolidFrame(int width, int height, byte value)
        => GifTestUtil.MakeBgra(width, height, (_, _) => (value, value, value));

    [Fact]
    public async Task Post_ComputesDelaysFromTimestamps()
    {
        using var buffer = new GifFrameBuffer(long.MaxValue, _spillDirectory);
        var pipeline = new GifFramePipeline(buffer, firstFrameDelayMs: 100);

        // grade CFR de ~15 fps: ms arredondados por frame, sem drift acumulado
        pipeline.Post(SolidFrame(8, 8, 1), 8, 8, TimeSpan.Zero);
        pipeline.Post(SolidFrame(8, 8, 2), 8, 8, TimeSpan.FromMilliseconds(66.6667));
        pipeline.Post(SolidFrame(8, 8, 3), 8, 8, TimeSpan.FromMilliseconds(133.3333));
        await pipeline.CompleteAsync();

        var snapshot = buffer.Snapshot();
        Assert.Equal(3, buffer.Count);
        Assert.Equal(100, snapshot[0].DelayMs); // primeiro frame: delay nominal
        Assert.Equal(67, snapshot[1].DelayMs);  // round(66.67) - 0
        Assert.Equal(66, snapshot[2].DelayMs);  // round(133.33) - 67
    }

    [Fact]
    public async Task Post_SkippedTicks_BecomeAccumulatedDelay()
    {
        using var buffer = new GifFrameBuffer(long.MaxValue, _spillDirectory);
        var pipeline = new GifFramePipeline(buffer, firstFrameDelayMs: 67);

        // engine pulou indices da grade sob carga: o proximo frame carrega o
        // tempo real decorrido, preservando a duracao do GIF (CA-F4.2)
        pipeline.Post(SolidFrame(8, 8, 1), 8, 8, TimeSpan.Zero);
        pipeline.Post(SolidFrame(8, 8, 2), 8, 8, TimeSpan.FromMilliseconds(200));
        await pipeline.CompleteAsync();

        Assert.Equal(2, buffer.Count);
        Assert.Equal(200, buffer.Snapshot()[1].DelayMs);
    }

    [Fact]
    public async Task Post_IdenticalFrames_DedupeAccumulatesRealDuration()
    {
        using var buffer = new GifFrameBuffer(long.MaxValue, _spillDirectory);
        var pipeline = new GifFramePipeline(buffer, firstFrameDelayMs: 50);

        var frame = SolidFrame(8, 8, 7);
        pipeline.Post(frame, 8, 8, TimeSpan.Zero);
        pipeline.Post(frame, 8, 8, TimeSpan.FromMilliseconds(100));
        pipeline.Post(frame, 8, 8, TimeSpan.FromMilliseconds(250));
        await pipeline.CompleteAsync();

        // dedupe RF-F4.06: um frame retido com 50 + 100 + 150 ms
        Assert.Equal(1, buffer.Count);
        Assert.Equal(300, buffer.Snapshot()[0].DelayMs);
    }

    [Fact]
    public async Task Post_WithScale_DownscalesLikeBgraScaler()
    {
        using var buffer = new GifFrameBuffer(long.MaxValue, _spillDirectory);
        var pipeline = new GifFramePipeline(buffer, firstFrameDelayMs: 67, scalePercent: 50);

        var source = GifTestUtil.MakeBgra(16, 16, (x, y) => ((byte)(x * 16), (byte)(y * 16), (byte)(x * 8 + y * 8)));
        pipeline.Post(source, 16, 16, TimeSpan.Zero);
        await pipeline.CompleteAsync();

        // RF-F4.03: escala antes da retencao, mesmo filtro box do BgraScaler
        var (expected, w, h) = BgraScaler.Downscale(source, 16, 16, 50);
        Assert.Equal(1, buffer.Count);
        var retained = buffer.Snapshot()[0];
        Assert.Equal(8, w);
        Assert.Equal(8, h);
        Assert.Equal(expected, retained.Bgra);
    }

    [Fact]
    public async Task FrameRetained_ReportsAccumulatedBytes()
    {
        using var buffer = new GifFrameBuffer(long.MaxValue, _spillDirectory);
        var pipeline = new GifFramePipeline(buffer, firstFrameDelayMs: 67);
        var totals = new List<long>();
        pipeline.FrameRetained += totals.Add;

        pipeline.Post(SolidFrame(8, 8, 1), 8, 8, TimeSpan.Zero);
        pipeline.Post(SolidFrame(8, 8, 2), 8, 8, TimeSpan.FromMilliseconds(67));
        pipeline.Post(SolidFrame(8, 8, 2), 8, 8, TimeSpan.FromMilliseconds(133)); // dedupe: nao conta
        await pipeline.CompleteAsync();

        // mesma contagem do teto RF-F4.04 (bytes de pixels retidos)
        Assert.Equal(new long[] { 256, 512 }, totals);
        Assert.Equal(512, pipeline.RetainedBytes);
    }

    [Fact]
    public async Task Post_AfterComplete_IsNoOp()
    {
        using var buffer = new GifFrameBuffer(long.MaxValue, _spillDirectory);
        var pipeline = new GifFramePipeline(buffer, firstFrameDelayMs: 67);
        pipeline.Post(SolidFrame(8, 8, 1), 8, 8, TimeSpan.Zero);
        await pipeline.CompleteAsync();

        pipeline.Post(SolidFrame(8, 8, 2), 8, 8, TimeSpan.FromMilliseconds(67));
        Assert.Equal(1, buffer.Count);
    }

    [Fact]
    public async Task CompleteAsync_PropagatesIngestFailure()
    {
        var buffer = new GifFrameBuffer(long.MaxValue, _spillDirectory);
        var pipeline = new GifFramePipeline(buffer, firstFrameDelayMs: 67);
        buffer.Dispose(); // forca ObjectDisposedException na ingestao

        pipeline.Post(SolidFrame(8, 8, 1), 8, 8, TimeSpan.Zero);
        await Assert.ThrowsAsync<ObjectDisposedException>(pipeline.CompleteAsync);
    }

    [Fact]
    public void Post_WrongBufferLength_Throws()
    {
        using var buffer = new GifFrameBuffer(long.MaxValue, _spillDirectory);
        var pipeline = new GifFramePipeline(buffer, firstFrameDelayMs: 67);
        Assert.Throws<ArgumentException>(() => pipeline.Post(new byte[10], 8, 8, TimeSpan.Zero));
    }
}
