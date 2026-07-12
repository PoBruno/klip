using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Klip.App.Controls;

/// <summary>
/// Turns a file path (thumbnail or PNG) into a BitmapImage, with an in-memory
/// LRU cache (max 200) so scrolling doesn't decode the same image twice.
/// </summary>
public sealed class PathToThumbnailConverter : IValueConverter
{
    private const int MaxCache = 200;
    private static readonly Dictionary<string, BitmapImage> Cache = new();
    private static readonly LinkedList<string> Order = new(); // front = most recent
    private static readonly Lock Sync = new();

    public int DecodeWidth { get; set; } = 256;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || path.Length == 0 || !File.Exists(path))
            return null;

        if (TryGetCached(path, out var cached))
            return cached;

        return DecodeAndCache(path, DecodeWidth);
    }

    /// <summary>Consulta o cache LRU sem decodificar (barato, qualquer thread).</summary>
    public static bool TryGetCached(string path, out BitmapImage? bitmap)
    {
        lock (Sync)
        {
            if (Cache.TryGetValue(path, out var cached))
            {
                Touch(path);
                bitmap = cached;
                return true;
            }
        }
        bitmap = null;
        return false;
    }

    /// <summary>
    /// Decodifica (pode rodar fora da UI thread - o bitmap sai Freeze()) e
    /// insere no cache LRU. Usado pelo HoverGifPlayer para tirar o decode da
    /// thumbnail estatica da UI thread (bug do decode sincrono no scroll).
    /// </summary>
    public static BitmapImage? DecodeAndCache(string path, int decodeWidth)
    {
        var bitmap = Decode(path, decodeWidth);
        if (bitmap is null)
            return null;

        lock (Sync)
        {
            if (!Cache.ContainsKey(path))
            {
                Cache[path] = bitmap;
                Order.AddFirst(path);
                Evict();
            }
            return Cache[path];
        }
    }

    private static BitmapImage? Decode(string path, int decodeWidth)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = decodeWidth;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            return null; // corrupt or deleted file: o card mostra só os metadados
        }
    }

    private static void Touch(string path)
    {
        Order.Remove(path);
        Order.AddFirst(path);
    }

    private static void Evict()
    {
        while (Cache.Count > MaxCache && Order.Last is not null)
        {
            var oldest = Order.Last.Value;
            Order.RemoveLast();
            Cache.Remove(oldest);
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
