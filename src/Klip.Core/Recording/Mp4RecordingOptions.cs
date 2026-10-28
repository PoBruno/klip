namespace Klip.Core.Recording;

/// <summary>Options of one mp4 recording.</summary>
public sealed class Mp4RecordingOptions
{
    /// <summary>Region in physical px (virtual desktop coordinates).</summary>
    public required RecordingRegion Region { get; init; }

    /// <summary>Full path of the .mp4 to write.</summary>
    public required string OutputPath { get; init; }

    /// <summary>Target fps (30 or 60, CFR in the capture engine).</summary>
    public int Fps { get; init; } = 30;

    /// <summary>Hidden override in kbps. 0 = bitrate from resolution x fps x quality x codec.</summary>
    public int BitrateKbps { get; init; }

    public VideoQualityPreset Quality { get; init; } = VideoQualityPreset.Balanced;

    /// <summary>Output height cap ("1080p" = 1080); null = native. Never scales up.</summary>
    public int? OutputHeight { get; init; }

    /// <summary>null = auto (h264, hevc when wider than 4096).</summary>
    public VideoCodec? Codec { get; init; }

    /// <summary>Fragmented mp4 on by default: a crash keeps what was recorded.</summary>
    public bool FragmentedMp4 { get; init; } = true;

    public bool CaptureCursor { get; init; } = true;

    /// <summary>Record the system sound (WASAPI loopback).</summary>
    public bool CaptureSystemAudio { get; init; } = true;

    /// <summary>Microphone ids (from <see cref="AudioSourceInfo.Id"/>) to mix. Empty = none.</summary>
    public IReadOnlyList<string> MicrophoneDeviceIds { get; init; } = [];
}

/// <summary>Result of a recording that finished fine.</summary>
public sealed record Mp4RecordingResult(
    string OutputPath,
    TimeSpan Duration,
    int Width,
    int Height,
    long FileSizeBytes);

/// <summary>Fatal recording failure (recording stopped; with fMP4 the file may still play).</summary>
public sealed record RecordingFailure(string Message, Exception? Exception = null);
