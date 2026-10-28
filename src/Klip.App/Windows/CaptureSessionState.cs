using Klip.Core.Settings;

namespace Klip.App.Windows;

/// <summary>
/// Mode and delay of one capture session, shared by every monitor overlay of that
/// session. A new one per Win+Shift+S, so nothing sticks across sessions unless the
/// user asked to remember the mode.
/// </summary>
public sealed class CaptureSessionState
{
    public CaptureSessionState(CaptureMode initialMode)
    {
        InitialMode = initialMode;
        Mode = initialMode;
    }

    /// <summary>What the session opened with (to know if the user changed it).</summary>
    public CaptureMode InitialMode { get; }

    public CaptureMode Mode { get; set; }

    /// <summary>
    /// A mode key (1, 2, T, G...) was pressed on some overlay. Keyboard focus can be on
    /// any monitor, but only the overlay that owns the toolbar listens and reacts.
    /// </summary>
    public event Action<CaptureMode>? ModeRequested;

    public void RequestMode(CaptureMode mode) => ModeRequested?.Invoke(mode);

    /// <summary>Opened by a direct hotkey in a fixed mode; never remembered.</summary>
    public bool Forced { get; init; }

    /// <summary>Always starts at zero: a forgotten 10 s delay is worse than a forgotten mode.</summary>
    public CaptureDelay Delay { get; set; } = CaptureDelay.None;
}
