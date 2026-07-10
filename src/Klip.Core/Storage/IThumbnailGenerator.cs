namespace Klip.Core.Storage;

/// <summary>
/// Makes a small JPEG thumbnail out of PNG bytes. The App does the real work
/// (WPF/WIC); we inject it into the ingest so the Core doesn't drag in WPF.
/// </summary>
public interface IThumbnailGenerator
{
    /// <summary>Returns JPEG thumbnail bytes (longest edge = maxSize), or null if it fails.</summary>
    byte[]? CreateJpegThumbnail(byte[] pngBytes, int maxSize = 256);
}
