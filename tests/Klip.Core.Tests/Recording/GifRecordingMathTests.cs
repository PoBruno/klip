using Klip.Core.Recording;

namespace Klip.Core.Tests.Recording;

/// <summary>RF-F4.02 / Q-F4.2: cadencia GIF honesta com o limite de centesimos.</summary>
public class GifRecordingMathTests
{
    [Theory]
    [InlineData(10, 10)]
    [InlineData(15, 7)]
    [InlineData(20, 5)]
    [InlineData(50, 2)]
    [InlineData(100, 2)] // clamp: nunca abaixo de 2 cs
    public void DelayCentiseconds_MatchesGifGrid(int fps, int expectedCs) =>
        Assert.Equal(expectedCs, GifRecordingMath.DelayCentiseconds(fps));

    [Theory]
    [InlineData(10, 10.0)]
    [InlineData(20, 20.0)]
    public void EffectiveFps_ExactWhenDelayDivides(int fps, double expected) =>
        Assert.Equal(expected, GifRecordingMath.EffectiveFps(fps), precision: 3);

    [Fact]
    public void EffectiveFps_15_Is14Point3()
    {
        // 15 fps nao existe no formato: delay 7 cs = 14,29 fps efetivo
        Assert.Equal(100.0 / 7.0, GifRecordingMath.EffectiveFps(15), precision: 3);
    }

    [Theory]
    [InlineData(10, 100)]
    [InlineData(15, 70)]
    [InlineData(20, 50)]
    public void FrameDelayMs_IsExactCentisecondMultiple(int fps, int expectedMs) =>
        Assert.Equal(expectedMs, GifRecordingMath.FrameDelayMs(fps));

    [Fact]
    public void DelayCentiseconds_RejectsNonPositiveFps() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => GifRecordingMath.DelayCentiseconds(0));
}
