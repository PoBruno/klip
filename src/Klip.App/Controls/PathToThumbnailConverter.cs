using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Klip.App.Controls;

/// <summary>
/// Thumbnail cache for the history cards: decodes a file (the JPEG thumb, or the PNG
/// when an old item has none) close to the drawn size, keeps an in-memory LRU, and
/// does the decode off the UI thread. The converter part is the synchronous fallback
/// and only answers from the cache.
/// </summary>
public sealed class PathToThumbnailConverter : IValueConverter
{
    private const int MaxCache = 200;

    // what is left in memory while the flyout is hidden
    public const int HiddenCacheSize = 40;

    // the card draws the thumbnail at MaxHeight=96 with DownOnly, so the useful width is
    // about 96-128 px at 100%; 192 covers 150-200% scaling with room to spare
    public const int CardDecodeWidth = 192;

    private static readonly Dictionary<string, BitmapImage> Cache = new();
    private static readonly LinkedList<string> Order = new(); // front = most recent
    private static readonly Lock Sync = new();

    // two decodes at a time: enough to keep up with a fast scroll, without
    // flooding the thread pool when a page of images shows up at once
    private static readonly SemaphoreSlim DecodeGate = new(2, 2);

    public int DecodeWidth { get; set; } = CardDecodeWidth;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || path.Length == 0)
            return null;
        // no disk work here: the cards load through LoadAsync
        return TryGetCached(path, out var cached) ? cached : null;
    }

    /// <summary>Looks up the LRU without decoding (cheap, any thread).</summary>
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

    /// <summary>Decodes on a pool thread (at most two at a time) and caches. Null when the file is gone or bad.</summary>
    public static async Task<BitmapImage?> LoadAsync(string path, int decodeWidth = CardDecodeWidth)
    {
        if (TryGetCached(path, out var cached))
            return cached;
        await DecodeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() => DecodeAndCache(path, decodeWidth)).ConfigureAwait(false);
        }
        finally
        {
            DecodeGate.Release();
        }
    }

    /// <summary>
    /// Decodes (fine off the UI thread, the bitmap comes out frozen) and puts it in the LRU.
    /// </summary>
    public static BitmapImage? DecodeAndCache(string path, int decodeWidth)
    {
        if (TryGetCached(path, out var cached))
            return cached;

        var bitmap = Decode(path, decodeWidth);
        if (bitmap is null)
            return null;

        lock (Sync)
        {
            if (!Cache.ContainsKey(path))
            {
                Cache[path] = bitmap;
                Order.AddFirst(path);
                Evict(MaxCache);
            }
            return Cache[path];
        }
    }

    /// <summary>Drops all but the most recent <paramref name="keep"/> entries (flyout hidden).</summary>
    public static void TrimCache(int keep)
    {
        lock (Sync)
            Evict(keep);
    }

    private static BitmapImage? Decode(string path, int decodeWidth)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
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

    private static void Evict(int max)
    {
        while (Cache.Count > max && Order.Last is not null)
        {
            var oldest = Order.Last.Value;
            Order.RemoveLast();
            Cache.Remove(oldest);
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
