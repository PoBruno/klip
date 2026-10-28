using Klip.Core.Recording;

namespace Klip.Core.Tests.Recording;

public class RecorderProtocolTests
{
    [Fact]
    public void StartMessage_RoundTripsEveryOption()
    {
        var options = new Mp4RecordingOptions
        {
            Region = new RecordingRegion(-1920, 10, 1001, 777),
            OutputPath = @"C:\Videos\a b\clip.mp4",
            Fps = 60,
            BitrateKbps = 12000,
            Quality = VideoQualityPreset.High,
            OutputHeight = 720,
            Codec = VideoCodec.Hevc,
            FragmentedMp4 = false,
            CaptureCursor = false,
            CaptureSystemAudio = false,
            MicrophoneDeviceIds = ["{0.0.1.00000000}.{abc}", "second"],
        };

        var line = RecorderProtocol.Serialize(new RecorderMessage { Kind = RecorderMessageKind.Start, Id = 7, Options = options });
        var back = RecorderProtocol.TryParse(line);

        Assert.NotNull(back);
        Assert.Equal(RecorderMessageKind.Start, back.Kind);
        Assert.Equal(7, back.Id);
        var o = back.Options!;
        Assert.Equal(options.Region, o.Region);
        Assert.Equal(options.OutputPath, o.OutputPath);
        Assert.Equal(60, o.Fps);
        Assert.Equal(12000, o.BitrateKbps);
        Assert.Equal(VideoQualityPreset.High, o.Quality);
        Assert.Equal(720, o.OutputHeight);
        Assert.Equal(VideoCodec.Hevc, o.Codec);
        Assert.False(o.FragmentedMp4);
        Assert.False(o.CaptureCursor);
        Assert.False(o.CaptureSystemAudio);
        Assert.Equal(options.MicrophoneDeviceIds, o.MicrophoneDeviceIds);
    }

    [Fact]
    public void Serialized_IsAlwaysOneLine()
    {
        var line = RecorderProtocol.Serialize(new RecorderMessage
        {
            Kind = RecorderMessageKind.Warning,
            Text = "first\r\nsecond\nthird",
        });

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.Equal("first\r\nsecond\nthird", RecorderProtocol.TryParse(line)!.Text);
    }

    [Fact]
    public void StatsAndResult_RoundTrip()
    {
        var plan = VideoEncodingMath.Plan(new VideoEncodingRequest
        {
            Source = new VideoSize(2560, 1440),
            Fps = 60,
        });
        var message = new RecorderMessage
        {
            Kind = RecorderMessageKind.Stats,
            Plan = plan,
            Stats = new RecorderStats(TimeSpan.FromSeconds(12.5), 0.031, IsRecording: true, IsPaused: false),
            Result = new Mp4RecordingResult(@"C:\x.mp4", TimeSpan.FromSeconds(3), 1920, 1080, 123456),
        };

        var back = RecorderProtocol.TryParse(RecorderProtocol.Serialize(message))!;

        Assert.Equal(plan, back.Plan);
        Assert.Equal(TimeSpan.FromSeconds(12.5), back.Stats!.Elapsed);
        Assert.Equal(0.031, back.Stats.DroppedFrameRatio);
        Assert.True(back.Stats.IsRecording);
        Assert.Equal(123456, back.Result!.FileSizeBytes);
        Assert.Equal(1920, back.Result.Width);
    }

    [Fact]
    public void KindsTravelAsNames()
    {
        var line = RecorderProtocol.Serialize(new RecorderMessage { Kind = RecorderMessageKind.SetCursor, Flag = true });

        Assert.Contains("\"SetCursor\"", line);
        Assert.True(RecorderProtocol.TryParse(line)!.Flag);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{\"Kind\":\"NoSuchKind\"}")]
    [InlineData("{\"Kind\":")]
    public void TryParse_BadLines_ReturnNullWithoutThrowing(string? line)
    {
        Assert.Null(RecorderProtocol.TryParse(line));
    }

    [Fact]
    public void Arguments_BuildThenParse()
    {
        var pipe = RecorderProtocol.NewPipeName(4321);
        var args = RecorderProtocol.BuildArguments(pipe, 4321).Split(' ');

        Assert.True(RecorderProtocol.IsRecorderInvocation(args));
        Assert.True(RecorderProtocol.TryParseArguments(args, out var parsedPipe, out var parent));
        Assert.Equal(pipe, parsedPipe);
        Assert.Equal(4321, parent);
    }

    [Fact]
    public void PipeNames_AreUniquePerLaunch()
    {
        Assert.NotEqual(RecorderProtocol.NewPipeName(1), RecorderProtocol.NewPipeName(1));
    }

    [Theory]
    [InlineData("--recorder")]
    [InlineData("--recorder", "--pipe")]
    [InlineData("--recorder", "--pipe", "bad name")]
    [InlineData("--recorder", "--pipe", @"..\evil")]
    [InlineData("--pipe", "ok", "--recorder")]
    public void Arguments_Invalid_AreRefused(params string[] args)
    {
        Assert.False(RecorderProtocol.TryParseArguments(args, out _, out _));
    }

    [Fact]
    public void Arguments_ParentIsOptional()
    {
        Assert.True(RecorderProtocol.TryParseArguments(["--recorder", "--pipe", "p1"], out var pipe, out var parent));
        Assert.Equal("p1", pipe);
        Assert.Equal(0, parent);
    }

    [Fact]
    public void NormalAppLaunch_IsNotARecorder()
    {
        Assert.False(RecorderProtocol.IsRecorderInvocation([]));
        Assert.False(RecorderProtocol.IsRecorderInvocation(["--verbose"]));
    }
}
