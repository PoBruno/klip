using System.Drawing;
using System.Windows;
using Microsoft.Win32;

namespace Klip.App.Services;

/// <summary>
/// Tray icon from the two shipped .ico files (dark glyph for a light taskbar, white
/// glyph for a dark one). Loading an .ico goes through user32, so GDI+ never starts.
/// </summary>
public static class TrayIconFactory
{
    /// <summary>True when the taskbar is light (SystemUsesLightTheme, not the app theme).</summary>
    public static bool TaskbarIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static Icon Create(bool lightTaskbar)
    {
        var name = lightTaskbar ? "TrayLight.ico" : "TrayDark.ico";
        var info = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/{name}"))
            ?? throw new InvalidOperationException($"missing resource {name}");
        using var stream = info.Stream;
        // tray size follows the dpi of the primary monitor (16 at 100%, 24 at 150%...)
        var size = (int)Math.Round(16 * VisualDpiScale());
        return new Icon(stream, size, size);
    }

    private static double VisualDpiScale()
    {
        const uint MONITOR_DEFAULTTOPRIMARY = 1;
        try
        {
            // the tray lives on the primary monitor
            var monitor = Interop.NativeMethods.MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY);
            Interop.NativeMethods.GetDpiForMonitor(monitor, Interop.NativeMethods.MDT_EFFECTIVE_DPI, out var dpi, out _);
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
        catch (Exception)
        {
            return 1.0;
        }
    }
}
