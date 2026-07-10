using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Klip.Core.Storage;

namespace Klip.App.Services;

/// <summary>
/// Makes JPEG thumbnails with WIC/WPF. Decodes the PNG at a smaller width
/// (cheap) and re-encodes as JPEG quality 80. Ingestion runs on a background
/// thread (MTA), so the WPF encode gets marshalled to the dispatcher (STA).
/// </summary>
public sealed class WpfThumbnailGenerator : IThumbnailGenerator
{
    private readonly Dispatcher _dispatcher = System.Windows.Application.Current?.Dispatcher
        ?? Dispatcher.CurrentDispatcher;

    public byte[]? CreateJpegThumbnail(byte[] pngBytes, int maxSize = 256)
    {
        // WPF encode needs STA, so run it on the UI dispatcher when we're off it.
        // the timeout keeps ingestion from stalling if the UI is busy.
        if (_dispatcher.CheckAccess())
            return Encode(pngBytes, maxSize);
        try
        {
            return _dispatcher.Invoke(() => Encode(pngBytes, maxSize),
                DispatcherPriority.Background, CancellationToken.None, TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            return null; // no thumbnail, the card falls back to the original PNG
        }
    }

    private static byte[]? Encode(byte[] pngBytes, int maxSize)
    {
        try
        {
            var decoder = new BitmapImage();
            decoder.BeginInit();
            decoder.CacheOption = BitmapCacheOption.OnLoad;
            decoder.DecodePixelWidth = maxSize; // decode already downscaled (long edge <= maxSize on width)
            decoder.StreamSource = new MemoryStream(pngBytes);
            decoder.EndInit();
            decoder.Freeze();

            BitmapSource source = decoder;
            // if the height still goes over (very tall image), scale it down proportionally
            if (source.PixelHeight > maxSize)
            {
                var scale = (double)maxSize / source.PixelHeight;
                var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
                scaled.Freeze();
                source = scaled;
            }

            var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("Thumbnail", ex);
            return null;
        }
    }
}
