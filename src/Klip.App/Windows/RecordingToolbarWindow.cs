using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Klip.Core.Recording;
using Klip.Interop;

namespace Klip.App.Windows;

/// <summary>
/// Estado inicial da sessao para montar a toolbar (spec M2-02). MP4 mostra o
/// conjunto completo; GIF mostra o subconjunto (timer, cursor, borda, parar -
/// RF-T2.07).
/// </summary>
public sealed class RecordingToolbarOptions
{
    /// <summary>Pausar/retomar (so MP4, RF-F3.13).</summary>
    public bool CanPause { get; init; }

    /// <summary>Toggle de mic visivel (MP4 com microfone escolhido, RF-T2.05).</summary>
    public bool ShowMicToggle { get; init; }

    /// <summary>Toggle de som do sistema visivel (MP4 com sistema ligado, RF-T2.05).</summary>
    public bool ShowSystemToggle { get; init; }

    public bool MicMuted { get; init; }
    public bool SystemMuted { get; init; }
    public bool CursorCaptureEnabled { get; init; } = true;
    public bool BorderVisible { get; init; } = true;

    /// <summary>AppSettings.RecordingToolbarPosition ("x,y,w,h" px fisicos) ou null.</summary>
    public string? SavedPosition { get; init; }
}

/// <summary>
/// Control hub flutuante da gravacao (spec M2-02): pill arrastavel
/// (RF-T2.01), com snap magnetico (RF-T2.03), posicao persistida em pixels
/// fisicos (RF-T2.02), toggles ao vivo de mic/sistema/cursor/borda (RF-T2.05)
/// e estados visuais REC/PAUSED (RF-T2.06). Sem foco (NOACTIVATE) e excluida
/// de qualquer captura (RF-T2.04/RF-F2.10) - durante a sessao a janela nunca
/// e fechada, apenas Hide/Show (a afinidade WDA persiste no HWND).
/// </summary>
public sealed class RecordingToolbarWindow : Window
{
    // RF-T2.03: distancia (px fisicos) em que a borda cola no rcWork
    private const int SnapPx = 16;

    // RF-T2.02: intersecao minima da posicao restaurada com o rcWork
    private const int MinVisiblePx = 32;

    // Bug 1: inicio do move-loop nativo (constante local: NativeMethods.* esta
    // congelado para trabalho paralelo) - zera a referencia de direcao do snap
    private const int WM_ENTERSIZEMOVE = 0x0231;

    private static readonly Color RecRed = Color.FromRgb(0xE8, 0x11, 0x23);
    private static readonly Color PausedAmber = Color.FromRgb(0xFF, 0xC8, 0x3D);
    private static readonly Color PillBackground = Color.FromArgb(0xE6, 0x2C, 0x2C, 0x2C);
    private static readonly Color PillBackgroundPaused = Color.FromArgb(0xE6, 0x4A, 0x3A, 0x12); // tint ambar (RF-T2.06)

    private readonly Border _pill;
    private readonly System.Windows.Shapes.Ellipse _recDot;
    private readonly TextBlock _timerLabel;
    private readonly TextBlock? _pauseGlyph;
    private readonly Button? _pauseButton;
    private readonly ToggleGlyph? _micToggle;
    private readonly ToggleGlyph? _systemToggle;
    private readonly ToggleGlyph _cursorToggle;
    private readonly ToggleGlyph _borderToggle;

    private HwndSource? _source;

    // Bug 1a: ultima proposta CRUA do move-loop (sem snap aplicado) - da a
    // direcao real do arraste para o snap so agir quando a borda se aproxima
    private NativeMethods.RECT? _lastMovingRect;

    public event Action? StopRequested;
    public event Action? PauseToggled;

    /// <summary>Novo estado: true = microfone mudo (RF-T2.05).</summary>
    public event Action<bool>? MicMuteToggled;

    /// <summary>Novo estado: true = som do sistema mudo (RF-T2.05).</summary>
    public event Action<bool>? SystemMuteToggled;

    /// <summary>Novo estado: true = cursor visivel na gravacao (RF-T2.05).</summary>
    public event Action<bool>? CursorToggled;

    /// <summary>Novo estado: true = moldura da regiao visivel (RF-T2.05).</summary>
    public event Action<bool>? BorderToggled;

    /// <summary>Fim do arraste (WM_EXITSIZEMOVE): rect em px fisicos (RF-T2.02).</summary>
    public event Action<NativeMethods.RECT>? PositionChanged;

    public RecordingToolbarWindow(RecordingToolbarOptions options)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false; // RF-T2.01: nunca rouba foco do app gravado
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.WidthAndHeight;
        AllowsTransparency = true;
        Background = Brushes.Transparent;

        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        // 1) grip de arraste (a barra inteira tambem arrasta em area vazia)
        panel.Children.Add(new TextBlock
        {
            Text = "\uE76F", // GripperBarHorizontal
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 4, 0),
            ToolTip = Localization.Loc.RecordingDragHint,
            Cursor = Cursors.SizeAll,
        });

        // 2) ponto REC pulsante (RF-T2.06: pulso lento, 10 fps para nao manter
        // o render thread acordado por um glifo de 8 px) + timer tabular
        _recDot = new System.Windows.Shapes.Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = new SolidColorBrush(RecRed),
            VerticalAlignment = VerticalAlignment.Center,
        };
        StartRecPulse();
        panel.Children.Add(_recDot);

        _timerLabel = new TextBlock
        {
            Text = "00:00",
            Foreground = Brushes.White,
            FontSize = 13,
            FontFamily = new FontFamily("Segoe UI"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 6, 0),
            MinWidth = 40,
        };
        // digitos tabulares: o timer nao "danca" a cada segundo
        System.Windows.Documents.Typography.SetNumeralAlignment(_timerLabel, FontNumeralAlignment.Tabular);
        panel.Children.Add(_timerLabel);

        // 3) pausar/retomar (so MP4)
        if (options.CanPause)
        {
            _pauseButton = ToolButton("\uE769", Localization.Loc.RecordingPause); // Pause
            _pauseGlyph = (TextBlock)_pauseButton.Content;
            _pauseButton.Click += (_, _) => PauseToggled?.Invoke();
            panel.Children.Add(_pauseButton);
        }

        panel.Children.Add(Divider());

        // 5-6) mic e som do sistema (so MP4 e apenas se a sessao tem a fonte)
        if (options.ShowMicToggle)
        {
            _micToggle = new ToggleGlyph("\uE720", "\uEC54", // Microphone / MicOff
                onTooltip: Localization.Loc.RecordingMicMute,
                offTooltip: Localization.Loc.RecordingMicUnmute,
                isOn: !options.MicMuted);
            _micToggle.Toggled += on => MicMuteToggled?.Invoke(!on);
            panel.Children.Add(_micToggle.Button);
        }
        if (options.ShowSystemToggle)
        {
            _systemToggle = new ToggleGlyph("\uE767", "\uE74F", // Volume / Mute
                onTooltip: Localization.Loc.RecordingSystemMute,
                offTooltip: Localization.Loc.RecordingSystemUnmute,
                isOn: !options.SystemMuted);
            _systemToggle.Toggled += on => SystemMuteToggled?.Invoke(!on);
            panel.Children.Add(_systemToggle.Button);
        }

        // 7) cursor (MP4 e GIF): sem glifo "off" nativo -> slash desenhado
        _cursorToggle = new ToggleGlyph("\uE7C9", offGlyph: null, // TouchPointer
            onTooltip: Localization.Loc.RecordingCursorHide,
            offTooltip: Localization.Loc.RecordingCursorShow,
            isOn: options.CursorCaptureEnabled);
        _cursorToggle.Toggled += on => CursorToggled?.Invoke(on);
        panel.Children.Add(_cursorToggle.Button);

        // 8) borda da regiao (MP4 e GIF)
        _borderToggle = new ToggleGlyph("\uE7FB", offGlyph: null,
            onTooltip: Localization.Loc.RecordingBorderHide,
            offTooltip: Localization.Loc.RecordingBorderShow,
            isOn: options.BorderVisible);
        _borderToggle.Toggled += on => BorderToggled?.Invoke(on);
        panel.Children.Add(_borderToggle.Button);

        panel.Children.Add(Divider());

        // 10) parar: quadrado vermelho, alvo >= 32x32 (RF-F3.03 / spec M2-02)
        var stop = ToolButton("\uE71A", Localization.Loc.RecordingStop); // Stop
        ((TextBlock)stop.Content).Foreground = new SolidColorBrush(RecRed);
        stop.MinWidth = 32;
        stop.MinHeight = 32;
        stop.Click += (_, _) => StopRequested?.Invoke();
        panel.Children.Add(stop);

        _pill = new Border
        {
            Background = new SolidColorBrush(PillBackground),
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(10, 4, 10, 4),
            Child = panel,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 12,
                ShadowDepth = 2,
                Opacity = 0.5,
            },
        };
        Content = _pill;

        // RF-T2.01: grip/areas vazias arrastam; botoes ja marcam o
        // MouseLeftButtonDown como handled, entao continuam clicaveis
        _pill.MouseLeftButtonDown += OnDragArea;
    }

    public void UpdateElapsed(TimeSpan elapsed) =>
        _timerLabel.Text = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");

    /// <summary>
    /// RF-T2.06: PAUSED = tint ambar no pill, ponto estatico ambar e o botao
    /// vira play; o timer congela sozinho (Elapsed do recorder exclui pausas).
    /// </summary>
    public void SetPaused(bool paused)
    {
        if (_pauseGlyph is not null && _pauseButton is not null)
        {
            _pauseGlyph.Text = paused ? "\uE768" : "\uE769"; // Play / Pause
            _pauseButton.ToolTip = paused ? Localization.Loc.RecordingResume : Localization.Loc.RecordingPause;
        }

        _pill.Background = new SolidColorBrush(paused ? PillBackgroundPaused : PillBackground);
        if (paused)
        {
            _recDot.BeginAnimation(OpacityProperty, null); // congela o pulso
            _recDot.Opacity = 1.0;
            _recDot.Fill = new SolidColorBrush(PausedAmber);
        }
        else
        {
            _recDot.Fill = new SolidColorBrush(RecRed);
            StartRecPulse();
        }
    }

    /// <summary>Reflete um mute vindo de fora (ex.: hotkey futura) sem reemitir evento.</summary>
    public void SetMicMuted(bool muted) => _micToggle?.SetState(!muted);

    /// <summary>
    /// Realce visual rapido (piscada de opacidade): a janela e NOACTIVATE,
    /// entao Activate() seria no-op - usado quando o usuario tenta iniciar
    /// outra gravacao com uma sessao ativa.
    /// </summary>
    public void FlashAttention()
    {
        var flash = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.3, TimeSpan.FromMilliseconds(120))
        {
            AutoReverse = true,
            RepeatBehavior = new System.Windows.Media.Animation.RepeatBehavior(2),
            FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop,
        };
        BeginAnimation(OpacityProperty, flash);
    }

    /// <summary>
    /// Mostra a toolbar: restaura a posicao salva se ela ainda cai num monitor
    /// valido (RF-T2.02); senao posiciona FORA da regiao - acima; sem espaco,
    /// abaixo (RF-T2.09). Tudo em px fisicos.
    /// </summary>
    public void ShowOutside(RecordingRegion region, NativeMethods.RECT monitor, string? savedPosition = null)
    {
        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();

        // sem roubo de foco do app gravado; fora de Alt-Tab e de capturas
        var exStyle = (long)NativeMethods.GetWindowLongPtr(helper.Handle, NativeMethods.GWL_EXSTYLE);
        exStyle |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongPtr(helper.Handle, NativeMethods.GWL_EXSTYLE, (nint)exStyle);
        Klip.Interop.Recording.WindowCaptureExclusion.Exclude(helper.Handle); // RF-T2.04/RF-F2.10

        // RF-T2.02/03: snap e persistencia via mensagens do move-loop nativo
        _source = HwndSource.FromHwnd(helper.Handle);
        _source?.AddHook(WndProc);

        Show();
        UpdateLayout();

        var dpi = NativeMethods.GetDpiForWindow(helper.Handle);
        if (dpi == 0)
            dpi = 96;
        var w = (int)(ActualWidth * dpi / 96.0);
        var h = (int)(ActualHeight * dpi / 96.0);

        // RF-T2.02: posicao salva vale se o rect (no tamanho atual) ainda
        // intersecta >= 32x32 o rcWork do monitor mais proximo
        if (TryRestorePosition(savedPosition, w, h, out var restored))
        {
            NativeMethods.SetWindowPos(helper.Handle, nint.Zero, restored.x, restored.y, 0, 0,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOSIZE);
            return;
        }

        // RF-T2.09: default fora da regiao (acima; flip para baixo)
        var x = Math.Clamp(region.Left + (region.Width - w) / 2,
            monitor.left + 8, Math.Max(monitor.left + 8, monitor.right - w - 8));
        int y = region.Top - h - 12; // acima da regiao (borda fica fora do video)
        if (y < monitor.top + 8)
            y = Math.Min(monitor.bottom - h - 8, region.Top + region.Height + 12); // fallback: abaixo

        NativeMethods.SetWindowPos(helper.Handle, nint.Zero, x, y, 0, 0,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOSIZE);
    }

    /// <summary>Serializa o rect (px fisicos) no formato de AppSettings.RecordingToolbarPosition.</summary>
    public static string FormatPosition(NativeMethods.RECT rect) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{rect.left},{rect.top},{rect.right - rect.left},{rect.bottom - rect.top}");

    protected override void OnClosed(EventArgs e)
    {
        _source?.RemoveHook(WndProc);
        _source = null;
        base.OnClosed(e);
    }

    // ----- drag / snap / persistencia (RF-T2.01..03) -----

    private void OnDragArea(object sender, MouseButtonEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0)
            return;
        // RF-T2.01: delega o arraste ao move-loop nativo sem ativar a janela
        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessage(hwnd, NativeMethods.WM_NCLBUTTONDOWN, NativeMethods.HTCAPTION, 0);
        e.Handled = true;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        // Bug 1b: excecao dentro do hook quebra o move-loop nativo e a toolbar
        // "trava de vez" - qualquer falha aqui deixa o RECT do sistema intacto
        try
        {
            switch (msg)
            {
                case WM_ENTERSIZEMOVE:
                    _lastMovingRect = null; // novo arraste: sem direcao anterior
                    break;
                case NativeMethods.WM_MOVING:
                    // RF-T2.03: ajusta o RECT proposto; Bug 1b: handled com
                    // lresult TRUE apenas quando o RECT foi de fato MODIFICADO
                    if (SnapToWorkArea(lParam))
                    {
                        handled = true;
                        return 1; // TRUE: RECT processado
                    }
                    break;
                case NativeMethods.WM_EXITSIZEMOVE:
                    _lastMovingRect = null;
                    // RF-T2.02: fim do arraste -> persistir a posicao em px fisicos
                    if (NativeMethods.GetWindowRect(hwnd, out var rect))
                        PositionChanged?.Invoke(rect);
                    break;
            }
        }
        catch (Exception)
        {
            // Bug 1b: falha no snap/persistencia nunca derruba o arraste
        }
        return 0;
    }

    /// <summary>
    /// RF-T2.03: snap magnetico ao rcWork. Bug 1a: a referencia e o monitor sob
    /// o CURSOR (GetCursorPos + MonitorFromPoint) - com MonitorFromRect do rect
    /// proposto, ao arrastar rumo a outro monitor o rect ficava
    /// majoritariamente no monitor antigo e o snap re-colava a borda,
    /// impedindo a travessia. O snap so age com a borda dentro do threshold e
    /// se APROXIMANDO do rcWork (acucar, nunca clamp) e, Bug 1c, preserva
    /// EXATAMENTE o tamanho proposto pelo sistema (apenas translada), para nao
    /// brigar com o WM_DPICHANGED na transicao entre monitores de DPI
    /// diferente. Retorna true apenas se modificou o RECT.
    /// </summary>
    private bool SnapToWorkArea(nint rectPtr)
    {
        var proposed = Marshal.PtrToStructure<NativeMethods.RECT>(rectPtr);
        var previous = _lastMovingRect;
        _lastMovingRect = proposed; // guarda a proposta crua (direcao do arraste)

        if (!NativeMethods.GetCursorPos(out var cursor))
            return false;
        var monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == 0)
            return false;
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            return false;

        var work = info.rcWork;
        int dx = 0;
        int dy = 0;
        if (Math.Abs(proposed.left - work.left) < SnapPx &&
            IsApproaching(previous?.left, proposed.left, work.left))
        {
            dx = work.left - proposed.left;
        }
        else if (Math.Abs(work.right - proposed.right) < SnapPx &&
            IsApproaching(previous?.right, proposed.right, work.right))
        {
            dx = work.right - proposed.right;
        }
        if (Math.Abs(proposed.top - work.top) < SnapPx &&
            IsApproaching(previous?.top, proposed.top, work.top))
        {
            dy = work.top - proposed.top;
        }
        else if (Math.Abs(work.bottom - proposed.bottom) < SnapPx &&
            IsApproaching(previous?.bottom, proposed.bottom, work.bottom))
        {
            dy = work.bottom - proposed.bottom;
        }

        if (dx == 0 && dy == 0)
            return false;

        // Bug 1c: so translacao - largura/altura propostas ficam intocadas
        proposed.left += dx;
        proposed.right += dx;
        proposed.top += dy;
        proposed.bottom += dy;
        Marshal.StructureToPtr(proposed, rectPtr, false);
        return true;
    }

    /// <summary>
    /// Bug 1a: a borda so cola se esta parada ou se aproximando da borda do
    /// rcWork - afastando (usuario puxando para fora / cruzando monitores) o
    /// snap solta na hora em vez de re-colar e prender a janela.
    /// </summary>
    private static bool IsApproaching(int? previousEdge, int currentEdge, int workEdge) =>
        previousEdge is not { } prev || Math.Abs(currentEdge - workEdge) <= Math.Abs(prev - workEdge);

    /// <summary>
    /// RF-T2.02: "x,y,w,h" invariante -> rect no tamanho ATUAL da janela,
    /// validado com MonitorFromRect + intersecao minima 32x32 com o rcWork
    /// (monitor removido/fora de tela -> false, cai no default).
    /// </summary>
    private static bool TryRestorePosition(string? saved, int width, int height, out (int x, int y) position)
    {
        position = default;
        if (string.IsNullOrWhiteSpace(saved))
            return false;
        var parts = saved.Split(',');
        if (parts.Length != 4 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
        {
            return false;
        }

        var rect = new NativeMethods.RECT { left = x, top = y, right = x + width, bottom = y + height };
        var monitor = NativeMethods.MonitorFromRect(ref rect, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == 0)
            return false;
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            return false;

        var work = info.rcWork;
        var visibleW = Math.Min(rect.right, work.right) - Math.Max(rect.left, work.left);
        var visibleH = Math.Min(rect.bottom, work.bottom) - Math.Max(rect.top, work.top);
        if (visibleW < MinVisiblePx || visibleH < MinVisiblePx)
            return false; // CA-T2.5: nunca restar fora de tela

        position = (x, y);
        return true;
    }

    // ----- construcao visual -----

    private void StartRecPulse()
    {
        var pulse = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.3, TimeSpan.FromSeconds(0.8))
        {
            AutoReverse = true,
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
        };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(pulse, 10);
        _recDot.BeginAnimation(OpacityProperty, pulse);
    }

    private static Border Divider() => new()
    {
        Width = 1,
        Margin = new Thickness(6, 4, 6, 4),
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
                FontSize = 15,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            ToolTip = tooltip,
            Padding = new Thickness(9, 7, 9, 7),
            Margin = new Thickness(2, 0, 2, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Arrow,
            Focusable = false,
        };
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
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        button.Template = template;
        return button;
    }

    /// <summary>
    /// Botao de toggle com estado "off" sempre marcado por glifo com slash
    /// (RF-T2.06: nunca so cor): glifo nativo de "off" quando existe
    /// (MicOff/Mute) ou slash desenhado por cima (cursor/borda).
    /// </summary>
    private sealed class ToggleGlyph
    {
        private readonly string _onGlyph;
        private readonly string? _offGlyph;
        private readonly string _onTooltip;
        private readonly string _offTooltip;
        private readonly TextBlock _glyph;
        private readonly System.Windows.Shapes.Line _slash;
        private bool _isOn;

        public Button Button { get; }

        /// <summary>Novo estado apos o clique (true = ligado).</summary>
        public event Action<bool>? Toggled;

        public ToggleGlyph(string onGlyph, string? offGlyph, string onTooltip, string offTooltip, bool isOn)
        {
            _onGlyph = onGlyph;
            _offGlyph = offGlyph;
            _onTooltip = onTooltip;
            _offTooltip = offTooltip;
            _isOn = isOn;

            Button = ToolButton(onGlyph, onTooltip);
            _glyph = (TextBlock)Button.Content;

            // slash desenhado (para glifos sem variante "off" nativa)
            _slash = new System.Windows.Shapes.Line
            {
                X1 = 1,
                Y1 = 16,
                X2 = 16,
                Y2 = 1,
                Stroke = Brushes.White,
                StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
            };
            var grid = new Grid
            {
                Width = 17,
                Height = 17,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _glyph.HorizontalAlignment = HorizontalAlignment.Center;
            _glyph.VerticalAlignment = VerticalAlignment.Center;
            // desconecta o glifo do Button antes de re-parentar no Grid (senao:
            // "Specified element is already the logical child of another element")
            Button.Content = null;
            grid.Children.Add(_glyph);
            grid.Children.Add(_slash);
            Button.Content = grid;

            Button.Click += (_, _) =>
            {
                SetState(!_isOn);
                Toggled?.Invoke(_isOn);
            };
            Apply();
        }

        /// <summary>Atualiza o visual sem disparar Toggled.</summary>
        public void SetState(bool isOn)
        {
            if (_isOn == isOn)
                return;
            _isOn = isOn;
            Apply();
        }

        private void Apply()
        {
            if (_offGlyph is not null)
            {
                // variante nativa ja carrega o slash no proprio glifo
                _glyph.Text = _isOn ? _onGlyph : _offGlyph;
                _slash.Visibility = Visibility.Collapsed;
                _glyph.Opacity = 1.0;
            }
            else
            {
                _glyph.Text = _onGlyph;
                _slash.Visibility = _isOn ? Visibility.Collapsed : Visibility.Visible;
                _glyph.Opacity = _isOn ? 1.0 : 0.55;
            }
            Button.ToolTip = _isOn ? _onTooltip : _offTooltip;
        }
    }
}
