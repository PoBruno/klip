using System.Diagnostics;
using Klip.Core.Diagnostics;

namespace Klip.Core.Tests.Diagnostics;

/// <summary>RF-P1.04: metricas do callback do hook contra o orcamento LowLevelHooksTimeout.</summary>
public class HookMetricsTests
{
    private static double TicksToMicroseconds(long ticks) => ticks * 1_000_000.0 / Stopwatch.Frequency;

    [Fact]
    public void Sample_WithoutAnyRecord_ReturnsZeros()
    {
        var metrics = new HookMetrics();

        var sample = metrics.Sample();

        Assert.Equal(0.0, sample.EventsPerSecond);
        Assert.Equal(0.0, sample.WorstMicroseconds);
        Assert.Equal(0.0, sample.BudgetPercent);
        Assert.Equal(0, sample.TotalCallbacks);
        Assert.Equal(0, sample.Dropped);
    }

    [Fact]
    public void BudgetMilliseconds_DefaultsTo300()
    {
        var metrics = new HookMetrics();

        Assert.Equal(300, metrics.BudgetMilliseconds);
    }

    [Fact]
    public void Record_CountsEveryCallback()
    {
        var metrics = new HookMetrics();

        metrics.Record(10);
        metrics.Record(20);
        metrics.Record(30);

        Assert.Equal(3, metrics.Sample().TotalCallbacks);
    }

    [Fact]
    public void TotalCallbacks_IsCumulativeAcrossSamples()
    {
        var metrics = new HookMetrics();

        metrics.Record(1);
        Assert.Equal(1, metrics.Sample().TotalCallbacks);

        metrics.Record(1);
        metrics.Record(1);
        Assert.Equal(3, metrics.Sample().TotalCallbacks);
    }

    [Fact]
    public void Sample_ReportsWorstCaseAndThenResetsIt()
    {
        var metrics = new HookMetrics();

        metrics.Record(100);
        metrics.Record(500);
        metrics.Record(200);

        var first = metrics.Sample();
        Assert.Equal(TicksToMicroseconds(500), first.WorstMicroseconds, 6);

        // O pior caso e por janela: a amostragem zera para a proxima medicao.
        var second = metrics.Sample();
        Assert.Equal(0.0, second.WorstMicroseconds);
        Assert.Equal(0.0, second.BudgetPercent);
        Assert.Equal(3, second.TotalCallbacks);
    }

    [Fact]
    public void Record_WithSmallerValue_DoesNotLowerWorstCase()
    {
        var metrics = new HookMetrics();

        metrics.Record(900);
        metrics.Record(1);
        metrics.Record(2);

        Assert.Equal(TicksToMicroseconds(900), metrics.Sample().WorstMicroseconds, 6);
    }

    [Fact]
    public void BudgetPercent_FollowsWorstCaseOverBudget()
    {
        var metrics = new HookMetrics { BudgetMilliseconds = 300 };

        long worstTicks = Stopwatch.Frequency / 100; // 10 ms
        metrics.Record(worstTicks);

        var sample = metrics.Sample();

        double expectedMicroseconds = TicksToMicroseconds(worstTicks);
        double expectedPercent = expectedMicroseconds / (300 * 1000.0) * 100.0;

        Assert.Equal(expectedMicroseconds, sample.WorstMicroseconds, 6);
        Assert.Equal(expectedPercent, sample.BudgetPercent, 6);
        Assert.True(Math.Abs(sample.BudgetPercent - 3.3333) < 0.01, $"BudgetPercent inesperado: {sample.BudgetPercent}");
    }

    [Fact]
    public void BudgetPercent_HonorsCustomBudget()
    {
        var metrics = new HookMetrics { BudgetMilliseconds = 1000 };

        long worstTicks = Stopwatch.Frequency / 10; // 100 ms
        metrics.Record(worstTicks);

        Assert.Equal(10.0, metrics.Sample().BudgetPercent, 6);
    }

    [Fact]
    public void RecordDrop_IncrementsDropped()
    {
        var metrics = new HookMetrics();

        metrics.RecordDrop();
        metrics.RecordDrop();

        Assert.Equal(2, metrics.Sample().Dropped);

        metrics.RecordDrop();
        Assert.Equal(3, metrics.Sample().Dropped);
    }

    [Fact]
    public void Sample_ComputesPositiveRateAfterRecords()
    {
        var metrics = new HookMetrics();

        metrics.Record(10);
        metrics.Record(10);
        metrics.Record(10);
        Thread.Sleep(20);

        var sample = metrics.Sample();

        Assert.True(sample.EventsPerSecond > 0.0, $"Taxa deveria ser positiva: {sample.EventsPerSecond}");

        // Janela seguinte sem nenhum Record: taxa volta a zero.
        Thread.Sleep(20);
        Assert.Equal(0.0, metrics.Sample().EventsPerSecond);
    }
}
