using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Klip.Interop;

namespace Klip.App.Services;

/// <summary>Frozen frame of a monitor in physical pixels (freeze-frame strategy).</summary>
public sealed record FrozenMonitor(
    NativeMethods.MonitorInfo Monitor,
    BitmapSource Frame);

/// <summary>
/// Screen capture with GDI BitBlt in physical pixels.
/// Grabbing before the overlay shows up kills timing issues and self-capture.
/// </summary>
public sealed class ScreenCaptureService
{
    /// <summary>Freezes one frame from each monitor.</summary>
    public List<FrozenMonitor> FreezeAllMonitors()
    {
        var result = new List<FrozenMonitor>();
        foreach (var monitor in NativeMethods.GetMonitors())
        {
            var width = monitor.Bounds.right - monitor.Bounds.left;
            var height = monitor.Bounds.bottom - monitor.Bounds.top;
            var frame = CaptureRegion(monitor.Bounds.left, monitor.Bounds.top, width, height);
            result.Add(new FrozenMonitor(monitor, frame));
        }
        return result;
    }

    /// <summary>Captures a virtual-screen region (physical coords) as a frozen BitmapSource.</summary>
    public BitmapSource CaptureRegion(int x, int y, int width, int height)
    {
        var hBitmap = NativeMethods.CaptureScreenRegionToHBitmap(x, y, width, height);
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, nint.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            NativeMethods.DeleteObject(hBitmap);
        }
    }

    /// <summary>Crops a frozen frame (rect in physical pixels of the frame).</summary>
    public static BitmapSource Crop(BitmapSource frame, Int32Rect rect)
    {
        var cropped = new CroppedBitmap(frame, rect);
        cropped.Freeze();
        return cropped;
    }

    /// <summary>Encodes as PNG.</summary>
    public static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
