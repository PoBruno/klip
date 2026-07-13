using Klip.Core.Media.Editing;

namespace Klip.Core.Tests.Media.Editing;

public class FfmpegProgressParserTests
{
    [Fact]
    public void ParsesTimeFromTypicalProgressLine()
    {
        var line = "frame=  360 fps=120 q=28.0 size=    1024KiB time=00:01:23.45 bitrate= 100.5kbits/s speed=4.1x";
        Assert.True(FfmpegProgressParser.TryParseTime(line, out var time));
        Assert.Equal(TimeSpan.FromSeconds(83.45).TotalSeconds, time.TotalSeconds, precision: 3);
    }

    [Fact]
    public void ParsesHoursAndWholeSeconds()
    {
        Assert.True(FfmpegProgressParser.TryParseTime("time=01:02:03 bitrate=", out var time));
        Assert.Equal(new TimeSpan(1, 2, 3), time);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ffmpeg version 7.1 Copyright (c) 2000-2024")]
    [InlineData("size=N/A time=N/A bitrate=N/A")]
    public void RejectsLinesWithoutTime(string? line)
    {
        Assert.False(FfmpegProgressParser.TryParseTime(line, out _));
    }

    [Fact]
    public void FractionClampsToUnitRange()
    {
        Assert.Equal(0.5, FfmpegProgressParser.Fraction(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)), precision: 6);
        Assert.Equal(1.0, FfmpegProgressParser.Fraction(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10)));
        Assert.Equal(0.0, FfmpegProgressParser.Fraction(TimeSpan.FromSeconds(5), TimeSpan.Zero));
    }
}
