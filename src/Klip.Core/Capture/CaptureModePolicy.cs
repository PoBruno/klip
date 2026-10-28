using Klip.Core.Settings;

namespace Klip.Core.Capture;

/// <summary>Which mode the capture overlay opens in, and what gets saved at the end.</summary>
public static class CaptureModePolicy
{
    /// <summary>
    /// Rectangle unless "remember" is on. A running recording always gets Rectangle
    /// (a second GIF/MP4 overlay would be useless). <paramref name="forced"/> is for
    /// direct hotkeys that open in a given mode and win over the setting.
    /// </summary>
    public static CaptureMode ResolveInitial(AppSettings settings, bool recordingActive, CaptureMode? forced = null)
    {
        if (recordingActive)
            return CaptureMode.Rectangle;
        if (forced is { } f)
            return f;
        if (!settings.RememberLastCaptureMode || !Enum.IsDefined(settings.LastCaptureMode))
            return CaptureMode.Rectangle;
        return settings.LastCaptureMode;
    }

    /// <summary>
    /// Mode to persist when a session ends (captured or cancelled, like the Snipping
    /// Tool), or null when nothing should be written. Forced sessions don't count.
    /// </summary>
    public static CaptureMode? ModeToRemember(AppSettings settings, CaptureMode chosen, bool forced)
    {
        if (forced || !settings.RememberLastCaptureMode)
            return null;
        return settings.LastCaptureMode == chosen ? null : chosen;
    }
}
