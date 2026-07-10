using System.IO;
using System.Windows.Media.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

// these two names clash with the WPF ones, so pin the WinRT versions
using WinRtBitmapDecoder = Windows.Graphics.Imaging.BitmapDecoder;

namespace Klip.App.Services;

/// <summary>
/// Text extraction (OCR) using the Windows.Media.Ocr engine. It's built into
/// Windows, runs fully offline and on-device, no external dependency. Great for
/// the "text actions" button and for indexing text inside history images.
/// </summary>
public sealed class OcrService : Klip.Core.Storage.IImageTextExtractor
{
    private readonly OcrEngine? _engine;
    // the Windows OCR engine allows only one RecognizeAsync at a time, so serialize
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OcrService()
    {
        // engine for the user's language, falls back to any installed OCR language
        _engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? TryFirstAvailable();
    }

    /// <summary>True when the machine has at least one OCR language pack.</summary>
    public bool IsAvailable => _engine is not null;

    /// <summary>Reads text from a PNG byte buffer. Empty string when nothing is found.</summary>
    public async Task<string> ReadTextAsync(byte[] pngBytes)
    {
        if (_engine is null || pngBytes.Length == 0)
            return "";

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var stream = new MemoryStream(pngBytes);
            var decoder = await WinRtBitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
            using var softwareBitmap = await decoder.GetSoftwareBitmapAsync();
            var result = await _engine.RecognizeAsync(softwareBitmap);
            return result.Text?.Trim() ?? "";
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("Ocr", ex);
            return "";
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reads text from a WPF bitmap (used by the editor button).</summary>
    public Task<string> ReadTextAsync(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ReadTextAsync(ms.ToArray());
    }

    /// <summary>OCRs a PNG and returns boxes of words that look like sensitive data.</summary>
    public async Task<IReadOnlyList<Klip.Core.Storage.PixelRect>> FindSensitiveRegionsAsync(byte[] pngBytes)
    {
        var regions = new List<Klip.Core.Storage.PixelRect>();
        if (_engine is null || pngBytes.Length == 0)
            return regions;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var stream = new MemoryStream(pngBytes);
            var decoder = await WinRtBitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
            using var softwareBitmap = await decoder.GetSoftwareBitmapAsync();
            var result = await _engine.RecognizeAsync(softwareBitmap);

            foreach (var line in result.Lines)
            {
                foreach (var word in line.Words)
                {
                    if (!Klip.Core.Clipboard.SensitiveDataDetector.IsSensitiveWord(word.Text))
                        continue;
                    var r = word.BoundingRect;
                    regions.Add(new Klip.Core.Storage.PixelRect(
                        (int)r.X, (int)r.Y, (int)r.Width, (int)r.Height));
                }
            }
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("OcrRedact", ex);
        }
        finally
        {
            _gate.Release();
        }
        return regions;
    }

    private static OcrEngine? TryFirstAvailable()
    {
        foreach (var lang in OcrEngine.AvailableRecognizerLanguages)
        {
            var engine = OcrEngine.TryCreateFromLanguage(lang);
            if (engine is not null)
                return engine;
        }
        return null;
    }
}
