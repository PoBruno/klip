using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Klip.App.Services;
using Klip.Interop;

namespace Klip.App.Windows;

/// <summary>Capture modes.</summary>
public enum CaptureMode
{
    Rectangle,
    Window,
    Fullscreen,
    Scrolling,
    Freeform,
}

/// <summary>Delay before the shot fires.</summary>
public enum CaptureDelay
{
    None = 0,
    Sec3 = 3,
    Sec5 = 5,
    Sec10 = 10,
}

/// <summary>
/// Per-monitor capture overlay: frozen frame, scrim around 40 percent,
/// dashed white marching-ants selection with a lit area, and a pill toolbar
/// on top of the monitor under the cursor. Built in code since it is all
/// interop and dynamic visuals.
/// </summary>
public sealed class CaptureOverlayWindow : Window
{
    private static CaptureMode _mode = CaptureMode.Rectangle; // shared across monitors
    private static CaptureDelay _delay = CaptureDelay.None;    // same deal
    private Button? _delayButton;
    private TextBlock? _countdownLabel;

    private readonly FrozenMonitor _source;
    private readonly IReadOnlyList<NativeMethods.TopLevelWindow> _topWindows;
    private readonly Action<FrozenMonitor, Int32Rect, CaptureMode, Point[]?> _onSelected;
    private readonly Action _onCancel;

    private readonly Grid _root = new();
    private readonly System.Windows.Shapes.Path _scrim;
    private readonly Rectangle _selectionBorder;
    private readonly TextBlock _sizeLabel;
    private readonly Border _sizeLabelHost;
    private readonly List<Button> _modeButtons = [];
    private Border? _toolbar;

    private bool _dragging;
    private Point _dragStartDip;
    private Rect _selectionDip = Rect.Empty;
    private double _scale = 1.0;

    // freeform lasso: points collected in DIP while dragging
    private readonly List<Point> _lassoDip = [];
    private readonly System.Windows.Shapes.Polygon _lassoShape;

    public CaptureOverlayWindow(
        FrozenMonitor source,
        bool showToolbar,
        IReadOnlyList<NativeMethods.TopLevelWindow> topWindows,
        Action<FrozenMonitor, Int32Rect, CaptureMode, Point[]?> onSelected,
        Action onCancel)
    {
        _source = source;
        _topWindows = topWindows;
        _onSelected = onSelected;
        _onCancel = onCancel;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = Cursors.Cross;
        Background = Brushes.Black;

        // frozen frame fills the whole window
        _root.Children.Add(new Image { Source = source.Frame, Stretch = Stretch.Fill });

        // scrim with a hole punched where the selection is (the lit area)
        _scrim = new System.Windows.Shapes.Path
        {
            Fill = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)), // ~40%
            IsHitTestVisible = false,
        };
        _root.Children.Add(_scrim);

        // animated white dashed border (marching ants)
        _selectionBorder = new Rectangle
        {
            Stroke = Brushes.White,
            StrokeThickness = 2,
            StrokeDashArray = [4, 3],
            Fill = Brushes.Transparent,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _selectionBorder.BeginAnimation(Shape.StrokeDashOffsetProperty,
            new DoubleAnimation(0, 14, TimeSpan.FromSeconds(0.6)) { RepeatBehavior = RepeatBehavior.Forever });
        _root.Children.Add(_selectionBorder);

        // freeform lasso outline (same marching-ants look, but a polygon)
        _lassoShape = new System.Windows.Shapes.Polygon
        {
            Stroke = Brushes.White,
            StrokeThickness = 2,
            StrokeDashArray = [4, 3],
            Fill = Brushes.Transparent,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _lassoShape.BeginAnimation(Shape.StrokeDashOffsetProperty,
            new DoubleAnimation(0, 14, TimeSpan.FromSeconds(0.6)) { RepeatBehavior = RepeatBehavior.Forever });
        _root.Children.Add(_lassoShape);

        // selection size in physical px, the native tool doesn't show this
        _sizeLabel = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 12,
            FontFamily = new FontFamily("Segoe UI Variable, Segoe UI"),
        };
        _sizeLabelHost = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x20, 0x20, 0x20)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Child = _sizeLabel,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _root.Children.Add(_sizeLabelHost);

        if (showToolbar)
            BuildToolbar();

        Content = _root;

        MouseDown += OnOverlayMouseDown;
        MouseMove += OnOverlayMouseMove;
        MouseUp += OnOverlayMouseUp;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                _onCancel();
        };
    }

    public void ShowOverlay()
    {
        // grab the handle first so we can place it in physical px on the right monitor
        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();

        // never show up in captures (belt and suspenders on top of the freeze frame)
        NativeMethods.SetWindowDisplayAffinity(helper.Handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE);

        var b = _source.Monitor.Bounds;
        NativeMethods.SetWindowPos(helper.Handle, nint.Zero, b.left, b.top,
            b.right - b.left, b.bottom - b.top, NativeMethods.SWP_NOZORDER);

        Show();
        Activate();
        Focus();

        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice;
        _scale = transform?.M11 ?? _source.Monitor.Dpi / 96.0;
        UpdateScrim();
    }

    public void CloseOverlay()
    {
        try
        {
            Close();
        }
        catch (InvalidOperationException)
        {
            // already closing
        }
    }

    // ----- Toolbar pill -----

    private void BuildToolbar()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        AddModeButton(panel, "\uF407", Localization.Loc.ModeRectangle, CaptureMode.Rectangle);
        AddModeButton(panel, "\uE7F4", Localization.Loc.ModeWindow, CaptureMode.Window);
        AddModeButton(panel, "\uE740", Localization.Loc.ModeFullscreen, CaptureMode.Fullscreen);
        AddModeButton(panel, "\uEF20", Localization.Loc.ModeFreeform, CaptureMode.Freeform);

        panel.Children.Add(Divider());

        // scrolling capture
        AddModeButton(panel, "\uEC8F", Localization.Loc.ModeScrolling, CaptureMode.Scrolling);

        panel.Children.Add(Divider());

        // delay cycles None -> 3 -> 5 -> 10s, only on the main toolbar
        _delayButton = ToolButton("\uE916", Localization.Loc.CaptureDelayTooltip);
        _delayButton.Click += (_, _) =>
        {
            _delay = _delay switch
            {
                CaptureDelay.None => CaptureDelay.Sec3,
                CaptureDelay.Sec3 => CaptureDelay.Sec5,
                CaptureDelay.Sec5 => CaptureDelay.Sec10,
                _ => CaptureDelay.None,
            };
            UpdateDelayButton();
        };
        panel.Children.Add(_delayButton);

        panel.Children.Add(Divider());

        var close = ToolButton("\uE711", Localization.Loc.CloseEsc);
        close.Click += (_, _) => _onCancel();
        panel.Children.Add(close);

        _toolbar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x2C, 0x2C, 0x2C)),
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 24, 0, 0),
            Child = panel,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 12,
                ShadowDepth = 2,
                Opacity = 0.5,
            },
        };
        _root.Children.Add(_toolbar);
        RefreshToolbarState();
        UpdateDelayButton();
    }

    private void AddModeButton(StackPanel panel, string glyph, string tooltip, CaptureMode mode)
    {
        var button = ToolButton(glyph, tooltip);
        button.Tag = mode;
        button.Click += (_, _) =>
        {
            _mode = mode;
            ResetSelection();
            ResetLasso();
            RefreshToolbarState();
        };
        _modeButtons.Add(button);
        panel.Children.Add(button);
    }

    private void RefreshToolbarState()
    {
        // light up the active mode with a subtle accent on the dark fill
        foreach (var button in _modeButtons)
        {
            button.Background = (CaptureMode)button.Tag == _mode
                ? new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF))
                : Brushes.Transparent;
        }
    }

    private static Border Divider() => new()
    {
        Width = 1,
        Margin = new Thickness(6),
        Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
    };

    private static Button ToolButton(string glyph, string tooltip)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 16,
                Foreground = Brushes.White,
            },
            ToolTip = tooltip,
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(2, 0, 2, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Arrow,
        };
        // flat rounded template, drop the classic WPF button chrome
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent),
        });
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(14));
        border.SetValue(Border.PaddingProperty, new System.Windows.Data.Binding("Padding")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent),
        });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        border.AppendChild(presenter);
        template.VisualTree = border;
        button.Template = template;
        return button;
    }

    // ----- Interaction -----

    private void OnOverlayMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || (_toolbar?.IsMouseOver ?? false))
            return;

        switch (_mode)
        {
            case CaptureMode.Rectangle or CaptureMode.Scrolling:
                _dragging = true;
                _dragStartDip = e.GetPosition(_root);
                _selectionDip = new Rect(_dragStartDip, _dragStartDip);
                UpdateSelectionVisuals();
                CaptureMouse();
                break;

            case CaptureMode.Freeform:
                _dragging = true;
                _lassoDip.Clear();
                _lassoDip.Add(e.GetPosition(_root));
                CaptureMouse();
                break;

            case CaptureMode.Window:
                if (!_selectionDip.IsEmpty && _selectionDip.Width > 0)
                    CompleteSelection(); // click grabs the highlighted window
                break;

            case CaptureMode.Fullscreen:
                // one click grabs the whole monitor
                FireSelection(new Int32Rect(0, 0, _source.Frame.PixelWidth, _source.Frame.PixelHeight));
                break;
        }
    }

    private void OnOverlayMouseMove(object sender, MouseEventArgs e)
    {
        var posDip = e.GetPosition(_root);

        if (_mode is CaptureMode.Rectangle or CaptureMode.Scrolling && _dragging)
        {
            _selectionDip = new Rect(_dragStartDip, posDip);
            UpdateSelectionVisuals();
        }
        else if (_mode == CaptureMode.Freeform && _dragging)
        {
            // only add a point when it moved a bit, keeps the polygon light
            if (_lassoDip.Count == 0 || (posDip - _lassoDip[^1]).Length >= 3)
                _lassoDip.Add(posDip);
            UpdateLassoVisuals();
        }
        else if (_mode == CaptureMode.Window && !(_toolbar?.IsMouseOver ?? false))
        {
            // highlight the window under the cursor, using Z order and real DWM bounds
            var screenPhys = PointToScreen(posDip);
            var hit = _topWindows.FirstOrDefault(w =>
                screenPhys.X >= w.Bounds.left && screenPhys.X < w.Bounds.right &&
                screenPhys.Y >= w.Bounds.top && screenPhys.Y < w.Bounds.bottom);

            if (hit.Handle != nint.Zero)
            {
                var m = _source.Monitor.Bounds;
                var x1 = Math.Max(hit.Bounds.left, m.left);
                var y1 = Math.Max(hit.Bounds.top, m.top);
                var x2 = Math.Min(hit.Bounds.right, m.right);
                var y2 = Math.Min(hit.Bounds.bottom, m.bottom);
                if (x2 > x1 && y2 > y1)
                {
                    _selectionDip = new Rect(
                        (x1 - m.left) / _scale, (y1 - m.top) / _scale,
                        (x2 - x1) / _scale, (y2 - y1) / _scale);
                    UpdateSelectionVisuals();
                    return;
                }
            }
            ResetSelection();
        }
    }

    private void OnOverlayMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_mode is CaptureMode.Rectangle or CaptureMode.Scrolling && _dragging && e.ChangedButton == MouseButton.Left)
        {
            _dragging = false;
            ReleaseMouseCapture();
            CompleteSelection();
        }
        else if (_mode == CaptureMode.Freeform && _dragging && e.ChangedButton == MouseButton.Left)
        {
            _dragging = false;
            ReleaseMouseCapture();
            CompleteFreeform();
        }
    }

    private void CompleteFreeform()
    {
        // need at least a triangle to have an area
        if (_lassoDip.Count < 3)
        {
            ResetLasso();
            return;
        }

        // bounding box of the lasso in DIP
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in _lassoDip)
        {
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }
        var boxDip = new Rect(minX, minY, maxX - minX, maxY - minY);
        if (boxDip.Width < 1 || boxDip.Height < 1)
        {
            ResetLasso();
            return;
        }

        // DIP -> physical px for the bounding box
        var rect = new Int32Rect(
            Math.Max(0, (int)Math.Round(boxDip.X * _scale)),
            Math.Max(0, (int)Math.Round(boxDip.Y * _scale)),
            (int)Math.Round(boxDip.Width * _scale),
            (int)Math.Round(boxDip.Height * _scale));
        rect.Width = Math.Min(rect.Width, _source.Frame.PixelWidth - rect.X);
        rect.Height = Math.Min(rect.Height, _source.Frame.PixelHeight - rect.Y);
        if (rect.Width < 1 || rect.Height < 1)
        {
            ResetLasso();
            return;
        }

        // polygon points in physical px, relative to the bounding box origin
        var mask = new Point[_lassoDip.Count];
        for (var i = 0; i < _lassoDip.Count; i++)
        {
            mask[i] = new Point(
                _lassoDip[i].X * _scale - rect.X,
                _lassoDip[i].Y * _scale - rect.Y);
        }

        _lassoShape.Visibility = Visibility.Collapsed;
        _onSelected(_source, rect, CaptureMode.Freeform, mask);
    }

    private void CompleteSelection()
    {
        if (_selectionDip.IsEmpty || _selectionDip.Width < 1 || _selectionDip.Height < 1)
        {
            ResetSelection();
            return;
        }

        // DIP -> physical px of the frame
        var rect = new Int32Rect(
            Math.Max(0, (int)Math.Round(_selectionDip.X * _scale)),
            Math.Max(0, (int)Math.Round(_selectionDip.Y * _scale)),
            (int)Math.Round(_selectionDip.Width * _scale),
            (int)Math.Round(_selectionDip.Height * _scale));

        rect.Width = Math.Min(rect.Width, _source.Frame.PixelWidth - rect.X);
        rect.Height = Math.Min(rect.Height, _source.Frame.PixelHeight - rect.Y);

        FireSelection(rect);
    }

    /// <summary>Runs the countdown delay before firing the shot.</summary>
    private void FireSelection(Int32Rect rect)
    {
        // scrolling ignores the delay, the user sets the pace there
        if (_delay == CaptureDelay.None || _mode == CaptureMode.Scrolling)
        {
            _onSelected(_source, rect, _mode, null);
            return;
        }

        // freeze the selection and show the count over the area
        _dragging = false;
        ReleaseMouseCapture();
        if (_toolbar is not null)
            _toolbar.Visibility = Visibility.Collapsed;

        _countdownLabel ??= CreateCountdownLabel();
        _countdownLabel.Visibility = Visibility.Visible;
        var remaining = (int)_delay;
        _countdownLabel.Text = remaining.ToString();

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        timer.Tick += (_, _) =>
        {
            remaining--;
            if (remaining <= 0)
            {
                timer.Stop();
                _countdownLabel.Visibility = Visibility.Collapsed;
                // grab the frame now, the screen may have changed while we waited
                _onSelected(_source, rect, _mode, null);
            }
            else
            {
                _countdownLabel.Text = remaining.ToString();
            }
        };
        timer.Start();
    }

    private TextBlock CreateCountdownLabel()
    {
        var label = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 96,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 0 },
        };
        _root.Children.Add(label);
        return label;
    }

    private void UpdateDelayButton()
    {
        if (_delayButton?.Content is not TextBlock tb)
            return;
        // show the number instead of the glyph when a delay is set
        tb.Text = _delay == CaptureDelay.None ? "\uE916" : ((int)_delay).ToString();
        tb.FontFamily = _delay == CaptureDelay.None
            ? new FontFamily("Segoe Fluent Icons")
            : new FontFamily("Segoe UI Variable, Segoe UI");
        tb.FontWeight = _delay == CaptureDelay.None ? FontWeights.Normal : FontWeights.Bold;
        _delayButton.Background = _delay == CaptureDelay.None
            ? Brushes.Transparent
            : new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF));
    }

    // ----- Visuals -----

    private void ResetSelection()
    {
        _selectionDip = Rect.Empty;
        UpdateSelectionVisuals();
    }

    private void ResetLasso()
    {
        _lassoDip.Clear();
        _lassoShape.Points = [];
        _lassoShape.Visibility = Visibility.Collapsed;
        UpdateScrim();
    }

    private void UpdateLassoVisuals()
    {
        if (_lassoDip.Count < 2)
        {
            _lassoShape.Visibility = Visibility.Collapsed;
            return;
        }
        _lassoShape.Points = new System.Windows.Media.PointCollection(_lassoDip);
        _lassoShape.Visibility = Visibility.Visible;

        // punch the lasso shape out of the scrim so the inside stays lit
        var full = new RectangleGeometry(new Rect(0, 0,
            Math.Max(_root.ActualWidth, 1), Math.Max(_root.ActualHeight, 1)));
        var poly = new StreamGeometry();
        using (var ctx = poly.Open())
        {
            ctx.BeginFigure(_lassoDip[0], true, true);
            ctx.PolyLineTo(_lassoDip.Skip(1).ToList(), true, true);
        }
        poly.Freeze();
        _scrim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, poly);
    }

    private void UpdateSelectionVisuals()
    {
        UpdateScrim();

        if (_selectionDip.IsEmpty || _selectionDip.Width < 1)
        {
            _selectionBorder.Visibility = Visibility.Collapsed;
            _sizeLabelHost.Visibility = Visibility.Collapsed;
            return;
        }

        _selectionBorder.Visibility = Visibility.Visible;
        _selectionBorder.Margin = new Thickness(_selectionDip.X, _selectionDip.Y, 0, 0);
        _selectionBorder.Width = _selectionDip.Width;
        _selectionBorder.Height = _selectionDip.Height;

        _sizeLabel.Text = $"{(int)(_selectionDip.Width * _scale)} x {(int)(_selectionDip.Height * _scale)}";
        _sizeLabelHost.Visibility = Visibility.Visible;
        var labelY = _selectionDip.Bottom + 8;
        if (labelY > _root.ActualHeight - 32)
            labelY = Math.Max(0, _selectionDip.Y - 28);
        _sizeLabelHost.Margin = new Thickness(_selectionDip.X, labelY, 0, 0);
    }

    private void UpdateScrim()
    {
        var full = new RectangleGeometry(new Rect(0, 0,
            Math.Max(_root.ActualWidth, 1), Math.Max(_root.ActualHeight, 1)));

        if (_selectionDip.IsEmpty || _selectionDip.Width < 1)
        {
            _scrim.Data = full;
            return;
        }

        // punch out the selection so it reads as the lit area
        _scrim.Data = new CombinedGeometry(GeometryCombineMode.Exclude,
            full, new RectangleGeometry(_selectionDip));
    }
}
