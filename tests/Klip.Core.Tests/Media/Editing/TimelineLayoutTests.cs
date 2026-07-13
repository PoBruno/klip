using Klip.Core.Media.Editing;

namespace Klip.Core.Tests.Media.Editing;

public class TimelineLayoutTests
{
    [Fact]
    public void BlocksAreProportionalAndContiguous()
    {
        var blocks = TimelineLayout.ComputeBlocks([10, 30, 60], 1000);

        Assert.Equal(3, blocks.Count);
        Assert.Equal(0, blocks[0].X, precision: 6);
        Assert.Equal(100, blocks[0].Width, precision: 6);
        Assert.Equal(100, blocks[1].X, precision: 6);
        Assert.Equal(300, blocks[1].Width, precision: 6);
        Assert.Equal(400, blocks[2].X, precision: 6);
        Assert.Equal(600, blocks[2].Width, precision: 6);
    }

    [Fact]
    public void EmptyOrDegenerateInputsYieldNoBlocks()
    {
        Assert.Empty(TimelineLayout.ComputeBlocks([], 500));
        Assert.Empty(TimelineLayout.ComputeBlocks([1, 2], 0));
        Assert.Empty(TimelineLayout.ComputeBlocks([0, 0], 500));
    }

    [Fact]
    public void HitTestFindsBlockUnderPixel()
    {
        var blocks = TimelineLayout.ComputeBlocks([50, 50], 200);
        Assert.Equal(0, TimelineLayout.HitTest(blocks, 0));
        Assert.Equal(0, TimelineLayout.HitTest(blocks, 99.9));
        Assert.Equal(1, TimelineLayout.HitTest(blocks, 100));
        Assert.Equal(-1, TimelineLayout.HitTest(blocks, 250));
        Assert.Equal(-1, TimelineLayout.HitTest(blocks, -1));
    }

    [Fact]
    public void UnitsAndPixelsRoundTrip()
    {
        var x = TimelineLayout.UnitsToX(30, 120, 480);
        Assert.Equal(120, x, precision: 6);
        Assert.Equal(30, TimelineLayout.XToUnits(x, 120, 480), precision: 6);
        // clamp nas bordas
        Assert.Equal(0, TimelineLayout.XToUnits(-50, 120, 480));
        Assert.Equal(120, TimelineLayout.XToUnits(9999, 120, 480));
    }

    [Fact]
    public void DropIndexCountsCentersExcludingDraggedBlock()
    {
        // 3 blocos de 100px: centros em 50, 150, 250
        var blocks = TimelineLayout.ComputeBlocks([1, 1, 1], 300);

        // arrastando o bloco 0 para depois do ultimo
        Assert.Equal(2, TimelineLayout.DropIndex(blocks, dragIndex: 0, x: 290));
        // arrastando o bloco 2 para o comeco
        Assert.Equal(0, TimelineLayout.DropIndex(blocks, dragIndex: 2, x: 10));
        // arrastando o bloco 0 para entre 1 e 2
        Assert.Equal(1, TimelineLayout.DropIndex(blocks, dragIndex: 0, x: 200));
        // soltando sobre a propria posicao
        Assert.Equal(0, TimelineLayout.DropIndex(blocks, dragIndex: 0, x: 50));
    }

    [Fact]
    public void PositionedBlocksFollowTimelineStartsLeavingGaps()
    {
        // RF-F5.17: [0-10s] | gap 10-25 | [25-40s] on a 40 s ruler, 400 px wide
        var blocks = TimelineLayout.ComputePositionedBlocks(
            [(0, 10), (25, 15)], totalUnits: 40, totalWidth: 400);

        Assert.Equal(2, blocks.Count);
        Assert.Equal(0, blocks[0].X, precision: 6);
        Assert.Equal(100, blocks[0].Width, precision: 6);
        Assert.Equal(250, blocks[1].X, precision: 6);
        Assert.Equal(150, blocks[1].Width, precision: 6);
    }

    [Fact]
    public void PositionedBlocksDegenerateInputsYieldNoBlocks()
    {
        Assert.Empty(TimelineLayout.ComputePositionedBlocks([], 10, 400));
        Assert.Empty(TimelineLayout.ComputePositionedBlocks([(0, 10)], 0, 400));
        Assert.Empty(TimelineLayout.ComputePositionedBlocks([(0, 10)], 10, 0));
    }

    [Fact]
    public void HitTestGapDetectsGapsWithinTheRuler()
    {
        var blocks = TimelineLayout.ComputePositionedBlocks(
            [(0, 10), (25, 15)], totalUnits: 40, totalWidth: 400);

        Assert.False(TimelineLayout.HitTestGap(blocks, 50, 400));   // on a block
        Assert.True(TimelineLayout.HitTestGap(blocks, 150, 400));   // inside the gap
        Assert.False(TimelineLayout.HitTestGap(blocks, 300, 400));  // second block
        Assert.False(TimelineLayout.HitTestGap(blocks, 400, 400));  // past the ruler
        Assert.False(TimelineLayout.HitTestGap(blocks, -1, 400));   // before the ruler
    }

    [Fact]
    public void RulerStepPicksNiceIntervals()
    {
        // 60 s em 600 px -> 0.1 un/px -> minimo 7 un por marca -> 10
        Assert.Equal(10, TimelineLayout.RulerStep(0.1));
        // timeline curta: passo pequeno
        Assert.Equal(0.1, TimelineLayout.RulerStep(0.001));
        Assert.Equal(1, TimelineLayout.RulerStep(0));
    }
}
