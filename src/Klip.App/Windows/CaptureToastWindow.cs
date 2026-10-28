using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Klip.App.Localization;
using Klip.Interop;

namespace Klip.App.Windows;

/// <summary>What a capture toast shows and what its buttons do.</summary>
public sealed record CaptureToast(string Title, string Message)
{
    /// <summary>Small preview on the left.</summary>
    public BitmapSource? Thumbnail { get; init; }

    /// <summary>Color sample on the left (color picker).</summary>
    public Color? Swatch { get; init; }

    /// <summary>Glyph on the left when there's no thumbnail or swatch.</summary>
    public string? Glyph { get; init; }

    /// <summary>Clicking the body.</summary>
    public Action? OnClick { get; init; }

    /// <summary>"Pin to screen" button, hidden when null.</summary>
    public Action? OnPin { get; init; }
}

/// <summary>
/// Post-capture toast in the corner of the work area, above the tray. The tray
/// balloon can't hold buttons or a color sample, so captures use this one.
/// Never activates, lives a few seconds (longer while hovered) and is destroyed
/// on close. Only one at a time.
/// </summary>
public sealed class CaptureToastWindow : Window
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons");
    private static CaptureToastWindow? _current;

    private readonly DispatcherTimer _timer;

    private CaptureToastWindow(CaptureToast toast)
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
        Title = "Klip";

        Content = Build(toast);

        // one shot, restarted on mouse leave; nothing ticks once it's gone
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Lifetime };
        _timer.Tick += (_, _) => Close();
        MouseEnter += (_, _) => _timer.Stop();
        MouseLeave += (_, _) => _timer.Start();

        SourceInitialized += (_, _) =>
        {
            // no focus steal and no Alt+Tab entry
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            ex |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, (nint)ex);
            // the toast never ends up inside a capture
            NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        };
        Loaded += (_, _) => PlaceInCorner();
        Closed += (_, _) =>
        {
            _timer.Stop();
            if (_current == this)
                _current = null;
        };
    }

    /// <summary>Shows the toast, replacing the one on screen.</summary>
    public static void Present(CaptureToast toast)
    {
        _current?.Close();
        var window = new CaptureToastWindow(toast);
        _current = window;
        window.Show();
        window._timer.Start();
    }

    /// <summary>Drops the toast on screen, if any (a new capture is starting).</summary>
    public static void Dismiss() => _current?.Close();

    private void PlaceInCorner()
    {
        // primary work area, where the tray lives
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 4;
        Top = area.Bottom - ActualHeight - 4;
    }

    private FrameworkElement Build(CaptureToast toast)
    {
        var grid = new Grid { MinWidth = 300, MaxWidth = 420 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lead = BuildLead(toast);
        grid.Children.Add(lead);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
        var title = new TextBlock { Text = toast.Title, FontWeight = FontWeights.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
        var message = new TextBlock { Text = toast.Message, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        message.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        text.Children.Add(title);
        if (toast.Message.Length > 0)
            text.Children.Add(message);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (toast.OnPin is { } pin)
        {
            buttons.Children.Add(GlyphButton("", Loc.ToastPin, () =>
            {
                pin();
                Close();
            }));
        }
        buttons.Children.Add(GlyphButton("", Loc.ToastDismiss, Close));
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(buttons);

        var card = new Border
        {
            Margin = new Thickness(12),
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = grid,
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.35 },
        };
        card.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorDefaultBrush");
        card.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");

        if (toast.OnClick is { } click)
        {
            card.Cursor = Cursors.Hand;
            card.MouseLeftButtonUp += (_, e) =>
            {
                if (e.OriginalSource is DependencyObject d && FindButton(d) is not null)
                    return;
                click();
                Close();
            };
        }
        return card;
    }

    private static FrameworkElement BuildLead(CaptureToast toast)
    {
        if (toast.Thumbnail is { } thumb)
        {
            var image = new Image
            {
                Source = thumb,
                Stretch = Stretch.Uniform,
                MaxWidth = 72,
                MaxHeight = 56,
                VerticalAlignment = VerticalAlignment.Center,
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            return new Border { Child = image, CornerRadius = new CornerRadius(4), ClipToBounds = true };
        }

        if (toast.Swatch is { } color)
        {
            var swatch = new Border
            {
                Width = 40,
                Height = 40,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(color),
                VerticalAlignment = VerticalAlignment.Center,
            };
            swatch.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
            return swatch;
        }

        var glyph = new TextBlock
        {
            Text = toast.Glyph ?? "",
            FontFamily = IconFont,
            FontSize = 24,
            VerticalAlignment = VerticalAlignment.Center,
        };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        return glyph;
    }

    private static Button GlyphButton(string glyph, string tooltip, Action onClick)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 14 },
            ToolTip = tooltip,
            Padding = new Thickness(8),
            Margin = new Thickness(2, 0, 0, 0),
            MinWidth = 0,
            MinHeight = 0,
            Cursor = Cursors.Arrow,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static Button? FindButton(DependencyObject d)
    {
        for (var node = d; node is not null; node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node))
        {
            if (node is Button b)
                return b;
            if (node is CaptureToastWindow)
                return null;
        }
        return null;
    }
}
