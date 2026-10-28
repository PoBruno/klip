using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Klip.App.Localization;
using Klip.App.Services;
using Klip.Interop;

namespace Klip.App.Windows;

/// <summary>
/// An image floating on top of everything (pin to screen). Borderless with a soft
/// shadow; drag moves, wheel zooms, Ctrl+wheel changes opacity, double-click or Esc
/// closes. Shows at 1:1 physical pixels on whatever monitor it sits on.
/// </summary>
/// <remarks>
/// It is NOT excluded from captures on purpose: pinning a reference and capturing
/// it together with the screen behind is a real use (Snipaste works the same way).
/// </remarks>
public sealed class PinnedImageWindow : Window
{
    private const double ShadowMargin = 14;
    private const double MinZoom = 0.1;
    private const double MaxZoom = 8;
    private const double MinOpacity = 0.2;

    private static readonly FontFamily IconFont = new("Segoe Fluent Icons");

    private readonly BitmapSource _image;
    private readonly ClipboardWriteGuard _writeGuard;
    private readonly Image _view;
    private double _zoom = 1;
    private double _dpiScale = 1;
    private bool _clickThrough;

    public PinnedImageWindow(BitmapSource image, ClipboardWriteGuard writeGuard)
    {
        _image = image;
        _writeGuard = writeGuard;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true; // layered: needed for the shadow and for click through
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        Title = "Klip";

        _view = new Image { Source = image, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(_view, BitmapScalingMode.HighQuality);

        var frame = new Border
        {
            Margin = new Thickness(ShadowMargin),
            BorderThickness = new Thickness(1),
            BorderBrush = FrameBrush,
            Child = _view,
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.45 },
            Cursor = Cursors.SizeAll,
            ToolTip = Loc.PinnedImageTooltip,
        };
        ToolTipService.SetInitialShowDelay(frame, 1200);
        Content = frame;
        ContextMenu = BuildMenu();

        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseWheel += OnMouseWheel;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };
        SourceInitialized += (_, _) =>
        {
            _dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            ApplySize();
        };
        DpiChanged += (_, e) =>
        {
            _dpiScale = e.NewDpi.DpiScaleX;
            ApplySize();
        };
    }

    private static readonly Brush FrameBrush = CreateFrameBrush();

    private static SolidColorBrush CreateFrameBrush()
    {
        var brush = new SolidColorBrush(Color.FromArgb(0x55, 0x80, 0x80, 0x80));
        brush.Freeze();
        return brush;
    }

    public bool IsClickThrough => _clickThrough;

    /// <summary>
    /// Centers the image on a point in physical px, clamped to the work area of
    /// that monitor, then shows it.
    /// </summary>
    public void ShowNear(int x, int y, NativeMethods.RECT workArea, uint dpi)
    {
        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle(); // SourceInitialized reads the DPI of wherever it was born

        _dpiScale = dpi / 96.0;
        ApplySize();

        var w = (int)Math.Round(_image.PixelWidth + 2 * ShadowMargin * _dpiScale);
        var h = (int)Math.Round(_image.PixelHeight + 2 * ShadowMargin * _dpiScale);
        var left = Math.Clamp(x - w / 2, workArea.left, Math.Max(workArea.left, workArea.right - w));
        var top = Math.Clamp(y - h / 2, workArea.top, Math.Max(workArea.top, workArea.bottom - h));
        NativeMethods.SetWindowPos(helper.Handle, nint.Zero, left, top, 0, 0,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        Show();
        Activate();
    }

    /// <summary>Turns click through on or off (WS_EX_TRANSPARENT on the layered window).</summary>
    public void SetClickThrough(bool on)
    {
        _clickThrough = on;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero)
            return;
        var ex = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        ex = on ? ex | NativeMethods.WS_EX_TRANSPARENT : ex & ~NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, (nint)ex);
        ClickThroughChanged?.Invoke();
    }

    public event Action? ClickThroughChanged;

    private void ApplySize()
    {
        // 1:1 physical pixels at zoom 100%
        _view.Width = _image.PixelWidth / _dpiScale * _zoom;
        _view.Height = _image.PixelHeight / _dpiScale * _zoom;
        RenderOptions.SetBitmapScalingMode(_view,
            _zoom >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            Close();
            return;
        }
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // button already released
        }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var up = e.Delta > 0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            Opacity = Math.Clamp(Math.Round(Opacity + (up ? 0.1 : -0.1), 1), MinOpacity, 1);
            return;
        }

        var old = _zoom;
        _zoom = Math.Clamp(up ? _zoom * 1.1 : _zoom / 1.1, MinZoom, MaxZoom);
        if (Math.Abs(_zoom - 1) < 0.05)
            _zoom = 1; // snap back to the real size
        if (_zoom == old)
            return;

        // keep the point under the cursor where it is
        var p = e.GetPosition(_view);
        var ratio = _zoom / old;
        Left -= p.X * (ratio - 1);
        Top -= p.Y * (ratio - 1);
        ApplySize();
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item(Loc.PinCopy, "", (_, _) => CopyToClipboard()));
        menu.Items.Add(Item(Loc.PinSave, "", (_, _) => SaveAs()));
        menu.Items.Add(Item(Loc.PinClickThrough, "", (_, _) => SetClickThrough(true)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(Loc.PinClose, "", (_, _) => Close()));
        return menu;
    }

    private static MenuItem Item(string header, string glyph, RoutedEventHandler onClick)
    {
        var item = new MenuItem
        {
            Header = header,
            Icon = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 14 },
        };
        item.Click += onClick;
        return item;
    }

    private void CopyToClipboard()
    {
        var image = _image;
        _ = Task.Run(() =>
        {
            try
            {
                _writeGuard.WriteImageFromPng(ScreenCaptureService.EncodePng(image), image);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("PinnedCopy", ex);
            }
        });
    }

    private void SaveAs()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG|*.png",
            FileName = $"Klip {DateTime.Now:yyyy-MM-dd HHmmss}.png",
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            File.WriteAllBytes(dialog.FileName, ScreenCaptureService.EncodePng(_image));
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("PinnedSave", ex);
        }
    }
}
