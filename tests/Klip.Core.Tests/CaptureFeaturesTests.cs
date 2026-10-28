using Klip.Core.Capture;
using Xunit;

namespace Klip.Core.Tests;

public class ColorFormatterTests
{
    [Theory]
    [InlineData("hex", "#FF8000")]
    [InlineData("HEX", "#FF8000")]
    [InlineData("rgb", "rgb(255, 128, 0)")]
    [InlineData("hsl", "hsl(30, 100%, 50%)")]
    [InlineData("", "#FF8000")]
    [InlineData(null, "#FF8000")]
    [InlineData("cmyk", "#FF8000")]
    public void Format_UsesTheChosenFormat(string? format, string expected)
    {
        Assert.Equal(expected, ColorFormatter.Format(255, 128, 0, format));
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 0, 0)]
    [InlineData(255, 255, 255, 0, 0, 100)]
    [InlineData(255, 0, 0, 0, 100, 50)]
    [InlineData(0, 255, 0, 120, 100, 50)]
    [InlineData(0, 0, 255, 240, 100, 50)]
    [InlineData(128, 128, 128, 0, 0, 50)]
    [InlineData(255, 0, 128, 330, 100, 50)]
    public void ToHsl_KnownColors(byte r, byte g, byte b, int h, int s, int l)
    {
        Assert.Equal((h, s, l), ColorFormatter.ToHsl(r, g, b));
    }

    [Fact]
    public void ToHex_IsUpperCaseWithHash()
    {
        Assert.Equal("#0A0B0C", ColorFormatter.ToHex(10, 11, 12));
    }
}

public class SavedCaptureRegionTests
{
    private static readonly ScreenRect Primary = new(0, 0, 1920, 1080);
    private static readonly ScreenRect Left = new(-2560, -200, 2560, 1440);

    [Fact]
    public void RoundTrip_KeepsEverything()
    {
        var saved = new SavedCaptureRegion(new ScreenRect(-2000, -100, 300, 200), Left, 144);
        Assert.True(SavedCaptureRegion.TryParse(saved.Serialize(), out var back));
        Assert.Equal(saved, back);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("0,0,1920,1080|10,10,0,5|96")]
    [InlineData("0,0,1920,1080|10,10,5,5|0")]
    [InlineData("0,0,1920,1080|10,10,5|96")]
    [InlineData("0,0,1920,1080|10,10,5,5")]
    [InlineData("monitor|10,10,5,5|96")]
    public void TryParse_RejectsBadText(string? text)
    {
        Assert.False(SavedCaptureRegion.TryParse(text, out _));
    }

    [Fact]
    public void IsStillValid_SameMonitorAndDpi()
    {
        var saved = new SavedCaptureRegion(new ScreenRect(100, 100, 400, 300), Primary, 96);
        Assert.True(saved.IsStillValid([new MonitorSnapshot(Left, 144), new MonitorSnapshot(Primary, 96)]));
    }

    [Fact]
    public void IsStillValid_FalseWhenDpiChanged()
    {
        var saved = new SavedCaptureRegion(new ScreenRect(100, 100, 400, 300), Primary, 96);
        Assert.False(saved.IsStillValid([new MonitorSnapshot(Primary, 120)]));
    }

    [Fact]
    public void IsStillValid_FalseWhenMonitorGoneOrMoved()
    {
        var saved = new SavedCaptureRegion(new ScreenRect(-2000, 0, 400, 300), Left, 96);
        Assert.False(saved.IsStillValid([new MonitorSnapshot(Primary, 96)]));
        Assert.False(saved.IsStillValid([new MonitorSnapshot(Left with { X = -1920 }, 96)]));
    }

    [Fact]
    public void IsStillValid_FalseWhenRegionSpillsOut()
    {
        var saved = new SavedCaptureRegion(new ScreenRect(1800, 1000, 400, 300), Primary, 96);
        Assert.False(saved.IsStillValid([new MonitorSnapshot(Primary, 96)]));
    }
}

public class MagnifierLayoutTests
{
    [Fact]
    public void Place_BottomRightByDefault()
    {
        Assert.Equal((120.0, 220.0), MagnifierLayout.Place(100, 200, 50, 50, 1000, 1000));
    }

    [Fact]
    public void Place_FlipsNearTheEdges()
    {
        var (x, y) = MagnifierLayout.Place(990, 990, 50, 50, 1000, 1000);
        Assert.Equal(920, x);
        Assert.Equal(920, y);
    }

    [Fact]
    public void Place_NeverNegative()
    {
        var (x, y) = MagnifierLayout.Place(10, 10, 500, 500, 400, 400);
        Assert.True(x >= 0 && y >= 0);
    }

    [Fact]
    public void SourceOrigin_CentersOnThePixel()
    {
        Assert.Equal((93, 193), MagnifierLayout.SourceOrigin(100, 200, 15));
    }
}
