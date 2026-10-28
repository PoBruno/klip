using System.Windows;
using System.Windows.Media.Imaging;
using Klip.App.Windows;
using Klip.Interop;

namespace Klip.App.Services;

/// <summary>
/// Owns the images pinned on screen. Nothing exists while no image is pinned:
/// each pin is its own window, created on demand and gone when closed.
/// </summary>
public sealed class PinnedImageService(ClipboardWriteGuard writeGuard, MemorySettler memory)
{
    private readonly List<PinnedImageWindow> _windows = [];

    /// <summary>Raised when a pin opens, closes or changes click through (tray menu state).</summary>
    public event Action? Changed;

    public int Count => _windows.Count;

    public bool AnyClickThrough => _windows.Exists(w => w.IsClickThrough);

    /// <summary>Pins a copy of the image on top of everything, centered on the cursor.</summary>
    public void Pin(BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var dispatcher = Application.Current.Dispatcher;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => Pin(image));
            return;
        }

        if (!image.IsFrozen)
            image = image.CanFreeze ? Frozen(image) : BitmapFrame.Create(image);

        try
        {
            NativeMethods.GetCursorPos(out var cursor);
            var handle = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var monitors = NativeMethods.GetMonitors();
            var monitor = monitors.Find(m => m.Handle == handle);
            if (monitor.Handle == nint.Zero && monitors.Count > 0)
                monitor = monitors[0];

            var window = new PinnedImageWindow(image, writeGuard);
            window.Closed += (_, _) => OnClosed(window);
            window.ClickThroughChanged += () => Changed?.Invoke();
            _windows.Add(window);
            window.ShowNear(cursor.x, cursor.y, monitor.WorkArea, monitor.Dpi == 0 ? 96 : monitor.Dpi);
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("PinImage", ex);
        }
    }

    public void CloseAll()
    {
        foreach (var window in _windows.ToArray())
            window.Close();
    }

    /// <summary>Gives the mouse back to every pin (the only way out once clicks go through).</summary>
    public void StopClickThrough()
    {
        foreach (var window in _windows)
        {
            if (window.IsClickThrough)
                window.SetClickThrough(false);
        }
    }

    private void OnClosed(PinnedImageWindow window)
    {
        _windows.Remove(window);
        Changed?.Invoke();
        if (_windows.Count == 0)
            memory.Schedule();
    }

    private static BitmapSource Frozen(BitmapSource image)
    {
        var copy = image.Clone();
        copy.Freeze();
        return copy;
    }
}
