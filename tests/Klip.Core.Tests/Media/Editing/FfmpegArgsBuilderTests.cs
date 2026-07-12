using Klip.Core.Media.Editing;

namespace Klip.Core.Tests.Media.Editing;

/// <summary>
/// RF-F5.11 golden-string tests: the builder is pure and deterministic, so we
/// assert the exact argument string.
/// </summary>
public class FfmpegArgsBuilderTests
{
    private static TimeSpan Sec(double s) => TimeSpan.FromSeconds(s);

    /// <summary>Video project with segments [30-45][5-20] (reordered) from a 60 s source.</summary>
    private static MediaEditProject ReorderedTwoSegments(int audioTracks)
    {
        var p = MediaEditProject.CreateVideo(@"C:\rec\in.mp4", Sec(60), audioTracks)
            .SplitAt(Sec(5)).SplitAt(Sec(20)).SplitAt(Sec(30)).SplitAt(Sec(45));
        // [0-5][5-20][20-30][30-45][45-60]
        // ripple removal keeps the timeline contiguous (RF-F5.18)
        p = p.RemoveSegmentRipple(p.Segments[0].Id); // drop 0-5
        p = p.RemoveSegmentRipple(p.Segments[1].Id); // drop 20-30
        p = p.RemoveSegmentRipple(p.Segments[2].Id); // drop 45-60
        // [5-20][30-45]
        return p.MoveSegment(p.Segments[1].Id, 0); // [30-45][5-20]
    }

    /// <summary>
    /// Video project [0-20] | 5 s gap | [20-60] from a 60 s source (RF-F5.17):
    /// the second segment is dragged from 20 s to 25 s on the edited timeline.
    /// </summary>
    private static MediaEditProject GapInTheMiddle(int audioTracks)
    {
        var p = MediaEditProject.CreateVideo(@"C:\rec\in.mp4", Sec(60), audioTracks).SplitAt(Sec(20));
        return p.MoveSegmentTo(p.Segments[1].Id, Sec(25));
    }

    [Fact]
    public void Video_GapInMiddle_VideoAndAudio_GoldenString()
    {
        // RF-F5.20: the gap becomes synthetic black video (color) + silence
        // (anullsrc) interleaved in timeline order inside the concat
        var p = GapInTheMiddle(audioTracks: 1);
        var s = new VideoExportSettings { VideoCodec = "h264_nvenc", FastStart = true, Width = 1280, Height = 720, Fps = 30 };

        var args = FfmpegArgsBuilder.BuildVideoExportArgs(p, s, @"C:\out\out.mp4");

        Assert.Equal(
            @"-i ""C:\rec\in.mp4"" -filter_complex ""[0:v]trim=start=0:end=20,setpts=PTS-STARTPTS[v0];color=c=black:s=1280x720:r=30:d=5[v1];[0:v]trim=start=20:end=60,setpts=PTS-STARTPTS[v2];[v0][v1][v2]concat=n=3:v=1:a=0[v];[0:a:0]atrim=start=0:end=20,asetpts=PTS-STARTPTS[a0s0];anullsrc=channel_layout=stereo:sample_rate=48000,atrim=0:5[a0s1];[0:a:0]atrim=start=20:end=60,asetpts=PTS-STARTPTS[a0s2];[a0s0][a0s1][a0s2]concat=n=3:v=0:a=1[a]"" -map ""[v]"" -map ""[a]"" -c:v h264_nvenc -movflags +faststart ""C:\out\out.mp4""",
            args);
    }

    [Fact]
    public void Video_GapInMiddle_NoAudio_GoldenString()
    {
        // RF-F5.20: without audible tracks the gap filler is video-only + -an
        var p = GapInTheMiddle(audioTracks: 0);
        var s = new VideoExportSettings(); // defaults: libopenh264, faststart, 1920x1080@30

        var args = FfmpegArgsBuilder.BuildVideoExportArgs(p, s, @"C:\out\out.mp4");

        Assert.Equal(
            @"-i ""C:\rec\in.mp4"" -filter_complex ""[0:v]trim=start=0:end=20,setpts=PTS-STARTPTS[v0];color=c=black:s=1920x1080:r=30:d=5[v1];[0:v]trim=start=20:end=60,setpts=PTS-STARTPTS[v2];[v0][v1][v2]concat=n=3:v=1:a=0[v]"" -map ""[v]"" -an -c:v libopenh264 -movflags +faststart ""C:\out\out.mp4""",
            args);
    }

    [Fact]
    public void Video_AllTracksMutedWithGap_VideoOnlyFiller()
    {
        // all tracks muted -> no anullsrc chains at all, only black video
        var p = GapInTheMiddle(audioTracks: 1).WithAudioTrack(0, 1.0, muted: true);

        var args = FfmpegArgsBuilder.BuildVideoExportArgs(p, new VideoExportSettings(), @"C:\out\out.mp4");

        Assert.Contains("color=c=black", args);
        Assert.DoesNotContain("anullsrc", args);
        Assert.Contains(" -an ", args);
    }

    [Fact]
    public void Video_SingleSegment_NoAudio_GoldenString()
    {
        var p = MediaEditProject.CreateVideo(@"C:\rec\in.mp4", Sec(60));
        var s = new VideoExportSettings { VideoCodec = "h264_nvenc", FastStart = true };

        var args = FfmpegArgsBuilder.BuildVideoExportArgs(p, s, @"C:\out\out.mp4");

        Assert.Equal(
            @"-i ""C:\rec\in.mp4"" -filter_complex ""[0:v]trim=start=0:end=60,setpts=PTS-STARTPTS[v]"" -map ""[v]"" -an -c:v h264_nvenc -movflags +faststart ""C:\out\out.mp4""",
            args);
    }

    [Fact]
    public void Video_TwoSegmentsReordered_TwoTracksOneMuted_GoldenString()
    {
        // spec F5 example: system audio at 80%, mic muted
        var p = ReorderedTwoSegments(audioTracks: 2)
            .WithAudioTrack(0, 0.8, muted: false)
            .WithAudioTrack(1, 1.0, muted: true);
        var s = new VideoExportSettings { VideoCodec = "h264_nvenc", FastStart = true };

        var args = FfmpegArgsBuilder.BuildVideoExportArgs(p, s, @"C:\out\out.mp4");

        Assert.Equal(
            @"-i ""C:\rec\in.mp4"" -filter_complex ""[0:v]trim=start=30:end=45,setpts=PTS-STARTPTS[v0];[0:v]trim=start=5:end=20,setpts=PTS-STARTPTS[v1];[v0][v1]concat=n=2:v=1:a=0[v];[0:a:0]atrim=start=30:end=45,asetpts=PTS-STARTPTS,volume=0.8[a0s0];[0:a:0]atrim=start=5:end=20,asetpts=PTS-STARTPTS,volume=0.8[a0s1];[a0s0][a0s1]concat=n=2:v=0:a=1[a]"" -map ""[v]"" -map ""[a]"" -c:v h264_nvenc -movflags +faststart ""C:\out\out.mp4""",
            args);
    }

    [Fact]
    public void Video_AllTracksMuted_ExportsWithoutAudio_GoldenString()
    {
        var p = MediaEditProject.CreateVideo(@"C:\rec\in.mp4", Sec(60), audioTrackCount: 2)
            .WithAudioTrack(0, 1.0, muted: true)
            .WithAudioTrack(1, 1.0, muted: true);
        var s = new VideoExportSettings(); // defaults: libopenh264, faststart

        var args = FfmpegArgsBuilder.BuildVideoExportArgs(p, s, @"C:\out\out.mp4");

        Assert.Equal(
            @"-i ""C:\rec\in.mp4"" -filter_complex ""[0:v]trim=start=0:end=60,setpts=PTS-STARTPTS[v]"" -map ""[v]"" -an -c:v libopenh264 -movflags +faststart ""C:\out\out.mp4""",
            args);
    }

    [Fact]
    public void Video_TwoAudibleTracks_MixesWithAmixNoNormalize_GoldenString()
    {
        var p = MediaEditProject.CreateVideo(@"C:\rec\in.mp4", Sec(60), audioTrackCount: 2)
            .WithAudioTrack(1, 1.5, muted: false); // track 0 stays at 1.0 (volume filter omitted)
        var s = new VideoExportSettings { VideoCodec = "h264_nvenc", BitrateKbps = 6000, FastStart = false };

        var args = FfmpegArgsBuilder.BuildVideoExportArgs(p, s, @"C:\out\out.mp4");

        Assert.Equal(
            @"-i ""C:\rec\in.mp4"" -filter_complex ""[0:v]trim=start=0:end=60,setpts=PTS-STARTPTS[v];[0:a:0]atrim=start=0:end=60,asetpts=PTS-STARTPTS[ta0];[0:a:1]atrim=start=0:end=60,asetpts=PTS-STARTPTS,volume=1.5[ta1];[ta0][ta1]amix=inputs=2:duration=longest:normalize=0[a]"" -map ""[v]"" -map ""[a]"" -c:v h264_nvenc -b:v 6000k ""C:\out\out.mp4""",
            args);
    }

    [Fact]
    public void Video_FractionalTimes_UseInvariantCulture()
    {
        var p = MediaEditProject.CreateVideo(@"C:\rec\in.mp4", Sec(60));
        p = p.TrimSegment(p.Segments[0].Id, Sec(12.5), Sec(30.25));

        var args = FfmpegArgsBuilder.BuildVideoExportArgs(p, new VideoExportSettings(), @"C:\out\out.mp4");

        Assert.Contains("trim=start=12.5:end=30.25", args);
        Assert.DoesNotContain("12,5", args);
    }

    [Fact]
    public void Video_IsDeterministic_SameInputSameString()
    {
        var p = ReorderedTwoSegments(audioTracks: 1).WithAudioTrack(0, 0.8, false);
        var s = new VideoExportSettings { VideoCodec = "h264_nvenc" };

        var a = FfmpegArgsBuilder.BuildVideoExportArgs(p, s, @"C:\out\out.mp4");
        var b = FfmpegArgsBuilder.BuildVideoExportArgs(p, s, @"C:\out\out.mp4");

        Assert.Equal(a, b);
    }

    [Fact]
    public void Video_GifProject_Throws()
    {
        var gif = MediaEditProject.CreateGif(@"C:\rec\in.gif", 10);
        Assert.Throws<ArgumentException>(
            () => FfmpegArgsBuilder.BuildVideoExportArgs(gif, new VideoExportSettings(), @"C:\out.mp4"));
    }

    [Fact]
    public void Gif_SingleSegment_FpsScaleDitherNone_GoldenString()
    {
        var p = MediaEditProject.CreateVideo(@"C:\rec\in.mp4", Sec(10));
        var s = new GifFromVideoSettings { Fps = 15, ScaleWidth = 640, Dithering = "none" };

        var args = FfmpegArgsBuilder.BuildGifFromVideoArgs(p, s, @"C:\out\out.gif");

        Assert.Equal(
            @"-i ""C:\rec\in.mp4"" -filter_complex ""[0:v]trim=start=0:end=10,setpts=PTS-STARTPTS,fps=15,scale=640:-1:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse=dither=none"" ""C:\out\out.gif""",
            args);
    }

    [Fact]
    public void Gif_TwoSegments_BayerNoScale_GoldenString()
    {
        var p = MediaEditProject.CreateVideo(@"C:\rec\in.mp4", Sec(10)).SplitAt(Sec(4));
        var s = new GifFromVideoSettings { Fps = 10, ScaleWidth = null, Dithering = "bayer" };

        var args = FfmpegArgsBuilder.BuildGifFromVideoArgs(p, s, @"C:\out\out.gif");

        Assert.Equal(
            @"-i ""C:\rec\in.mp4"" -filter_complex ""[0:v]trim=start=0:end=4,setpts=PTS-STARTPTS[v0];[0:v]trim=start=4:end=10,setpts=PTS-STARTPTS[v1];[v0][v1]concat=n=2:v=1:a=0[v];[v]fps=10,split[a][b];[a]palettegen[p];[b][p]paletteuse=dither=bayer"" ""C:\out\out.gif""",
            args);
    }

    [Fact]
    public void Gif_FromVideoWithGap_BlackFillerAtTargetFps_GoldenString()
    {
        // RF-F5.20: MP4->GIF route also fills gaps with black (at the GIF fps)
        var p = MediaEditProject.CreateVideo(@"C:\rec\in.mp4", Sec(10)).SplitAt(Sec(4));
        p = p.MoveSegmentTo(p.Segments[1].Id, Sec(6)); // gap (4,6)
        var s = new GifFromVideoSettings { Fps = 10, ScaleWidth = null, Dithering = "none", Width = 1280, Height = 720 };

        var args = FfmpegArgsBuilder.BuildGifFromVideoArgs(p, s, @"C:\out\out.gif");

        Assert.Equal(
            @"-i ""C:\rec\in.mp4"" -filter_complex ""[0:v]trim=start=0:end=4,setpts=PTS-STARTPTS[v0];color=c=black:s=1280x720:r=10:d=2[v1];[0:v]trim=start=4:end=10,setpts=PTS-STARTPTS[v2];[v0][v1][v2]concat=n=3:v=1:a=0[v];[v]fps=10,split[a][b];[a]palettegen[p];[b][p]paletteuse=dither=none"" ""C:\out\out.gif""",
            args);
    }

    [Fact]
    public void Gif_InvalidSettings_Throw()
    {
        var p = MediaEditProject.CreateVideo(@"C:\rec\in.mp4", Sec(10));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => FfmpegArgsBuilder.BuildGifFromVideoArgs(p, new GifFromVideoSettings { Fps = 0 }, @"C:\o.gif"));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FfmpegArgsBuilder.BuildGifFromVideoArgs(p, new GifFromVideoSettings { Dithering = "floyd" }, @"C:\o.gif"));
        Assert.Throws<ArgumentException>(
            () => FfmpegArgsBuilder.BuildGifFromVideoArgs(MediaEditProject.CreateGif(@"C:\g.gif", 5), new GifFromVideoSettings(), @"C:\o.gif"));
    }

    [Fact]
    public void Paths_WithEmbeddedQuotes_AreEscaped()
    {
        var p = MediaEditProject.CreateVideo("C:\\weird \"name\"\\in.mp4", Sec(10));

        var args = FfmpegArgsBuilder.BuildVideoExportArgs(p, new VideoExportSettings(), @"C:\out\out.mp4");

        Assert.StartsWith("-i \"C:\\weird \\\"name\\\"\\in.mp4\"", args);
    }
}
