using Klip.Core.Media.Editing;

namespace Klip.Core.Tests.Media.Editing;

/// <summary>RF-F5.08 / CA-F5.4: decimation preserves total duration.</summary>
public class GifTimelineOpsTests
{
    [Fact]
    public void ReduceFps_Uniform20To10_KeepsEveryOtherFrameAndDoublesDelays()
    {
        var delays = Enumerable.Repeat(50, 20).ToList(); // 20 fps, 1000 ms total

        var result = GifTimelineOps.ReduceFps(delays, targetFps: 10);

        Assert.Equal(10, result.Count);
        Assert.Equal(Enumerable.Range(0, 10).Select(i => i * 2), result.Select(r => r.FrameIndex));
        Assert.All(result, r => Assert.Equal(100, r.DelayMs));
        Assert.Equal(delays.Sum(), result.Sum(r => r.DelayMs)); // duration preserved
    }

    [Fact]
    public void ReduceFps_NonUniformDelays_RedistributesToPreviousKeptFrame()
    {
        var delays = new[] { 30, 40, 50, 80, 20, 100 }; // 320 ms total

        var result = GifTimelineOps.ReduceFps(delays, targetFps: 10); // 100 ms interval

        Assert.Equal([(0, 120), (3, 100), (5, 100)], result);
        Assert.Equal(delays.Sum(), result.Sum(r => r.DelayMs)); // duration preserved
    }

    [Fact]
    public void ReduceFps_TargetAboveSourceFps_KeepsAllFrames()
    {
        var delays = new[] { 100, 100, 100 }; // 10 fps source

        var result = GifTimelineOps.ReduceFps(delays, targetFps: 20);

        Assert.Equal([(0, 100), (1, 100), (2, 100)], result);
    }

    [Fact]
    public void ReduceFps_SingleFrame_IsKeptUnchanged()
    {
        var result = GifTimelineOps.ReduceFps([70], targetFps: 10);
        Assert.Equal([(0, 70)], result);
    }

    [Fact]
    public void ReduceFps_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(GifTimelineOps.ReduceFps([], targetFps: 10));
    }

    [Fact]
    public void ReduceFps_InvalidFps_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GifTimelineOps.ReduceFps([50, 50], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => GifTimelineOps.ReduceFps([50, 50], -5));
    }

    [Fact]
    public void ReduceFps_SumAlwaysPreserved_VariousTargets()
    {
        var delays = new[] { 16, 33, 10, 90, 45, 5, 120, 60, 25, 33 };

        foreach (var fps in new[] { 5, 10, 15, 24, 30, 60 })
        {
            var result = GifTimelineOps.ReduceFps(delays, fps);
            Assert.Equal(delays.Sum(), result.Sum(r => r.DelayMs));
            // indices are strictly increasing and start at frame 0
            Assert.Equal(0, result[0].FrameIndex);
            for (var i = 1; i < result.Count; i++)
                Assert.True(result[i].FrameIndex > result[i - 1].FrameIndex);
        }
    }
}
