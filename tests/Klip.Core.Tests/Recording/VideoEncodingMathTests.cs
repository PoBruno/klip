using Klip.Core.Recording;

namespace Klip.Core.Tests.Recording;

public class VideoEncodingMathTests
{
    private static readonly VideoCodec[] AllHardware = [VideoCodec.H264, VideoCodec.Hevc, VideoCodec.Av1];

    // ----- output size -----

    [Fact]
    public void OutputSize_Native_KeepsSourceRoundedToEven()
    {
        var size = VideoEncodingMath.ComputeOutputSize(new VideoSize(1001, 777), null);
        Assert.Equal(new VideoSize(1000, 776), size);
    }

    [Fact]
    public void OutputSize_720pFrom1440p_Is1280x720()
    {
        var size = VideoEncodingMath.ComputeOutputSize(new VideoSize(2560, 1440), 720);
        Assert.Equal(new VideoSize(1280, 720), size);
    }

    [Theory]
    [InlineData(1920, 1080, 1440)]
    [InlineData(1280, 720, 1080)]
    [InlineData(800, 600, 720)]
    public void OutputSize_NeverScalesUp(int width, int height, int target)
    {
        var size = VideoEncodingMath.ComputeOutputSize(new VideoSize(width, height), target);
        Assert.Equal(new VideoSize(width, height), size);
    }

    [Fact]
    public void OutputSize_Ultrawide_FitsTheLongSideAndKeepsAspect()
    {
        // 3440x1440 at 1080p: long side limited to 1920
        var size = VideoEncodingMath.ComputeOutputSize(new VideoSize(3440, 1440), 1080);
        Assert.Equal(1920, size.Width);
        Assert.Equal(804, size.Height);
        Assert.Equal(3440 / 1440.0, size.Width / (double)size.Height, 2);
    }

    [Fact]
    public void OutputSize_Portrait_MatchesLongSideAgainstBoxLongSide()
    {
        // a 1080x1920 phone-like region is already "1080p"
        var size = VideoEncodingMath.ComputeOutputSize(new VideoSize(1080, 1920), 1080);
        Assert.Equal(new VideoSize(1080, 1920), size);

        var smaller = VideoEncodingMath.ComputeOutputSize(new VideoSize(1080, 1920), 720);
        Assert.Equal(new VideoSize(720, 1280), smaller);
    }

    [Fact]
    public void OutputSize_AlwaysEven()
    {
        var size = VideoEncodingMath.ComputeOutputSize(new VideoSize(1999, 1333), 720);
        Assert.Equal(0, size.Width % 2);
        Assert.Equal(0, size.Height % 2);
    }

    [Fact]
    public void FitWidth_ShrinksTo4096KeepingAspect()
    {
        var size = VideoEncodingMath.FitWidth(new VideoSize(5120, 1440), 4096);
        Assert.Equal(new VideoSize(4096, 1152), size);
    }

    // ----- letterbox -----

    [Fact]
    public void Letterbox_SameAspect_FillsTheFrame()
    {
        var rect = VideoEncodingMath.Letterbox(2560, 1440, 1280, 720);
        Assert.Equal(new VideoRect(0, 0, 1280, 720), rect);
    }

    [Fact]
    public void Letterbox_NarrowerSource_GetsPillarBars()
    {
        // 4:3 into 16:9: 960 wide, centered
        var rect = VideoEncodingMath.Letterbox(1024, 768, 1280, 720);
        Assert.Equal(new VideoRect(160, 0, 960, 720), rect);
    }

    [Fact]
    public void Letterbox_WiderSource_GetsTopAndBottomBars()
    {
        var rect = VideoEncodingMath.Letterbox(2000, 500, 1000, 1000);
        Assert.Equal(1000, rect.Width);
        Assert.Equal(250, rect.Height);
        Assert.Equal(0, rect.Left);
        Assert.Equal(374, rect.Top); // 375 rounded down to even
    }

    [Fact]
    public void Letterbox_RoundingNoiseDoesNotLeaveOnePixelBars()
    {
        var rect = VideoEncodingMath.Letterbox(1001, 777, 1000, 776);
        Assert.Equal(new VideoRect(0, 0, 1000, 776), rect);
    }

    // ----- bitrate -----

    [Theory]
    [InlineData(1280, 720, 30, 6000)]
    [InlineData(1280, 720, 60, 9000)]
    [InlineData(1920, 1080, 30, 10000)]
    [InlineData(1920, 1080, 60, 16000)]
    [InlineData(2560, 1440, 30, 18000)]
    [InlineData(2560, 1440, 60, 28000)]
    [InlineData(3840, 2160, 30, 40000)]
    [InlineData(3840, 2160, 60, 60000)]
    public void Bitrate_MatchesTableRows(int width, int height, int fps, int expected)
    {
        var kbps = VideoEncodingMath.BitrateKbps(new VideoSize(width, height), fps, VideoQualityPreset.Balanced, VideoCodec.H264);
        Assert.Equal(expected, kbps);
    }

    [Fact]
    public void Bitrate_QualityAndCodecFactors()
    {
        var size = new VideoSize(1920, 1080);
        Assert.Equal(9600, VideoEncodingMath.BitrateKbps(size, 60, VideoQualityPreset.Small, VideoCodec.H264));
        Assert.Equal(25600, VideoEncodingMath.BitrateKbps(size, 60, VideoQualityPreset.High, VideoCodec.H264));
        Assert.Equal(9600, VideoEncodingMath.BitrateKbps(size, 60, VideoQualityPreset.Balanced, VideoCodec.Hevc));
        Assert.Equal(9600, VideoEncodingMath.BitrateKbps(size, 60, VideoQualityPreset.Balanced, VideoCodec.Av1));
    }

    [Fact]
    public void Bitrate_BetweenRows_IsInterpolatedAndMonotonic()
    {
        int at1080 = VideoEncodingMath.BitrateKbps(new VideoSize(1920, 1080), 30, VideoQualityPreset.Balanced, VideoCodec.H264);
        int between = VideoEncodingMath.BitrateKbps(new VideoSize(2240, 1260), 30, VideoQualityPreset.Balanced, VideoCodec.H264);
        int at1440 = VideoEncodingMath.BitrateKbps(new VideoSize(2560, 1440), 30, VideoQualityPreset.Balanced, VideoCodec.H264);
        Assert.InRange(between, at1080 + 1, at1440 - 1);
    }

    [Fact]
    public void Bitrate_TinyRegion_HasAFloor()
    {
        var kbps = VideoEncodingMath.BitrateKbps(new VideoSize(64, 64), 30, VideoQualityPreset.Small, VideoCodec.Hevc);
        Assert.Equal(1000, kbps);
    }

    [Fact]
    public void Peak_IsOneAndAHalfTimesTheAverage() =>
        Assert.Equal(24000, VideoEncodingMath.PeakKbps(16000));

    // ----- codec choice -----

    [Fact]
    public void Codec_Auto_IsH264UpTo4096()
    {
        var choice = VideoEncodingMath.ChooseCodec(null, 4096, AllHardware);
        Assert.Equal(new CodecChoice(VideoCodec.H264, false, null, false), choice);
    }

    [Fact]
    public void Codec_Auto_IsHevcAbove4096()
    {
        var choice = VideoEncodingMath.ChooseCodec(null, 5120, AllHardware);
        Assert.Equal(VideoCodec.Hevc, choice.Codec);
        Assert.Null(choice.MaxWidth);
    }

    [Fact]
    public void Codec_Auto_Above4096_FallsToAv1ThenShrinkingH264()
    {
        Assert.Equal(VideoCodec.Av1, VideoEncodingMath.ChooseCodec(null, 5120, [VideoCodec.H264, VideoCodec.Av1]).Codec);

        var h264Only = VideoEncodingMath.ChooseCodec(null, 5120, [VideoCodec.H264]);
        Assert.Equal(VideoCodec.H264, h264Only.Codec);
        Assert.Equal(4096, h264Only.MaxWidth);
    }

    [Fact]
    public void Codec_Explicit_WinsWhenAvailable()
    {
        var choice = VideoEncodingMath.ChooseCodec(VideoCodec.Av1, 1920, AllHardware);
        Assert.Equal(new CodecChoice(VideoCodec.Av1, false, null, false), choice);
    }

    [Fact]
    public void Codec_ExplicitMissing_FallsBackAndSaysSo()
    {
        var choice = VideoEncodingMath.ChooseCodec(VideoCodec.Hevc, 1920, [VideoCodec.H264]);
        Assert.Equal(VideoCodec.H264, choice.Codec);
        Assert.True(choice.FellBack);
    }

    [Fact]
    public void Codec_NoHardware_IsSoftwareH264()
    {
        var choice = VideoEncodingMath.ChooseCodec(null, 1920, []);
        Assert.Equal(VideoCodec.H264, choice.Codec);
        Assert.True(choice.Software);
    }

    // ----- plan -----

    [Fact]
    public void Plan_WideMonitorWithoutHevc_ShrinksToH264Limit()
    {
        var plan = VideoEncodingMath.Plan(new VideoEncodingRequest
        {
            Source = new VideoSize(5120, 1440),
            Fps = 60,
            HardwareCodecs = [VideoCodec.H264],
        });
        Assert.Equal(VideoCodec.H264, plan.Codec);
        Assert.Equal(new VideoSize(4096, 1152), plan.Output);
    }

    [Fact]
    public void Plan_WideMonitorWithHevc_KeepsNativeSize()
    {
        var plan = VideoEncodingMath.Plan(new VideoEncodingRequest
        {
            Source = new VideoSize(5120, 1440),
            Fps = 60,
            HardwareCodecs = AllHardware,
        });
        Assert.Equal(VideoCodec.Hevc, plan.Codec);
        Assert.Equal(new VideoSize(5120, 1440), plan.Output);
    }

    [Fact]
    public void Plan_SoftwareEncoder_CapsAt30Fps()
    {
        var plan = VideoEncodingMath.Plan(new VideoEncodingRequest
        {
            Source = new VideoSize(1920, 1080),
            Fps = 60,
            HardwareCodecs = [],
        });
        Assert.True(plan.SoftwareEncoder);
        Assert.Equal(30, plan.Fps);
        Assert.Equal(10000, plan.BitrateKbps);
    }

    [Fact]
    public void Plan_WithoutGpuScaler_KeepsSourceSize()
    {
        var plan = VideoEncodingMath.Plan(new VideoEncodingRequest
        {
            Source = new VideoSize(2560, 1440),
            TargetHeight = 720,
            HardwareCodecs = AllHardware,
            CanScale = false,
        });
        Assert.Equal(new VideoSize(2560, 1440), plan.Output);
    }

    [Fact]
    public void Plan_BitrateOverride_Wins()
    {
        var plan = VideoEncodingMath.Plan(new VideoEncodingRequest
        {
            Source = new VideoSize(1920, 1080),
            HardwareCodecs = AllHardware,
            BitrateOverrideKbps = 12345,
        });
        Assert.Equal(12345, plan.BitrateKbps);
        Assert.Equal(18517, plan.PeakBitrateKbps);
    }

    [Fact]
    public void Plan_TuningFollowsFpsMode()
    {
        var screen = VideoEncodingMath.Tuning(30, new VideoSize(2560, 1440));
        Assert.True(screen.ScreenContent);
        Assert.Equal(80, screen.QualityVsSpeed);
        Assert.Equal(60, screen.GopFrames);

        var motion = VideoEncodingMath.Tuning(60, new VideoSize(2560, 1440));
        Assert.False(motion.ScreenContent);
        Assert.Equal(60, motion.QualityVsSpeed);
        Assert.Equal(120, motion.GopFrames);

        Assert.Equal(80, VideoEncodingMath.Tuning(60, new VideoSize(1920, 1080)).QualityVsSpeed);
    }

    // ----- small helpers -----

    [Theory]
    [InlineData(30, 4)]
    [InlineData(60, 8)]
    [InlineData(15, 4)]
    public void CatchUp_CoversAbout130Ms(int fps, int expected) =>
        Assert.Equal(expected, VideoEncodingMath.MaxCatchUpFrames(fps));

    [Theory]
    [InlineData(0, 30)]
    [InlineData(30, 30)]
    [InlineData(44, 30)]
    [InlineData(45, 60)]
    [InlineData(144, 60)]
    public void NormalizeFps_SnapsTo30Or60(int input, int expected) =>
        Assert.Equal(expected, VideoEncodingMath.NormalizeFps(input));

    [Fact]
    public void Parsing_RoundTripsAndDefaults()
    {
        Assert.Equal(VideoQualityPreset.High, VideoEncodingMath.ParseQuality("high"));
        Assert.Equal(VideoQualityPreset.Balanced, VideoEncodingMath.ParseQuality("whatever"));
        Assert.Equal("small", VideoEncodingMath.ToSetting(VideoQualityPreset.Small));
        Assert.Equal(1080, VideoEncodingMath.ParseTargetHeight("1080"));
        Assert.Null(VideoEncodingMath.ParseTargetHeight("native"));
        Assert.Equal(VideoCodec.Hevc, VideoEncodingMath.ParseCodec("HEVC"));
        Assert.Null(VideoEncodingMath.ParseCodec("auto"));
        Assert.Equal("av1", VideoEncodingMath.ToSetting(VideoCodec.Av1));
    }
}
