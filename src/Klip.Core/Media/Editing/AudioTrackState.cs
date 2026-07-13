namespace Klip.Core.Media.Editing;

/// <summary>
/// State of one audio stream of a video source (RF-F5.05 / RF-F5.09).
/// <see cref="StreamIndex"/> is the audio-stream ordinal in the source
/// (maps to ffmpeg's "0:a:N"). Volume range is 0.0-2.0 (0-200%). Immutable.
/// </summary>
public sealed class AudioTrackState
{
    public int StreamIndex { get; }
    public double Volume { get; }
    public bool IsMuted { get; }

    public AudioTrackState(int streamIndex, double volume = 1.0, bool isMuted = false)
    {
        if (streamIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(streamIndex));
        // RF-F5.09: volume 0-200%, clamped rather than rejected (slider-friendly)
        Volume = Math.Clamp(volume, 0.0, 2.0);
        StreamIndex = streamIndex;
        IsMuted = isMuted;
    }
}
