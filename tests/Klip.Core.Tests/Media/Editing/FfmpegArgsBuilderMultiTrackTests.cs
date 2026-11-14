using Klip.Core.Media.Editing;

namespace Klip.Core.Tests.Media.Editing;

public class FfmpegArgsBuilderMultiTrackTests
{
    private static TimeSpan Sec(double s) => TimeSpan.FromSeconds(s);

    private static readonly VideoExportSettings Settings = new()
    {
        VideoCodec = "libopenh264",
        BitrateKbps = null,
        FastStart = true,
        Width = 1920,
        Height = 1080,
        Fps = 30,
    };

    [Fact]
    public void SingleTrack_FallsBackToTheSingleInputBuilder()
    {
        var p = MediaEditProject.CreateVideo(@"C:\rec\in.mp4", Sec(10));

        var multi = FfmpegArgsBuilder.BuildMultiTrackVideoExportArgs(p, Settings, new Dictionary<int, FfmpegArgsBuilder.TrackOverlay>(), @"C:\out\out.mp4");
        var single = FfmpegArgsBuilder.BuildVideoExportArgs(p, Settings, @"C:\out\out.mp4");

        Assert.Equal(single, multi);
    }

    [Fact]
    public void TwoTracks_AddsOneInputPerTrackAndLayersFullFrame()
    {
        var p = MediaEditProject.CreateVideo(@"C:\rec\base.mp4", Sec(10), audioTrackCount: 1)
            .AddTrack(@"C:\rec\overlay.mp4", Sec(10), TimeSpan.Zero);

        var args = FfmpegArgsBuilder.BuildMultiTrackVideoExportArgs(p, Settings,
            new Dictionary<int, FfmpegArgsBuilder.TrackOverlay>(), @"C:\out\out.mp4");

        Assert.Contains("-i \"C:\\rec\\base.mp4\"", args);
        Assert.Contains("-i \"C:\\rec\\overlay.mp4\"", args);
        Assert.Contains("[0:v]", args);
        Assert.Contains("[1:v]", args);
        // ambas as trilhas escalam pro canvas do export e a segunda cobre a
        // primeira full frame (0,0) - layers de verdade, nao um bubble PiP
        Assert.Contains("scale=1920:1080", args);
        Assert.Contains("overlay=0:0", args);
        Assert.Contains("-map \"[v]\"", args);
        Assert.Contains("-map \"[a]\"", args);
        Assert.EndsWith("\"C:\\out\\out.mp4\"", args);
    }

    [Fact]
    public void MutedExtraTrack_IsLeftOutOfTheAudioMix()
    {
        var p = MediaEditProject.CreateVideo(@"C:\rec\base.mp4", Sec(10), audioTrackCount: 1)
            .AddTrack(@"C:\rec\overlay.mp4", Sec(10), TimeSpan.Zero);
        var overlays = new Dictionary<int, FfmpegArgsBuilder.TrackOverlay>
        {
            [1] = new FfmpegArgsBuilder.TrackOverlay(VolumePercent: 0),
        };

        var args = FfmpegArgsBuilder.BuildMultiTrackVideoExportArgs(p, Settings, overlays, @"C:\out\out.mp4");

        Assert.DoesNotContain("[1:a:0]", args);
        Assert.Contains("-map \"[a]\"", args); // a trilha do projeto ainda e audivel
    }

    [Fact]
    public void AllTracksMuted_ExportsWithoutAudioMap()
    {
        // trilha do projeto sem audio (audioTrackCount: 0, igual a um source
        // sem trilha de audio) + trilha extra com volume 0 = ninguem audivel
        var p = MediaEditProject.CreateVideo(@"C:\rec\base.mp4", Sec(10), audioTrackCount: 0)
            .AddTrack(@"C:\rec\overlay.mp4", Sec(10), TimeSpan.Zero);
        var overlays = new Dictionary<int, FfmpegArgsBuilder.TrackOverlay>
        {
            [1] = new FfmpegArgsBuilder.TrackOverlay(VolumePercent: 0),
        };

        var args = FfmpegArgsBuilder.BuildMultiTrackVideoExportArgs(p, Settings, overlays, @"C:\out\out.mp4");

        Assert.Contains("-an", args);
        Assert.DoesNotContain("-map \"[a]\"", args);
    }

    [Fact]
    public void ReducedExtraTrackVolume_AppliesAVolumeFilter()
    {
        var p = MediaEditProject.CreateVideo(@"C:\rec\base.mp4", Sec(10), audioTrackCount: 1)
            .AddTrack(@"C:\rec\overlay.mp4", Sec(10), TimeSpan.Zero);
        var overlays = new Dictionary<int, FfmpegArgsBuilder.TrackOverlay>
        {
            [1] = new FfmpegArgsBuilder.TrackOverlay(VolumePercent: 50),
        };

        var args = FfmpegArgsBuilder.BuildMultiTrackVideoExportArgs(p, Settings, overlays, @"C:\out\out.mp4");

        Assert.Contains("volume=0.5", args);
    }

    [Fact]
    public void ReducedOpacity_AddsAlphaMixBeforeTheOverlay()
    {
        var p = MediaEditProject.CreateVideo(@"C:\rec\base.mp4", Sec(10))
            .AddTrack(@"C:\rec\overlay.mp4", Sec(10), TimeSpan.Zero);
        var overlays = new Dictionary<int, FfmpegArgsBuilder.TrackOverlay>
        {
            [1] = new FfmpegArgsBuilder.TrackOverlay(Opacity: 50),
        };

        var args = FfmpegArgsBuilder.BuildMultiTrackVideoExportArgs(p, Settings, overlays, @"C:\out\out.mp4");

        Assert.Contains("colorchannelmixer=aa=0.5", args);
    }

    [Fact]
    public void FullOpacity_LeavesTheChainUnchanged()
    {
        var p = MediaEditProject.CreateVideo(@"C:\rec\base.mp4", Sec(10))
            .AddTrack(@"C:\rec\overlay.mp4", Sec(10), TimeSpan.Zero);

        var args = FfmpegArgsBuilder.BuildMultiTrackVideoExportArgs(p, Settings,
            new Dictionary<int, FfmpegArgsBuilder.TrackOverlay>(), @"C:\out\out.mp4");

        Assert.DoesNotContain("colorchannelmixer", args);
    }

    [Fact]
    public void ThreeTracks_MixesAllThreeAudioStreams()
    {
        var p = MediaEditProject.CreateVideo(@"C:\rec\base.mp4", Sec(10), audioTrackCount: 1)
            .AddTrack(@"C:\rec\a.mp4", Sec(10), TimeSpan.Zero)
            .AddTrack(@"C:\rec\b.mp4", Sec(10), TimeSpan.Zero);

        var args = FfmpegArgsBuilder.BuildMultiTrackVideoExportArgs(p, Settings,
            new Dictionary<int, FfmpegArgsBuilder.TrackOverlay>(), @"C:\out\out.mp4");

        Assert.Contains("amix=inputs=3:duration=longest:normalize=0[a]", args);
    }
}
