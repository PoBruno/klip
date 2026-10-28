using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Klip.App.Services;
using Klip.Core.Capture;
using Klip.Core.Recording;
using Klip.Core.Settings;
using Klip.Interop;
using CaptureMode = Klip.Core.Settings.CaptureMode;

namespace Klip.App.Windows;

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
    // mode and delay belong to the session (shared by all monitors of one Win+Shift+S)
    private readonly CaptureSessionState _session;
    private CaptureMode Mode { get => _session.Mode; set => _session.Mode = value; }
    private CaptureDelay Delay { get => _session.Delay; set => _session.Delay = value; }

    private Button? _delayButton;
    private TextBlock? _countdownLabel;
    // kept so a cancel can stop the countdown
    private System.Windows.Threading.DispatcherTimer? _countdownTimer;

    private readonly FrozenMonitor _source;
    // only window mode needs them; the controller enumerates on the first call
    private readonly Func<IReadOnlyList<NativeMethods.TopLevelWindow>> _topWindows;
    private readonly Action<FrozenMonitor, Int32Rect, CaptureMode, Point[]?, bool> _onSelected;
    private readonly Action _onCancel;

    // modifier that sends the capture straight to the editor
    private readonly CaptureEditorModifier _editorModifier;
    private readonly bool _alwaysOpenEditor;
    private bool _modifierHeldSinceOpen; // modifier still held from the hotkey itself

    private readonly Grid _root = new();
    private readonly System.Windows.Shapes.Path _scrim;
    private readonly Rectangle _selectionBorder;
    private readonly TextBlock _sizeLabel;
    private readonly TextBlock _editorHintLabel;
    private readonly Border _sizeLabelHost;
    private readonly List<Button> _modeButtons = [];
    private Border? _toolbar;

    // recording options, inline under the GIF/MP4 button
    private readonly SettingsService _settings;
    private readonly Func<IAudioDeviceEnumerator> _audioEnumeratorFactory;
    private RecordingOptionsPanel? _recordingPanel;

    private bool _dragging;
    private Point _dragStartDip;
    private Rect _selectionDip = Rect.Empty;
    private double _scale = 1.0;

    // freeform lasso: points collected in DIP while dragging
    private readonly List<Point> _lassoDip = [];
    private readonly System.Windows.Shapes.Polygon _lassoShape;
    private bool _antsRunning;

    // static and frozen: built once per process, not per overlay per monitor
    private static readonly Brush ScrimBrush = Frozen(Color.FromArgb(0x66, 0, 0, 0)); // ~40%
    private static readonly Brush LabelBackBrush = Frozen(Color.FromArgb(0xCC, 0x20, 0x20, 0x20));
    private static readonly Brush ToolbarBrush = Frozen(Color.FromArgb(0xE6, 0x2C, 0x2C, 0x2C));
    private static readonly Brush ActiveButtonBrush = Frozen(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF));
    private static readonly Brush DividerBrush = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons");
    private static readonly FontFamily TextFont = new("Segoe UI Variable, Segoe UI");
    private static readonly ControlTemplate FlatButtonTemplate = CreateFlatButtonTemplate();

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public CaptureOverlayWindow(
        FrozenMonitor source,
        bool showToolbar,
        CaptureSessionState session,
        Func<IReadOnlyList<NativeMethods.TopLevelWindow>> topWindows,
        CaptureEditorModifier editorModifier,
        bool alwaysOpenEditor,
        SettingsService settings,
        Func<IAudioDeviceEnumerator> audioEnumeratorFactory,
        Action<FrozenMonitor, Int32Rect, CaptureMode, Point[]?, bool> onSelected,
        Action onCancel)
    {
        _source = source;
        _session = session;
        _topWindows = topWindows;
        _editorModifier = editorModifier;
        _alwaysOpenEditor = alwaysOpenEditor;
        _settings = settings;
        _audioEnumeratorFactory = audioEnumeratorFactory;
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
            Fill = ScrimBrush,
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
        // the marching ants only start when a selection shows up (StartMarchingAnts)
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
        _root.Children.Add(_lassoShape);

        // selection size in physical px, the native tool doesn't show this
        _sizeLabel = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 12,
            FontFamily = TextFont,
        };
        // small hint about the editor modifier, next to the size label
        _editorHintLabel = new TextBlock
        {
            Foreground = HintIdleBrush,
            FontSize = 12,
            FontFamily = TextFont,
            Margin = new Thickness(10, 0, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        var labelPanel = new StackPanel { Orientation = Orientation.Horizontal };
        labelPanel.Children.Add(_sizeLabel);
        labelPanel.Children.Add(_editorHintLabel);
        _sizeLabelHost = new Border
        {
            Background = LabelBackBrush,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Child = labelPanel,
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
        KeyDown += OnOverlayKeyDown;
        KeyUp += OnOverlayKeyUp;
    }

    /// <summary>
    /// Marching ants capped at 15 fps. Every tick re-tessellates the dash on the
    /// render thread; uncapped it runs at the monitor rate (60-360 Hz) over the whole
    /// frozen screen. 15 fps still reads as ants and costs 4x less or better.
    /// </summary>
    private static DoubleAnimation MarchingAntsAnimation()
    {
        var animation = new DoubleAnimation(0, 14, TimeSpan.FromSeconds(0.6))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Timeline.SetDesiredFrameRate(animation, 15);
        return animation;
    }

    /// <summary>Starts the ants the first time a selection is visible on this monitor.</summary>
    private void StartMarchingAnts()
    {
        if (_antsRunning)
            return;
        _antsRunning = true;
        _selectionBorder.BeginAnimation(Shape.StrokeDashOffsetProperty, MarchingAntsAnimation());
        _lassoShape.BeginAnimation(Shape.StrokeDashOffsetProperty, MarchingAntsAnimation());
    }

    /// <summary>
    /// Drops the Forever animations when the overlay goes away, otherwise the
    /// clock stays alive until the window is collected.
    /// </summary>
    private void StopMarchingAnts()
    {
        _antsRunning = false;
        _selectionBorder.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        _lassoShape.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
    }

    /// <summary>A cancelled countdown (Esc/close) can't outlive the overlay.</summary>
    private void StopCountdown()
    {
        _countdownTimer?.Stop();
        _countdownTimer = null;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _magnifierBrush = null;
        StopMarchingAnts();
        StopCountdown();
        base.OnClosing(e);
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

        // modifier already down when the overlay opened (Ctrl+Shift+S has Ctrl):
        // ignore it until released and pressed again. One GetAsyncKeyState read
        // because the original WM_KEYDOWN went to another window
        _modifierHeldSinceOpen = _editorModifier switch
        {
            CaptureEditorModifier.Control => NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL),
            CaptureEditorModifier.Shift => NativeMethods.IsKeyDown(NativeMethods.VK_SHIFT),
            CaptureEditorModifier.Alt => NativeMethods.IsKeyDown(NativeMethods.VK_MENU),
            _ => false,
        };

        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice;
        _scale = transform?.M11 ?? _source.Monitor.Dpi / 96.0;
        UpdateScrim();

        // session opened on GIF/MP4 (remembered mode): show the options right away,
        // after the toolbar layout or TranslatePoint returns garbage
        if (_toolbar is not null && Mode is CaptureMode.Gif or CaptureMode.Mp4)
        {
            Dispatcher.BeginInvoke(
                () => ShowRecordingOptions(Mode),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
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

        AddModeButton(panel, "\uF407", Localization.Loc.ModeRectangle, CaptureMode.Rectangle, "1");
        AddModeButton(panel, "\uE7F4", Localization.Loc.ModeWindow, CaptureMode.Window, "2");
        AddModeButton(panel, "\uE740", Localization.Loc.ModeFullscreen, CaptureMode.Fullscreen, "3");
        AddModeButton(panel, "\uEF20", Localization.Loc.ModeFreeform, CaptureMode.Freeform, "4");

        panel.Children.Add(Divider());

        // scrolling capture
        AddModeButton(panel, "\uEC8F", Localization.Loc.ModeScrolling, CaptureMode.Scrolling, "R");

        panel.Children.Add(Divider());

        // text (OCR straight to the clipboard) and color picker
        AddModeButton(panel, "\uE8D2", Localization.Loc.ModeText, CaptureMode.Text, "T");
        AddModeButton(panel, "\uEF3C", Localization.Loc.ModeColor, CaptureMode.Color, "C");

        panel.Children.Add(Divider());

        // GIF (\uF4A9) and MP4 (\uE714 = Video) recording, rectangle selection
        AddModeButton(panel, "\uF4A9", Localization.Loc.ModeGif, CaptureMode.Gif, "G");
        AddModeButton(panel, "\uE714", Localization.Loc.ModeMp4, CaptureMode.Mp4, "V");

        panel.Children.Add(Divider());

        // delay cycles None -> 3 -> 5 -> 10s, only on the main toolbar
        _delayButton = ToolButton("\uE916", Localization.Loc.CaptureDelayTooltip);
        _delayButton.Click += (_, _) =>
        {
            Delay = Delay switch
            {
                CaptureDelay.None => CaptureDelay.Sec3,
                CaptureDelay.Sec3 => CaptureDelay.Sec5,
                CaptureDelay.Sec5 => CaptureDelay.Sec10,
                _ => CaptureDelay.None,
            };
            UpdateDelayButton();
        };
        panel.Children.Add(_delayButton);

        var close = ToolButton("\uE711", Localization.Loc.CloseEsc);
        close.Click += (_, _) =>
        {
            StopCountdown(); // same as Esc
            _onCancel();
        };
        panel.Children.Add(close);

        _toolbar = new Border
        {
            Background = ToolbarBrush,
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

        // key presses can land on any monitor's overlay; this one owns the toolbar
        _session.ModeRequested += SelectMode;
        Closed += (_, _) => _session.ModeRequested -= SelectMode;

        RefreshToolbarState();
        UpdateDelayButton();
    }

    // one key per tool, shown in the tooltips
    private static readonly Dictionary<Key, CaptureMode> ModeKeys = new()
    {
        [Key.D1] = CaptureMode.Rectangle, [Key.NumPad1] = CaptureMode.Rectangle,
        [Key.D2] = CaptureMode.Window, [Key.NumPad2] = CaptureMode.Window,
        [Key.D3] = CaptureMode.Fullscreen, [Key.NumPad3] = CaptureMode.Fullscreen,
        [Key.D4] = CaptureMode.Freeform, [Key.NumPad4] = CaptureMode.Freeform,
        [Key.R] = CaptureMode.Scrolling,
        [Key.T] = CaptureMode.Text,
        [Key.C] = CaptureMode.Color,
        [Key.G] = CaptureMode.Gif,
        [Key.V] = CaptureMode.Mp4,
    };

    private void AddModeButton(StackPanel panel, string glyph, string tooltip, CaptureMode mode, string? shortcut = null)
    {
        var button = ToolButton(glyph, shortcut is null ? tooltip : $"{tooltip} ({shortcut})");
        button.Tag = mode;
        button.Click += (_, _) => SelectMode(mode);
        _modeButtons.Add(button);
        panel.Children.Add(button);
    }

    /// <summary>Same thing as clicking the tool button: the click and the keys both land here.</summary>
    private void SelectMode(CaptureMode mode)
    {
        // clicking the active recording mode again toggles its options;
        // switching modes closes the other one
        var repeat = Mode == mode;
        Mode = mode;
        ResetSelection();
        ResetLasso();
        RefreshToolbarState();
        HideMagnifier();

        if (mode is CaptureMode.Gif or CaptureMode.Mp4)
        {
            if (repeat && _recordingPanel is not null)
                HideRecordingOptions();
            else
                ShowRecordingOptions(mode);
        }
        else
        {
            HideRecordingOptions(); // only recording modes have options
        }
    }

    // ----- recording options (inline GIF/MP4 panel) -----

    /// <summary>
    /// Options panel anchored under the GIF/MP4 button. Changes are saved right
    /// away and apply to the next recording; selecting an area starts recording.
    /// </summary>
    private void ShowRecordingOptions(CaptureMode mode)
    {
        HideRecordingOptions();
        if (_toolbar is null)
            return;

        var panel = new RecordingOptionsPanel(
            mode == CaptureMode.Gif ? RecordingKind.Gif : RecordingKind.Mp4,
            _settings,
            _audioEnumeratorFactory);
        _recordingPanel = panel;
        _root.Children.Add(panel);
        // the mic list loads async and changes the size: re-anchor on every change
        panel.SizeChanged += (_, _) => PositionRecordingOptions();
        PositionRecordingOptions();
    }

    private void HideRecordingOptions()
    {
        if (_recordingPanel is null)
            return;
        _root.Children.Remove(_recordingPanel);
        _recordingPanel = null;
    }

    /// <summary>Centers the panel under the mode button, below the pill, clamped to the monitor.</summary>
    private void PositionRecordingOptions()
    {
        if (_recordingPanel is null || _toolbar is null)
            return;
        var mode = _recordingPanel.Kind == RecordingKind.Gif ? CaptureMode.Gif : CaptureMode.Mp4;
        var button = _modeButtons.FirstOrDefault(b => (CaptureMode)b.Tag == mode);
        if (button is null)
            return;

        var anchorX = button.TranslatePoint(new Point(button.ActualWidth / 2, 0), _root).X;
        var top = _toolbar.TranslatePoint(new Point(0, _toolbar.ActualHeight), _root).Y + 8;
        var width = _recordingPanel.ActualWidth;
        var left = Math.Clamp(anchorX - width / 2, 8.0, Math.Max(8.0, _root.ActualWidth - width - 8));
        _recordingPanel.Margin = new Thickness(left, top, 0, 0);
    }

    private void RefreshToolbarState()
    {
        // light up the active mode with a subtle accent on the dark fill
        foreach (var button in _modeButtons)
        {
            button.Background = (CaptureMode)button.Tag == Mode
                ? ActiveButtonBrush
                : Brushes.Transparent;
        }
    }

    private static Border Divider() => new()
    {
        Width = 1,
        Margin = new Thickness(6),
        Background = DividerBrush,
    };

    private static Button ToolButton(string glyph, string tooltip) => new()
    {
        Content = new TextBlock
        {
            Text = glyph,
            FontFamily = IconFont,
            FontSize = 16,
            Foreground = Brushes.White,
        },
        ToolTip = tooltip,
        Padding = new Thickness(10, 8, 10, 8),
        Margin = new Thickness(2, 0, 2, 0),
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Cursor = Cursors.Arrow,
        Template = FlatButtonTemplate,
    };

    // flat rounded template without the classic WPF button chrome, shared by every button
    private static ControlTemplate CreateFlatButtonTemplate()
    {
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
        border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        template.VisualTree = border;
        template.Seal();
        return template;
    }

    // ----- Interaction -----

    /// <summary>Modes that select by dragging a rectangle (recording included).</summary>
    private bool IsDragRectMode =>
        Mode is CaptureMode.Rectangle or CaptureMode.Scrolling or CaptureMode.Gif or CaptureMode.Mp4
            or CaptureMode.Text;

    private void OnOverlayMouseDown(object sender, MouseButtonEventArgs e)
    {
        // clicks on the toolbar or on the recording options never start a selection
        if (e.ChangedButton != MouseButton.Left || (_toolbar?.IsMouseOver ?? false)
            || (_recordingPanel?.IsMouseOver ?? false))
            return;

        switch (Mode)
        {
            case CaptureMode.Rectangle or CaptureMode.Scrolling or CaptureMode.Gif or CaptureMode.Mp4
                or CaptureMode.Text:
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

            case CaptureMode.Color:
                // one click picks the pixel under the cursor, straight from the frozen frame
                var (px, py) = PixelAt(e.GetPosition(_root));
                _onSelected(_source, new Int32Rect(px, py, 1, 1), CaptureMode.Color, null, false);
                break;
        }
    }

    private void OnOverlayMouseMove(object sender, MouseEventArgs e)
    {
        var posDip = e.GetPosition(_root);
        UpdateMagnifier(posDip);

        if (IsDragRectMode && _dragging)
        {
            _selectionDip = new Rect(_dragStartDip, posDip);
            UpdateSelectionVisuals();
        }
        else if (Mode == CaptureMode.Freeform && _dragging)
        {
            // only add a point when it moved a bit, keeps the polygon light
            if (_lassoDip.Count == 0 || (posDip - _lassoDip[^1]).Length >= 3)
                _lassoDip.Add(posDip);
            UpdateLassoVisuals();
        }
        else if (Mode == CaptureMode.Window && !(_toolbar?.IsMouseOver ?? false))
        {
            // highlight the window under the cursor, using Z order and real DWM bounds
            var screenPhys = PointToScreen(posDip);
            var hit = _topWindows().FirstOrDefault(w =>
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
        if (IsDragRectMode && _dragging && e.ChangedButton == MouseButton.Left)
        {
            _dragging = false;
            ReleaseMouseCapture();
            CompleteSelection();
        }
        else if (Mode == CaptureMode.Freeform && _dragging && e.ChangedButton == MouseButton.Left)
        {
            _dragging = false;
            ReleaseMouseCapture();
            CompleteFreeform();
        }
    }

    // ----- Editor modifier -----

    private void OnOverlayKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            StopCountdown(); // cancel during the countdown never fires the shot
            _onCancel();
            return;
        }

        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down && NudgeCursor(e.Key))
        {
            e.Handled = true;
            return;
        }

        // 1 2 3 4 R T C G V pick the tool, same as clicking it. Ctrl/Alt combos stay out
        // of the way, and so does a selection being dragged or a running countdown
        if (ModeKeys.TryGetValue(e.Key, out var requested)
            && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0
            && !_dragging && _countdownTimer is null)
        {
            _session.RequestMode(requested);
            e.Handled = true;
            return;
        }

        if (!IsConfiguredModifierKey(e))
            return;

        // the hint follows the key live
        UpdateEditorHint();
        if (e.Key == Key.System)
            e.Handled = true; // a configured Alt must not open the window menu
    }

    private void OnOverlayKeyUp(object sender, KeyEventArgs e)
    {
        if (!IsConfiguredModifierKey(e))
            return;

        // releasing the key ends the "held since the hotkey" state
        _modifierHeldSinceOpen = false;
        UpdateEditorHint();
        if (e.Key == Key.System)
            e.Handled = true;
    }

    /// <summary>True when the key is the configured modifier (Alt arrives as Key.System).</summary>
    private bool IsConfiguredModifierKey(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        return _editorModifier switch
        {
            CaptureEditorModifier.Control => key is Key.LeftCtrl or Key.RightCtrl,
            CaptureEditorModifier.Shift => key is Key.LeftShift or Key.RightShift,
            CaptureEditorModifier.Alt => key is Key.LeftAlt or Key.RightAlt,
            _ => false,
        };
    }

    /// <summary>Physical state via Keyboard.Modifiers; the overlay has focus here.</summary>
    private bool IsConfiguredModifierDown() => _editorModifier switch
    {
        CaptureEditorModifier.Control => Keyboard.Modifiers.HasFlag(ModifierKeys.Control),
        CaptureEditorModifier.Shift => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift),
        CaptureEditorModifier.Alt => Keyboard.Modifiers.HasFlag(ModifierKeys.Alt),
        _ => false,
    };

    /// <summary>Open-in-editor request at the moment the selection ends.</summary>
    private bool IsEditorModifierActive() =>
        EditorOpenDecision.IsModifierRequestValid(
            _editorModifier, IsConfiguredModifierDown(), _modifierHeldSinceOpen);

    private string ModifierDisplayName => _editorModifier switch
    {
        CaptureEditorModifier.Control => "Ctrl",
        CaptureEditorModifier.Shift => "Shift",
        CaptureEditorModifier.Alt => "Alt",
        _ => string.Empty,
    };

    // quiet grey when idle, system accent when active; frozen, the hint only swaps
    // the Foreground reference
    private static Brush HintIdleBrush { get; } = CreateHintIdleBrush();

    private static SolidColorBrush CreateHintIdleBrush()
    {
        var brush = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
        brush.Freeze();
        return brush;
    }

    private static Brush HintActiveBrush =>
        Application.Current?.TryFindResource("SystemAccentColorSecondaryBrush") as Brush
            ?? new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF)); // default light accent

    /// <summary>No hint with the modifier disabled or with "always open the editor".</summary>
    private void UpdateEditorHint()
    {
        if (_editorModifier == CaptureEditorModifier.None || _alwaysOpenEditor
            || Mode == CaptureMode.Scrolling // scrolling opens in the editor anyway
            || Mode is CaptureMode.Gif or CaptureMode.Mp4 // recordings never go to the image editor
            || Mode is CaptureMode.Text or CaptureMode.Color) // the result is text, nothing to edit
        {
            _editorHintLabel.Visibility = Visibility.Collapsed;
            return;
        }

        _editorHintLabel.Visibility = Visibility.Visible;
        if (IsEditorModifierActive())
        {
            _editorHintLabel.Text = Localization.Loc.EditorHintRelease;
            _editorHintLabel.Foreground = HintActiveBrush;
        }
        else
        {
            _editorHintLabel.Text = string.Format(Localization.Loc.EditorHint, ModifierDisplayName);
            _editorHintLabel.Foreground = HintIdleBrush;
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
        // modifier read at the moment the selection ends
        _onSelected(_source, rect, CaptureMode.Freeform, mask, IsEditorModifierActive());
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
        // recording: no overlay delay (the 3 s countdown belongs to the recording
        // flow) and no editor modifier
        if (Mode is CaptureMode.Gif or CaptureMode.Mp4)
        {
            _onSelected(_source, rect, Mode, null, false);
            return;
        }

        // modifier read at the end of the selection; never for scrolling or text
        var openEditor = Mode is not (CaptureMode.Scrolling or CaptureMode.Text) && IsEditorModifierActive();

        // scrolling ignores the delay, the user sets the pace there
        if (Delay == CaptureDelay.None || Mode == CaptureMode.Scrolling)
        {
            _onSelected(_source, rect, Mode, null, openEditor);
            return;
        }

        // freeze the selection and show the count over the area
        HideMagnifier();
        _dragging = false;
        ReleaseMouseCapture();
        if (_toolbar is not null)
            _toolbar.Visibility = Visibility.Collapsed;

        _countdownLabel ??= CreateCountdownLabel();
        _countdownLabel.Visibility = Visibility.Visible;
        var remaining = (int)Delay;
        _countdownLabel.Text = remaining.ToString();

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        StopCountdown(); // never two countdowns alive
        _countdownTimer = timer;
        timer.Tick += (_, _) =>
        {
            remaining--;
            if (remaining <= 0)
            {
                StopCountdown();
                _countdownLabel.Visibility = Visibility.Collapsed;
                // grab the frame now, the screen may have changed while we waited
                _onSelected(_source, rect, Mode, null, openEditor);
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
        tb.Text = Delay == CaptureDelay.None ? "\uE916" : ((int)Delay).ToString();
        tb.FontFamily = Delay == CaptureDelay.None ? IconFont : TextFont;
        tb.FontWeight = Delay == CaptureDelay.None ? FontWeights.Normal : FontWeights.Bold;
        _delayButton.Background = Delay == CaptureDelay.None
            ? Brushes.Transparent
            : ActiveButtonBrush;
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
        StartMarchingAnts();
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

        StartMarchingAnts();
        _selectionBorder.Visibility = Visibility.Visible;
        _selectionBorder.Margin = new Thickness(_selectionDip.X, _selectionDip.Y, 0, 0);
        _selectionBorder.Width = _selectionDip.Width;
        _selectionBorder.Height = _selectionDip.Height;

        _sizeLabel.Text = $"{(int)(_selectionDip.Width * _scale)} x {(int)(_selectionDip.Height * _scale)}";
        UpdateEditorHint(); // hint follows the size label
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

    // ----- Magnifier -----

    // 8 device pixels per frame pixel, 17 cells so the pixel under the cursor sits in the middle
    private const int MagnifierCells = 17;
    private const int MagnifierZoom = 8;

    private Border? _magnifier;
    private ImageBrush? _magnifierBrush;
    private TextBlock? _magnifierCoords;
    private TextBlock? _magnifierColor;
    private SolidColorBrush? _magnifierSwatch;
    private readonly byte[] _pixel = new byte[4];
    private int _lastPixelX = -1;
    private int _lastPixelY = -1;

    private bool WantsMagnifier =>
        Mode == CaptureMode.Color
        || (_settings.Current.CaptureMagnifier && Mode is not (CaptureMode.Window or CaptureMode.Fullscreen));

    /// <summary>Frame pixel under a point of the overlay (DIP to physical, clamped).</summary>
    private (int X, int Y) PixelAt(Point posDip) => (
        Math.Clamp((int)(posDip.X * _scale), 0, _source.Frame.PixelWidth - 1),
        Math.Clamp((int)(posDip.Y * _scale), 0, _source.Frame.PixelHeight - 1));

    /// <summary>
    /// Built once per overlay on the first move. Reads straight from the frozen frame
    /// through an ImageBrush viewbox, so a move only changes a rect and two strings.
    /// </summary>
    private void EnsureMagnifier()
    {
        if (_magnifier is not null)
            return;

        var cell = MagnifierZoom / _scale;
        var side = MagnifierCells * cell;

        _magnifierBrush = new ImageBrush(_source.Frame)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill,
        };
        RenderOptions.SetBitmapScalingMode(_magnifierBrush, BitmapScalingMode.NearestNeighbor);

        var lens = new Rectangle { Width = side, Height = side, Fill = _magnifierBrush };
        RenderOptions.SetBitmapScalingMode(lens, BitmapScalingMode.NearestNeighbor);

        // outline of the picked pixel
        var center = new Rectangle
        {
            Width = cell + 2,
            Height = cell + 2,
            Stroke = Brushes.White,
            StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(MagnifierCells / 2 * cell - 1, MagnifierCells / 2 * cell - 1, 0, 0),
        };
        var lensHost = new Grid { Width = side, Height = side, ClipToBounds = true, Background = Brushes.Black };
        lensHost.Children.Add(lens);
        lensHost.Children.Add(center);

        _magnifierCoords = new TextBlock { Foreground = Brushes.White, FontSize = 11, FontFamily = TextFont, Margin = new Thickness(2, 4, 2, 0) };
        _magnifierSwatch = new SolidColorBrush(); // mutated in place, never reallocated
        _magnifierColor = new TextBlock { Foreground = Brushes.White, FontSize = 11, FontFamily = TextFont, VerticalAlignment = VerticalAlignment.Center };
        var colorRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 2, 2, 0) };
        colorRow.Children.Add(new Border
        {
            Width = 12,
            Height = 12,
            Margin = new Thickness(0, 0, 6, 0),
            Background = _magnifierSwatch,
            BorderBrush = DividerBrush,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        });
        colorRow.Children.Add(_magnifierColor);

        var panel = new StackPanel();
        panel.Children.Add(lensHost);
        panel.Children.Add(_magnifierCoords);
        panel.Children.Add(colorRow);

        _magnifier = new Border
        {
            Background = LabelBackBrush,
            BorderBrush = DividerBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4),
            Child = panel,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
        };
        _root.Children.Add(_magnifier);
    }

    private void UpdateMagnifier(Point posDip)
    {
        if (!WantsMagnifier || _countdownTimer is not null
            || (_toolbar?.IsMouseOver ?? false) || (_recordingPanel?.IsMouseOver ?? false))
        {
            HideMagnifier();
            return;
        }

        EnsureMagnifier();
        var (px, py) = PixelAt(posDip);
        if (px != _lastPixelX || py != _lastPixelY)
        {
            _lastPixelX = px;
            _lastPixelY = py;

            // viewbox is in the brush DIP space; the frame is 96 dpi but don't assume it
            var k = 96.0 / (_source.Frame.DpiX > 0 ? _source.Frame.DpiX : 96.0);
            var (ox, oy) = MagnifierLayout.SourceOrigin(px, py, MagnifierCells);
            _magnifierBrush!.Viewbox = new Rect(ox * k, oy * k, MagnifierCells * k, MagnifierCells * k);

            // coordinates of the virtual desktop, physical px
            var b = _source.Monitor.Bounds;
            _magnifierCoords!.Text = $"{b.left + px}, {b.top + py}";
            if (ScreenCaptureService.TryReadPixel(_source.Frame, px, py, _pixel, out var r, out var g, out var bl))
            {
                _magnifierSwatch!.Color = Color.FromRgb(r, g, bl);
                _magnifierColor!.Text = ColorFormatter.ToHex(r, g, bl);
            }
        }

        var w = _magnifier!.ActualWidth > 0 ? _magnifier.ActualWidth : MagnifierCells * MagnifierZoom / _scale + 10;
        var h = _magnifier.ActualHeight > 0 ? _magnifier.ActualHeight : MagnifierCells * MagnifierZoom / _scale + 44;
        var (x, y) = MagnifierLayout.Place(posDip.X, posDip.Y, w, h, _root.ActualWidth, _root.ActualHeight);
        _magnifier.Margin = new Thickness(x, y, 0, 0);
        _magnifier.Visibility = Visibility.Visible;
    }

    private void HideMagnifier()
    {
        if (_magnifier is not null)
            _magnifier.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Arrow keys move the cursor 1 px (Shift = 10 px) on this monitor. While
    /// dragging, the moving edge of the selection follows it.
    /// </summary>
    private bool NudgeCursor(Key key)
    {
        if (Mode is CaptureMode.Window or CaptureMode.Fullscreen)
            return false;

        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        var (dx, dy) = key switch
        {
            Key.Left => (-step, 0),
            Key.Right => (step, 0),
            Key.Up => (0, -step),
            _ => (0, step),
        };
        NativeMethods.GetCursorPos(out var p);
        var m = _source.Monitor.Bounds;
        NativeMethods.SetCursorPos(
            Math.Clamp(p.x + dx, m.left, m.right - 1),
            Math.Clamp(p.y + dy, m.top, m.bottom - 1));
        return true;
    }
}
