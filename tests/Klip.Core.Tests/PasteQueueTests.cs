using Klip.Core.Clipboard;

namespace Klip.Core.Tests;

public class PasteQueueTests
{
    [Fact]
    public void Begin_ClampsTargetTo1Through5()
    {
        var q = new PasteQueue<int>();
        q.Begin(0);
        Assert.Equal(1, q.Target);
        q.Begin(9);
        Assert.Equal(5, q.Target);
        q.Begin(3);
        Assert.Equal(3, q.Target);
    }

    [Fact]
    public void Toggle_AddsInOrder_ReturnsOneBasedPosition()
    {
        // selection order is preserved
        var q = new PasteQueue<int>();
        q.Begin(3);
        Assert.Equal(1, q.Toggle(10));
        Assert.Equal(2, q.Toggle(20));
        Assert.Equal(3, q.Toggle(30));
        Assert.Equal([10, 20, 30], q.Snapshot());
    }

    [Fact]
    public void Toggle_SameItemTwice_RemovesIt_AndReorders()
    {
        var q = new PasteQueue<int>();
        q.Begin(3);
        q.Toggle(10);
        q.Toggle(20);
        q.Toggle(30);

        Assert.Equal(0, q.Toggle(20)); // drops the middle one

        Assert.Equal([10, 30], q.Snapshot());
        Assert.Equal(1, q.OrderOf(10));
        Assert.Equal(2, q.OrderOf(30)); // 30 shifts from 3 to 2
        Assert.Equal(0, q.OrderOf(20));
    }

    [Fact]
    public void Toggle_RejectsWhenFull()
    {
        var q = new PasteQueue<int>();
        q.Begin(2);
        q.Toggle(10);
        q.Toggle(20);
        Assert.True(q.IsFull);
        Assert.Equal(0, q.Toggle(30)); // rejected, queue is full
        Assert.Equal([10, 20], q.Snapshot());
    }

    [Fact]
    public void Consume_AdvancesThroughItemsInOrder()
    {
        // cada Ctrl+V cola o proximo item na ordem
        var q = new PasteQueue<string>();
        q.Begin(3);
        q.Toggle("ALPHA");
        q.Toggle("BRAVO");
        q.Toggle("CHARLIE");

        Assert.Equal("ALPHA", q.Current);
        Assert.Equal(1, q.CursorPosition);

        Assert.True(q.Advance());              // first Ctrl+V pasted ALPHA
        Assert.Equal("BRAVO", q.Current);
        Assert.Equal(2, q.CursorPosition);

        Assert.True(q.Advance());              // second pasted BRAVO
        Assert.Equal("CHARLIE", q.Current);

        Assert.False(q.Advance());             // third pasted CHARLIE, queue drains
        Assert.False(q.HasCurrent);
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        var q = new PasteQueue<int>();
        q.Begin(3);
        q.Toggle(10);
        q.Reset();
        Assert.False(q.IsArmed);
        Assert.Equal(0, q.Count);
        Assert.Empty(q.Snapshot());
    }

    [Fact]
    public void CustomComparer_MatchesByProjectedKey()
    {
        // in the app, VMs compare by Id: different instances, same item
        var q = new PasteQueue<(int id, string name)>();
        var byId = EqualityComparer<(int id, string name)>.Create((a, b) => a.id == b.id, x => x.id);
        q.Begin(2);
        q.Toggle((1, "primeira"), byId);
        Assert.Equal(1, q.OrderOf((1, "instancia-diferente"), byId));
        Assert.Equal(0, q.Toggle((1, "outra"), byId)); // same id, so remove
    }
}
