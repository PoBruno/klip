using System.Windows;
using System.Windows.Controls;
using Klip.App.Diagnostics;
using Klip.App.Localization;
using Klip.App.Services;
using Klip.App.ViewModels;
using Klip.App.Windows;
using Klip.Core.Clipboard;
using Klip.Core.Common;
using Klip.Core.Hotkeys;
using Klip.Core.Settings;
using Klip.Core.Storage;
using Klip.Interop;
using Klip.Interop.Input;
using Klip.Interop.SystemIntegration;
using H.NotifyIcon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Klip.App;

/// <summary>
/// App bootstrap: single instance, DI, database, tray, hotkeys and the clipboard engine.
/// </summary>
public partial class App : Application
{
    private const string MutexName = "Klip.SingleInstance";

    private Mutex? _mutex;
    private IHost? _host;
    private TaskbarIcon? _tray;
    private MainWindow? _mainWindow;
    private HistoryFlyoutWindow? _flyout;
    private PasteService? _pasteService;
    private ClipboardMonitorService? _clipboardMonitor;
    private CaptureController? _captureController;
    private RecordingController? _recordingController;

    // RF-P1.06 / ADR-P.07: rede de seguranca contra interferencia com jogos.
    private SystemActivityMonitor? _activityMonitor;

    // RF-P0.02: transforma travamento da UI thread em evidencia rastreavel.
    private UiThreadWatchdog? _uiWatchdog;

    // RF-P2.09: retencao fora do DispatcherTimer (nao acordar a UI thread num app
    // que fica horas ocioso) e pulada enquanto houver jogo em tela cheia.
    private System.Threading.Timer? _retentionTimer;

    // pasta do ultimo toast de gravacao: clique no balao abre a pasta (RF-F3.16)
    private string? _recordingToastFolder;

    // arquivo do ultimo toast de gravacao MP4: clique abre o editor de midia (RF-F5.16)
    private string? _recordingToastFile;

    // editor de midia (spec F5): uma janela por arquivo, reativada se ja aberta
    private readonly Dictionary<string, MediaEditorWindow> _mediaEditors = new(StringComparer.OrdinalIgnoreCase);

    public bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // elevated helper mode: run one HKLM op and quit.
        // handled before the mutex so it can live next to the main instance
        if (e.Args.Length >= 2 && e.Args[0] == "--registry")
        {
            HandleElevatedRegistryOperation(e.Args[1]);
            return;
        }

        // silent registry rollback called by the uninstaller.
        // puts DisabledHotkeys and PrintScreen back to the backup, no UI
        if (e.Args.Contains("--revert-registry"))
        {
            RevertRegistrySilently();
            return;
        }

        // single instance
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // dump unhandled exceptions to the log before the app goes down
        DispatcherUnhandledException += (_, args) =>
            StartupLog.WriteException("DispatcherUnhandledException", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            StartupLog.WriteException("UnhandledException", (Exception)args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) =>
            StartupLog.WriteException("UnobservedTaskException", args.Exception);

        AppPaths.EnsureCreated();
        // RF-P3.05: --verbose liga o log de alta frequencia (ingestao, estado dos hooks).
        // Desligado por padrao: em uso normal o log so registra transicoes e erros.
        StartupLog.VerboseEnabled = e.Args.Contains("--verbose");
        StartupLog.Write("OnStartup: iniciando");

        // ADR-P.08: o app nasce ocioso na bandeja. EcoQoS pede frequencia eficiente e
        // agendamento em efficient cores INCLUSIVE na tomada (o nivel Low automatico por
        // visibilidade so vale na bateria). Prioridade de memoria baixa faz o gerenciador
        // aparar as NOSSAS paginas antes das dos outros sob pressao - alternativa correta
        // e nao destrutiva ao EmptyWorkingSet.
        PowerEfficiency.SetProcessMemoryPriorityLow();
        PowerEfficiency.EnterProcessEcoQos();
        // Defesa contra dependencia que chame timeBeginPeriod: no Win11 o efeito e por
        // processo e some quando estamos ocultos, mas o custo de troca de contexto fica.
        PowerEfficiency.IgnoreTimerResolutionRequests(true);

        // ADR-P.01: sobe a thread dedicada dos hooks LL ja no startup. Nenhum hook e
        // instalado aqui - so o pump fica pronto, para o primeiro Acquire ser instantaneo.
        LowLevelHookHost.Shared.Start();

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<SettingsService>();
        builder.Services.AddSingleton(_ =>
        {
            var db = new Database(AppPaths.DatabaseFile);
            db.Initialize();
            return db;
        });
        builder.Services.AddSingleton<ClipboardItemRepository>();
        builder.Services.AddSingleton<MediaStore>();
        builder.Services.AddSingleton<BackupService>();
        builder.Services.AddSingleton<Klip.Core.Storage.IThumbnailGenerator, WpfThumbnailGenerator>();
        builder.Services.AddSingleton<OcrService>();
        builder.Services.AddSingleton<Klip.Core.Storage.IImageTextExtractor>(sp => sp.GetRequiredService<OcrService>());
        builder.Services.AddSingleton<ClipboardIngestService>(sp => new ClipboardIngestService(
            sp.GetRequiredService<ClipboardItemRepository>(),
            sp.GetRequiredService<MediaStore>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<Klip.Core.Storage.IThumbnailGenerator>(),
            sp.GetRequiredService<Klip.Core.Storage.IImageTextExtractor>()));
        // ADR-P.05: thread STA dedicada dona de todo acesso ao clipboard. Precisa vir
        // antes do WriteGuard/Monitor, que a recebem por injecao.
        builder.Services.AddSingleton<ClipboardThread>();
        builder.Services.AddSingleton<ClipboardWriteGuard>();
        builder.Services.AddSingleton<ClipboardMonitorService>();
        builder.Services.AddSingleton<PasteService>();
        builder.Services.AddSingleton<PasteQueueService>();
        builder.Services.AddSingleton<ScreenCaptureService>();
        builder.Services.AddSingleton<PanoramicCaptureService>();
        // gravacao (specs F3/F4): concretas do Interop so via factory (ponto unico)
        builder.Services.AddSingleton(sp => new RecordingController(
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<ClipboardIngestService>(),
            sp.GetRequiredService<HotkeyService>(),
            RecordingInteropFactory.CreateAudioDeviceEnumerator,
            RecordingInteropFactory.CreateMp4Recorder));
        builder.Services.AddSingleton<CaptureController>();
        builder.Services.AddSingleton<AutostartService>();
        builder.Services.AddSingleton<HotkeyService>();
        builder.Services.AddSingleton<SystemHotkeyService>();
        builder.Services.AddSingleton<HistoryFlyoutViewModel>();
        builder.Services.AddSingleton<HistoryFlyoutWindow>();
        // RF-P1.06 / RF-P0.02: rede de seguranca e instrumentacao.
        builder.Services.AddSingleton<SystemActivityMonitor>();
        builder.Services.AddSingleton(_ => new UiThreadWatchdog(Dispatcher));
        builder.Services.AddTransient<EditorWindow>(); // one window per edit
        builder.Services.AddSingleton<MainWindow>();
        _host = builder.Build();
        _host.Start();
        StartupLog.Write("OnStartup: host iniciado");

        // RF-P0.02: mede a latencia da UI thread. Um bloqueio longo aqui e o que
        // congela o input do sistema quando ha hook LL instalado.
        _uiWatchdog = _host.Services.GetRequiredService<UiThreadWatchdog>();
        _uiWatchdog.Start();

        // RF-P1.06: polling de 3 s. O Windows NAO notifica entrada/saida de app em
        // tela cheia (doc de SHQueryUserNotificationState), entao polling e obrigatorio.
        _activityMonitor = _host.Services.GetRequiredService<SystemActivityMonitor>();
        _activityMonitor.StateChanged += state =>
            Dispatcher.BeginInvoke(new Action(() => OnActivityStateChanged(state)));
        _activityMonitor.Start();

        var settings = _host.Services.GetRequiredService<SettingsService>();

        // resolve language before any window shows up (default: Windows)
        Localization.Loc.Initialize(settings.Current.Language);
        ThemeManager.Apply(settings.Current.Theme);

        // autostart following the saved setting
        try
        {
            _host.Services.GetRequiredService<AutostartService>().SetEnabled(settings.Current.StartWithWindows);
        }
        catch (Exception ex)
        {
            // no registry access is not fatal here
            StartupLog.WriteException("Autostart", ex);
        }

        // ADR-S.01: a tela de Configuracoes NAO e mais construida no startup. A antiga
        // MainWindow montava ~247 elementos e 31 cards mesmo com o app iniciando
        // minimizado - o UiThreadWatchdog media 793 ms de UI thread travada por causa
        // disso. Agora a janela nasce na primeira abertura (ver ShowMainWindow).
        // O banco continua sendo inicializado aqui perto, pelo ClipboardMonitorService.

        // clipboard engine + flyout
        _pasteService = _host.Services.GetRequiredService<PasteService>();
        // warn when paste fails (item stays copied at least)
        _pasteService.PasteFailed += () =>
            _tray?.ShowNotification("Klip", Loc.PasteFailedToast);
        _flyout = _host.Services.GetRequiredService<HistoryFlyoutWindow>();
        // ADR-P.08: o flyout fecha por varios caminhos (Esc, clique fora, colar,
        // hotkey). Ao inves de espalhar EnterProcessEcoQos por todos eles, reage a
        // visibilidade: sem janela visivel, o app volta ao estado de repouso.
        _flyout.IsVisibleChanged += (_, _) => ApplyIdleQosIfNoWindowVisible();
        _clipboardMonitor = _host.Services.GetRequiredService<ClipboardMonitorService>();
        StartupLog.Write("OnStartup: clipboard monitor ativo");

        // indicator for the sequential paste queue
        var pasteQueue = _host.Services.GetRequiredService<PasteQueueService>();
        pasteQueue.QueueProgress += (current, total) =>
            Dispatcher.BeginInvoke(() =>
            {
                if (_tray is not null)
                    _tray.ToolTipText = $"Klip - colando {current} de {total}";
            });
        pasteQueue.QueueFinished += () =>
            Dispatcher.BeginInvoke(() =>
            {
                if (_tray is not null)
                    _tray.ToolTipText = "Klip";
            });

        // capture overlay
        _captureController = _host.Services.GetRequiredService<CaptureController>();
        _captureController.CaptureCompleted += message =>
        {
            _recordingToastFolder = null; // o clique volta a abrir o editor
            _recordingToastFile = null;
            _tray?.ShowNotification("Klip", message);
        };
        _captureController.EditRequested += OpenEditor; // scroll capture opens in the editor

        // gravacao GIF/MP4 (specs F3/F4): toasts + estado no tray
        _recordingController = _host.Services.GetRequiredService<RecordingController>();
        _recordingController.RecordingToast += (message, folder, editorFile) =>
        {
            // estado do toast anterior nao vaza para o clique deste (limpa e
            // reatribui a cada toast de gravacao)
            _recordingToastFolder = folder;
            _recordingToastFile = editorFile; // MP4: clique abre o editor de midia
            _tray?.ShowNotification("Klip", message);
        };
        // RF-F3.04: tooltip do tray com o timer enquanto grava (tick de 1 s)
        _recordingController.StateChanged += () =>
        {
            if (_tray is null || _recordingController is null)
                return;
            _tray.ToolTipText = _recordingController.IsActive
                ? string.Format(Loc.TrayRecordingTooltip, _recordingController.Elapsed.ToString(@"mm\:ss"))
                : "Klip";
        };

        // editor: opened from the flyout and from the toast
        _host.Services.GetRequiredService<HistoryFlyoutViewModel>().OpenInEditorRequested += OpenEditor;

        // editor de midia (spec F5): registra o gateway para historico/toasts
        MediaEditorGateway.Opener = path => Dispatcher.BeginInvoke(() => OpenMediaEditor(path));

        RegisterHotkeys(settings);
        CreateTrayIcon();
        StartupLog.Write("OnStartup: tray e hotkeys prontos");
        // RF-P0.01 / CA-P1.3: em idle nenhum hook LL pode estar instalado
        StartupLog.Write($"OnStartup: {HookHealth.FormatSummary()}");

        RunRetentionInBackground(settings);

        // warm up the flyout at idle so the first Win+V opens instantly (pre-pays
        // the first layout and the Mica composition without the user seeing it)
        Dispatcher.BeginInvoke(new Action(() => _flyout?.WarmUp()),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        // onboarding na primeira execucao (passa por cima do start minimizado)
        if (!settings.Current.OnboardingCompleted)
        {
            var onboarding = new OnboardingWindow(settings, TakeoverWinVAsync);
            onboarding.Show();
            onboarding.Activate();
        }
        else
        {
            var startMinimized = settings.Current.StartMinimized || e.Args.Contains("--minimized");
            if (!startMinimized)
                ShowMainWindow();
        }
    }

    private void HandleElevatedRegistryOperation(string operation)
    {
        // RF-P3.06: este processo faz uma operacao e morre. O Update do SettingsService
        // agora tem debounce de 500 ms, entao a instancia precisa ser descartada
        // explicitamente (Dispose faz Flush) antes do Shutdown, senao a escrita se perde.
        using var settings = new SettingsService();
        try
        {
            AppPaths.EnsureCreated();
            var service = new SystemHotkeyService(settings);
            switch (operation)
            {
                case "clipboard-feature-off":
                    service.SetHklmClipboardFeature(off: true);
                    break;
                case "clipboard-feature-restore":
                    service.SetHklmClipboardFeature(off: false);
                    break;
                default:
                    Shutdown(2);
                    return;
            }
            settings.Flush();
            Shutdown(0);
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("ElevatedRegistry", ex);
            Shutdown(1);
        }
    }

    /// <summary>
    /// Silent rollback called by the uninstaller. Puts the HKCU keys
    /// (DisabledHotkeys, PrintScreen) back to the backup. No UI and no Explorer
    /// restart, the system picks it up again on the next logon.
    /// </summary>
    private void RevertRegistrySilently()
    {
        // RF-P3.06: mesmo motivo do metodo acima - o processo morre logo em seguida.
        using var settings = new SettingsService();
        try
        {
            AppPaths.EnsureCreated();
            var service = new SystemHotkeyService(settings);
            service.RestoreDisabledHotkeys();
            service.SetPrintScreenFreed(false);
            settings.Flush();
            StartupLog.Write("Uninstall: registro HKCU restaurado");
            StartupLog.Flush();
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("RevertRegistrySilently", ex);
        }
        Shutdown(0);
    }

    private void RegisterHotkeys(SettingsService settings)
    {
        ApplyHotkeys(settings);
    }

    private int _historyHotkeyId;
    private int _captureHotkeyId;

    /// <summary>(Re)applies the global hotkeys from settings.</summary>
    public bool ApplyHotkeys(SettingsService settings)
    {
        var hotkeys = _host!.Services.GetRequiredService<HotkeyService>();

        if (_historyHotkeyId != 0)
        {
            hotkeys.Unregister(_historyHotkeyId);
            _historyHotkeyId = 0;
        }
        if (_captureHotkeyId != 0)
        {
            hotkeys.Unregister(_captureHotkeyId);
            _captureHotkeyId = 0;
        }

        var allOk = true;

        // hotkey toggles the history flyout
        if (HotkeyGesture.TryParse(settings.Current.HotkeyHistory, out var historyGesture))
        {
            if (!hotkeys.TryRegister(historyGesture, ToggleHistoryFlyout, out _historyHotkeyId))
            {
                NotifyHotkeyConflict(historyGesture);
                allOk = false;
            }
        }

        if (HotkeyGesture.TryParse(settings.Current.HotkeyCapture, out var captureGesture))
        {
            if (!hotkeys.TryRegister(captureGesture, StartCapture, out _captureHotkeyId))
            {
                NotifyHotkeyConflict(captureGesture);
                allOk = false;
            }
        }

        return allOk;
    }

    /// <summary>
    /// "Take over Win+V" flow: merge into the registry, restart Explorer,
    /// then retry the registration. Returns a status message for the UI.
    /// </summary>
    public async Task<string> TakeoverWinVAsync()
    {
        var settings = _host!.Services.GetRequiredService<SettingsService>();
        var registry = _host.Services.GetRequiredService<SystemHotkeyService>();

        registry.AddDisabledHotkeyLetters("V");
        await SystemHotkeyService.RestartExplorerAsync();

        if (await TryRegisterWithRetryAsync("Win+V", ToggleHistoryFlyout, isHistory: true))
        {
            settings.UpdateAndFlush(s => s.HotkeyHistory = "Win+V");
            return "ok";
        }

        // 24H2 fallback via HKLM (needs elevation)
        return "precisa-hklm";
    }

    /// <summary>Elevated fallback: kills the native history feature.</summary>
    public async Task<string> TakeoverWinVWithHklmFallbackAsync()
    {
        var settings = _host!.Services.GetRequiredService<SettingsService>();

        if (!SystemHotkeyService.RunElevated("clipboard-feature-off"))
            return "uac-cancelado";
        settings.UpdateAndFlush(s => s.RegistryHklmClipboardOffApplied = true);

        await SystemHotkeyService.RestartExplorerAsync();
        if (await TryRegisterWithRetryAsync("Win+V", ToggleHistoryFlyout, isHistory: true))
        {
            settings.UpdateAndFlush(s => s.HotkeyHistory = "Win+V");
            return "ok";
        }
        return "falhou";
    }

    /// <summary>The recommended route: Print Screen opens capture.</summary>
    public string TakeoverPrintScreen()
    {
        var settings = _host!.Services.GetRequiredService<SettingsService>();
        var registry = _host.Services.GetRequiredService<SystemHotkeyService>();

        registry.SetPrintScreenFreed(true);
        settings.UpdateAndFlush(s => s.HotkeyCapture = "PrintScreen");
        return ApplyHotkeys(settings) ? "ok" : "conflito";
    }

    /// <summary>Win+Shift+S route (desativa o Win+S junto, avisar antes).</summary>
    public async Task<string> TakeoverWinShiftSAsync()
    {
        var settings = _host!.Services.GetRequiredService<SettingsService>();
        var registry = _host.Services.GetRequiredService<SystemHotkeyService>();

        registry.AddDisabledHotkeyLetters("S");
        await SystemHotkeyService.RestartExplorerAsync();

        if (await TryRegisterWithRetryAsync("Win+Shift+S", StartCapture, isHistory: false))
        {
            settings.UpdateAndFlush(s => s.HotkeyCapture = "Win+Shift+S");
            return "ok";
        }
        return "falhou";
    }

    /// <summary>Full rollback of every takeover.</summary>
    public async Task RevertTakeoversAsync()
    {
        var settings = _host!.Services.GetRequiredService<SettingsService>();
        var registry = _host.Services.GetRequiredService<SystemHotkeyService>();

        registry.RestoreDisabledHotkeys();
        registry.SetPrintScreenFreed(false);
        if (settings.Current.RegistryHklmClipboardOffApplied &&
            SystemHotkeyService.RunElevated("clipboard-feature-restore"))
        {
            settings.UpdateAndFlush(s => s.RegistryHklmClipboardOffApplied = false);
        }

        settings.UpdateAndFlush(s =>
        {
            s.HotkeyHistory = "Ctrl+Shift+V";
            s.HotkeyCapture = "Ctrl+Shift+S";
        });

        await SystemHotkeyService.RestartExplorerAsync();
        ApplyHotkeys(settings);
    }

    /// <summary>Retry loop after the Explorer restart (it may grab the hotkey again while the shell boots).</summary>
    private async Task<bool> TryRegisterWithRetryAsync(string gestureText, Action callback, bool isHistory)
    {
        var hotkeys = _host!.Services.GetRequiredService<HotkeyService>();
        if (!HotkeyGesture.TryParse(gestureText, out var gesture))
            return false;

        // drop the current registration on the same slot
        if (isHistory && _historyHotkeyId != 0)
        {
            hotkeys.Unregister(_historyHotkeyId);
            _historyHotkeyId = 0;
        }
        if (!isHistory && _captureHotkeyId != 0)
        {
            hotkeys.Unregister(_captureHotkeyId);
            _captureHotkeyId = 0;
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (hotkeys.TryRegister(gesture, callback, out var id))
            {
                if (isHistory)
                    _historyHotkeyId = id;
                else
                    _captureHotkeyId = id;
                return true;
            }
            await Task.Delay(1000);
        }
        return false;
    }

    /// <summary>Opens the capture overlay.</summary>
    private void StartCapture()
    {
        // Aqui NAO ha bloqueio por jogo em tela cheia de proposito. Capturar um jogo ou
        // um video em tela cheia e caso de uso primario de uma ferramenta de captura;
        // recusar a hotkey seria uma regressao muito pior do que o custo de composicao
        // de um overlay que o usuario acabou de pedir. O que a auto-suspensao corta e o
        // custo em REPOUSO (RF-P1.06), nao acao explicita do usuario.
        if (_activityMonitor?.State == SystemActivityState.Suspended)
            StartupLog.WriteVerbose("Captura sobre app em tela cheia (a pedido do usuario)");

        _flyout?.HideFlyout(); // overlay cant capture the flyout while its open
        PowerEfficiency.EnterProcessHighQos(); // ADR-P.08: UI a frente, sai do Eco
        _captureController?.StartCapture();
    }

    /// <summary>
    /// RF-P1.06 / ADR-P.07: reage a entrada e saida de jogo em tela cheia. O
    /// SystemActivityMonitor ja removeu os hooks LL ociosos e aplicou EcoQoS na thread
    /// dele; aqui cuidamos apenas do que precisa da UI thread.
    ///
    /// De proposito NAO fechamos o flyout aberto: com um video ou um jogo em tela cheia
    /// em primeiro plano, "Win+V para de funcionar" seria uma regressao pior do que o
    /// custo de composicao de um painel pequeno que o usuario acabou de pedir. O que
    /// bloqueamos e a CAPTURA (StartCapture), que abre uma janela Topmost do tamanho do
    /// monitor e usa GDI de tela cheia - essa sim derruba o DWM de Independent Flip.
    /// </summary>
    private void OnActivityStateChanged(SystemActivityState state)
    {
        if (IsExiting)
            return;

        // reservado para reagir na UI thread; hoje a politica inteira e do monitor
        _ = state;
    }

    /// <summary>Abre a pasta de gravacoes no Explorer (acao do toast, RF-F3.16).</summary>
    private static void OpenFolder(string folder)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("OpenRecordingFolder", ex);
        }
    }

    /// <summary>
    /// ADR-P.08: devolve o processo ao EcoQoS quando nenhuma janela do Klip esta
    /// visivel. Chamado pelas transicoes de visibilidade; e barato e idempotente.
    /// Nao rebaixa enquanto houver gravacao em andamento nem durante o shutdown.
    /// </summary>
    private void ApplyIdleQosIfNoWindowVisible()
    {
        if (IsExiting || _recordingController?.IsActive == true)
            return;
        foreach (Window window in Windows)
        {
            if (window.IsVisible)
                return;
        }
        PowerEfficiency.EnterProcessEcoQos();
    }

    /// <summary>Opens the editor with an image from history or capture.</summary>
    private void OpenEditor(ClipboardItem item)    {
        if (item.FilePath is null || _host is null)
            return;
        try
        {
            PowerEfficiency.EnterProcessHighQos(); // ADR-P.08: janela a frente
            var mediaStore = _host.Services.GetRequiredService<MediaStore>();
            var editor = _host.Services.GetRequiredService<EditorWindow>();
            editor.OpenFromHistory(mediaStore.ToAbsolute(item.FilePath));
            editor.Show();
            editor.Activate();
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("OpenEditor", ex);
        }
    }

    /// <summary>
    /// Abre o editor de midia (spec F5): uma janela por arquivo; se ja existe
    /// uma para o mesmo caminho, apenas reativa (RF-F5.16).
    /// </summary>
    private void OpenMediaEditor(string filePath)
    {
        if (_host is null)
            return;
        try
        {
            PowerEfficiency.EnterProcessHighQos(); // ADR-P.08: janela a frente
            if (_mediaEditors.TryGetValue(filePath, out var existing))
            {
                existing.Show();
                if (existing.WindowState == WindowState.Minimized)
                    existing.WindowState = WindowState.Normal;
                existing.Activate();
                return;
            }

            var window = new MediaEditorWindow(
                _host.Services.GetRequiredService<SettingsService>(),
                _host.Services.GetRequiredService<Klip.Core.Clipboard.ClipboardIngestService>());
            window.ExportCompleted += message => _tray?.ShowNotification("Klip", message);
            window.FileOpened += (w, path) =>
            {
                // drag-and-drop trocou o arquivo: re-mapeia a janela
                foreach (var stale in _mediaEditors.Where(kv => kv.Value == w).Select(kv => kv.Key).ToList())
                    _mediaEditors.Remove(stale);
                _mediaEditors[path] = w;
            };
            window.Closed += (_, _) =>
            {
                foreach (var stale in _mediaEditors.Where(kv => kv.Value == window).Select(kv => kv.Key).ToList())
                    _mediaEditors.Remove(stale);
            };
            window.OpenFile(filePath);
            window.Show();
            window.Activate();
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("OpenMediaEditor", ex);
        }
    }

    /// <summary>Grabs the target app before showing the flyout.</summary>
    private void ToggleHistoryFlyout()
    {
        if (_flyout is null)
            return;
        // opening history cancels a paste queue that is running
        _host?.Services.GetRequiredService<PasteQueueService>().Cancel();
        if (_flyout.IsVisible)
        {
            _flyout.HideFlyout();
            PowerEfficiency.EnterProcessEcoQos(); // ADR-P.08: sem UI visivel, volta ao Eco
            return;
        }
        // ADR-P.08: HighQoS ANTES de montar/mostrar a janela, senao o primeiro layout
        // e a composicao Mica rodam num efficient core e o painel abre devagar.
        PowerEfficiency.EnterProcessHighQos();
        _pasteService?.CaptureForegroundTarget(_flyout.Hwnd);
        _flyout.ShowFlyout();
    }

    /// <summary>
    /// RF-P2.09: retencao em timer de background (nao DispatcherTimer) e pulada
    /// enquanto houver jogo em tela cheia. A primeira execucao e adiada 60 s para
    /// nao disputar IO com o boot do Windows.
    /// </summary>
    private void RunRetentionInBackground(SettingsService settings)
    {
        _retentionTimer = new System.Threading.Timer(
            _ => RunRetentionOnce(settings),
            state: null,
            dueTime: TimeSpan.FromSeconds(60),
            period: TimeSpan.FromMinutes(30));
    }

    private void RunRetentionOnce(SettingsService settings)
    {
        // nao disputar CPU e IO com um jogo; o proximo tick pega
        if (_activityMonitor?.State == SystemActivityState.Suspended || IsExiting)
            return;

        var repository = _host!.Services.GetRequiredService<ClipboardItemRepository>();
        var mediaStore = _host.Services.GetRequiredService<MediaStore>();
        var database = _host.Services.GetRequiredService<Database>();
        // ADR-P.08: prioridade de CPU, IO e memoria baixas nesta varredura
        PowerEfficiency.RunAsBackgroundIo(() =>
        {
            try
            {
                var orphans = repository.ApplyRetention(
                    settings.Current.RetentionMaxItems,
                    settings.Current.RetentionMaxAgeDays,
                    settings.Current.RetentionMaxTotalBytes);
                mediaStore.DeleteFiles(orphans);
                if (orphans.Count > 0)
                    StartupLog.Write($"Retencao: {orphans.Count} arquivo(s) removido(s)");

                // RF-P2.07 / RF-P2.10: manutencao periodica junto da retencao, na mesma
                // thread de background. PRAGMA optimize atualiza as estatisticas do
                // planner, wal_checkpoint(TRUNCATE) impede o arquivo -wal crescer sem
                // limite num app que fica dias ligado, e o optimize do FTS5 compacta os
                // segmentos do indice.
                repository.OptimizeFullTextIndex();
                database.RunMaintenance();
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("Retencao", ex);
            }
        });
    }

    private void CreateTrayIcon()
    {
        _tray = new TaskbarIcon
        {
            Icon = TrayIconFactory.Create(),
            ToolTipText = "Klip",
        };
        BuildTrayMenu();
        _tray.TrayLeftMouseUp += (_, _) =>
        {
            if (IsExiting)
                return; // shutdown pumpeando o dispatcher: sem reentrancia via tray
            // RF-F3.04: durante uma gravacao o clique no tray PARA a gravacao
            if (_recordingController?.IsActive == true)
                _recordingController.RequestStop();
            else
                ToggleHistoryFlyout();
        };
        // clicking the toast: MP4 opens the media editor, recording opens its
        // folder, capture opens the image editor
        _tray.TrayBalloonTipClicked += (_, _) =>
        {
            if (IsExiting)
                return;
            if (_recordingToastFile is { } file)
            {
                _recordingToastFile = null;
                MediaEditorGateway.Open(file);
            }
            else if (_recordingToastFolder is { } folder)
            {
                _recordingToastFolder = null;
                OpenFolder(folder);
            }
            else if (_captureController?.LastCapturedItem is { } lastCapture)
            {
                OpenEditor(lastCapture);
            }
        };
        _tray.ForceCreate();

        // switching language rebuilds the menu right away
        Loc.LanguageChanged += BuildTrayMenu;
    }

    private void BuildTrayMenu()
    {
        if (_tray is null)
            return;

        var menu = new ContextMenu();
        menu.Items.Add(CreateMenuItem(Loc.TrayOpenHistory, "\uE81C", (_, _) => ToggleHistoryFlyout()));
        menu.Items.Add(CreateMenuItem(Loc.TrayCapture, "\uE722", (_, _) => StartCapture()));
        menu.Items.Add(CreateMenuItem(Loc.TraySettings, "\uE713", (_, _) => ShowMainWindow()));
        menu.Items.Add(new Separator());

        // pause/resume on a single click (no checkbox), last item before Exit;
        // the state shows up in the text and glyph themselves
        var paused = _clipboardMonitor?.IsPaused == true;
        var pauseItem = CreateMenuItem(paused ? Loc.TrayResumeHistory : Loc.TrayPauseHistory,
            paused ? "\uE768" : "\uE769", (_, _) => { });
        pauseItem.ToolTip = Loc.TrayPauseTooltip;
        pauseItem.Click += (_, _) =>
        {
            if (_clipboardMonitor is null)
                return;
            _clipboardMonitor.IsPaused = !_clipboardMonitor.IsPaused;
            pauseItem.Header = _clipboardMonitor.IsPaused ? Loc.TrayResumeHistory : Loc.TrayPauseHistory;
            ((TextBlock)pauseItem.Icon).Text = _clipboardMonitor.IsPaused ? "\uE768" : "\uE769";
        };
        menu.Items.Add(pauseItem);

        menu.Items.Add(CreateMenuItem(Loc.TrayExit, "\uE7E8", (_, _) => ExitApplication()));

        _tray.ContextMenu = menu;
    }

    private static MenuItem CreateMenuItem(string header, string glyph, RoutedEventHandler onClick)
    {
        var item = new MenuItem
        {
            Header = header,
            Icon = new TextBlock
            {
                Text = glyph,
                FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons"),
                FontSize = 14,
            },
        };
        item.Click += onClick;
        return item;
    }

    /// <summary>
    /// ADR-S.01: cria a janela de Configuracoes sob demanda. O shell so monta a si
    /// mesmo e a pagina ativa (ADR-S.03: as outras 6 nascem na primeira navegacao),
    /// entao o custo e uma fracao da tela monolitica anterior.
    /// </summary>
    private void ShowMainWindow()
    {
        if (_host is null || IsExiting)
            return;
        // ADR-P.08: sai do Eco antes de montar/mostrar (senao o layout roda em E-core)
        PowerEfficiency.EnterProcessHighQos();

        if (_mainWindow is null)
        {
            _mainWindow = _host.Services.GetRequiredService<MainWindow>();
            _mainWindow.IsVisibleChanged += (_, _) => ApplyIdleQosIfNoWindowVisible();
            StartupLog.Write("Configuracoes: janela criada sob demanda");
        }

        _mainWindow.RefreshStatus();
        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void NotifyHotkeyConflict(HotkeyGesture gesture)
    {
        _tray?.ShowNotification("Klip", string.Format(Loc.NotifyHotkeyConflict, gesture));
    }

    // RF-F3.16: finalizacao graciosa no encerramento do app - o Sair do tray
    // para a gravacao ativa e espera a finalizacao (MP4 com teto de 30 s; GIF
    // ate o fim do encode) antes do shutdown; a janela "Finalizando
    // gravacao..." do proprio StopAsync fica visivel enquanto isso
    private async void ExitApplication()
    {
        if (IsExiting)
            return;
        IsExiting = true;

        if (_recordingController is { } recording)
        {
            // contagem/painel pendentes sao cancelados antes de sair (nao
            // deixar o countdown ligar engine/recorder durante o shutdown)
            if (recording.IsActive && !recording.IsBusy)
                recording.RequestStop();

            if (recording.IsBusy)
            {
                try
                {
                    _recordingFinalizeHandled = true;
                    await recording.StopAndFinalizeAsync(TimeSpan.FromSeconds(30));
                }
                catch (Exception ex)
                {
                    StartupLog.WriteException("ExitFinalizeRecording", ex);
                }
            }
        }

        Shutdown();
    }

    // evita finalizar duas vezes quando o OnExit roda depois do Sair do tray
    private bool _recordingFinalizeHandled;

    /// <summary>
    /// RF-F3.16: shutdown/logoff do Windows nao espera awaits da UI - melhor
    /// esforco sincrono com timeout curto (5 s), bombeando o dispatcher via
    /// DispatcherFrame para as continuations da finalizacao rodarem sem
    /// deadlock na UI thread. MP4 preserva o ja fragmentado; GIF pode perder
    /// o buffer se o encode nao couber nos 5 s.
    /// </summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (_recordingController?.IsBusy == true && !_recordingFinalizeHandled)
        {
            _recordingFinalizeHandled = true;
            PumpRecordingFinalize(TimeSpan.FromSeconds(5));
        }
        // RF-P3.05 / RF-P3.06: shutdown do Windows nao espera - persiste o pendente agora
        _host?.Services.GetService<SettingsService>()?.Flush();
        StartupLog.Flush();
    }

    /// <summary>Espera a finalizacao da gravacao sem bloquear o dispatcher (RF-F3.16).</summary>
    private void PumpRecordingFinalize(TimeSpan timeout)
    {
        if (_recordingController is not { } controller)
            return;
        try
        {
            // o PushFrame reentra no dispatcher: hotkeys globais e tray
            // poderiam disparar handlers (flyout, captura, nova gravacao) no
            // meio do shutdown - corta as entradas antes de pumpear
            IsExiting = true;
            try
            {
                var hotkeys = _host?.Services.GetService<HotkeyService>();
                if (hotkeys is not null)
                {
                    if (_historyHotkeyId != 0)
                    {
                        hotkeys.Unregister(_historyHotkeyId);
                        _historyHotkeyId = 0;
                    }
                    if (_captureHotkeyId != 0)
                    {
                        hotkeys.Unregister(_captureHotkeyId);
                        _captureHotkeyId = 0;
                    }
                }
                if (_tray is not null)
                    _tray.ContextMenu = null; // cliques guardados por IsExiting
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("ShutdownDisableInputs", ex);
            }

            if (Dispatcher.HasShutdownStarted)
            {
                // dispatcher ja encerrando: PushFrame nao e permitido e as
                // continuations de UI nao rodariam - melhor esforco bloqueante
                // com teto curto (fMP4 preserva o ja fragmentado)
                controller.StopAndFinalizeAsync(timeout).Wait(TimeSpan.FromSeconds(2));
                return;
            }

            var frame = new System.Windows.Threading.DispatcherFrame();
            _ = controller.StopAndFinalizeAsync(timeout)
                .ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = timeout };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            timer.Stop();
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("ShutdownFinalizeRecording", ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // contagem/painel pendentes nao podem ligar engine/recorder no shutdown
        if (_recordingController is { } pending && pending.IsActive && !pending.IsBusy)
            pending.RequestStop();

        // RF-F3.16: rede de seguranca para saidas que nao passaram pelo
        // ExitApplication (ex.: Shutdown() direto) - mesmo melhor esforco
        // do SessionEnding, com o teto de 30 s do fluxo normal
        if (_recordingController?.IsBusy == true && !_recordingFinalizeHandled)
        {
            _recordingFinalizeHandled = true;
            PumpRecordingFinalize(TimeSpan.FromSeconds(30));
        }

        _tray?.Dispose();

        // RF-P1.06 / RF-P0.02: parar antes de derrubar o host
        _retentionTimer?.Dispose();
        _activityMonitor?.Dispose();
        _uiWatchdog?.Dispose();

        _clipboardMonitor?.Dispose();
        if (_host is not null)
        {
            // limpa o historico ao sair (mantem os fixados/favoritos)
            try
            {
                var settings = _host.Services.GetService<SettingsService>();
                if (settings?.Current.ClearHistoryOnExit == true)
                {
                    var repo = _host.Services.GetService<ClipboardItemRepository>();
                    repo?.ClearAll();
                    repo?.Vacuum();
                }
                // RF-P3.06: grava o que o debounce ainda nao persistiu
                settings?.Flush();
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("ClearOnExit", ex);
            }

            _host.Services.GetService<PasteQueueService>()?.Dispose();
            _host.Services.GetService<HotkeyService>()?.Dispose();
            // ADR-P.05: a thread do clipboard vai DEPOIS do monitor, que precisa dela
            // viva para remover o listener na mesma thread que o registrou.
            _host.Services.GetService<ClipboardThread>()?.Dispose();
            _host.Services.GetService<Database>()?.Dispose();
            _host.Dispose();
        }
        // ADR-P.01: derruba o pump e o worker dos hooks LL
        LowLevelHookHost.Shared.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);

        // RF-P3.05: por ultimo - qualquer Write depois disso cai no append sincrono
        StartupLog.Flush();
        StartupLog.Shutdown();
    }
}
