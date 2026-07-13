using Klip.Core.Media.Editing;

namespace Klip.Core.Tests.Media.Editing;

public class MediaEditProjectTests
{
    private static MediaEditProject Video60s(int audioTracks = 0)
        => MediaEditProject.CreateVideo(@"C:\rec\in.mp4", TimeSpan.FromSeconds(60), audioTracks);

    private static TimeSpan Sec(double s) => TimeSpan.FromSeconds(s);

    /// <summary>Builds the reordered 3-segment project [40-50][0-10][20-35] used by mapping tests.</summary>
    private static MediaEditProject ReorderedThreeSegments()
    {
        var p = Video60s()
            .SplitAt(Sec(10)).SplitAt(Sec(20)).SplitAt(Sec(35)).SplitAt(Sec(40)).SplitAt(Sec(50));
        // [0-10][10-20][20-35][35-40][40-50][50-60]
        // RF-F5.18: ripple removal keeps the timeline contiguous (RemoveSegment now leaves gaps)
        p = p.RemoveSegmentRipple(p.Segments[1].Id); // drop 10-20
        p = p.RemoveSegmentRipple(p.Segments[2].Id); // drop 35-40
        p = p.RemoveSegmentRipple(p.Segments[3].Id); // drop 50-60
        // [0-10][20-35][40-50]
        p = p.MoveSegment(p.Segments[2].Id, 0);
        // [40-50][0-10][20-35]
        return p;
    }

    [Fact]
    public void CreateVideo_StartsWithSingleFullSegment()
    {
        var p = Video60s(audioTracks: 2);

        Assert.Equal(MediaKind.Video, p.Kind);
        var seg = Assert.Single(p.Segments);
        Assert.Equal(TimeSpan.Zero, seg.SourceStart);
        Assert.Equal(Sec(60), seg.SourceEnd);
        Assert.Equal(Sec(60), p.EditedDuration);
        Assert.Equal(2, p.AudioTracks.Count);
        Assert.All(p.AudioTracks, t => Assert.False(t.IsMuted));
        Assert.All(p.AudioTracks, t => Assert.Equal(1.0, t.Volume));
    }

    [Fact]
    public void CreateGif_StartsWithSingleFullSegment()
    {
        var p = MediaEditProject.CreateGif(@"C:\rec\in.gif", 120);

        var seg = Assert.Single(p.Segments);
        Assert.Equal(0, seg.FrameStart);
        Assert.Equal(120, seg.FrameEnd);
        Assert.Equal(120, p.EditedFrameCount);
    }

    [Fact]
    public void SplitAt_Middle_ProducesTwoAdjacentSegments()
    {
        var p = Video60s();
        var originalId = p.Segments[0].Id;

        var split = p.SplitAt(Sec(25));

        Assert.Equal(2, split.Segments.Count);
        Assert.Equal(originalId, split.Segments[0].Id); // first half keeps the Id
        Assert.Equal(TimeSpan.Zero, split.Segments[0].SourceStart);
        Assert.Equal(Sec(25), split.Segments[0].SourceEnd);
        Assert.Equal(Sec(25), split.Segments[1].SourceStart);
        Assert.Equal(Sec(60), split.Segments[1].SourceEnd);
        Assert.Equal(Sec(60), split.EditedDuration); // duration preserved
    }

    [Fact]
    public void SplitAt_ExactBoundary_IsNoOp()
    {
        var p = Video60s().SplitAt(Sec(30));

        Assert.Same(p, p.SplitAt(Sec(30)));        // inter-segment boundary
        Assert.Same(p, p.SplitAt(TimeSpan.Zero));  // timeline start
        Assert.Same(p, p.SplitAt(Sec(60)));        // timeline end
        Assert.Same(p, p.SplitAt(Sec(120)));       // beyond the end
    }

    [Fact]
    public void SplitAt_OnGifProject_Throws()
    {
        var gif = MediaEditProject.CreateGif(@"C:\rec\in.gif", 10);
        Assert.Throws<InvalidOperationException>(() => gif.SplitAt(Sec(1)));
        Assert.Throws<InvalidOperationException>(() => Video60s().SplitAtFrame(1));
    }

    [Fact]
    public void SplitAtFrame_Middle_SplitsFrameRange()
    {
        var p = MediaEditProject.CreateGif(@"C:\rec\in.gif", 100);

        var split = p.SplitAtFrame(40);

        Assert.Equal(2, split.Segments.Count);
        Assert.Equal(0, split.Segments[0].FrameStart);
        Assert.Equal(40, split.Segments[0].FrameEnd);
        Assert.Equal(40, split.Segments[1].FrameStart);
        Assert.Equal(100, split.Segments[1].FrameEnd);
        Assert.Equal(100, split.EditedFrameCount);
    }

    [Fact]
    public void SplitAtFrame_Boundary_IsNoOp()
    {
        var p = MediaEditProject.CreateGif(@"C:\rec\in.gif", 100).SplitAtFrame(50);

        Assert.Same(p, p.SplitAtFrame(50));
        Assert.Same(p, p.SplitAtFrame(0));
        Assert.Same(p, p.SplitAtFrame(100));
    }

    [Fact]
    public void RemoveSegment_LeavesGapInPlace()
    {
        // RF-F5.18: default removal leaves a gap - nothing shifts
        var p = Video60s().SplitAt(Sec(20));

        var removed = p.RemoveSegment(p.Segments[0].Id);

        var seg = Assert.Single(removed.Segments);
        Assert.Equal(Sec(20), seg.SourceStart);
        Assert.Equal(Sec(20), seg.TimelineStart);              // did not move
        Assert.Equal(Sec(60), removed.EditedDuration);         // gap counts (RF-F5.17)
        var gap = Assert.Single(removed.GetGaps());
        Assert.Equal((TimeSpan.Zero, Sec(20)), gap);
    }

    [Fact]
    public void RemoveSegment_LastOnTimeline_ShrinksDuration()
    {
        // no trailing gap by construction: the timeline ends at the last segment
        var p = Video60s().SplitAt(Sec(20));

        var removed = p.RemoveSegment(p.Segments[1].Id);

        Assert.Equal(Sec(20), removed.EditedDuration);
        Assert.Empty(removed.GetGaps());
    }

    [Fact]
    public void RemoveSegmentRipple_ClosesTheHole()
    {
        // RF-F5.18 "excluir e fechar": later segments shift left by the removed length
        var p = Video60s().SplitAt(Sec(20)).SplitAt(Sec(40));

        var removed = p.RemoveSegmentRipple(p.Segments[1].Id);

        Assert.Equal(2, removed.Segments.Count);
        Assert.Equal(TimeSpan.Zero, removed.Segments[0].TimelineStart);
        Assert.Equal(Sec(20), removed.Segments[1].TimelineStart);
        Assert.Equal(Sec(40), removed.Segments[1].SourceStart); // source untouched
        Assert.Equal(Sec(40), removed.EditedDuration);
        Assert.Empty(removed.GetGaps());
    }

    [Fact]
    public void RemoveSegmentRipple_PreservesOtherGaps()
    {
        // gap (20,25) before the removed segment stays; only later segments shift
        var p = Video60s().SplitAt(Sec(20)).SplitAt(Sec(40));
        p = p.MoveSegmentTo(p.Segments[2].Id, Sec(45)); // [40-60] -> @45
        p = p.MoveSegmentTo(p.Segments[1].Id, Sec(25)); // [20-40] -> @25 (clamped by @45 neighbour)
        // [0-20]@0 | gap 20-25 | [20-40]@25 | [40-60]@45

        var removed = p.RemoveSegmentRipple(p.Segments[1].Id);

        Assert.Equal(2, removed.Segments.Count);
        Assert.Equal(Sec(25), removed.Segments[1].TimelineStart); // 45 - 20 (removed length)
        var gap = Assert.Single(removed.GetGaps());
        Assert.Equal((Sec(20), Sec(25)), gap);
    }

    [Fact]
    public void RemoveSegment_LastRemaining_Throws()
    {
        var p = Video60s();
        Assert.Throws<InvalidOperationException>(() => p.RemoveSegment(p.Segments[0].Id));
        Assert.Throws<InvalidOperationException>(() => p.RemoveSegmentRipple(p.Segments[0].Id));
    }

    [Fact]
    public void RemoveSegment_UnknownId_Throws()
    {
        var p = Video60s().SplitAt(Sec(30));
        Assert.Throws<ArgumentException>(() => p.RemoveSegment(Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => p.RemoveSegmentRipple(Guid.NewGuid()));
    }

    [Fact]
    public void MoveSegment_ReordersPreservingContent()
    {
        var p = Video60s().SplitAt(Sec(20)).SplitAt(Sec(40));
        var lastId = p.Segments[2].Id;

        var moved = p.MoveSegment(lastId, 0);

        Assert.Equal(3, moved.Segments.Count);
        Assert.Equal(lastId, moved.Segments[0].Id);
        Assert.Equal(Sec(40), moved.Segments[0].SourceStart);
        Assert.Equal(Sec(60), moved.Segments[0].SourceEnd);
        Assert.Equal(Sec(60), moved.EditedDuration);
        // same set of segments, only order changed
        Assert.Equal(
            p.Segments.Select(s => s.Id).OrderBy(g => g),
            moved.Segments.Select(s => s.Id).OrderBy(g => g));
    }

    [Fact]
    public void MoveSegment_IndexOutOfRange_ClampsToValidRange()
    {
        var p = Video60s().SplitAt(Sec(30));
        var firstId = p.Segments[0].Id;

        var moved = p.MoveSegment(firstId, 99);
        Assert.Equal(firstId, moved.Segments[^1].Id);

        var movedBack = moved.MoveSegment(firstId, -5);
        Assert.Equal(firstId, movedBack.Segments[0].Id);
    }

    [Fact]
    public void TrimSegment_ClampsToSourceBounds()
    {
        var p = Video60s();

        var trimmed = p.TrimSegment(p.Segments[0].Id, Sec(-5), Sec(70));

        var seg = Assert.Single(trimmed.Segments);
        Assert.Equal(TimeSpan.Zero, seg.SourceStart);
        Assert.Equal(Sec(60), seg.SourceEnd);
    }

    [Fact]
    public void TrimSegment_EmptyRange_Throws()
    {
        var p = Video60s();
        Assert.Throws<ArgumentException>(() => p.TrimSegment(p.Segments[0].Id, Sec(10), Sec(10)));
        Assert.Throws<ArgumentException>(() => p.TrimSegment(p.Segments[0].Id, Sec(20), Sec(10)));
    }

    [Fact]
    public void TrimSegmentFrames_ClampsToSourceAndRejectsEmpty()
    {
        var p = MediaEditProject.CreateGif(@"C:\rec\in.gif", 50);
        var id = p.Segments[0].Id;

        var trimmed = p.TrimSegmentFrames(id, -3, 200);
        Assert.Equal(0, trimmed.Segments[0].FrameStart);
        Assert.Equal(50, trimmed.Segments[0].FrameEnd);

        Assert.Throws<ArgumentException>(() => p.TrimSegmentFrames(id, 10, 10));
    }

    [Fact]
    public void WithAudioTrack_UpdatesExistingTrackAndClampsVolume()
    {
        var p = Video60s(audioTracks: 2);

        var updated = p.WithAudioTrack(1, 5.0, muted: true);

        Assert.Equal(2, updated.AudioTracks.Count);
        var track = updated.AudioTracks.Single(t => t.StreamIndex == 1);
        Assert.True(track.IsMuted);
        Assert.Equal(2.0, track.Volume); // RF-F5.09: clamped to 200%
        Assert.False(updated.AudioTracks.Single(t => t.StreamIndex == 0).IsMuted);
    }

    [Fact]
    public void WithAudioTrack_OnGif_Throws()
    {
        var gif = MediaEditProject.CreateGif(@"C:\rec\in.gif", 10);
        Assert.Throws<InvalidOperationException>(() => gif.WithAudioTrack(0, 1.0, false));
    }

    [Fact]
    public void MapToSource_WithReorderedSegments_MapsStartMiddleEndAndBeyond()
    {
        // [40-50][0-10][20-35], edited total = 35 s
        var p = ReorderedThreeSegments();

        Assert.Equal(Sec(35), p.EditedDuration);
        Assert.Equal(Sec(40), p.MapToSource(TimeSpan.Zero));       // start
        Assert.Equal(Sec(45), p.MapToSource(Sec(5)));              // inside 1st
        Assert.Equal(Sec(0), p.MapToSource(Sec(10)));              // boundary -> 2nd segment start
        Assert.Equal(Sec(5), p.MapToSource(Sec(15)));              // inside 2nd
        Assert.Equal(Sec(25), p.MapToSource(Sec(25)));             // inside 3rd
        Assert.Equal(Sec(35), p.MapToSource(Sec(35)));             // end -> last instant
        Assert.Equal(Sec(35), p.MapToSource(Sec(500)));            // beyond -> clamped
        Assert.Equal(Sec(40), p.MapToSource(Sec(-1)));             // negative -> clamped to start
    }

    [Fact]
    public void MapFrameToSource_ClampsAndMapsAcrossSegments()
    {
        var p = MediaEditProject.CreateGif(@"C:\rec\in.gif", 100).SplitAtFrame(40);
        p = p.MoveSegment(p.Segments[1].Id, 0); // [40-100)[0-40)

        Assert.Equal(40, p.MapFrameToSource(0));
        Assert.Equal(99, p.MapFrameToSource(59));
        Assert.Equal(0, p.MapFrameToSource(60));
        Assert.Equal(39, p.MapFrameToSource(99));
        Assert.Equal(39, p.MapFrameToSource(1000)); // beyond -> last frame
        Assert.Equal(40, p.MapFrameToSource(-1));   // negative -> first frame
    }

    [Fact]
    public void Operations_AreImmutable_PreviousInstanceIntact()
    {
        // RF-F5.10: undo/redo relies on old instances staying untouched
        var original = Video60s(audioTracks: 1);
        var originalSegmentId = original.Segments[0].Id;

        var edited = original
            .SplitAt(Sec(20))
            .SplitAt(Sec(40));
        edited = edited
            .RemoveSegmentRipple(edited.Segments[1].Id) // ripple keeps the 40 s expectation below
            .WithAudioTrack(0, 0.5, muted: true);

        Assert.Single(original.Segments);
        Assert.Equal(originalSegmentId, original.Segments[0].Id);
        Assert.Equal(Sec(60), original.EditedDuration);
        Assert.False(original.AudioTracks[0].IsMuted);
        Assert.Equal(1.0, original.AudioTracks[0].Volume);

        Assert.Equal(2, edited.Segments.Count);
        Assert.Equal(Sec(40), edited.EditedDuration);
    }

    [Fact]
    public void TenMixedOperations_KeepStateConsistent()
    {
        // CA-F5.5: a chain of mixed edits never corrupts the invariants
        // (RF-F5.17/19: sorted by timeline position, no overlaps, gaps allowed)
        var p = Video60s(audioTracks: 1);
        p = p.SplitAt(Sec(10)).SplitAt(Sec(20)).SplitAt(Sec(30)).SplitAt(Sec(40));
        p = p.RemoveSegment(p.Segments[2].Id);           // leaves a gap (RF-F5.18)
        p = p.MoveSegment(p.Segments[3].Id, 0);          // reorder re-packs (gaps discarded)
        p = p.TrimSegment(p.Segments[0].Id, Sec(45), Sec(55));
        p = p.MoveSegmentTo(p.Segments[0].Id, Sec(2));   // reposition within the leading gap
        p = p.SplitAt(Sec(5));
        p = p.RemoveSegmentRipple(p.Segments[0].Id);
        p = p.WithAudioTrack(0, 1.5, muted: false);

        Assert.All(p.Segments, s => Assert.True(s.SourceStart < s.SourceEnd));
        Assert.All(p.Segments, s => Assert.True(s.SourceStart >= TimeSpan.Zero && s.SourceEnd <= Sec(60)));
        Assert.All(p.Segments, s => Assert.True(s.TimelineStart >= TimeSpan.Zero));
        for (var i = 1; i < p.Segments.Count; i++)
            Assert.True(p.Segments[i].TimelineStart >= p.Segments[i - 1].TimelineEnd); // RF-F5.19
        Assert.Equal(p.Segments[^1].TimelineEnd, p.EditedDuration);
        // edited duration = segment content + gaps
        var gapTotal = p.GetGaps().Sum(g => (g.End - g.Start).TotalSeconds);
        Assert.Equal(
            p.Segments.Sum(s => s.Duration.TotalSeconds) + gapTotal,
            p.EditedDuration.TotalSeconds, 6);
    }

    [Fact]
    public void MoveSegmentTo_CreatesGapAndExtendsDuration()
    {
        // RF-F5.17: dragging a segment away creates a gap and grows the timeline
        var p = Video60s().SplitAt(Sec(20));
        var id = p.Segments[1].Id;

        var moved = p.MoveSegmentTo(id, Sec(25));

        Assert.Equal(Sec(25), moved.Segments[1].TimelineStart);
        Assert.Equal(Sec(20), moved.Segments[1].SourceStart); // source untouched
        Assert.Equal(Sec(65), moved.EditedDuration);          // 25 + 40
        var gap = Assert.Single(moved.GetGaps());
        Assert.Equal((Sec(20), Sec(25)), gap);
    }

    [Fact]
    public void MoveSegmentTo_ClampsAgainstNeighbours()
    {
        // RF-F5.19: no overlap and no reorder - clamp on the neighbours' edges
        var p = Video60s().SplitAt(Sec(20)).SplitAt(Sec(40));
        var middle = p.Segments[1].Id;

        // fully contiguous: any target clamps back to the current position (no-op)
        Assert.Same(p, p.MoveSegmentTo(middle, TimeSpan.Zero));
        Assert.Same(p, p.MoveSegmentTo(middle, Sec(100)));

        // open a gap on the right, then overshoot: clamps to next.TimelineStart - duration
        var spread = p.MoveSegmentTo(p.Segments[2].Id, Sec(50)); // [40-60] -> @50
        var clamped = spread.MoveSegmentTo(middle, Sec(45));
        Assert.Equal(Sec(30), clamped.Segments[1].TimelineStart); // 50 - 20

        // undershoot: clamps to previous.TimelineEnd
        var clampedLeft = clamped.MoveSegmentTo(middle, Sec(-10));
        Assert.Equal(Sec(20), clampedLeft.Segments[1].TimelineStart);
    }

    [Fact]
    public void MoveSegmentTo_BackAgainstNeighbour_ConsumesGap()
    {
        var p = Video60s().SplitAt(Sec(20));
        var id = p.Segments[1].Id;
        var withGap = p.MoveSegmentTo(id, Sec(30));

        var closed = withGap.MoveSegmentTo(id, Sec(20));

        Assert.Empty(closed.GetGaps());
        Assert.Equal(Sec(60), closed.EditedDuration);
    }

    [Fact]
    public void MoveSegmentTo_OnGifProject_Throws()
    {
        var gif = MediaEditProject.CreateGif(@"C:\rec\in.gif", 10);
        Assert.Throws<InvalidOperationException>(() => gif.MoveSegmentTo(gif.Segments[0].Id, Sec(1)));
        var video = Video60s();
        Assert.Throws<InvalidOperationException>(() => video.MoveSegmentToFrame(video.Segments[0].Id, 1));
    }

    [Fact]
    public void MoveSegmentToFrame_CreatesGapAndClamps()
    {
        var p = MediaEditProject.CreateGif(@"C:\rec\in.gif", 100).SplitAtFrame(40);
        var second = p.Segments[1].Id;

        var moved = p.MoveSegmentToFrame(second, 50); // gap slots [40,50)

        Assert.Equal(50, moved.Segments[1].TimelineFrameStart);
        Assert.Equal(40, moved.Segments[1].FrameStart);   // source untouched
        Assert.Equal(110, moved.EditedFrameCount);        // 50 + 60
        var gap = Assert.Single(moved.GetFrameGaps());
        Assert.Equal((40, 50), gap);

        // clamp on the previous neighbour's edge (RF-F5.19)
        var clamped = moved.MoveSegmentToFrame(second, 10);
        Assert.Equal(40, clamped.Segments[1].TimelineFrameStart);
    }

    [Fact]
    public void MoveSegment_Reorder_RepacksDiscardingGaps()
    {
        // documented semantics: index reorder re-packs the whole timeline from zero
        var p = Video60s().SplitAt(Sec(20));
        p = p.MoveSegmentTo(p.Segments[1].Id, Sec(30)); // gap (20,30)

        var reordered = p.MoveSegment(p.Segments[1].Id, 0);

        Assert.Empty(reordered.GetGaps());
        Assert.Equal(TimeSpan.Zero, reordered.Segments[0].TimelineStart);
        Assert.Equal(Sec(40), reordered.Segments[1].TimelineStart);
        Assert.Equal(Sec(60), reordered.EditedDuration);
    }

    [Fact]
    public void SplitAt_InsideGap_IsNoOp()
    {
        var p = Video60s().SplitAt(Sec(20));
        p = p.MoveSegmentTo(p.Segments[1].Id, Sec(30)); // gap (20,30)

        Assert.Same(p, p.SplitAt(Sec(25)));
        Assert.Same(p, p.SplitAt(Sec(20))); // gap edge = segment boundary
        Assert.Same(p, p.SplitAt(Sec(30))); // segment start boundary
    }

    [Fact]
    public void SplitAt_PositionedSegment_PreservesTimelinePositions()
    {
        var p = Video60s().SplitAt(Sec(20));
        p = p.MoveSegmentTo(p.Segments[1].Id, Sec(25)); // [20-60]@25

        var split = p.SplitAt(Sec(45)); // 20 s into the moved segment

        Assert.Equal(3, split.Segments.Count);
        Assert.Equal(Sec(25), split.Segments[1].TimelineStart);
        Assert.Equal(Sec(40), split.Segments[1].SourceEnd);
        Assert.Equal(Sec(45), split.Segments[2].TimelineStart);
        Assert.Equal(Sec(40), split.Segments[2].SourceStart);
        Assert.Equal(Sec(65), split.EditedDuration); // unchanged
    }

    [Fact]
    public void TrimSegment_RightEdge_KeepsTimelineStart()
    {
        // documented semantics: right trim keeps TimelineStart fixed
        var p = Video60s();
        var trimmed = p.TrimSegment(p.Segments[0].Id, TimeSpan.Zero, Sec(50));

        Assert.Equal(TimeSpan.Zero, trimmed.Segments[0].TimelineStart);
        Assert.Equal(Sec(50), trimmed.EditedDuration);
    }

    [Fact]
    public void TrimSegment_LeftEdge_KeepsRightTimelineEdgeFixed()
    {
        // documented semantics: left trim shifts TimelineStart so the right
        // timeline edge stays put - a leading gap appears (RF-F5.17)
        var p = Video60s();
        var trimmed = p.TrimSegment(p.Segments[0].Id, Sec(10), Sec(60));

        Assert.Equal(Sec(10), trimmed.Segments[0].TimelineStart);
        Assert.Equal(Sec(60), trimmed.Segments[0].TimelineEnd); // right edge fixed
        Assert.Equal(Sec(60), trimmed.EditedDuration);
        var gap = Assert.Single(trimmed.GetGaps());
        Assert.Equal((TimeSpan.Zero, Sec(10)), gap);
    }

    [Fact]
    public void TrimSegment_ClampsAgainstNeighbours()
    {
        // RF-F5.19: extending an edge into a neighbour shrinks back to its border
        var p = Video60s().SplitAt(Sec(20)).SplitAt(Sec(40));

        // right edge of the first segment cannot cross the second's start
        var right = p.TrimSegment(p.Segments[0].Id, TimeSpan.Zero, Sec(30));
        Assert.Equal(Sec(20), right.Segments[0].SourceEnd);
        Assert.Equal(Sec(20), right.Segments[0].TimelineEnd);

        // left edge of the middle segment cannot cross the first's end
        var left = p.TrimSegment(p.Segments[1].Id, Sec(10), Sec(40));
        Assert.Equal(Sec(20), left.Segments[1].SourceStart);
        Assert.Equal(Sec(20), left.Segments[1].TimelineStart);
    }

    [Fact]
    public void TrimSegmentFrames_LeftEdge_ShiftsTimelineSlot()
    {
        var p = MediaEditProject.CreateGif(@"C:\rec\in.gif", 50);
        var trimmed = p.TrimSegmentFrames(p.Segments[0].Id, 10, 50);

        Assert.Equal(10, trimmed.Segments[0].TimelineFrameStart); // right edge fixed at 50
        Assert.Equal(50, trimmed.EditedFrameCount);
        var gap = Assert.Single(trimmed.GetFrameGaps());
        Assert.Equal((0, 10), gap);
    }

    [Fact]
    public void TryMapToSource_GapInsideAndOutside()
    {
        // RF-F5.20: gap positions report false so the player paints black
        var p = Video60s().SplitAt(Sec(20));
        p = p.MoveSegmentTo(p.Segments[1].Id, Sec(30)); // [0-20]@0 | gap | [20-60]@30

        Assert.True(p.TryMapToSource(Sec(10), out var src));
        Assert.Equal(Sec(10), src);

        Assert.False(p.TryMapToSource(Sec(25), out _));            // inside the gap
        Assert.True(p.TryMapToSource(Sec(30), out src));           // segment start
        Assert.Equal(Sec(20), src);
        Assert.True(p.TryMapToSource(Sec(45), out src));           // inside 2nd
        Assert.Equal(Sec(35), src);
        Assert.False(p.TryMapToSource(p.EditedDuration, out _));   // exact end (half-open)
        Assert.False(p.TryMapToSource(Sec(-1), out _));            // before zero
        Assert.False(p.TryMapToSource(Sec(500), out _));           // beyond the end
    }

    [Fact]
    public void TryMapFrameToSource_GapReportsFalseWithMinusOne()
    {
        var p = MediaEditProject.CreateGif(@"C:\rec\in.gif", 100).SplitAtFrame(40);
        p = p.MoveSegmentToFrame(p.Segments[1].Id, 50); // gap slots [40,50)

        Assert.True(p.TryMapFrameToSource(0, out var src));
        Assert.Equal(0, src);
        Assert.False(p.TryMapFrameToSource(45, out src));  // gap slot
        Assert.Equal(-1, src);
        Assert.True(p.TryMapFrameToSource(50, out src));
        Assert.Equal(40, src);
        Assert.False(p.TryMapFrameToSource(110, out _));   // end (half-open)
        Assert.False(p.TryMapFrameToSource(-1, out _));
    }

    [Fact]
    public void MapToSource_InGap_ClampsForwardToNextSegment()
    {
        // compat contract: the clamping variant snaps gap positions to the
        // start of the NEXT segment
        var p = Video60s().SplitAt(Sec(20));
        p = p.MoveSegmentTo(p.Segments[1].Id, Sec(30));

        Assert.Equal(Sec(20), p.MapToSource(Sec(25))); // gap -> next segment start
        Assert.Equal(Sec(60), p.MapToSource(Sec(999)));

        var gif = MediaEditProject.CreateGif(@"C:\rec\in.gif", 100).SplitAtFrame(40);
        gif = gif.MoveSegmentToFrame(gif.Segments[1].Id, 50);
        Assert.Equal(40, gif.MapFrameToSource(45)); // gap slot -> next segment first frame
    }

    [Fact]
    public void GetGaps_IncludesLeadingGap_NeverTrailing()
    {
        var p = Video60s().SplitAt(Sec(20)).SplitAt(Sec(40));
        p = p.MoveSegmentTo(p.Segments[2].Id, Sec(50));  // gap (40,50)
        p = p.MoveSegmentTo(p.Segments[1].Id, Sec(25));  // gap (20,25) e (45,50)
        p = p.RemoveSegment(p.Segments[0].Id);           // leading gap (0,25)

        var gaps = p.GetGaps();

        Assert.Equal(2, gaps.Count);
        Assert.Equal((TimeSpan.Zero, Sec(25)), gaps[0]);
        Assert.Equal((Sec(45), Sec(50)), gaps[1]);
        Assert.Equal(Sec(70), p.EditedDuration); // ends at the last segment
    }

    [Fact]
    public void GetGaps_RequiresMatchingKind()
    {
        var gif = MediaEditProject.CreateGif(@"C:\rec\in.gif", 10);
        Assert.Throws<InvalidOperationException>(() => gif.GetGaps());
        Assert.Throws<InvalidOperationException>(() => Video60s().GetFrameGaps());
        Assert.Empty(Video60s().GetGaps());
        Assert.Empty(gif.GetFrameGaps());
    }
}
