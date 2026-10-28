namespace Klip.Core.Media.Editing;

/// <summary>Settings for the MP4 re-encode export.</summary>
public sealed class VideoExportSettings
{
    /// <summary>
    /// FFmpeg video encoder name: "h264_nvenc", "h264_qsv", "h264_amf",
    /// "libopenh264", etc. Hardware probing/fallback is the caller's job.
    /// </summary>
    public string VideoCodec { get; init; } = "libopenh264";

    /// <summary>Target video bitrate in kbps; null lets the encoder decide.</summary>
    public int? BitrateKbps { get; init; }

    /// <summary>Emits "-movflags +faststart" (moov atom up front).</summary>
    public bool FastStart { get; init; } = true;

    /// <summary>
    /// Source video width in pixels. Only used to synthesize black filler for
    /// timeline gaps ("color=" source); it MUST match the real source
    /// dimensions or the concat filter fails at runtime. The App fills this
    /// from the probed source; the default is a sensible 1080p fallback.
    /// </summary>
    public int Width { get; init; } = 1920;

    /// <summary>Source video height in pixels. See <see cref="Width"/>.</summary>
    public int Height { get; init; } = 1080;

    /// <summary>
    /// Source framerate, used for the black filler of timeline gaps
    /// Should match the source stream framerate.
    /// </summary>
    public int Fps { get; init; } = 30;
}

/// <summary>Settings for the MP4-to-GIF export route.</summary>
public sealed class GifFromVideoSettings
{
    /// <summary>Target GIF framerate.</summary>
    public int Fps { get; init; } = 15;

    /// <summary>Output width in pixels (height keeps aspect); null keeps the source size.</summary>
    public int? ScaleWidth { get; init; }

    /// <summary>Paletteuse dithering: "none" or "bayer".</summary>
    public string Dithering { get; init; } = "none";

    /// <summary>
    /// SOURCE video width in pixels (not the output width): only used to
    /// synthesize black filler for timeline gaps before scaling.
    /// Must match the real source dimensions when the project has gaps.
    /// </summary>
    public int Width { get; init; } = 1920;

    /// <summary>Source video height in pixels. See <see cref="Width"/>.</summary>
    public int Height { get; init; } = 1080;
}
