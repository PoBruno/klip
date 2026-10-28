namespace Klip.Core.Recording;

/// <summary>Options for the frame capture engine.</summary>
public sealed class FrameCaptureOptions
{
    /// <summary>Include the mouse cursor in the frames. Works on 19041+.</summary>
    public bool CaptureCursor { get; init; } = true;

    /// <summary>
    /// Ask the system to drop the yellow capture border. If consent is denied or the api
    /// is missing, capture goes on with the border.
    /// </summary>
    public bool RequestBorderless { get; init; } = true;

    /// <summary>
    /// null = VFR (QPC timestamps, the content sets the pace).
    /// A value = CFR: frames resampled at 1/FPS, last frame repeated while the screen is
    /// still, plus a BGRA readback to the cpu (gif mode).
    /// </summary>
    public int? FixedFps { get; init; }

    /// <summary>
    /// GPU frames come out as NV12 at <see cref="OutputSize"/>: crop, scale and color
    /// conversion in a single video processor pass. If the gpu cannot do it the engine
    /// silently keeps the old BGRA path at the crop size (check
    /// <c>FrameCaptureEngine.IsNv12Output</c> after start).
    /// </summary>
    public bool Nv12Output { get; init; }

    /// <summary>Size of the NV12 frames; null = crop size. Ignored without <see cref="Nv12Output"/>.</summary>
    public VideoSize? OutputSize { get; init; }

    /// <summary>
    /// MMCSS "Capture" on the cfr loop thread and a higher gpu thread priority on the
    /// device. Only for the recorder process: inside the ui process it would fight the app.
    /// </summary>
    public bool HighPriority { get; init; }
}
