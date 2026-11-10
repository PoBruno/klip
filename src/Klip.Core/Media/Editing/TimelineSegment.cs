namespace Klip.Core.Media.Editing;

/// <summary>
/// One contiguous slice of an immutable source. Video segments use
/// <see cref="SourceStart"/>/<see cref="SourceEnd"/> (time in the source file);
/// GIF segments use <see cref="FrameStart"/>/<see cref="FrameEnd"/> where the
/// range is half-open: [FrameStart, FrameEnd). A segment is never empty
/// (start &lt; end). Instances are immutable.
///
/// every segment also carries an EXPLICIT position on the EDITED
/// timeline (<see cref="TimelineStart"/> for video, <see cref="TimelineFrameStart"/>
/// for GIF). Segments may be placed apart from each other, creating gaps that
/// render as black and extend the edited duration. The source range never
/// changes when a segment is repositioned.
///
/// <see cref="TrackIndex"/> groups segments into independent video tracks
/// (GIF projects only ever use track 0). <see cref="SourcePath"/> and
/// <see cref="SourceFileDuration"/> let each track reference its own file -
/// track 0 keeps the project's own source for backward compatibility.
/// </summary>
public sealed class TimelineSegment
{
    public Guid Id { get; }

    /// <summary>Which video track this segment belongs to (0 = base track; always 0 for GIF).</summary>
    public int TrackIndex { get; }

    /// <summary>Source file this segment cuts from (Video only; same as the project's for GIF).</summary>
    public string SourcePath { get; }

    /// <summary>Total duration of <see cref="SourcePath"/> (Video only; zero for GIF) - fixed even after trims.</summary>
    public TimeSpan SourceFileDuration { get; }

    /// <summary>Position of the segment on the EDITED timeline (Video only; zero for GIF).</summary>
    public TimeSpan TimelineStart { get; }

    /// <summary>First timeline frame slot occupied by the segment (GIF only; zero for Video).</summary>
    public int TimelineFrameStart { get; }

    /// <summary>Start time in the source (Video only; zero for GIF).</summary>
    public TimeSpan SourceStart { get; }

    /// <summary>End time in the source, exclusive (Video only; zero for GIF).</summary>
    public TimeSpan SourceEnd { get; }

    /// <summary>First frame index in the source, inclusive (GIF only; zero for Video).</summary>
    public int FrameStart { get; }

    /// <summary>Frame index one past the last frame, exclusive (GIF only; zero for Video).</summary>
    public int FrameEnd { get; }

    /// <summary>Segment length on the edited timeline (Video).</summary>
    public TimeSpan Duration => SourceEnd - SourceStart;

    /// <summary>Number of frames covered by the segment (GIF).</summary>
    public int FrameCount => FrameEnd - FrameStart;

    /// <summary>End of the segment on the EDITED timeline, exclusive (Video).</summary>
    public TimeSpan TimelineEnd => TimelineStart + Duration;

    /// <summary>Timeline frame slot one past the segment, exclusive (GIF).</summary>
    public int TimelineFrameEnd => TimelineFrameStart + FrameCount;

    private TimelineSegment(Guid id, TimeSpan timelineStart, TimeSpan sourceStart, TimeSpan sourceEnd,
        int timelineFrameStart, int frameStart, int frameEnd, int trackIndex, string sourcePath,
        TimeSpan sourceFileDuration)
    {
        Id = id;
        TimelineStart = timelineStart;
        SourceStart = sourceStart;
        SourceEnd = sourceEnd;
        TimelineFrameStart = timelineFrameStart;
        FrameStart = frameStart;
        FrameEnd = frameEnd;
        TrackIndex = trackIndex;
        SourcePath = sourcePath;
        SourceFileDuration = sourceFileDuration;
    }

    /// <summary>Creates a time-based segment at timeline position zero on track 0. Throws if the range is empty or negative.</summary>
    public static TimelineSegment ForVideo(TimeSpan sourceStart, TimeSpan sourceEnd, string sourcePath, TimeSpan sourceFileDuration)
        => CreateVideo(Guid.NewGuid(), TimeSpan.Zero, sourceStart, sourceEnd, 0, sourcePath, sourceFileDuration);

    /// <summary>Creates a time-based segment at an explicit timeline position and track.</summary>
    public static TimelineSegment ForVideoAt(TimeSpan timelineStart, TimeSpan sourceStart, TimeSpan sourceEnd,
        string sourcePath, TimeSpan sourceFileDuration, int trackIndex = 0)
        => CreateVideo(Guid.NewGuid(), timelineStart, sourceStart, sourceEnd, trackIndex, sourcePath, sourceFileDuration);

    /// <summary>Creates a frame-based segment over [frameStart, frameEnd) at timeline slot zero. Throws if empty.</summary>
    public static TimelineSegment ForFrames(int frameStart, int frameEnd, string sourcePath)
        => CreateFrames(Guid.NewGuid(), 0, frameStart, frameEnd, 0, sourcePath);

    /// <summary>Creates a frame-based segment at an explicit timeline slot.</summary>
    public static TimelineSegment ForFramesAt(int timelineFrameStart, int frameStart, int frameEnd, string sourcePath)
        => CreateFrames(Guid.NewGuid(), timelineFrameStart, frameStart, frameEnd, 0, sourcePath);

    // Internal factories that keep an existing Id, used by split/trim/move so
    // the "surviving" half of an operation stays addressable by the caller.
    internal static TimelineSegment CreateVideo(Guid id, TimeSpan timelineStart, TimeSpan sourceStart, TimeSpan sourceEnd,
        int trackIndex, string sourcePath, TimeSpan sourceFileDuration)
    {
        if (timelineStart < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timelineStart), "Timeline position cannot be negative.");
        if (sourceStart < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(sourceStart), "Segment start cannot be negative.");
        if (sourceEnd <= sourceStart)
            throw new ArgumentException("Segment must not be empty (start < end).", nameof(sourceEnd));
        if (trackIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(trackIndex), "Track index cannot be negative.");
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        return new TimelineSegment(id, timelineStart, sourceStart, sourceEnd, 0, 0, 0, trackIndex, sourcePath, sourceFileDuration);
    }

    internal static TimelineSegment CreateFrames(Guid id, int timelineFrameStart, int frameStart, int frameEnd,
        int trackIndex, string sourcePath)
    {
        if (timelineFrameStart < 0)
            throw new ArgumentOutOfRangeException(nameof(timelineFrameStart), "Timeline position cannot be negative.");
        if (frameStart < 0)
            throw new ArgumentOutOfRangeException(nameof(frameStart), "Segment start cannot be negative.");
        if (frameEnd <= frameStart)
            throw new ArgumentException("Segment must not be empty (start < end).", nameof(frameEnd));
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        return new TimelineSegment(id, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, timelineFrameStart, frameStart, frameEnd,
            trackIndex, sourcePath, TimeSpan.Zero);
    }
}
