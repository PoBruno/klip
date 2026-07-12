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

namespace Klip.App.Windows;

/// <summary>Capture modes.</summary>
public enum CaptureMode
{
    Rectangle,
    Window,
    Fullscreen,
    Scrolling,
    Freeform,

    // RF-F4.01 / RF-F3.01: modos de gravacao (selecao retangular apenas)
    Gif,
    Mp4,
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
    private readonly Action<FrozenMonitor, Int32Rect, CaptureMode, Point[]?, bool> _onSelected;
    private readonly Action _onCancel;

    // F1: modificador que abre a captura direto no editor
    private readonly CaptureEditorModifier _editorModifier;
    private readonly bool _alwaysOpenEditor;
    private bool _modifierHeldSinceOpen; // RF-F1.07: debounce do modificador vindo do hotkey

    private readonly Grid _root = new();
    private readonly System.Windows.Shapes.Path _scrim;
    private readonly Rectangle _selectionBorder;
    private readonly TextBlock _sizeLabel;
    private readonly TextBlock _editorHintLabel;
    private readonly Border _sizeLabelHost;
    private readonly List<Button> _modeButtons = [];
    private Border? _toolbar;

    // UX submenu de gravacao: painel inline abaixo do botao GIF/MP4 da toolbar
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

    public CaptureOverlayWindow(
        FrozenMonitor source,
        bool showToolbar,
        IReadOnlyList<NativeMethods.TopLevelWindow> topWindows,
        CaptureEditorModifier editorModifier,
        bool alwaysOpenEditor,
        SettingsService settings,
        Func<IAudioDeviceEnumerator> audioEnumeratorFactory,
        Action<FrozenMonitor, Int32Rect, CaptureMode, Point[]?, bool> onSelected,
        Action onCancel)
    {
        _source = source;
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
        // RF-F1.04: hint discreto do modificador junto ao label de dimensões
        _editorHintLabel = new TextBlock
        {
            Foreground = HintIdleBrush,
            FontSize = 12,
            FontFamily = new FontFamily("Segoe UI Variable, Segoe UI"),
            Margin = new Thickness(10, 0, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        var labelPanel = new StackPanel { Orientation = Orientation.Horizontal };
        labelPanel.Children.Add(_sizeLabel);
        labelPanel.Children.Add(_editorHintLabel);
        _sizeLabelHost = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x20, 0x20, 0x20)),
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

        // RF-F1.07: se o modificador configurado já estava pressionado quando o
        // overlay abriu (ex.: hotkey Ctrl+Shift+S contém Ctrl), ignora até que
        // seja solto e pressionado de novo. Leitura única via GetAsyncKeyState
        // porque o WM_KEYDOWN original foi para outra janela (D-F1.1).
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

        // UX submenu de gravacao: o modo e compartilhado entre sessoes; se o
        // overlay ja abre em GIF/MP4, o submenu aparece direto abaixo do botao
        // (apos o layout da toolbar, senao TranslatePoint devolve lixo)
        if (_toolbar is not null && _mode is CaptureMode.Gif or CaptureMode.Mp4)
        {
            Dispatcher.BeginInvoke(
                () => ShowRecordingOptions(_mode),
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

        AddModeButton(panel, "\uF407", Localization.Loc.ModeRectangle, CaptureMode.Rectangle);
        AddModeButton(panel, "\uE7F4", Localization.Loc.ModeWindow, CaptureMode.Window);
        AddModeButton(panel, "\uE740", Localization.Loc.ModeFullscreen, CaptureMode.Fullscreen);
        AddModeButton(panel, "\uEF20", Localization.Loc.ModeFreeform, CaptureMode.Freeform);

        panel.Children.Add(Divider());

        // scrolling capture
        AddModeButton(panel, "\uEC8F", Localization.Loc.ModeScrolling, CaptureMode.Scrolling);

        panel.Children.Add(Divider());

        // RF-F4.01/RF-F3.01: gravacao GIF (\uF4A9 = GIF, Segoe Fluent Icons)
        // e MP4 (\uE714 = Video); selecao retangular igual ao modo retangulo
        AddModeButton(panel, "\uF4A9", Localization.Loc.ModeGif, CaptureMode.Gif);
        AddModeButton(panel, "\uE714", Localization.Loc.ModeMp4, CaptureMode.Mp4);

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
            // UX submenu de gravacao: clicar de novo no modo ja ativo alterna
            // o submenu; trocar de modo fecha o do outro e abre o deste
            var repeat = _mode == mode;
            _mode = mode;
            ResetSelection();
            ResetLasso();
            RefreshToolbarState();

            if (mode is CaptureMode.Gif or CaptureMode.Mp4)
            {
                if (repeat && _recordingPanel is not null)
                    HideRecordingOptions();
                else
                    ShowRecordingOptions(mode);
            }
            else
            {
                HideRecordingOptions(); // modos nao-gravacao nao tem submenu
            }
        };
        _modeButtons.Add(button);
        panel.Children.Add(button);
    }

    // ----- UX submenu de gravacao (painel inline GIF/MP4) -----

    /// <summary>
    /// Abre o painel de opcoes ancorado abaixo do botao GIF/MP4. As mudancas
    /// persistem na hora (SettingsService) e valem para a proxima gravacao;
    /// a selecao de area segue direto para gravar, sem painel pre-gravacao.
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
        // a lista de microfones carrega async e muda a altura/largura:
        // reancora a cada mudanca de tamanho
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

    /// <summary>Centraliza o painel sob o botao do modo, logo abaixo da pill, clampado ao monitor.</summary>
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

    /// <summary>Modos que selecionam arrastando um retangulo (inclui gravacao).</summary>
    private bool IsDragRectMode =>
        _mode is CaptureMode.Rectangle or CaptureMode.Scrolling or CaptureMode.Gif or CaptureMode.Mp4;

    private void OnOverlayMouseDown(object sender, MouseButtonEventArgs e)
    {
        // UX submenu de gravacao: interacoes na toolbar OU no submenu inline
        // nunca iniciam a selecao de area (clicar fora deles segue normal)
        if (e.ChangedButton != MouseButton.Left || (_toolbar?.IsMouseOver ?? false)
            || (_recordingPanel?.IsMouseOver ?? false))
            return;

        switch (_mode)
        {
            case CaptureMode.Rectangle or CaptureMode.Scrolling or CaptureMode.Gif or CaptureMode.Mp4:
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

        if (IsDragRectMode && _dragging)
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
        if (IsDragRectMode && _dragging && e.ChangedButton == MouseButton.Left)
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

    // ----- Editor modifier (spec F1) -----

    private void OnOverlayKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _onCancel();
            return;
        }

        if (!IsConfiguredModifierKey(e))
            return;

        // RF-F1.04 / CA-F1.6: estado do hint acompanha a tecla em tempo real
        UpdateEditorHint();
        if (e.Key == Key.System)
            e.Handled = true; // Alt configurado não deve acionar semântica de menu
    }

    private void OnOverlayKeyUp(object sender, KeyEventArgs e)
    {
        if (!IsConfiguredModifierKey(e))
            return;

        // RF-F1.07: soltar a tecla encerra o debounce herdado do hotkey de captura
        _modifierHeldSinceOpen = false;
        UpdateEditorHint();
        if (e.Key == Key.System)
            e.Handled = true;
    }

    /// <summary>True se a tecla do evento é o modificador configurado (Alt chega como Key.System).</summary>
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

    /// <summary>Estado físico via Keyboard.Modifiers; o overlay tem foco (D-F1.1).</summary>
    private bool IsConfiguredModifierDown() => _editorModifier switch
    {
        CaptureEditorModifier.Control => Keyboard.Modifiers.HasFlag(ModifierKeys.Control),
        CaptureEditorModifier.Shift => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift),
        CaptureEditorModifier.Alt => Keyboard.Modifiers.HasFlag(ModifierKeys.Alt),
        _ => false,
    };

    /// <summary>Pedido de abrir no editor no instante da seleção (RF-F1.01/RF-F1.03).</summary>
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

    // cinza discreto no estado ocioso; accent do sistema quando ativo
    private static Brush HintIdleBrush { get; } = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));

    private static Brush HintActiveBrush =>
        Application.Current?.TryFindResource("SystemAccentColorSecondaryBrush") as Brush
            ?? new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF)); // fallback: accent claro padrão

    /// <summary>RF-F1.04/RF-F1.05: hint some com modificador desativado ou toggle "sempre editor".</summary>
    private void UpdateEditorHint()
    {
        if (_editorModifier == CaptureEditorModifier.None || _alwaysOpenEditor
            || _mode == CaptureMode.Scrolling // RF-F1.06: rolagem já abre no editor
            || _mode is CaptureMode.Gif or CaptureMode.Mp4) // gravacao nao abre no editor de imagem
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
        // RF-F1.03: estado do modificador lido no instante em que a seleção termina
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
        // gravacao (RF-F3.01/RF-F4.01): sem delay do overlay (a contagem de 3 s
        // vem do fluxo de gravacao) e sem modificador de editor
        if (_mode is CaptureMode.Gif or CaptureMode.Mp4)
        {
            _onSelected(_source, rect, _mode, null, false);
            return;
        }

        // RF-F1.03/RF-F1.06: modificador lido no fim da seleção; nunca vale para rolagem
        var openEditor = _mode != CaptureMode.Scrolling && IsEditorModifierActive();

        // scrolling ignores the delay, the user sets the pace there
        if (_delay == CaptureDelay.None || _mode == CaptureMode.Scrolling)
        {
            _onSelected(_source, rect, _mode, null, openEditor);
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
                _onSelected(_source, rect, _mode, null, openEditor);
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
        UpdateEditorHint(); // RF-F1.04: hint acompanha o label de dimensões
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
