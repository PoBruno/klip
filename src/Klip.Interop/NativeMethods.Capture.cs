using System.Runtime.InteropServices;

namespace Klip.Interop;

/// <summary>Monitor and window enumeration P/Invoke for the overlay.</summary>
public static partial class NativeMethods
{
    // ----- Monitors -----

    public delegate bool MonitorEnumProc(nint hMonitor, nint hdcMonitor, ref RECT lprcMonitor, nint dwData);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(nint hdc, nint lprcClip, MonitorEnumProc lpfnEnum, nint dwData);

    public readonly record struct MonitorInfo(nint Handle, RECT Bounds, RECT WorkArea, uint Dpi);

    [LibraryImport("shcore.dll")]
    public static partial int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    public const int MDT_EFFECTIVE_DPI = 0;

    /// <summary>Lists monitors with physical bounds and effective DPI.</summary>
    public static List<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        EnumDisplayMonitors(nint.Zero, nint.Zero, (nint hMonitor, nint _, ref RECT rect, nint _) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(hMonitor, ref info);
            GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out var dpiX, out _);
            monitors.Add(new MonitorInfo(hMonitor, info.rcMonitor, info.rcWork, dpiX == 0 ? 96 : dpiX));
            return true;
        }, nint.Zero);
        return monitors;
    }

    // ----- Keep our own window out of the capture -----

    public const uint WDA_NONE = 0x0;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

    // ----- Window enumeration for Window mode -----

    public delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hWnd);

    public const int DWMWA_CLOAKED = 14;

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(nint hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_TRANSPARENT = 0x00000020;
    public const long WS_EX_NOACTIVATE = 0x08000000;

    // mouse activation: a no-activate window can still opt in to focus on click
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int MA_ACTIVATE = 1;
    public const int MA_NOACTIVATE = 3;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static partial nint GetWindowLongPtr(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static partial nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    public readonly record struct TopLevelWindow(nint Handle, RECT Bounds, string Title);

    /// <summary>
    /// Visible top-level windows in Z order (top first) with real bounds
    /// (DWMWA_EXTENDED_FRAME_BOUNDS, avoids the rounded-corner artifacts).
    /// </summary>
    public static List<TopLevelWindow> GetVisibleTopLevelWindows(HashSet<nint> exclude)
    {
        var windows = new List<TopLevelWindow>();
        EnumWindows((hWnd, _) =>
        {
            if (exclude.Contains(hWnd) || !IsWindowVisible(hWnd) || IsIconic(hWnd))
                return true;

            // suspended UWP windows show up as cloaked but still visible
            if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;

            var exStyle = (long)GetWindowLongPtr(hWnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0)
                return true;

            if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT bounds,
                    Marshal.SizeOf<RECT>()) != 0)
                return true;

            if (bounds.right - bounds.left < 32 || bounds.bottom - bounds.top < 32)
                return true;

            windows.Add(new TopLevelWindow(hWnd, bounds, GetWindowTextSafe(hWnd)));
            return true;
        }, nint.Zero);
        return windows;
    }
}
