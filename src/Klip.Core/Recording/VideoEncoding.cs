namespace Klip.Core.Recording;

public enum VideoCodec
{
    H264,
    Hevc,
    Av1,
}

public enum VideoQualityPreset
{
    Small,
    Balanced,
    High,
}

public readonly record struct VideoSize(int Width, int Height);

public readonly record struct VideoRect(int Left, int Top, int Width, int Height);

/// <summary>Knobs handed to the encoder; the user never sees any of these.</summary>
/// <param name="QualityVsSpeed">CODECAPI QualityVsSpeed, 0 (fast) to 100 (best).</param>
/// <param name="ScreenContent">true = tuned for text and ui (DisplayRemoting), false = motion (Archive).</param>
/// <param name="GopFrames">Keyframe interval in frames.</param>
/// <param name="BFrames">B pictures between references.</param>
public readonly record struct EncoderTuning(int QualityVsSpeed, bool ScreenContent, int GopFrames, int BFrames);

/// <summary>Everything the recorder needs to configure one mp4 session.</summary>
public sealed record VideoEncodingPlan(
    VideoSize Output,
    VideoCodec Codec,
    bool SoftwareEncoder,
    bool CodecFellBack,
    int Fps,
    int BitrateKbps,
    int PeakBitrateKbps,
    EncoderTuning Tuning);

/// <summary>Inputs for <see cref="VideoEncodingMath.Plan"/>.</summary>
public sealed record VideoEncodingRequest
{
    /// <summary>Captured area in physical px (already clamped to the monitor).</summary>
    public required VideoSize Source { get; init; }

    public int Fps { get; init; } = 30;

    public VideoQualityPreset Quality { get; init; } = VideoQualityPreset.Balanced;

    /// <summary>null = native size.</summary>
    public int? TargetHeight { get; init; }

    /// <summary>null = auto.</summary>
    public VideoCodec? RequestedCodec { get; init; }

    /// <summary>Codecs with a hardware encoder on this machine.</summary>
    public IReadOnlyCollection<VideoCodec> HardwareCodecs { get; init; } = [];

    /// <summary>Old hidden override in kbps; 0 = use the table.</summary>
    public int BitrateOverrideKbps { get; init; }

    /// <summary>false when the gpu scaler is not available: output stays at the source size.</summary>
    public bool CanScale { get; init; } = true;
}

/// <summary>
/// Pure math behind the mp4 recorder: output size, letterbox, codec pick, bitrate
/// and encoder tuning. No Media Foundation in here so it can be unit tested.
/// </summary>
public static class VideoEncodingMath
{
    /// <summary>Practical width ceiling of h264 encoders (level 5.1/5.2).</summary>
    public const int H264MaxWidth = 4096;

    // how long the cfr loop may fall behind and still fill the gap with duplicates
    private const double CatchUpWindowSeconds = 0.130;

    // (pixels, kbps at 30 fps, kbps at 60 fps) for h264
    private static readonly (double Pixels, double Kbps30, double Kbps60)[] BitrateTable =
    [
        (1280 * 720, 6000, 9000),
        (1920 * 1080, 10000, 16000),
        (2560 * 1440, 18000, 28000),
        (3840 * 2160, 40000, 60000),
    ];

    public static VideoQualityPreset ParseQuality(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "small" => VideoQualityPreset.Small,
        "high" => VideoQualityPreset.High,
        _ => VideoQualityPreset.Balanced,
    };

    public static string ToSetting(VideoQualityPreset quality) => quality switch
    {
        VideoQualityPreset.Small => "small",
        VideoQualityPreset.High => "high",
        _ => "balanced",
    };

    /// <summary>"native" (or anything unknown) = null; "1440"/"1080"/"720" = that height.</summary>
    public static int? ParseTargetHeight(string? value) => value?.Trim() switch
    {
        "1440" => 1440,
        "1080" => 1080,
        "720" => 720,
        _ => null,
    };

    /// <summary>"auto" (or unknown) = null.</summary>
    public static VideoCodec? ParseCodec(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "h264" => VideoCodec.H264,
        "hevc" => VideoCodec.Hevc,
        "av1" => VideoCodec.Av1,
        _ => null,
    };

    public static string ToSetting(VideoCodec codec) => codec switch
    {
        VideoCodec.Hevc => "hevc",
        VideoCodec.Av1 => "av1",
        _ => "h264",
    };

    /// <summary>Only 30 and 60 are offered; anything else snaps to the closest.</summary>
    public static int NormalizeFps(int fps) => fps >= 45 ? 60 : 30;

    public static double QualityFactor(VideoQualityPreset quality) => quality switch
    {
        VideoQualityPreset.Small => 0.6,
        VideoQualityPreset.High => 1.6,
        _ => 1.0,
    };

    public static double CodecFactor(VideoCodec codec) => codec == VideoCodec.H264 ? 1.0 : 0.6;

    /// <summary>
    /// Duplicates the cfr loop may emit in one tick: enough to cover ~130 ms at any fps
    /// (4 at 30 fps, 8 at 60), never less than the old fixed 4.
    /// </summary>
    public static int MaxCatchUpFrames(int fps) =>
        Math.Max(4, (int)Math.Ceiling(Math.Max(1, fps) * CatchUpWindowSeconds));

    /// <summary>
    /// Output size for a source and a target "p" height. Only scales down, keeps the aspect
    /// ratio and works for portrait too: the target is a 16:9 box (1080 -> 1920x1080) and the
    /// source long side is matched against the box long side. Always even.
    /// </summary>
    public static VideoSize ComputeOutputSize(VideoSize source, int? targetHeight)
    {
        int srcW = Even(source.Width);
        int srcH = Even(source.Height);
        if (targetHeight is not int target || target <= 0)
            return new VideoSize(srcW, srcH);

        double boxShort = target;
        double boxLong = Even((int)Math.Round(target * 16.0 / 9.0));
        double longSide = Math.Max(srcW, srcH);
        double shortSide = Math.Min(srcW, srcH);
        double scale = Math.Min(1.0, Math.Min(boxLong / longSide, boxShort / shortSide));
        if (scale >= 1.0)
            return new VideoSize(srcW, srcH);

        return new VideoSize(Even((int)Math.Round(srcW * scale)), Even((int)Math.Round(srcH * scale)));
    }

    /// <summary>Shrinks a size so its width fits <paramref name="maxWidth"/>, aspect kept, even.</summary>
    public static VideoSize FitWidth(VideoSize size, int maxWidth)
    {
        if (size.Width <= maxWidth)
            return size;
        double scale = maxWidth / (double)size.Width;
        return new VideoSize(Even(maxWidth), Even((int)Math.Round(size.Height * scale)));
    }

    /// <summary>
    /// Where a source of <paramref name="srcWidth"/> x <paramref name="srcHeight"/> lands inside
    /// the output frame: scaled to fit, centered, black bars on the leftover sides.
    /// </summary>
    public static VideoRect Letterbox(int srcWidth, int srcHeight, int outWidth, int outHeight)
    {
        if (srcWidth <= 0 || srcHeight <= 0 || outWidth <= 0 || outHeight <= 0)
            return new VideoRect(0, 0, Math.Max(0, outWidth), Math.Max(0, outHeight));

        double scale = Math.Min(outWidth / (double)srcWidth, outHeight / (double)srcHeight);
        int width = Math.Clamp((int)Math.Round(srcWidth * scale), 1, outWidth);
        int height = Math.Clamp((int)Math.Round(srcHeight * scale), 1, outHeight);

        // a bar of 1 px is just rounding noise; fill the frame instead
        if (outWidth - width <= 1)
            width = outWidth;
        if (outHeight - height <= 1)
            height = outHeight;

        // even offsets keep the nv12 chroma grid aligned with the picture
        int left = ((outWidth - width) / 2) & ~1;
        int top = ((outHeight - height) / 2) & ~1;
        return new VideoRect(left, top, width, height);
    }

    /// <summary>
    /// Average bitrate in kbps: table above (linear in pixel count between rows, proportional
    /// outside them), linear between 30 and 60 fps, times the quality and codec factors.
    /// Rounded to 100 kbps, never below 1 Mbps.
    /// </summary>
    public static int BitrateKbps(VideoSize output, int fps, VideoQualityPreset quality, VideoCodec codec)
    {
        double pixels = Math.Max(1, (double)output.Width * output.Height);
        double kbps30 = Interpolate(pixels, row => row.Kbps30);
        double kbps60 = Interpolate(pixels, row => row.Kbps60);

        double baseKbps = fps switch
        {
            <= 30 => kbps30 * Math.Max(1, fps) / 30.0,
            >= 60 => kbps60 * fps / 60.0,
            _ => kbps30 + (kbps60 - kbps30) * (fps - 30) / 30.0,
        };

        double kbps = baseKbps * QualityFactor(quality) * CodecFactor(codec);
        return Math.Max(1000, (int)Math.Round(kbps / 100.0) * 100);
    }

    /// <summary>Peak for PeakConstrainedVBR: 1.5x the average.</summary>
    public static int PeakKbps(int averageKbps) => averageKbps * 3 / 2;

    /// <summary>
    /// Picks the codec. An explicit choice wins when the gpu can encode it; otherwise auto:
    /// h264, or hevc (then av1) when the output is wider than h264 allows. When nothing fits
    /// the width, h264 stays and the caller must shrink to <see cref="CodecChoice.MaxWidth"/>.
    /// </summary>
    public static CodecChoice ChooseCodec(VideoCodec? requested, int outputWidth, IReadOnlyCollection<VideoCodec> hardware)
    {
        bool fellBack = false;
        if (requested is VideoCodec wanted)
        {
            if (hardware.Contains(wanted))
            {
                int? maxWidth = wanted == VideoCodec.H264 && outputWidth > H264MaxWidth ? H264MaxWidth : null;
                return new CodecChoice(wanted, Software: false, maxWidth, FellBack: false);
            }

            // h264 always has the software encoder behind it, the others do not
            if (wanted != VideoCodec.H264)
                fellBack = true;
            else
                return new CodecChoice(VideoCodec.H264, Software: true,
                    outputWidth > H264MaxWidth ? H264MaxWidth : null, FellBack: false);
        }

        if (outputWidth > H264MaxWidth)
        {
            if (hardware.Contains(VideoCodec.Hevc))
                return new CodecChoice(VideoCodec.Hevc, false, null, fellBack);
            if (hardware.Contains(VideoCodec.Av1))
                return new CodecChoice(VideoCodec.Av1, false, null, fellBack);
        }

        bool software = !hardware.Contains(VideoCodec.H264);
        return new CodecChoice(VideoCodec.H264, software,
            outputWidth > H264MaxWidth ? H264MaxWidth : null, fellBack);
    }

    /// <summary>
    /// 30 fps is the "screen" mode (lessons, demos): tuned for sharp text. 60 fps is the
    /// "motion" mode (games): archive scenario and a faster preset above 1080p so the
    /// encoder keeps up. B-frames stay off until the media editor seeks fine with them.
    /// </summary>
    public static EncoderTuning Tuning(int fps, VideoSize output)
    {
        bool screen = fps <= 30;
        bool large = (long)output.Width * output.Height > 1920L * 1080;
        int qvs = screen || !large ? 80 : 60;
        return new EncoderTuning(qvs, screen, Math.Max(1, fps) * 2, 0);
    }

    /// <summary>Puts all of the above together for one recording.</summary>
    public static VideoEncodingPlan Plan(VideoEncodingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        int fps = NormalizeFps(request.Fps);
        var output = request.CanScale
            ? ComputeOutputSize(request.Source, request.TargetHeight)
            : new VideoSize(Even(request.Source.Width), Even(request.Source.Height));

        var choice = ChooseCodec(request.RequestedCodec, output.Width, request.HardwareCodecs);
        if (choice.MaxWidth is int maxWidth && request.CanScale)
            output = FitWidth(output, maxWidth);

        // the software encoder does not keep up with 60 fps on a normal cpu
        if (choice.Software)
            fps = 30;

        int kbps = request.BitrateOverrideKbps > 0
            ? request.BitrateOverrideKbps
            : BitrateKbps(output, fps, request.Quality, choice.Codec);

        return new VideoEncodingPlan(
            output,
            choice.Codec,
            choice.Software,
            choice.FellBack,
            fps,
            kbps,
            PeakKbps(kbps),
            Tuning(fps, output));
    }

    private static int Even(int value) => Math.Max(2, value & ~1);

    private static double Interpolate(double pixels, Func<(double Pixels, double Kbps30, double Kbps60), double> pick)
    {
        var first = BitrateTable[0];
        if (pixels <= first.Pixels)
            return pick(first) * pixels / first.Pixels;

        for (int i = 1; i < BitrateTable.Length; i++)
        {
            var low = BitrateTable[i - 1];
            var high = BitrateTable[i];
            if (pixels <= high.Pixels)
            {
                double t = (pixels - low.Pixels) / (high.Pixels - low.Pixels);
                return pick(low) + (pick(high) - pick(low)) * t;
            }
        }

        var last = BitrateTable[^1];
        return pick(last) * pixels / last.Pixels;
    }
}

/// <summary>Result of <see cref="VideoEncodingMath.ChooseCodec"/>.</summary>
/// <param name="Codec">Codec to use.</param>
/// <param name="Software">No hardware encoder for it: Media Foundation falls back to the cpu one.</param>
/// <param name="MaxWidth">Output must be shrunk to this width (h264 limit), or null.</param>
/// <param name="FellBack">The codec picked in settings is not available here.</param>
public readonly record struct CodecChoice(VideoCodec Codec, bool Software, int? MaxWidth, bool FellBack);
