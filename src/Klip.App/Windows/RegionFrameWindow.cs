using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Klip.Interop;

namespace Klip.App.Windows;

/// <summary>
/// Accent frame pinned around the panoramic capture region: click-through
/// (WS_EX_TRANSPARENT), no focus, kept out of the capture. The content scrolls
/// inside it like Snagit, but without the HUD landing in the shot.
/// </summary>
public sealed class RegionFrameWindow : Window
{
    public RegionFrameWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;

        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)), // accent
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(2),
        };
    }

    /// <summary>Places it around the region (physical px), border sitting outside the content.</summary>
    public void ShowAround(int x, int y, int width, int height)
    {
        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();

        // click-through, no activation, out of Alt-Tab and out of captures
        var exStyle = (long)NativeMethods.GetWindowLongPtr(helper.Handle, NativeMethods.GWL_EXSTYLE);
        exStyle |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongPtr(helper.Handle, NativeMethods.GWL_EXSTYLE, (nint)exStyle);
        NativeMethods.SetWindowDisplayAffinity(helper.Handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE);

        const int pad = 3; // a borda fica FORA da área capturada
        NativeMethods.SetWindowPos(helper.Handle, nint.Zero,
            x - pad, y - pad, width + pad * 2, height + pad * 2,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        Show();
    }
}
