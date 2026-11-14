using Klip.Core.Common;

namespace Klip.Core.Tests;

public class IdleRecycleTests
{
    private static readonly TimeSpan LongUptime = TimeSpan.FromHours(10);

    [Fact]
    public void ShouldRecycle_IdlePastThreshold_True()
    {
        Assert.True(IdleRecycle.ShouldRecycle(true, false, LongUptime, TimeSpan.FromHours(4), busy: false));
    }

    [Fact]
    public void ShouldRecycle_IdleBelowThreshold_False()
    {
        Assert.False(IdleRecycle.ShouldRecycle(true, false, LongUptime, TimeSpan.FromHours(3.9), busy: false));
    }

    [Fact]
    public void ShouldRecycle_ResumeUsesTheShorterSleepThreshold()
    {
        Assert.True(IdleRecycle.ShouldRecycle(true, true, LongUptime, TimeSpan.FromHours(1), busy: false));
        Assert.False(IdleRecycle.ShouldRecycle(true, true, LongUptime, TimeSpan.FromMinutes(50), busy: false));
    }

    [Fact]
    public void ShouldRecycle_Busy_False()
    {
        Assert.False(IdleRecycle.ShouldRecycle(true, false, LongUptime, TimeSpan.FromHours(8), busy: true));
    }

    [Fact]
    public void ShouldRecycle_Disabled_False()
    {
        Assert.False(IdleRecycle.ShouldRecycle(false, false, LongUptime, TimeSpan.FromHours(8), busy: false));
    }

    [Fact]
    public void ShouldRecycle_FreshProcess_False()
    {
        Assert.False(IdleRecycle.ShouldRecycle(true, true, TimeSpan.FromMinutes(30), TimeSpan.FromHours(8), busy: false));
    }

    [Fact]
    public void Args_RoundTrip()
    {
        var args = IdleRecycle.BuildArgs(4242, 987654321u, verbose: true);

        Assert.Contains("--minimized", args);
        Assert.Contains("--verbose", args);
        Assert.True(IdleRecycle.TryParseArgs(args, out var pid, out var seq));
        Assert.Equal(4242, pid);
        Assert.Equal(987654321u, seq);
    }

    [Fact]
    public void TryParseArgs_NoFlagOrBadValues_False()
    {
        Assert.False(IdleRecycle.TryParseArgs(["--minimized"], out _, out _));
        Assert.False(IdleRecycle.TryParseArgs(["--recycled", "abc", "1"], out _, out _));
        Assert.False(IdleRecycle.TryParseArgs(["--recycled", "12"], out _, out _));
        Assert.False(IdleRecycle.TryParseArgs(["--recycled", "0", "1"], out _, out _));
    }
}
