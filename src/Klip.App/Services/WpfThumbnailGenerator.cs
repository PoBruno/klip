using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Klip.Core.Storage;

namespace Klip.App.Services;

/// <summary>
/// Gera miniaturas JPEG com WIC/WPF. Decodifica o PNG ja numa largura menor
/// (barato) e reencoda como JPEG qualidade 80.
///
/// RF-P2.04: roda na thread do chamador (a ingestao, em Task.Run). Antes isso
/// era marshalizado de volta para a UI thread com Dispatcher.Invoke e um
/// timeout de 5 s - ou seja, o decode+encode de cada imagem copiada caia na UI
/// thread e a thread de ingestao ficava parada esperando ate 5 s.
///
/// BitmapImage, TransformedBitmap e JpegBitmapEncoder sao objetos WIC: nao
/// exigem STA nem dispatcher, basta congelar tudo que atravessa thread - o que
/// este codigo ja fazia. Congelar tambem e o que permite o BitmapSource ser
/// entregue ao encoder de outra thread sem afinidade de dispatcher.
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
