using Klip.Core.Media.Editing;

namespace Klip.Core.Tests.Media.Editing;

public class MediaEditProjectTracksTests
{
    private static MediaEditProject Video60s()
        => MediaEditProject.CreateVideo(@"C:\rec\base.mp4", TimeSpan.FromSeconds(60));

    private static TimeSpan Sec(double s) => TimeSpan.FromSeconds(s);

    [Fact]
    public void AddTrack_AppendsIndependentTrack()
    {
        var p = Video60s().AddTrack(@"C:\rec\overlay.mp4", Sec(20), Sec(5));

        Assert.Equal([0, 1], p.GetTrackIndices());
        var track1 = p.SegmentsOnTrack(1);
        var seg = Assert.Single(track1);
        Assert.Equal(1, seg.TrackIndex);
        Assert.Equal(@"C:\rec\overlay.mp4", seg.SourcePath);
        Assert.Equal(Sec(5), seg.TimelineStart);
        Assert.Equal(Sec(25), seg.TimelineEnd);
        Assert.Equal(Sec(20), seg.SourceFileDuration);
    }

    [Fact]
    public void AddTrack_TrackIndexIsOnePastTheHighestExisting()
    {
        var p = Video60s()
            .AddTrack(@"C:\rec\a.mp4", Sec(10), TimeSpan.Zero)
            .AddTrack(@"C:\rec\b.mp4", Sec(10), TimeSpan.Zero);

        Assert.Equal([0, 1, 2], p.GetTrackIndices());
    }

    [Fact]
    public void EditedDuration_IsTheLongestTrack()
    {
        var p = Video60s().AddTrack(@"C:\rec\overlay.mp4", Sec(90), TimeSpan.Zero);

        Assert.Equal(Sec(90), p.EditedDuration);
    }

    [Fact]
    public void SplitAt_OnExtraTrack_DoesNotAffectTrack0()
    {
        var p = Video60s().AddTrack(@"C:\rec\overlay.mp4", Sec(20), TimeSpan.Zero);

        var split = p.SplitAt(Sec(8), trackIndex: 1);

        Assert.Single(split.SegmentsOnTrack(0)); // track 0 untouched
        Assert.Equal(2, split.SegmentsOnTrack(1).Count);
    }

    [Fact]
    public void TrimSegment_OnExtraTrack_ClampsToItsOwnSourceFileDuration()
    {
        var p = Video60s().AddTrack(@"C:\rec\overlay.mp4", Sec(20), TimeSpan.Zero);
        var seg = p.SegmentsOnTrack(1)[0];

        var trimmed = p.TrimSegment(seg.Id, Sec(5), Sec(999));

        var result = Assert.Single(trimmed.SegmentsOnTrack(1));
        Assert.Equal(Sec(5), result.SourceStart);
        Assert.Equal(Sec(20), result.SourceEnd); // clamped to the overlay's own 20s, not track 0's 60s
    }

    [Fact]
    public void MoveSegment_ReorderOnOneTrack_LeavesOtherTrackUntouched()
    {
        var p = Video60s()
            .AddTrack(@"C:\rec\overlay.mp4", Sec(10), TimeSpan.Zero)
            .SplitAt(Sec(5), trackIndex: 1);
        var track0Before = p.SegmentsOnTrack(0)[0];

        var reordered = p.MoveSegment(p.SegmentsOnTrack(1)[1].Id, 0);

        Assert.Equal(track0Before.TimelineStart, reordered.SegmentsOnTrack(0)[0].TimelineStart);
        Assert.Equal(track0Before.SourceStart, reordered.SegmentsOnTrack(0)[0].SourceStart);
    }

    [Fact]
    public void RemoveTrack_DropsOnlyThatTracksSegments()
    {
        var p = Video60s().AddTrack(@"C:\rec\overlay.mp4", Sec(10), TimeSpan.Zero);

        var removed = p.RemoveTrack(1);

        Assert.Equal([0], removed.GetTrackIndices());
    }

    [Fact]
    public void RemoveTrack_LastRemainingTrack_Throws()
    {
        var p = Video60s();

        Assert.Throws<InvalidOperationException>(() => p.RemoveTrack(0));
    }

    [Fact]
    public void TryMapToSource_ResolvesTheRequestedTracksOwnFile()
    {
        var p = Video60s().AddTrack(@"C:\rec\overlay.mp4", Sec(10), Sec(5));

        var found = p.TryMapToSource(Sec(7), 1, out var sourcePosition, out var sourcePath);

        Assert.True(found);
        Assert.Equal(Sec(2), sourcePosition);
        Assert.Equal(@"C:\rec\overlay.mp4", sourcePath);
    }

    [Fact]
    public void TryMapToSource_GapOnTrack_ReturnsFalse()
    {
        var p = Video60s().AddTrack(@"C:\rec\overlay.mp4", Sec(10), Sec(5));

        var found = p.TryMapToSource(Sec(2), 1, out _, out _);

        Assert.False(found); // before the overlay track's own start: a gap on that track
    }

    [Fact]
    public void MoveSegmentToTrack_ToBrandNewIndex_CreatesTheTrack()
    {
        var p = Video60s().AddTrack(@"C:\rec\overlay.mp4", Sec(10), TimeSpan.Zero);
        var seg = p.SegmentsOnTrack(1)[0];

        var moved = p.MoveSegmentToTrack(seg.Id, 5, Sec(2));

        Assert.Equal([0, 5], moved.GetTrackIndices());
        var result = Assert.Single(moved.SegmentsOnTrack(5));
        Assert.Equal(Sec(2), result.TimelineStart);
        Assert.Equal(@"C:\rec\overlay.mp4", result.SourcePath);
    }

    [Fact]
    public void MoveSegmentToTrack_LeavingTheOnlySegmentOfATrack_MakesThatTrackDisappear()
    {
        var p = Video60s().AddTrack(@"C:\rec\overlay.mp4", Sec(10), TimeSpan.Zero);
        var seg = p.SegmentsOnTrack(1)[0];

        var moved = p.MoveSegmentToTrack(seg.Id, 2, TimeSpan.Zero);

        Assert.Equal([0, 2], moved.GetTrackIndices()); // track 1 had only this segment: it vanished
    }

    [Fact]
    public void MoveSegmentToTrack_OverlappingTheDestination_Throws()
    {
        var p = Video60s()
            .AddTrack(@"C:\rec\a.mp4", Sec(10), TimeSpan.Zero)
            .AddTrack(@"C:\rec\b.mp4", Sec(10), Sec(20));
        var segB = p.SegmentsOnTrack(2)[0];

        Assert.Throws<InvalidOperationException>(() => p.MoveSegmentToTrack(segB.Id, 1, TimeSpan.Zero));
    }

    [Fact]
    public void DuplicateSegmentToNewTrack_CopiesOntoANewTrackAtTheSamePosition()
    {
        var p = Video60s().SplitAt(Sec(10)); // [0-10][10-60] on track 0
        var seg = p.SegmentsOnTrack(0)[0];

        var dup = p.DuplicateSegmentToNewTrack(seg.Id);

        Assert.Equal([0, 1], dup.GetTrackIndices());
        var copy = Assert.Single(dup.SegmentsOnTrack(1));
        Assert.Equal(seg.TimelineStart, copy.TimelineStart);
        Assert.Equal(seg.SourceStart, copy.SourceStart);
        Assert.Equal(seg.SourceEnd, copy.SourceEnd);
        Assert.Equal(seg.SourcePath, copy.SourcePath);
        // original track untouched
        Assert.Equal(2, dup.SegmentsOnTrack(0).Count);
    }

    [Fact]
    public void AddTrack_OnGifProject_Throws()
    {
        var gif = MediaEditProject.CreateGif(@"C:\rec\in.gif", frameCount: 30);

        Assert.Throws<InvalidOperationException>(() => gif.AddTrack(@"C:\rec\x.mp4", Sec(1), TimeSpan.Zero));
    }
}
