using System.Diagnostics;
using Klip.Core.Input;

namespace Klip.Core.Tests.Input;

/// <summary>RF-P1.01/RF-P1.02: fila SPSC lock-free do hook, com descarte contabilizado.</summary>
public class InputEventRingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(-4)]
    public void Constructor_RejectsInvalidCapacity(int capacity) =>
        Assert.Throws<ArgumentException>(() => new InputEventRing(capacity));

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(1024)]
    public void Constructor_AcceptsPowersOfTwo(int capacity)
    {
        var ring = new InputEventRing(capacity);
        Assert.Equal(capacity, ring.Capacity);
        Assert.Equal(0, ring.Count);
        Assert.Equal(0, ring.DroppedCount);
        Assert.Equal(0, ring.EnqueuedCount);
    }

    [Fact]
    public void TryDequeue_OnEmptyRing_ReturnsFalse()
    {
        var ring = new InputEventRing(4);

        Assert.False(ring.TryDequeue(out var e));
        Assert.Equal(default, e);
    }

    [Fact]
    public void TryEnqueue_ThenTryDequeue_PreservesFifoOrderAndAllFields()
    {
        var ring = new InputEventRing(8);

        Assert.True(ring.TryEnqueue(0x0100, 10, 11, 1000));
        Assert.True(ring.TryEnqueue(0x0201, 20, 21, 2000));
        Assert.True(ring.TryEnqueue(KlipInputMessages.CtrlV, 30, 31, 3000));

        Assert.Equal(3, ring.Count);
        Assert.Equal(3, ring.EnqueuedCount);

        Assert.True(ring.TryDequeue(out var first));
        Assert.Equal(0x0100u, first.Message);
        Assert.Equal(10, first.A);
        Assert.Equal(11, first.B);
        Assert.Equal(1000u, first.Time);

        Assert.True(ring.TryDequeue(out var second));
        Assert.Equal(0x0201u, second.Message);
        Assert.Equal(20, second.A);
        Assert.Equal(21, second.B);
        Assert.Equal(2000u, second.Time);

        Assert.True(ring.TryDequeue(out var third));
        Assert.Equal(KlipInputMessages.CtrlV, third.Message);
        Assert.Equal(30, third.A);
        Assert.Equal(31, third.B);
        Assert.Equal(3000u, third.Time);

        Assert.False(ring.TryDequeue(out _));
        Assert.Equal(0, ring.Count);
    }

    [Fact]
    public void TryEnqueue_WhenFull_DropsExcessAndKeepsEarlierItems()
    {
        var ring = new InputEventRing(8);
        const int Excess = 5;

        for (int i = 0; i < ring.Capacity; i++)
            Assert.True(ring.TryEnqueue(1, i, i, (uint)i));

        for (int i = 0; i < Excess; i++)
            Assert.False(ring.TryEnqueue(1, 1000 + i, 0, 0));

        Assert.Equal(Excess, ring.DroppedCount);
        Assert.Equal(ring.Capacity, ring.EnqueuedCount);
        Assert.Equal(ring.Capacity, ring.Count);

        // Politica e descartar o novo, nunca sobrescrever o que ja estava na fila.
        for (int i = 0; i < ring.Capacity; i++)
        {
            Assert.True(ring.TryDequeue(out var e));
            Assert.Equal(i, e.A);
            Assert.Equal(i, e.B);
            Assert.Equal((uint)i, e.Time);
        }

        Assert.False(ring.TryDequeue(out _));
    }

    [Fact]
    public void TryEnqueue_AfterFullDrain_WrapsAroundAndKeepsOrder()
    {
        var ring = new InputEventRing(4);

        for (int round = 0; round < 5; round++)
        {
            for (int i = 0; i < ring.Capacity; i++)
                Assert.True(ring.TryEnqueue(7, round * 100 + i, 0, 0));

            for (int i = 0; i < ring.Capacity; i++)
            {
                Assert.True(ring.TryDequeue(out var e));
                Assert.Equal(round * 100 + i, e.A);
            }

            Assert.False(ring.TryDequeue(out _));
        }

        Assert.Equal(0, ring.DroppedCount);
        Assert.Equal(20, ring.EnqueuedCount);
    }

    [Fact]
    public void WaitForWork_WithoutItems_TimesOutAndReturnsFalse()
    {
        var ring = new InputEventRing(4);

        Assert.False(ring.WaitForWork(20));
    }

    [Fact]
    public void WaitForWork_AfterEnqueue_ReturnsTrue()
    {
        var ring = new InputEventRing(4);
        ring.TryEnqueue(1, 2, 3, 4);

        Assert.True(ring.WaitForWork(1000));
    }

    [Fact]
    public void Signal_WakesConsumerWithoutItems()
    {
        var ring = new InputEventRing(4);
        ring.Signal();

        Assert.True(ring.WaitForWork(1000));
        Assert.Equal(0, ring.Count);
    }

    [Fact]
    public void Reset_EmptiesRingButKeepsCumulativeCounters()
    {
        var ring = new InputEventRing(2);
        ring.TryEnqueue(1, 1, 1, 1);
        ring.TryEnqueue(1, 2, 2, 2);
        Assert.False(ring.TryEnqueue(1, 3, 3, 3));

        ring.Reset();

        Assert.Equal(0, ring.Count);
        Assert.False(ring.TryDequeue(out _));
        Assert.Equal(2, ring.EnqueuedCount);
        Assert.Equal(1, ring.DroppedCount);

        Assert.True(ring.TryEnqueue(1, 42, 0, 0));
        Assert.True(ring.TryDequeue(out var e));
        Assert.Equal(42, e.A);
    }

    [Fact]
    public async Task SingleProducerSingleConsumer_PreservesOrderAndAccountsForDrops()
    {
        const int Total = 50_000;
        var ring = new InputEventRing(64);

        var producer = Task.Run(() =>
        {
            for (int i = 0; i < Total; i++)
                ring.TryEnqueue(0x0100, i, 0, (uint)i);
        });

        int received = 0;
        int last = -1;
        var deadline = Stopwatch.StartNew();

        while (true)
        {
            while (ring.TryDequeue(out var e))
            {
                // Descartes so criam buracos na sequencia; a ordem relativa nunca inverte.
                Assert.True(e.A > last, $"Fora de ordem: {e.A} apos {last}");
                last = e.A;
                received++;
            }

            if (producer.IsCompleted && received + ring.DroppedCount == Total)
                break;

            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(10),
                $"Timeout: recebidos={received}, descartados={ring.DroppedCount}");

            ring.WaitForWork(5);
        }

        await producer;

        Assert.Equal(Total, received + ring.DroppedCount);
        Assert.Equal(Total, ring.EnqueuedCount + ring.DroppedCount);
        Assert.Equal(received, (int)ring.EnqueuedCount);
        Assert.True(last < Total);
    }
}
