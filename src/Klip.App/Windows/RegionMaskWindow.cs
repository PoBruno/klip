using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Klip.Interop;

namespace Klip.App.Windows;

/// <summary>
/// Scrim over the whole monitor with a hole where the recording region is,
/// the same dimming the screenshot overlay gives the area outside the
/// selection. Click-through, no focus, kept out of the capture (same rules as
/// <see cref="RegionFrameWindow"/>).
/// </summary>
public sealed class RegionMaskWindow : Window
{
    private readonly System.Windows.Shapes.Path _scrim;

    public RegionMaskWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _scrim = new System.Windows.Shapes.Path
        {
            Fill = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)), // ~40%, igual ao overlay de captura
            IsHitTestVisible = false,
        };
        Content = new Grid { Children = { _scrim } };
    }

    /// <summary>Cobre o monitor (px fisicos) e deixa a regiao (px fisicos) sem escurecer.</summary>
    public void ShowAround(NativeMethods.RECT monitor, int x, int y, int width, int height)
    {
        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();

        // click-through, no activation, out of Alt-Tab and out of captures
        var exStyle = (long)NativeMethods.GetWindowLongPtr(helper.Handle, NativeMethods.GWL_EXSTYLE);
        exStyle |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongPtr(helper.Handle, NativeMethods.GWL_EXSTYLE, (nint)exStyle);
        NativeMethods.SetWindowDisplayAffinity(helper.Handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE);

        var monitorWidth = monitor.right - monitor.left;
        var monitorHeight = monitor.bottom - monitor.top;
        NativeMethods.SetWindowPos(helper.Handle, nint.Zero,
            monitor.left, monitor.top, monitorWidth, monitorHeight,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        Show();

        // WPF trabalha em DIP; a regiao chega em px fisicos do desktop virtual
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var full = new RectangleGeometry(new Rect(0, 0, monitorWidth / scale, monitorHeight / scale));
        var hole = new RectangleGeometry(new Rect(
            (x - monitor.left) / scale, (y - monitor.top) / scale,
            width / scale, height / scale));
        _scrim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, hole);
    }
}
