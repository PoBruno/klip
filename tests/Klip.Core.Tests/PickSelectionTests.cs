using Klip.Core.Clipboard;

namespace Klip.Core.Tests;

public class PickSelectionTests
{
    [Fact]
    public void Toggle_KeepsClickOrderAndRenumbersOnRemove()
    {
        var pick = new PickSelection<long>();
        pick.Toggle(10, PickMode.Join);
        pick.Toggle(20, PickMode.Join);
        pick.Toggle(30, PickMode.Join);
        Assert.Equal(PickMode.Join, pick.Mode);
        Assert.Equal(2, pick.OrderOf(20));

        Assert.Equal(PickChange.Removed, pick.Toggle(20, PickMode.Join));
        Assert.Equal(0, pick.OrderOf(20));
        Assert.Equal(2, pick.OrderOf(30));
    }

    [Fact]
    public void Toggle_WithTheOtherModeConvertsThePick()
    {
        var pick = new PickSelection<long>();
        pick.Toggle(1, PickMode.Join);
        pick.Toggle(2, PickMode.Join);

        pick.Toggle(3, PickMode.Sequence);

        Assert.Equal(PickMode.Sequence, pick.Mode);
        Assert.Equal([1L, 2L, 3L], pick.Keys);
    }

    [Fact]
    public void RemovingTheLastCard_EndsTheMode()
    {
        var pick = new PickSelection<long>();
        pick.Toggle(5, PickMode.Sequence);
        pick.Toggle(5, PickMode.Sequence);
        Assert.True(pick.IsEmpty);
        Assert.Equal(PickMode.None, pick.Mode);
    }

    [Fact]
    public void PastTheLimit_IsRejected()
    {
        var pick = new PickSelection<long>();
        for (var i = 0; i < PickSelection<long>.DefaultMax; i++)
            Assert.Equal(PickChange.Added, pick.Toggle(i, PickMode.Join));

        Assert.Equal(20, pick.Count);
        Assert.Equal(PickChange.Rejected, pick.Toggle(99, PickMode.Join));
        Assert.Equal(20, pick.Count);
        // removing still works at the limit
        Assert.Equal(PickChange.Removed, pick.Toggle(0, PickMode.Join));
    }

    [Fact]
    public void Include_DoesNotUnpick()
    {
        var pick = new PickSelection<long>();
        pick.Include(7, PickMode.Join);
        pick.Include(7, PickMode.Join);
        Assert.Equal(1, pick.Count);
    }

    [Fact]
    public void NoneIsNotAPickMode()
    {
        var pick = new PickSelection<long>();
        Assert.Throws<ArgumentException>(() => pick.Toggle(1, PickMode.None));
    }

    [Fact]
    public void PlanSeries_GroupsRunsOfTextAndKeepsTheRestApart()
    {
        // t = text, i = image
        string[] items = ["t1", "t2", "i1", "t3", "i2", "i3", "t4"];
        var steps = PasteJoin.PlanSeries(items, s => s[0] == 't');

        Assert.Equal(6, steps.Count);
        Assert.Equal(["t1", "t2"], steps[0]);
        Assert.Equal(["i1"], steps[1]);
        Assert.Equal(["t3"], steps[2]);
        Assert.Equal(["i2"], steps[3]);
        Assert.Equal(["i3"], steps[4]);
        Assert.Equal(["t4"], steps[5]);
    }

    [Fact]
    public void PlanSeries_AllTextIsOneStep()
    {
        string[] items = ["t1", "t2", "t3"];
        Assert.Single(PasteJoin.PlanSeries(items, _ => true));
    }
}
