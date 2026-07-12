namespace Klip.Core.Media.Editing;

/// <summary>Kind of media loaded in the editor (RF-F5.05).</summary>
public enum MediaKind
{
    /// <summary>Frame-based source (frame list + per-frame delay).</summary>
    Gif,
    /// <summary>Time-based source (segments reference time ranges in the file).</summary>
    Video,
}
