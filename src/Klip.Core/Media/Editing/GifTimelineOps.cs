namespace Klip.Core.Media.Editing;

/// <summary>
/// RF-F5.08: pure frame-based GIF timeline operations (ScreenToGif model:
/// frames are a list with per-frame delays; reducing framerate is decimation
/// with delay redistribution so the total duration is preserved).
/// </summary>
public static class GifTimelineOps
{
    /// <summary>
    /// Decimates frames to approximate <paramref name="targetFps"/>. A frame is
    /// dropped when the frame kept before it has not yet covered a full target
    /// interval (1000/targetFps ms); the dropped frame's delay is ADDED to that
    /// previous kept frame, so the sum of delays - i.e. the total duration - is
    /// exactly preserved (CA-F5.4). Returns (source frame index, new delay) in
    /// order. The first frame is always kept.
    /// </summary>
    public static IReadOnlyList<(int FrameIndex, int DelayMs)> ReduceFps(IReadOnlyList<int> delaysMs, int targetFps)
    {
        ArgumentNullException.ThrowIfNull(delaysMs);
        if (targetFps <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetFps), "Target fps must be positive.");
        if (delaysMs.Count == 0)
            return [];

        var interval = 1000.0 / targetFps;
        var result = new List<(int FrameIndex, int DelayMs)>();
        var keptIndex = 0;
        var keptDelay = delaysMs[0];

        for (var i = 1; i < delaysMs.Count; i++)
        {
            if (keptDelay < interval)
            {
                // still inside the target interval: merge this frame's delay
                // into the previous kept frame (redistribution)
                keptDelay += delaysMs[i];
            }
            else
            {
                result.Add((keptIndex, keptDelay));
                keptIndex = i;
                keptDelay = delaysMs[i];
            }
        }

        result.Add((keptIndex, keptDelay));
        return result;
    }
}
