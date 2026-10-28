namespace Klip.Core.Recording;

/// <summary>
/// MP4 recorder (H.264/HEVC/AV1 + AAC) used by the app, implemented in Interop on top
/// of the Media Foundation SinkWriter and WASAPI.
/// </summary>
public interface IMp4Recorder : IAsyncDisposable
{
    /// <summary>
    /// Optional warm up while the countdown runs: device, capture, encoder and audio get
    /// created and frames are thrown away until <see cref="StartAsync"/>. After a prepare,
    /// StartAsync ignores its options and keeps the prepared ones. Disposing a prepared
    /// recorder that never started removes the empty file.
    /// </summary>
    Task PrepareAsync(Mp4RecordingOptions options, CancellationToken ct = default);

    /// <summary>
    /// Starts recording (or, after <see cref="PrepareAsync"/>, lets the first frame
    /// through). Throws if a recording is already running.
    /// </summary>
    Task StartAsync(Mp4RecordingOptions options, CancellationToken ct = default);

    /// <summary>Pauses; timestamps stay contiguous after resume.</summary>
    Task PauseAsync();

    Task ResumeAsync();

    /// <summary>
    /// Stops and finalizes the file. Can take seconds, so the caller should show a
    /// "finalizing" state.
    /// </summary>
    Task<Mp4RecordingResult> StopAsync();

    bool IsRecording { get; }
    bool IsPaused { get; }

    /// <summary>Recorded time, pauses excluded.</summary>
    TimeSpan Elapsed { get; }

    /// <summary>
    /// What the encoder ended up with (size, codec, fps, software fallback). null before
    /// the first frame reaches the encoder.
    /// </summary>
    VideoEncodingPlan? ActivePlan { get; }

    /// <summary>Share of frames lost so far (0..1), for the live drop indicator.</summary>
    double DroppedFrameRatio { get; }

    // ------ live toggles, safe before/after recording (become initial state or no-op) ------

    /// <summary>Mutes/unmutes microphones (gain ramp in the mixer; capture never stops).</summary>
    void SetMicrophoneMuted(bool muted);
    bool IsMicrophoneMuted { get; }

    /// <summary>Mutes/unmutes the system sound (loopback).</summary>
    void SetSystemAudioMuted(bool muted);
    bool IsSystemAudioMuted { get; }

    /// <summary>Shows/hides the cursor in the recording, live.</summary>
    void SetCursorCaptureEnabled(bool enabled);
    bool IsCursorCaptureEnabled { get; }

    /// <summary>Fatal failure (recording stopped by itself). Handler should call StopAsync/DisposeAsync.</summary>
    event Action<RecordingFailure>? Failed;

    /// <summary>
    /// Recording stopped on its own with a valid file (low disk, for example). The consumer
    /// should close the recording ui as if the user had stopped. Not raised by StopAsync.
    /// </summary>
    event Action<Mp4RecordingResult>? AutoStopped;

    /// <summary>Non fatal warning (mic removed, low disk, frame drops).</summary>
    event Action<string>? Warning;
}
