using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Klip.Core.Storage;

namespace Klip.App.Services;

/// <summary>
/// Builds the JPEG thumbnails with WIC: decodes the PNG straight at a smaller width
/// (cheap) and re-encodes at quality 80. Runs on the caller thread (ingest, already off
/// the UI); WIC objects need no STA, only freezing what crosses threads.
/// </summary>
public sealed class WpfThumbnailGenerator : IThumbnailGenerator
{
    public byte[]? CreateJpegThumbnail(byte[] pngBytes, int maxSize = 256)
    {
        try
        {
            var decoder = new BitmapImage();
            decoder.BeginInit();
            decoder.CacheOption = BitmapCacheOption.OnLoad;
            decoder.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            decoder.DecodePixelWidth = maxSize; // ja decodifica reduzido (lado longo <= maxSize na largura)
            decoder.StreamSource = new MemoryStream(pngBytes);
            decoder.EndInit();
            decoder.Freeze();

            BitmapSource source = decoder;
            // se a altura ainda passar (imagem muito alta), reduz proporcionalmente
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
            return null; // sem miniatura, o card cai no PNG original
        }
    }
}
