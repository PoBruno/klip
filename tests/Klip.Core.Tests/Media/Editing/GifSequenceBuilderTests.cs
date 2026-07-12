using Klip.Core.Media.Editing;

namespace Klip.Core.Tests.Media.Editing;

public class GifSequenceBuilderTests
{
    private static readonly int[] Delays = [100, 100, 100, 100, 100, 100];

    [Fact]
    public void WithoutTargetFpsReturnsEditedTimelineInSegmentOrder()
    {
        // 6 frames, corta [2,4) e move para o inicio
        var project = MediaEditProject.CreateGif("a.gif", 6)
            .SplitAtFrame(2)
            .SplitAtFrame(4);
        var middle = project.Segments[1];
        project = project.MoveSegment(middle.Id, 0);

        var seq = GifSequenceBuilder.Build(project, Delays, targetFps: null);

        Assert.Equal(6, seq.Count);
        Assert.Equal([2, 3, 0, 1, 4, 5], seq.Select(e => e.SourceFrame).ToArray());
        Assert.Equal(Enumerable.Range(0, 6).ToArray(), seq.Select(e => e.EditedFrame).ToArray());
        Assert.All(seq, e => Assert.Equal(100, e.DelayMs));
    }

    [Fact]
    public void TargetFpsPreservesTotalDuration()
    {
        // 10 fps sobre delays de 100 ms (10 fps ja) nao muda nada; 5 fps decima
        var project = MediaEditProject.CreateGif("a.gif", 6);
        var seq = GifSequenceBuilder.Build(project, Delays, targetFps: 5);

        Assert.True(seq.Count < 6);
        Assert.Equal(600, seq.Sum(e => e.DelayMs)); // CA-F5.4: duracao preservada
    }

    [Fact]
    public void RemovedSegmentRipple_DoesNotAppearInSequence()
    {
        var project = MediaEditProject.CreateGif("a.gif", 6).SplitAtFrame(3);
        project = project.RemoveSegmentRipple(project.Segments[0].Id);

        var seq = GifSequenceBuilder.Build(project, Delays, targetFps: null);

        Assert.Equal([3, 4, 5], seq.Select(e => e.SourceFrame).ToArray());
        Assert.All(seq, e => Assert.False(e.IsGap));
    }

    [Fact]
    public void RemovedSegmentLeavesBlackGapFrames()
    {
        // RF-F5.18/20: default removal leaves a gap; gap slots materialize as
        // black entries (SourceFrame = -1) so the preview/export paint black
        var project = MediaEditProject.CreateGif("a.gif", 6).SplitAtFrame(3);
        project = project.RemoveSegment(project.Segments[0].Id);

        var seq = GifSequenceBuilder.Build(project, Delays, targetFps: null);

        Assert.Equal(6, seq.Count); // EditedFrameCount includes the gap
        Assert.Equal([-1, -1, -1, 3, 4, 5], seq.Select(e => e.SourceFrame).ToArray());
        Assert.Equal([true, true, true, false, false, false], seq.Select(e => e.IsGap).ToArray());
        Assert.Equal(Enumerable.Range(0, 6).ToArray(), seq.Select(e => e.EditedFrame).ToArray());
    }

    [Fact]
    public void GapFramesInheritLocalCadence()
    {
        // gap black frames use the delay of the previous REAL frame; a leading
        // gap uses the first real frame's delay (documented contract, RF-F5.20)
        int[] delays = [50, 100, 100, 200, 100, 100];
        var project = MediaEditProject.CreateGif("a.gif", 6).SplitAtFrame(4);
        project = project.MoveSegmentToFrame(project.Segments[1].Id, 7);  // gap slots 5-6
        project = project.MoveSegmentToFrame(project.Segments[0].Id, 1);  // leading gap slot 0

        var seq = GifSequenceBuilder.Build(project, delays, targetFps: null);

        Assert.Equal(9, seq.Count);
        // slot 0: leading gap -> delay of source frame 0 (first real frame)
        Assert.True(seq[0].IsGap);
        Assert.Equal(50, seq[0].DelayMs);
        // slots 5-6: gap after segment [0,4) -> delay of source frame 3 (200 ms)
        Assert.True(seq[5].IsGap);
        Assert.Equal(200, seq[5].DelayMs);
        Assert.True(seq[6].IsGap);
        Assert.Equal(200, seq[6].DelayMs);
        // real frames unchanged
        Assert.Equal([0, 1, 2, 3], seq.Skip(1).Take(4).Select(e => e.SourceFrame).ToArray());
        Assert.Equal([4, 5], seq.Skip(7).Select(e => e.SourceFrame).ToArray());
    }

    [Fact]
    public void GapFramesParticipateInFpsReduction()
    {
        // decimation runs over the full slot list (gaps included) and still
        // preserves the total duration (CA-F5.4)
        var project = MediaEditProject.CreateGif("a.gif", 6).SplitAtFrame(3);
        project = project.MoveSegmentToFrame(project.Segments[1].Id, 5); // 2 gap slots

        var seq = GifSequenceBuilder.Build(project, Delays, targetFps: 5);

        Assert.True(seq.Count < 8);
        Assert.Equal(800, seq.Sum(e => e.DelayMs)); // 8 slots x 100 ms
    }

    [Fact]
    public void RejectsVideoProjectsAndShortDelayLists()
    {
        var video = MediaEditProject.CreateVideo("a.mp4", TimeSpan.FromSeconds(10));
        Assert.Throws<ArgumentException>(() => GifSequenceBuilder.Build(video, Delays, null));

        var gif = MediaEditProject.CreateGif("a.gif", 6);
        Assert.Throws<ArgumentException>(() => GifSequenceBuilder.Build(gif, [100], null));
    }
}
