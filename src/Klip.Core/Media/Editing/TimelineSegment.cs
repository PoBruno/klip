namespace Klip.Core.Media.Editing;

/// <summary>
/// One contiguous slice of the immutable source (RF-F5.05). Video segments use
/// <see cref="SourceStart"/>/<see cref="SourceEnd"/> (time in the source file);
/// GIF segments use <see cref="FrameStart"/>/<see cref="FrameEnd"/> where the
/// range is half-open: [FrameStart, FrameEnd). A segment is never empty
/// (start &lt; end). Instances are immutable.
///
/// RF-F5.17: every segment also carries an EXPLICIT position on the EDITED
/// timeline (<see cref="TimelineStart"/> for video, <see cref="TimelineFrameStart"/>
/// for GIF). Segments may be placed apart from each other, creating gaps that
/// render as black and extend the edited duration. The source range never
/// changes when a segment is repositioned.
/// </summary>
public sealed class TimelineSegment
{
    public Guid Id { get; }

    /// <summary>Position of the segment on the EDITED timeline (Video only; zero for GIF). RF-F5.17.</summary>
    public TimeSpan TimelineStart { get; }

    /// <summary>First timeline frame slot occupied by the segment (GIF only; zero for Video). RF-F5.17.</summary>
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

    /// <summary>End of the segment on the EDITED timeline, exclusive (Video). RF-F5.17.</summary>
    public TimeSpan TimelineEnd => TimelineStart + Duration;

    /// <summary>Timeline frame slot one past the segment, exclusive (GIF). RF-F5.17.</summary>
    public int TimelineFrameEnd => TimelineFrameStart + FrameCount;

    private TimelineSegment(Guid id, TimeSpan timelineStart, TimeSpan sourceStart, TimeSpan sourceEnd,
        int timelineFrameStart, int frameStart, int frameEnd)
    {
        Id = id;
        TimelineStart = timelineStart;
        SourceStart = sourceStart;
        SourceEnd = sourceEnd;
        TimelineFrameStart = timelineFrameStart;
        FrameStart = frameStart;
        FrameEnd = frameEnd;
    }

    /// <summary>Creates a time-based segment at timeline position zero. Throws if the range is empty or negative.</summary>
    public static TimelineSegment ForVideo(TimeSpan sourceStart, TimeSpan sourceEnd)
        => CreateVideo(Guid.NewGuid(), TimeSpan.Zero, sourceStart, sourceEnd);

    /// <summary>Creates a time-based segment at an explicit timeline position (RF-F5.17).</summary>
    public static TimelineSegment ForVideoAt(TimeSpan timelineStart, TimeSpan sourceStart, TimeSpan sourceEnd)
        => CreateVideo(Guid.NewGuid(), timelineStart, sourceStart, sourceEnd);

    /// <summary>Creates a frame-based segment over [frameStart, frameEnd) at timeline slot zero. Throws if empty.</summary>
    public static TimelineSegment ForFrames(int frameStart, int frameEnd)
        => CreateFrames(Guid.NewGuid(), 0, frameStart, frameEnd);

    /// <summary>Creates a frame-based segment at an explicit timeline slot (RF-F5.17).</summary>
    public static TimelineSegment ForFramesAt(int timelineFrameStart, int frameStart, int frameEnd)
        => CreateFrames(Guid.NewGuid(), timelineFrameStart, frameStart, frameEnd);

    // Internal factories that keep an existing Id, used by split/trim/move so
    // the "surviving" half of an operation stays addressable by the caller.
    internal static TimelineSegment CreateVideo(Guid id, TimeSpan timelineStart, TimeSpan sourceStart, TimeSpan sourceEnd)
    {
        if (timelineStart < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timelineStart), "Timeline position cannot be negative.");
        if (sourceStart < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(sourceStart), "Segment start cannot be negative.");
        if (sourceEnd <= sourceStart)
            throw new ArgumentException("Segment must not be empty (start < end).", nameof(sourceEnd));
        return new TimelineSegment(id, timelineStart, sourceStart, sourceEnd, 0, 0, 0);
    }

    internal static TimelineSegment CreateFrames(Guid id, int timelineFrameStart, int frameStart, int frameEnd)
    {
        if (timelineFrameStart < 0)
            throw new ArgumentOutOfRangeException(nameof(timelineFrameStart), "Timeline position cannot be negative.");
        if (frameStart < 0)
            throw new ArgumentOutOfRangeException(nameof(frameStart), "Segment start cannot be negative.");
        if (frameEnd <= frameStart)
            throw new ArgumentException("Segment must not be empty (start < end).", nameof(frameEnd));
        return new TimelineSegment(id, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, timelineFrameStart, frameStart, frameEnd);
    }
}
