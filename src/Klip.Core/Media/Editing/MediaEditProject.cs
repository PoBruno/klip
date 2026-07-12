namespace Klip.Core.Media.Editing;

/// <summary>
/// Non-destructive edit project over an immutable source file (RF-F5.05).
/// Segments reference ranges of the source and carry an explicit position on
/// the EDITED timeline (RF-F5.17). Every operation is immutable and returns a
/// new project, so undo/redo (RF-F5.10, RF-F5.18) is just a stack of instances.
///
/// Invariants:
/// - at least one segment; segments are never empty (start &lt; end);
/// - segments are ordered by timeline position and NEVER overlap (RF-F5.19);
/// - gaps may exist between segments and before the first one; gaps render
///   black and count towards <see cref="EditedDuration"/> (RF-F5.17);
/// - there is never a gap AFTER the last segment by construction: the edited
///   timeline ends exactly at the last segment's end.
///
/// Timeline semantics decided for v2 (documented per task):
/// - <see cref="MoveSegmentTo"/>/<see cref="MoveSegmentToFrame"/> clamp against
///   the neighbours' edges instead of overlapping or swapping order (RF-F5.19);
///   reordering remains an index-based operation (<see cref="MoveSegment"/>).
/// - <see cref="MoveSegment"/> (reorder) re-packs the WHOLE timeline
///   contiguously from zero: reordering is a list operation and discards any
///   existing gaps (simplest deterministic semantics; a UI that wants to keep
///   gaps repositions with <see cref="MoveSegmentTo"/> instead).
/// - <see cref="RemoveSegment"/> leaves a gap in place (RF-F5.18);
///   <see cref="RemoveSegmentRipple"/> closes the hole (legacy behaviour).
/// - Trim: trimming the RIGHT edge keeps <see cref="TimelineSegment.TimelineStart"/>
///   fixed; trimming the LEFT edge keeps the RIGHT timeline edge fixed (the
///   timeline start shifts by the same delta as the source start).
/// - Split preserves positions: both halves stay exactly where the original
///   segment was (first half keeps the Id).
/// </summary>
public sealed class MediaEditProject
{
    public MediaKind Kind { get; }

    /// <summary>Path of the original recording. Never modified by the editor.</summary>
    public string SourcePath { get; }

    /// <summary>Total duration of the source (Video only).</summary>
    public TimeSpan SourceDuration { get; }

    /// <summary>Total number of frames in the source (GIF only).</summary>
    public int SourceFrameCount { get; }

    /// <summary>Segments in timeline order (which is also the export order, RF-F5.17).</summary>
    public IReadOnlyList<TimelineSegment> Segments { get; }

    /// <summary>Audio streams of the source (Video only; empty for GIF).</summary>
    public IReadOnlyList<AudioTrackState> AudioTracks { get; }

    private MediaEditProject(MediaKind kind, string sourcePath, TimeSpan sourceDuration,
        int sourceFrameCount, IReadOnlyList<TimelineSegment> segments, IReadOnlyList<AudioTrackState> audioTracks)
    {
        Kind = kind;
        SourcePath = sourcePath;
        SourceDuration = sourceDuration;
        SourceFrameCount = sourceFrameCount;
        Segments = segments;
        AudioTracks = audioTracks;
    }

    /// <summary>
    /// Creates a video project with a single segment spanning the whole source
    /// at timeline position zero and <paramref name="audioTrackCount"/> tracks
    /// at 100% volume, unmuted.
    /// </summary>
    public static MediaEditProject CreateVideo(string sourcePath, TimeSpan sourceDuration, int audioTrackCount = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        if (sourceDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(sourceDuration));
        if (audioTrackCount < 0)
            throw new ArgumentOutOfRangeException(nameof(audioTrackCount));

        var tracks = new AudioTrackState[audioTrackCount];
        for (var i = 0; i < audioTrackCount; i++)
            tracks[i] = new AudioTrackState(i);

        return new MediaEditProject(MediaKind.Video, sourcePath, sourceDuration, 0,
            [TimelineSegment.ForVideo(TimeSpan.Zero, sourceDuration)], tracks);
    }

    /// <summary>Creates a GIF project with a single segment spanning all frames at slot zero.</summary>
    public static MediaEditProject CreateGif(string sourcePath, int frameCount)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        if (frameCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameCount));

        return new MediaEditProject(MediaKind.Gif, sourcePath, TimeSpan.Zero, frameCount,
            [TimelineSegment.ForFrames(0, frameCount)], []);
    }

    /// <summary>
    /// Total duration of the edited timeline (Video only). Gaps count: the
    /// timeline ends at the end of the LAST segment (RF-F5.17). A gap after the
    /// last segment cannot exist by construction.
    /// </summary>
    public TimeSpan EditedDuration
    {
        get
        {
            RequireKind(MediaKind.Video);
            return Segments[^1].TimelineEnd;
        }
    }

    /// <summary>
    /// Total number of frame slots on the edited timeline (GIF only), gaps
    /// included (RF-F5.17).
    /// </summary>
    public int EditedFrameCount
    {
        get
        {
            RequireKind(MediaKind.Gif);
            return Segments[^1].TimelineFrameEnd;
        }
    }

    /// <summary>
    /// Splits the segment under <paramref name="position"/> (a position on the
    /// EDITED timeline) into two. Both halves keep their timeline positions
    /// (the cut point stays where it was); the first half keeps the original
    /// segment Id. Splitting on a boundary, inside a gap or outside the
    /// timeline is a no-op.
    /// </summary>
    public MediaEditProject SplitAt(TimeSpan position)
    {
        RequireKind(MediaKind.Video);
        if (position <= TimeSpan.Zero || position >= EditedDuration)
            return this;

        for (var i = 0; i < Segments.Count; i++)
        {
            var seg = Segments[i];
            if (position <= seg.TimelineStart)
                return this; // gap or exact boundary: no-op

            if (position < seg.TimelineEnd)
            {
                var offset = position - seg.TimelineStart;
                var splitSource = seg.SourceStart + offset;
                var list = new List<TimelineSegment>(Segments)
                {
                    [i] = TimelineSegment.CreateVideo(seg.Id, seg.TimelineStart, seg.SourceStart, splitSource),
                };
                list.Insert(i + 1, TimelineSegment.ForVideoAt(position, splitSource, seg.SourceEnd));
                return WithSegments(list);
            }
        }

        return this;
    }

    /// <summary>
    /// GIF variant of <see cref="SplitAt"/>: <paramref name="timelineFrame"/> is
    /// the timeline slot (on the EDITED timeline) of the first frame of the
    /// second half. No-op on boundaries, inside gaps or outside the timeline.
    /// </summary>
    public MediaEditProject SplitAtFrame(int timelineFrame)
    {
        RequireKind(MediaKind.Gif);
        if (timelineFrame <= 0 || timelineFrame >= EditedFrameCount)
            return this;

        for (var i = 0; i < Segments.Count; i++)
        {
            var seg = Segments[i];
            if (timelineFrame <= seg.TimelineFrameStart)
                return this;

            if (timelineFrame < seg.TimelineFrameEnd)
            {
                var offset = timelineFrame - seg.TimelineFrameStart;
                var splitFrame = seg.FrameStart + offset;
                var list = new List<TimelineSegment>(Segments)
                {
                    [i] = TimelineSegment.CreateFrames(seg.Id, seg.TimelineFrameStart, seg.FrameStart, splitFrame),
                };
                list.Insert(i + 1, TimelineSegment.ForFramesAt(timelineFrame, splitFrame, seg.FrameEnd));
                return WithSegments(list);
            }
        }

        return this;
    }

    /// <summary>
    /// Repositions a video segment on the edited timeline (RF-F5.17). The
    /// target position is clamped so the segment never overlaps its neighbours
    /// nor goes negative (RF-F5.19): lower bound is the previous segment's end
    /// (or zero); upper bound is the next segment's start minus this segment's
    /// duration (unbounded for the last segment - moving it right grows the
    /// timeline). Order between segments never changes here; use
    /// <see cref="MoveSegment"/> to reorder.
    /// </summary>
    public MediaEditProject MoveSegmentTo(Guid id, TimeSpan newTimelineStart)
    {
        RequireKind(MediaKind.Video);
        var index = IndexOf(id);
        var seg = Segments[index];

        var lower = index > 0 ? Segments[index - 1].TimelineEnd : TimeSpan.Zero;
        var upper = index < Segments.Count - 1
            ? Segments[index + 1].TimelineStart - seg.Duration
            : TimeSpan.MaxValue - seg.Duration;
        var target = Clamp(newTimelineStart, lower, upper);
        if (target == seg.TimelineStart)
            return this;

        var list = new List<TimelineSegment>(Segments)
        {
            [index] = TimelineSegment.CreateVideo(seg.Id, target, seg.SourceStart, seg.SourceEnd),
        };
        return WithSegments(list);
    }

    /// <summary>
    /// GIF variant of <see cref="MoveSegmentTo"/>: repositions the segment to
    /// timeline slot <paramref name="newTimelineFrameStart"/>, clamped against
    /// the neighbours (RF-F5.19).
    /// </summary>
    public MediaEditProject MoveSegmentToFrame(Guid id, int newTimelineFrameStart)
    {
        RequireKind(MediaKind.Gif);
        var index = IndexOf(id);
        var seg = Segments[index];

        var lower = index > 0 ? Segments[index - 1].TimelineFrameEnd : 0;
        var upper = index < Segments.Count - 1
            ? Segments[index + 1].TimelineFrameStart - seg.FrameCount
            : int.MaxValue - seg.FrameCount;
        var target = Math.Clamp(newTimelineFrameStart, lower, upper);
        if (target == seg.TimelineFrameStart)
            return this;

        var list = new List<TimelineSegment>(Segments)
        {
            [index] = TimelineSegment.CreateFrames(seg.Id, target, seg.FrameStart, seg.FrameEnd),
        };
        return WithSegments(list);
    }

    /// <summary>
    /// Removes a segment LEAVING A GAP in its place (RF-F5.18): no other
    /// segment moves, so the edited duration only shrinks when the LAST segment
    /// is removed. Throws if it is the last remaining one (the timeline must
    /// always keep at least one segment) or if the Id is unknown. Use
    /// <see cref="RemoveSegmentRipple"/> for the "remove and close" behaviour.
    /// </summary>
    public MediaEditProject RemoveSegment(Guid id)
    {
        var index = IndexOf(id);
        if (Segments.Count == 1)
            throw new InvalidOperationException("Cannot remove the last remaining segment.");

        var list = new List<TimelineSegment>(Segments);
        list.RemoveAt(index);
        return WithSegments(list);
    }

    /// <summary>
    /// Removes a segment AND shifts every later segment left by the removed
    /// segment's length, closing the hole it occupied (RF-F5.18 "excluir e
    /// fechar"). Gaps that existed before or after the removed segment are
    /// preserved as-is. Throws if it is the last remaining segment or the Id is
    /// unknown.
    /// </summary>
    public MediaEditProject RemoveSegmentRipple(Guid id)
    {
        var index = IndexOf(id);
        if (Segments.Count == 1)
            throw new InvalidOperationException("Cannot remove the last remaining segment.");

        var removed = Segments[index];
        var list = new List<TimelineSegment>(Segments);
        list.RemoveAt(index);
        for (var i = index; i < list.Count; i++)
        {
            var seg = list[i];
            list[i] = Kind == MediaKind.Video
                ? TimelineSegment.CreateVideo(seg.Id, seg.TimelineStart - removed.Duration, seg.SourceStart, seg.SourceEnd)
                : TimelineSegment.CreateFrames(seg.Id, seg.TimelineFrameStart - removed.FrameCount, seg.FrameStart, seg.FrameEnd);
        }
        return WithSegments(list);
    }

    /// <summary>
    /// Moves a segment to list position <paramref name="newIndex"/> (clamped,
    /// drag-and-drop friendly, RF-F5.07). Reordering RE-PACKS the whole
    /// timeline contiguously from zero: any gaps are discarded (documented
    /// semantics - reorder is a list operation; use <see cref="MoveSegmentTo"/>
    /// to reposition while keeping gaps). Moving to the same index is a no-op
    /// and preserves gaps.
    /// </summary>
    public MediaEditProject MoveSegment(Guid id, int newIndex)
    {
        var index = IndexOf(id);
        newIndex = Math.Clamp(newIndex, 0, Segments.Count - 1);
        if (newIndex == index)
            return this;

        var list = new List<TimelineSegment>(Segments);
        var seg = list[index];
        list.RemoveAt(index);
        list.Insert(newIndex, seg);
        return WithSegments(Repack(list));
    }

    /// <summary>
    /// Retrims a video segment. <paramref name="newStart"/>/<paramref name="newEnd"/>
    /// are SOURCE times, clamped to [0, SourceDuration]. Timeline semantics
    /// (documented per task): the timeline start shifts by the SAME delta as
    /// the source start, so a right-edge trim keeps TimelineStart fixed and a
    /// left-edge trim keeps the RIGHT timeline edge fixed. The resulting span
    /// additionally clamps against the neighbours' edges (RF-F5.19) by
    /// shrinking the source range. Throws if the final range would be empty.
    /// </summary>
    public MediaEditProject TrimSegment(Guid id, TimeSpan newStart, TimeSpan newEnd)
    {
        RequireKind(MediaKind.Video);
        var index = IndexOf(id);
        var seg = Segments[index];

        var start = Clamp(newStart, TimeSpan.Zero, SourceDuration);
        var end = Clamp(newEnd, TimeSpan.Zero, SourceDuration);
        if (start >= end)
            throw new ArgumentException("Trim would produce an empty segment (start < end required).");

        // Left edge moves TimelineStart by the same source delta (right timeline
        // edge fixed when only the left edge is trimmed).
        var timelineStart = seg.TimelineStart + (start - seg.SourceStart);

        // RF-F5.19: clamp against neighbours (and timeline zero) by shrinking
        // the source range instead of overlapping.
        var lower = index > 0 ? Segments[index - 1].TimelineEnd : TimeSpan.Zero;
        if (timelineStart < lower)
        {
            start += lower - timelineStart;
            timelineStart = lower;
        }
        if (index < Segments.Count - 1)
        {
            var upper = Segments[index + 1].TimelineStart;
            var timelineEnd = timelineStart + (end - start);
            if (timelineEnd > upper)
                end -= timelineEnd - upper;
        }
        if (start >= end)
            throw new ArgumentException("Trim would produce an empty segment (start < end required).");

        var list = new List<TimelineSegment>(Segments)
        {
            [index] = TimelineSegment.CreateVideo(id, timelineStart, start, end),
        };
        return WithSegments(list);
    }

    /// <summary>
    /// Retrims a GIF segment over [newStart, newEnd) in SOURCE frame indices,
    /// clamped to [0, SourceFrameCount]. Same timeline semantics as
    /// <see cref="TrimSegment"/>: the timeline slot shifts with the left edge
    /// and the span clamps against the neighbours (RF-F5.19). Throws if the
    /// final range is empty.
    /// </summary>
    public MediaEditProject TrimSegmentFrames(Guid id, int newStart, int newEnd)
    {
        RequireKind(MediaKind.Gif);
        var index = IndexOf(id);
        var seg = Segments[index];

        var start = Math.Clamp(newStart, 0, SourceFrameCount);
        var end = Math.Clamp(newEnd, 0, SourceFrameCount);
        if (start >= end)
            throw new ArgumentException("Trim would produce an empty segment (start < end required).");

        var timelineStart = seg.TimelineFrameStart + (start - seg.FrameStart);

        var lower = index > 0 ? Segments[index - 1].TimelineFrameEnd : 0;
        if (timelineStart < lower)
        {
            start += lower - timelineStart;
            timelineStart = lower;
        }
        if (index < Segments.Count - 1)
        {
            var upper = Segments[index + 1].TimelineFrameStart;
            var timelineEnd = timelineStart + (end - start);
            if (timelineEnd > upper)
                end -= timelineEnd - upper;
        }
        if (start >= end)
            throw new ArgumentException("Trim would produce an empty segment (start < end required).");

        var list = new List<TimelineSegment>(Segments)
        {
            [index] = TimelineSegment.CreateFrames(id, timelineStart, start, end),
        };
        return WithSegments(list);
    }

    /// <summary>
    /// Sets volume/mute for the audio stream <paramref name="streamIndex"/>
    /// (RF-F5.09). Updates the existing track or appends a new one. Video only.
    /// </summary>
    public MediaEditProject WithAudioTrack(int streamIndex, double volume, bool muted)
    {
        RequireKind(MediaKind.Video);
        var track = new AudioTrackState(streamIndex, volume, muted);

        var list = new List<AudioTrackState>(AudioTracks);
        var existing = list.FindIndex(t => t.StreamIndex == streamIndex);
        if (existing >= 0)
            list[existing] = track;
        else
            list.Add(track);

        return new MediaEditProject(Kind, SourcePath, SourceDuration, SourceFrameCount, Segments, list);
    }

    /// <summary>
    /// Maps a position on the EDITED timeline to the corresponding source time
    /// (RF-F5.20). Returns false when the position falls inside a gap, before
    /// zero or at/after <see cref="EditedDuration"/> - the player paints black
    /// there; <paramref name="sourcePosition"/> is zero in that case.
    /// </summary>
    public bool TryMapToSource(TimeSpan editedPosition, out TimeSpan sourcePosition)
    {
        RequireKind(MediaKind.Video);
        foreach (var seg in Segments)
        {
            if (editedPosition < seg.TimelineStart)
                break; // gap (segments are sorted)
            if (editedPosition < seg.TimelineEnd)
            {
                sourcePosition = seg.SourceStart + (editedPosition - seg.TimelineStart);
                return true;
            }
        }

        sourcePosition = TimeSpan.Zero;
        return false;
    }

    /// <summary>
    /// GIF variant of <see cref="TryMapToSource"/> (RF-F5.20): returns false
    /// for slots inside gaps or outside the timeline (the player paints a
    /// black frame); <paramref name="sourceFrame"/> is -1 in that case.
    /// </summary>
    public bool TryMapFrameToSource(int timelineFrame, out int sourceFrame)
    {
        RequireKind(MediaKind.Gif);
        foreach (var seg in Segments)
        {
            if (timelineFrame < seg.TimelineFrameStart)
                break;
            if (timelineFrame < seg.TimelineFrameEnd)
            {
                sourceFrame = seg.FrameStart + (timelineFrame - seg.TimelineFrameStart);
                return true;
            }
        }

        sourceFrame = -1;
        return false;
    }

    /// <summary>
    /// Clamping variant of <see cref="TryMapToSource"/> kept for compatibility:
    /// positions inside a gap (or before zero) clamp FORWARD to the start of
    /// the next segment; positions at/after the end clamp to the last instant
    /// of the last segment. Identical to the old behaviour on gapless projects.
    /// </summary>
    public TimeSpan MapToSource(TimeSpan editedPosition)
    {
        RequireKind(MediaKind.Video);
        foreach (var seg in Segments)
        {
            if (editedPosition < seg.TimelineStart)
                return seg.SourceStart; // gap or before zero: clamp forward
            if (editedPosition < seg.TimelineEnd)
                return seg.SourceStart + (editedPosition - seg.TimelineStart);
        }

        return Segments[^1].SourceEnd;
    }

    /// <summary>
    /// Clamping variant of <see cref="TryMapFrameToSource"/> kept for
    /// compatibility: slots inside a gap clamp forward to the next segment's
    /// first frame; slots past the end clamp to the last frame.
    /// </summary>
    public int MapFrameToSource(int timelineFrame)
    {
        RequireKind(MediaKind.Gif);
        foreach (var seg in Segments)
        {
            if (timelineFrame < seg.TimelineFrameStart)
                return seg.FrameStart;
            if (timelineFrame < seg.TimelineFrameEnd)
                return seg.FrameStart + (timelineFrame - seg.TimelineFrameStart);
        }

        return Segments[^1].FrameEnd - 1;
    }

    /// <summary>
    /// Gaps of the edited VIDEO timeline in order: [start, end) spans not
    /// covered by any segment, including a leading gap before the first
    /// segment (RF-F5.17). There is never a trailing gap by construction.
    /// Used by the UI (black regions) and by the export (RF-F5.20).
    /// </summary>
    public IReadOnlyList<(TimeSpan Start, TimeSpan End)> GetGaps()
    {
        RequireKind(MediaKind.Video);
        var gaps = new List<(TimeSpan, TimeSpan)>();
        var pos = TimeSpan.Zero;
        foreach (var seg in Segments)
        {
            if (seg.TimelineStart > pos)
                gaps.Add((pos, seg.TimelineStart));
            pos = seg.TimelineEnd;
        }
        return gaps;
    }

    /// <summary>
    /// GIF variant of <see cref="GetGaps"/>: half-open [start, end) ranges of
    /// timeline frame slots not covered by any segment (RF-F5.17).
    /// </summary>
    public IReadOnlyList<(int Start, int End)> GetFrameGaps()
    {
        RequireKind(MediaKind.Gif);
        var gaps = new List<(int, int)>();
        var pos = 0;
        foreach (var seg in Segments)
        {
            if (seg.TimelineFrameStart > pos)
                gaps.Add((pos, seg.TimelineFrameStart));
            pos = seg.TimelineFrameEnd;
        }
        return gaps;
    }

    /// <summary>Rebuilds every segment contiguously from timeline zero, in list order.</summary>
    private List<TimelineSegment> Repack(List<TimelineSegment> list)
    {
        if (Kind == MediaKind.Video)
        {
            var pos = TimeSpan.Zero;
            for (var i = 0; i < list.Count; i++)
            {
                var seg = list[i];
                list[i] = TimelineSegment.CreateVideo(seg.Id, pos, seg.SourceStart, seg.SourceEnd);
                pos += seg.Duration;
            }
        }
        else
        {
            var pos = 0;
            for (var i = 0; i < list.Count; i++)
            {
                var seg = list[i];
                list[i] = TimelineSegment.CreateFrames(seg.Id, pos, seg.FrameStart, seg.FrameEnd);
                pos += seg.FrameCount;
            }
        }
        return list;
    }

    private MediaEditProject WithSegments(IReadOnlyList<TimelineSegment> segments)
    {
        // Safety net for the RF-F5.19 invariant: ordered by timeline position,
        // no overlaps. Every operation maintains this by construction.
        for (var i = 1; i < segments.Count; i++)
        {
            var overlap = Kind == MediaKind.Video
                ? segments[i].TimelineStart < segments[i - 1].TimelineEnd
                : segments[i].TimelineFrameStart < segments[i - 1].TimelineFrameEnd;
            if (overlap)
                throw new InvalidOperationException("Timeline segments must be ordered and must not overlap (RF-F5.19).");
        }
        return new(Kind, SourcePath, SourceDuration, SourceFrameCount, segments, AudioTracks);
    }

    private int IndexOf(Guid id)
    {
        for (var i = 0; i < Segments.Count; i++)
        {
            if (Segments[i].Id == id)
                return i;
        }
        throw new ArgumentException($"No segment with id {id}.", nameof(id));
    }

    private void RequireKind(MediaKind kind)
    {
        if (Kind != kind)
            throw new InvalidOperationException($"Operation requires a {kind} project, but this project is {Kind}.");
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
        => value < min ? min : value > max ? max : value;
}
