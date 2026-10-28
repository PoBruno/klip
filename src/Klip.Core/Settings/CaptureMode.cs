namespace Klip.Core.Settings;

/// <summary>Capture overlay modes.</summary>
/// <remarks>Saved in settings.json as a number, so never reorder or reuse values.</remarks>
public enum CaptureMode
{
    Rectangle = 0,
    Window = 1,
    Fullscreen = 2,
    Scrolling = 3,
    Freeform = 4,
    Gif = 5,
    Mp4 = 6,
    Text = 7,
    Color = 8,
}
