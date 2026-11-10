namespace Klip.Core.Media.Editing;

/// <summary>
/// Non-destructive edit project over one or more immutable source files.
/// Segments reference ranges of a source and carry an explicit position on
/// the EDITED timeline. Every operation is immutable and returns a
/// new project, so undo/redo is just a stack of instances.
///
/// Invariants:
/// - at least one segment; segments are never empty (start &lt; end);
/// - WITHIN A TRACK, segments are ordered by timeline position and NEVER
///   overlap; different tracks are fully independent of each other;
/// - gaps may exist between segments and before the first one on a track;
///   gaps render black and count towards that track's own span;
/// - there is never a gap AFTER a track's last segment by construction: a
///   track's span ends exactly at its last segment's end.
///
/// Multiple video tracks: GIF projects only ever use track 0 (a single
/// frame-based source); the track API below throws for GIF. Track 0 of a
/// video project keeps the exact single-track behaviour this type always
/// had; <see cref="AddTrack"/> appends independent tracks with their own
/// source file, composited (never blended/crossfaded - a hard cut in
/// visibility at each segment's edge) at export.
///
/// Timeline semantics decided for v2 (documented per task):
/// - <see cref="MoveSegmentTo"/>/<see cref="MoveSegmentToFrame"/> clamp against
///   the neighbours' edges instead of overlapping or swapping order;
///   reordering remains an index-based operation (<see cref="MoveSegment"/>).
/// - <see cref="MoveSegment"/> (reorder) re-packs the segment's OWN TRACK
///   contiguously from zero, leaving every other track untouched: reordering
///   is a list operation and discards any existing gaps on that track only.
/// - <see cref="RemoveSegment"/> leaves a gap in place;
///   <see cref="RemoveSegmentRipple"/> closes the hole (legacy behaviour),
///   scoped to the removed segment's own track.
/// - Trim: trimming the RIGHT edge keeps <see cref="TimelineSegment.TimelineStart"/>
///   fixed; trimming the LEFT edge keeps the RIGHT timeline edge fixed (the
///   timeline start shifts by the same delta as the source start).
/// - Split preserves positions: both halves stay exactly where the original
///   segment was (first half keeps the Id).
/// </summary>
public sealed class MediaEditProject
{
    public MediaKind Kind { get; }

    /// <summary>Path of the original recording (track 0's source). Never modified by the editor.</summary>
    public string SourcePath { get; }

    /// <summary>Total duration of the track 0 source (Video only).</summary>
    public TimeSpan SourceDuration { get; }

    /// <summary>Total number of frames in the source (GIF only).</summary>
    public int SourceFrameCount { get; }

    /// <summary>Segments across every track, in insertion order (NOT necessarily timeline order across tracks).</summary>
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
    /// at timeline position zero on track 0, and <paramref name="audioTrackCount"/>
    /// audio tracks at 100% volume, unmuted.
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
            [TimelineSegment.ForVideo(TimeSpan.Zero, sourceDuration, sourcePath, sourceDuration)], tracks);
    }

    /// <summary>Creates a GIF project with a single segment spanning all frames at slot zero.</summary>
    public static MediaEditProject CreateGif(string sourcePath, int frameCount)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        if (frameCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameCount));

        return new MediaEditProject(MediaKind.Gif, sourcePath, TimeSpan.Zero, frameCount,
            [TimelineSegment.ForFrames(0, frameCount, sourcePath)], []);
    }

    /// <summary>
    /// Total duration of the edited timeline (Video only): the longest of all
    /// tracks' own spans. Gaps count; a track's span ends at its own last
    /// segment (no trailing gap on any single track by construction).
    /// </summary>
    public TimeSpan EditedDuration
    {
        get
        {
            RequireKind(MediaKind.Video);
            var max = TimeSpan.Zero;
            foreach (var trackIndex in GetTrackIndices())
            {
                var last = SegmentsOnTrack(trackIndex)[^1].TimelineEnd;
                if (last > max)
                    max = last;
            }
            return max;
        }
    }

    /// <summary>
    /// Total number of frame slots on the edited timeline (GIF only), gaps
    /// included.
    /// </summary>
    public int EditedFrameCount
    {
        get
        {
            RequireKind(MediaKind.Gif);
            return Segments[^1].TimelineFrameEnd;
        }
    }

    /// <summary>Distinct track indices present, in ascending order (always just [0] for GIF).</summary>
    public IReadOnlyList<int> GetTrackIndices()
        => Segments.Select(s => s.TrackIndex).Distinct().OrderBy(i => i).ToArray();

    /// <summary>Segments of one track, sorted by timeline position.</summary>
    public IReadOnlyList<TimelineSegment> SegmentsOnTrack(int trackIndex)
        => Segments.Where(s => s.TrackIndex == trackIndex)
            .OrderBy(s => Kind == MediaKind.Video ? s.TimelineStart.Ticks : s.TimelineFrameStart)
            .ToArray();

    /// <summary>
    /// Adds a new independent video track from <paramref name="sourcePath"/>,
    /// covering it whole, starting at <paramref name="timelineStart"/>. The
    /// new track index is one past the highest existing one.
    /// </summary>
    public MediaEditProject AddTrack(string sourcePath, TimeSpan sourceDuration, TimeSpan timelineStart)
    {
        RequireKind(MediaKind.Video);
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        if (sourceDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(sourceDuration));
        if (timelineStart < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timelineStart));

        var trackIndex = Segments.Count == 0 ? 0 : Segments.Max(s => s.TrackIndex) + 1;
        var segment = TimelineSegment.ForVideoAt(timelineStart, TimeSpan.Zero, sourceDuration, sourcePath, sourceDuration, trackIndex);
        var list = new List<TimelineSegment>(Segments) { segment };
        return WithSegments(list);
    }

    /// <summary>
    /// Moves a segment to a DIFFERENT track (any existing index, or one past
    /// the highest existing - a track that does not exist yet comes into
    /// being the moment a segment references it, and the segment's old track
    /// vanishes on its own if this was its last segment). Throws if the
    /// segment would overlap an existing segment on the destination track.
    /// </summary>
    public MediaEditProject MoveSegmentToTrack(Guid id, int trackIndex, TimeSpan newTimelineStart)
    {
        RequireKind(MediaKind.Video);
        if (trackIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(trackIndex));
        var index = IndexOf(id);
        var seg = Segments[index];
        var start = newTimelineStart < TimeSpan.Zero ? TimeSpan.Zero : newTimelineStart;

        var list = new List<TimelineSegment>(Segments)
        {
            [index] = TimelineSegment.CreateVideo(seg.Id, start, seg.SourceStart, seg.SourceEnd,
                trackIndex, seg.SourcePath, seg.SourceFileDuration),
        };
        return WithSegments(list);
    }

    /// <summary>
    /// Duplicates a video segment onto a brand new track at the same timeline
    /// position (same source range, same file) - the simplest "duplicate" that
    /// can never collide with an existing segment, since the new track starts
    /// out with nothing else on it.
    /// </summary>
    public MediaEditProject DuplicateSegmentToNewTrack(Guid id)
    {
        RequireKind(MediaKind.Video);
        var seg = Segments.First(s => s.Id == id);
        var trackIndex = Segments.Max(s => s.TrackIndex) + 1;
        var copy = TimelineSegment.ForVideoAt(seg.TimelineStart, seg.SourceStart, seg.SourceEnd,
            seg.SourcePath, seg.SourceFileDuration, trackIndex);
        var list = new List<TimelineSegment>(Segments) { copy };
        return WithSegments(list);
    }

    /// <summary>Removes every segment of a track. Throws if it is the only remaining track.</summary>
    public MediaEditProject RemoveTrack(int trackIndex)
    {
        RequireKind(MediaKind.Video);
        var remaining = Segments.Where(s => s.TrackIndex != trackIndex).ToList();
        if (remaining.Count == 0)
            throw new InvalidOperationException("Cannot remove the last remaining track.");
        return WithSegments(remaining);
    }

    /// <summary>
    /// Splits the segment under <paramref name="position"/> (a position on the
    /// EDITED timeline, within <paramref name="trackIndex"/>) into two. Both
    /// halves keep their timeline positions (the cut point stays where it
    /// was); the first half keeps the original segment Id. Splitting on a
    /// boundary, inside a gap or outside the track's span is a no-op.
    /// </summary>
    public MediaEditProject SplitAt(TimeSpan position, int trackIndex = 0)
    {
        RequireKind(MediaKind.Video);
        var track = SegmentsOnTrack(trackIndex);
        if (track.Count == 0 || position <= TimeSpan.Zero || position >= track[^1].TimelineEnd)
            return this;

        foreach (var seg in track)
        {
            if (position <= seg.TimelineStart)
                return this; // gap or exact boundary: no-op

            if (position < seg.TimelineEnd)
            {
                var offset = position - seg.TimelineStart;
                var splitSource = seg.SourceStart + offset;
                var list = new List<TimelineSegment>(Segments);
                var index = list.FindIndex(s => s.Id == seg.Id);
                list[index] = TimelineSegment.CreateVideo(seg.Id, seg.TimelineStart, seg.SourceStart, splitSource,
                    seg.TrackIndex, seg.SourcePath, seg.SourceFileDuration);
                list.Insert(index + 1, TimelineSegment.ForVideoAt(position, splitSource, seg.SourceEnd,
                    seg.SourcePath, seg.SourceFileDuration, seg.TrackIndex));
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
                    [i] = TimelineSegment.CreateFrames(seg.Id, seg.TimelineFrameStart, seg.FrameStart, splitFrame,
                        seg.TrackIndex, seg.SourcePath),
                };
                list.Insert(i + 1, TimelineSegment.ForFramesAt(timelineFrame, splitFrame, seg.FrameEnd, seg.SourcePath));
                return WithSegments(list);
            }
        }

        return this;
    }

    /// <summary>
    /// Repositions a video segment on its own track's edited timeline. The
    /// target position is clamped so the segment never overlaps its
    /// same-track neighbours nor goes negative: lower bound is the previous
    /// segment's end (or zero); upper bound is the next segment's start minus
    /// this segment's duration (unbounded for the track's last segment -
    /// moving it right grows the timeline). Order between segments never
    /// changes here; use <see cref="MoveSegment"/> to reorder.
    /// </summary>
    public MediaEditProject MoveSegmentTo(Guid id, TimeSpan newTimelineStart)
    {
        RequireKind(MediaKind.Video);
        var seg = Segments.First(s => s.Id == id);
        var track = SegmentsOnTrack(seg.TrackIndex);
        var index = track.ToList().FindIndex(s => s.Id == id);

        var lower = index > 0 ? track[index - 1].TimelineEnd : TimeSpan.Zero;
        var upper = index < track.Count - 1
            ? track[index + 1].TimelineStart - seg.Duration
            : TimeSpan.MaxValue - seg.Duration;
        var target = Clamp(newTimelineStart, lower, upper);
        if (target == seg.TimelineStart)
            return this;

        var list = new List<TimelineSegment>(Segments);
        var globalIndex = list.FindIndex(s => s.Id == id);
        list[globalIndex] = TimelineSegment.CreateVideo(seg.Id, target, seg.SourceStart, seg.SourceEnd,
            seg.TrackIndex, seg.SourcePath, seg.SourceFileDuration);
        return WithSegments(list);
    }

    /// <summary>
    /// GIF variant of <see cref="MoveSegmentTo"/>: repositions the segment to
    /// timeline slot <paramref name="newTimelineFrameStart"/>, clamped against
    /// the neighbours.
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
            [index] = TimelineSegment.CreateFrames(seg.Id, target, seg.FrameStart, seg.FrameEnd, seg.TrackIndex, seg.SourcePath),
        };
        return WithSegments(list);
    }

    /// <summary>
    /// Removes a segment LEAVING A GAP in its place: no other
    /// segment moves, so its track's span only shrinks when the LAST segment
    /// on that track is removed. Throws if it is the last segment of the
    /// WHOLE project (the project must always keep at least one segment) or
    /// if the Id is unknown. Use <see cref="RemoveSegmentRipple"/> for the
    /// "remove and close" behaviour.
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
    /// Removes a segment AND shifts every later segment ON ITS OWN TRACK left
    /// by the removed segment's length, closing the hole it occupied
    /// ("excluir e fechar"). Other tracks are untouched. Gaps that existed
    /// before or after the removed segment are preserved as-is. Throws if it
    /// is the last remaining segment or the Id is unknown.
    /// </summary>
    public MediaEditProject RemoveSegmentRipple(Guid id)
    {
        var removedIndex = IndexOf(id);
        if (Segments.Count == 1)
            throw new InvalidOperationException("Cannot remove the last remaining segment.");

        var removed = Segments[removedIndex];
        var list = new List<TimelineSegment>(Segments);
        list.RemoveAt(removedIndex);
        for (var i = 0; i < list.Count; i++)
        {
            var seg = list[i];
            if (seg.TrackIndex != removed.TrackIndex)
                continue;
            if (Kind == MediaKind.Video)
            {
                if (seg.TimelineStart <= removed.TimelineStart)
                    continue;
                list[i] = TimelineSegment.CreateVideo(seg.Id, seg.TimelineStart - removed.Duration, seg.SourceStart,
                    seg.SourceEnd, seg.TrackIndex, seg.SourcePath, seg.SourceFileDuration);
            }
            else
            {
                if (seg.TimelineFrameStart <= removed.TimelineFrameStart)
                    continue;
                list[i] = TimelineSegment.CreateFrames(seg.Id, seg.TimelineFrameStart - removed.FrameCount,
                    seg.FrameStart, seg.FrameEnd, seg.TrackIndex, seg.SourcePath);
            }
        }
        return WithSegments(list);
    }

    /// <summary>
    /// Moves a segment to position <paramref name="newIndex"/> WITHIN ITS OWN
    /// TRACK (clamped, drag-and-drop friendly). Reordering RE-PACKS that
    /// track contiguously from zero, leaving every other track untouched: any
    /// gaps on this track are discarded (documented semantics - reorder is a
    /// list operation; use <see cref="MoveSegmentTo"/> to reposition while
    /// keeping gaps). Moving to the same index is a no-op and preserves gaps.
    /// </summary>
    public MediaEditProject MoveSegment(Guid id, int newIndex)
    {
        var moving = Segments.First(s => s.Id == id);
        var track = SegmentsOnTrack(moving.TrackIndex).ToList();
        var index = track.FindIndex(s => s.Id == id);
        newIndex = Math.Clamp(newIndex, 0, track.Count - 1);
        if (newIndex == index)
            return this;

        track.RemoveAt(index);
        track.Insert(newIndex, moving);
        var repacked = Repack(track);

        var others = Segments.Where(s => s.TrackIndex != moving.TrackIndex).ToList();
        others.AddRange(repacked);
        return WithSegments(others);
    }

    /// <summary>
    /// Retrims a video segment. <paramref name="newStart"/>/<paramref name="newEnd"/>
    /// are SOURCE times, clamped to [0, segment's own SourceFileDuration].
    /// Timeline semantics (documented per task): the timeline start shifts by
    /// the SAME delta as the source start, so a right-edge trim keeps
    /// TimelineStart fixed and a left-edge trim keeps the RIGHT timeline edge
    /// fixed. The resulting span additionally clamps against the same-track
    /// neighbours' edges by shrinking the source range. Throws if the final
    /// range would be empty.
    /// </summary>
    public MediaEditProject TrimSegment(Guid id, TimeSpan newStart, TimeSpan newEnd)
    {
        RequireKind(MediaKind.Video);
        var seg = Segments.First(s => s.Id == id);
        var track = SegmentsOnTrack(seg.TrackIndex);
        var index = track.ToList().FindIndex(s => s.Id == id);

        var start = Clamp(newStart, TimeSpan.Zero, seg.SourceFileDuration);
        var end = Clamp(newEnd, TimeSpan.Zero, seg.SourceFileDuration);
        if (start >= end)
            throw new ArgumentException("Trim would produce an empty segment (start < end required).");

        // Left edge moves TimelineStart by the same source delta (right timeline
        // edge fixed when only the left edge is trimmed).
        var timelineStart = seg.TimelineStart + (start - seg.SourceStart);

        // clamp against same-track neighbours (and timeline zero) by shrinking
        // the source range instead of overlapping.
        var lower = index > 0 ? track[index - 1].TimelineEnd : TimeSpan.Zero;
        if (timelineStart < lower)
        {
            start += lower - timelineStart;
            timelineStart = lower;
        }
        if (index < track.Count - 1)
        {
            var upper = track[index + 1].TimelineStart;
            var timelineEnd = timelineStart + (end - start);
            if (timelineEnd > upper)
                end -= timelineEnd - upper;
        }
        if (start >= end)
            throw new ArgumentException("Trim would produce an empty segment (start < end required).");

        var list = new List<TimelineSegment>(Segments);
        var globalIndex = list.FindIndex(s => s.Id == id);
        list[globalIndex] = TimelineSegment.CreateVideo(id, timelineStart, start, end,
            seg.TrackIndex, seg.SourcePath, seg.SourceFileDuration);
        return WithSegments(list);
    }

    /// <summary>
    /// Retrims a GIF segment over [newStart, newEnd) in SOURCE frame indices,
    /// clamped to [0, SourceFrameCount]. Same timeline semantics as
    /// <see cref="TrimSegment"/>: the timeline slot shifts with the left edge
    /// and the span clamps against the neighbours. Throws if the
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
            [index] = TimelineSegment.CreateFrames(id, timelineStart, start, end, seg.TrackIndex, seg.SourcePath),
        };
        return WithSegments(list);
    }

    /// <summary>
    /// Sets volume/mute for the audio stream <paramref name="streamIndex"/>
    /// Updates the existing track or appends a new one. Video only.
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
    /// on track 0. Returns false when the position falls inside a gap, before
    /// zero or at/after that track's span - the player paints black
    /// there; <paramref name="sourcePosition"/> is zero in that case.
    /// </summary>
    public bool TryMapToSource(TimeSpan editedPosition, out TimeSpan sourcePosition)
        => TryMapToSource(editedPosition, 0, out sourcePosition, out _);

    /// <summary>Track-aware variant of <see cref="TryMapToSource(TimeSpan, out TimeSpan)"/>, also returning which file to seek into.</summary>
    public bool TryMapToSource(TimeSpan editedPosition, int trackIndex, out TimeSpan sourcePosition, out string sourcePath)
    {
        RequireKind(MediaKind.Video);
        foreach (var seg in SegmentsOnTrack(trackIndex))
        {
            if (editedPosition < seg.TimelineStart)
                break; // gap (segments are sorted)
            if (editedPosition < seg.TimelineEnd)
            {
                sourcePosition = seg.SourceStart + (editedPosition - seg.TimelineStart);
                sourcePath = seg.SourcePath;
                return true;
            }
        }

        sourcePosition = TimeSpan.Zero;
        sourcePath = SourcePath;
        return false;
    }

    /// <summary>
    /// GIF variant of <see cref="TryMapToSource(TimeSpan, out TimeSpan)"/>: returns false
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
    /// Clamping variant of <see cref="TryMapToSource(TimeSpan, out TimeSpan)"/> kept for
    /// compatibility (track 0 only): positions inside a gap (or before zero)
    /// clamp FORWARD to the start of the next segment; positions at/after the
    /// end clamp to the last instant of the last segment. Identical to the
    /// old behaviour on gapless projects.
    /// </summary>
    public TimeSpan MapToSource(TimeSpan editedPosition)
    {
        RequireKind(MediaKind.Video);
        var track = SegmentsOnTrack(0);
        foreach (var seg in track)
        {
            if (editedPosition < seg.TimelineStart)
                return seg.SourceStart; // gap or before zero: clamp forward
            if (editedPosition < seg.TimelineEnd)
                return seg.SourceStart + (editedPosition - seg.TimelineStart);
        }

        return track[^1].SourceEnd;
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
    /// Gaps of track 0's edited VIDEO timeline, in order: [start, end) spans
    /// not covered by any segment, including a leading gap before the first
    /// segment. There is never a trailing gap by construction.
    /// Used by the UI (black regions) and by the export.
    /// </summary>
    public IReadOnlyList<(TimeSpan Start, TimeSpan End)> GetGaps() => GetGaps(0);

    /// <summary>Track-aware variant of <see cref="GetGaps()"/>.</summary>
    public IReadOnlyList<(TimeSpan Start, TimeSpan End)> GetGaps(int trackIndex)
    {
        RequireKind(MediaKind.Video);
        var gaps = new List<(TimeSpan, TimeSpan)>();
        var pos = TimeSpan.Zero;
        foreach (var seg in SegmentsOnTrack(trackIndex))
        {
            if (seg.TimelineStart > pos)
                gaps.Add((pos, seg.TimelineStart));
            pos = seg.TimelineEnd;
        }
        return gaps;
    }

    /// <summary>
    /// GIF variant of <see cref="GetGaps()"/>: half-open [start, end) ranges of
    /// timeline frame slots not covered by any segment.
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

    /// <summary>Rebuilds every segment of the given list contiguously from timeline zero, in list order.</summary>
    private List<TimelineSegment> Repack(List<TimelineSegment> list)
    {
        if (Kind == MediaKind.Video)
        {
            var pos = TimeSpan.Zero;
            for (var i = 0; i < list.Count; i++)
            {
                var seg = list[i];
                list[i] = TimelineSegment.CreateVideo(seg.Id, pos, seg.SourceStart, seg.SourceEnd,
                    seg.TrackIndex, seg.SourcePath, seg.SourceFileDuration);
                pos += seg.Duration;
            }
        }
        else
        {
            var pos = 0;
            for (var i = 0; i < list.Count; i++)
            {
                var seg = list[i];
                list[i] = TimelineSegment.CreateFrames(seg.Id, pos, seg.FrameStart, seg.FrameEnd, seg.TrackIndex, seg.SourcePath);
                pos += seg.FrameCount;
            }
        }
        return list;
    }

    private MediaEditProject WithSegments(IReadOnlyList<TimelineSegment> segments)
    {
        // Safety net for the invariant: WITHIN EACH TRACK, ordered by
        // timeline position, no overlaps. Every operation maintains this by
        // construction.
        foreach (var group in segments.GroupBy(s => s.TrackIndex))
        {
            var ordered = group.OrderBy(s => Kind == MediaKind.Video ? s.TimelineStart.Ticks : s.TimelineFrameStart).ToArray();
            for (var i = 1; i < ordered.Length; i++)
            {
                var overlap = Kind == MediaKind.Video
                    ? ordered[i].TimelineStart < ordered[i - 1].TimelineEnd
                    : ordered[i].TimelineFrameStart < ordered[i - 1].TimelineFrameEnd;
                if (overlap)
                    throw new InvalidOperationException("Timeline segments must be ordered and must not overlap within a track.");
            }
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
