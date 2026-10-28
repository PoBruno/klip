using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using Klip.Core.Storage;

namespace Klip.App.Services;

/// <summary>
/// Text extraction (OCR) using the Windows.Media.Ocr engine. It's built into
/// Windows, runs fully offline and on-device, no external dependency. Great for
/// the "text actions" button and for indexing text inside history images.
/// </summary>
/// <remarks>
/// No Windows.* type shows up in this class (fields, signatures, state machines).
/// Touching one maps the 26 MB WinRT projection and the OCR runtime, so all of it
/// lives in <see cref="WinRtOcr"/> and only gets JITted on the first real OCR call.
/// </remarks>
public sealed class OcrService : IImageTextExtractor
{
    // the Windows OCR engine allows only one RecognizeAsync at a time, so serialize
    private readonly SemaphoreSlim _gate = new(1, 1);

    // OcrEngine boxed as object on purpose, see the remarks above
    private object? _engine;

    // 0 = not tried yet, 1 = engine ready, 2 = no OCR language on this machine
    private int _state;

    /// <summary>
    /// False only after a real attempt found no OCR language. Before the first use
    /// it says true, so asking never builds the engine.
    /// </summary>
    public bool IsAvailable => Volatile.Read(ref _state) != 2;

    /// <summary>Reads text from a PNG byte buffer. Empty string when nothing is found.</summary>
    public async Task<string> ReadTextAsync(byte[] pngBytes)
    {
        if (pngBytes.Length == 0 || !IsAvailable)
            return "";

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var engine = EnsureEngine();
            if (engine is null)
                return "";
            return await WinRtOcr.ReadTextAsync(engine, pngBytes).ConfigureAwait(false);
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
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ReadTextAsync(ms.ToArray());
    }

    /// <summary>OCRs a PNG and returns boxes of words that look like sensitive data.</summary>
    public async Task<IReadOnlyList<PixelRect>> FindSensitiveRegionsAsync(byte[] pngBytes)
    {
        if (pngBytes.Length == 0 || !IsAvailable)
            return [];

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var engine = EnsureEngine();
            if (engine is null)
                return [];
            return await WinRtOcr.FindSensitiveRegionsAsync(engine, pngBytes).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("OcrRedact", ex);
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    // always called under _gate, so no double init
    private object? EnsureEngine()
    {
        if (_engine is not null)
            return _engine;
        if (Volatile.Read(ref _state) == 2)
            return null;

        _engine = WinRtOcr.CreateEngine();
        Volatile.Write(ref _state, _engine is null ? 2 : 1);
        StartupLog.Write(_engine is null ? "OCR: nenhum idioma instalado" : "OCR: motor carregado");
        return _engine;
    }

    /// <summary>Everything that touches WinRT. Never inlined into the caller.</summary>
    private static class WinRtOcr
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static object? CreateEngine()
        {
            // engine for the user's language, falls back to any installed OCR language
            var engine = global::Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is not null)
                return engine;
            foreach (var lang in global::Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages)
            {
                engine = global::Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(lang);
                if (engine is not null)
                    return engine;
            }
            return null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static async Task<string> ReadTextAsync(object engine, byte[] pngBytes)
        {
            var result = await RecognizeAsync(engine, pngBytes).ConfigureAwait(false);
            return result.Text?.Trim() ?? "";
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static async Task<IReadOnlyList<PixelRect>> FindSensitiveRegionsAsync(object engine, byte[] pngBytes)
        {
            var result = await RecognizeAsync(engine, pngBytes).ConfigureAwait(false);
            var regions = new List<PixelRect>();
            foreach (var line in result.Lines)
            {
                foreach (var word in line.Words)
                {
                    if (!Klip.Core.Clipboard.SensitiveDataDetector.IsSensitiveWord(word.Text))
                        continue;
                    var r = word.BoundingRect;
                    regions.Add(new PixelRect((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height));
                }
            }
            return regions;
        }

        private static async Task<global::Windows.Media.Ocr.OcrResult> RecognizeAsync(object engine, byte[] pngBytes)
        {
            using var stream = new MemoryStream(pngBytes);
            var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder
                .CreateAsync(stream.AsRandomAccessStream());
            using var softwareBitmap = await decoder.GetSoftwareBitmapAsync();
            return await ((global::Windows.Media.Ocr.OcrEngine)engine).RecognizeAsync(softwareBitmap);
        }
    }
}
