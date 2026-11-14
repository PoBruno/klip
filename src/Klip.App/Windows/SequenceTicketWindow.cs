using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Klip.Interop;

namespace Klip.App.Windows;

/// <summary>
/// Small "2/3" ticket next to the text caret while a paste sequence is running, same
/// shape as the badge on the cards. Never takes focus, ignores the mouse and stays out
/// of captures. One instance, moved and updated in place.
/// </summary>
public sealed class SequenceTicketWindow : Window
{
    private const double TicketHeight = 20;
    private const double Notch = 4;

    private readonly TextBlock _text;
    private readonly Grid _ticket;

    public SequenceTicketWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        Focusable = false;
        IsHitTestVisible = false;
        Title = "Klip";

        _text = new TextBlock
        {
            Margin = new Thickness(10, 0, 10, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
        };
        _text.SetResourceReference(TextBlock.ForegroundProperty, "TextOnAccentFillColorPrimaryBrush");

        var fill = new Border();
        fill.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");

        _ticket = new Grid { Height = TicketHeight };
        _ticket.Children.Add(fill);
        _ticket.Children.Add(_text);
        _ticket.SizeChanged += (_, e) => _ticket.Clip = TicketShape(e.NewSize.Width);

        // the shadow sits on a wrapper, so the clip does not cut it off
        Content = new Border
        {
            Margin = new Thickness(6),
            Child = _ticket,
            Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.35 },
        };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            ex |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TRANSPARENT;
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, (nint)ex);
            NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        };
    }

    /// <summary>Shows "current/total" by the caret (or the mouse when there is no caret).</summary>
    public void ShowProgress(int current, int total)
    {
        _text.Text = $"{current}/{total}";
        if (IsVisible)
            return;

        // the window is placed in physical pixels, straight on the HWND: the caret can
        // be on a monitor with another scale, where DIP math would drift
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        if (AnchorPoint() is { } p)
        {
            NativeMethods.SetWindowPos(hwnd, nint.Zero, p.x, p.y, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }
        Show();
    }

    // just above the caret line, so the ticket never covers the text being pasted.
    // the window has a 6 DIP margin around the ticket (for the shadow), hence the offsets
    private NativeMethods.POINT? AnchorPoint()
    {
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        var height = (int)Math.Round((TicketHeight + 12) * scale);
        if (NativeMethods.TryGetCaretRect(out var caret))
            return new NativeMethods.POINT { x = caret.left - (int)(6 * scale), y = caret.top - height };
        if (NativeMethods.GetCursorPos(out var cursor))
            return new NativeMethods.POINT { x = cursor.x + 12, y = cursor.y + 12 };
        return null;
    }

    // rounded rectangle with a half circle bitten out of each side, like a ticket stub
    private static Geometry TicketShape(double width)
    {
        var body = new RectangleGeometry(new Rect(0, 0, width, TicketHeight), 3, 3);
        var notches = new GeometryGroup();
        notches.Children.Add(new EllipseGeometry(new Point(0, TicketHeight / 2), Notch, Notch));
        notches.Children.Add(new EllipseGeometry(new Point(width, TicketHeight / 2), Notch, Notch));
        var shape = new CombinedGeometry(GeometryCombineMode.Exclude, body, notches);
        shape.Freeze();
        return shape;
    }
}
