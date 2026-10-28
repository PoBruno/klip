using Klip.Core.Capture;
using Klip.Core.Settings;

namespace Klip.Core.Tests;

public class CaptureModePolicyTests
{
    [Fact]
    public void Default_IsRectangle_EvenWithAnotherModeSaved()
    {
        var s = new AppSettings { LastCaptureMode = CaptureMode.Mp4 };
        Assert.Equal(CaptureMode.Rectangle, CaptureModePolicy.ResolveInitial(s, recordingActive: false));
    }

    [Fact]
    public void Remember_OpensInLastMode()
    {
        var s = new AppSettings { RememberLastCaptureMode = true, LastCaptureMode = CaptureMode.Mp4 };
        Assert.Equal(CaptureMode.Mp4, CaptureModePolicy.ResolveInitial(s, recordingActive: false));
    }

    [Fact]
    public void ActiveRecording_AlwaysRectangle()
    {
        var s = new AppSettings { RememberLastCaptureMode = true, LastCaptureMode = CaptureMode.Gif };
        Assert.Equal(CaptureMode.Rectangle, CaptureModePolicy.ResolveInitial(s, recordingActive: true));
        Assert.Equal(CaptureMode.Rectangle, CaptureModePolicy.ResolveInitial(s, true, CaptureMode.Mp4));
    }

    [Fact]
    public void InvalidSavedValue_FallsBackToRectangle()
    {
        var s = new AppSettings { RememberLastCaptureMode = true, LastCaptureMode = (CaptureMode)99 };
        Assert.Equal(CaptureMode.Rectangle, CaptureModePolicy.ResolveInitial(s, recordingActive: false));
    }

    [Fact]
    public void Forced_WinsOverSetting()
    {
        var s = new AppSettings { RememberLastCaptureMode = true, LastCaptureMode = CaptureMode.Gif };
        Assert.Equal(CaptureMode.Window, CaptureModePolicy.ResolveInitial(s, false, CaptureMode.Window));
    }

    [Fact]
    public void ModeToRemember_OnlyWhenOnAndChanged()
    {
        var off = new AppSettings { LastCaptureMode = CaptureMode.Rectangle };
        Assert.Null(CaptureModePolicy.ModeToRemember(off, CaptureMode.Mp4, forced: false));

        var on = new AppSettings { RememberLastCaptureMode = true, LastCaptureMode = CaptureMode.Rectangle };
        Assert.Equal(CaptureMode.Mp4, CaptureModePolicy.ModeToRemember(on, CaptureMode.Mp4, forced: false));
        Assert.Null(CaptureModePolicy.ModeToRemember(on, CaptureMode.Rectangle, forced: false));
        Assert.Null(CaptureModePolicy.ModeToRemember(on, CaptureMode.Mp4, forced: true));
    }
}
