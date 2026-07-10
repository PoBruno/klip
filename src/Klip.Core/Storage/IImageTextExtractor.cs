namespace Klip.Core.Storage;

/// <summary>A rectangle in image pixels. Used to point at regions to redact.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>
/// Pulls text out of an image (OCR). Lives as an interface so the Core stays
/// free of any WinRT/WPF dependency. The App wires the real Windows OCR engine.
/// </summary>
public interface IImageTextExtractor
{
    /// <summary>True when OCR can actually run (a language pack is installed).</summary>
    bool IsAvailable { get; }

    /// <summary>Reads text from a PNG buffer. Empty string when nothing is found.</summary>
    Task<string> ReadTextAsync(byte[] pngBytes);

    /// <summary>
    /// OCRs the PNG and returns the bounding boxes of words that look like
    /// sensitive data (email, phone, card, IP, CPF). For quick redact.
    /// </summary>
    Task<IReadOnlyList<PixelRect>> FindSensitiveRegionsAsync(byte[] pngBytes);
}
