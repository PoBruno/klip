using System.Windows;
using System.Windows.Controls;
using Klip.App.Diagnostics;
using Klip.App.Localization;
using Klip.App.Services;
using Klip.App.ViewModels;
using Klip.App.Views;
using Klip.App.Views.Pages;
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
using Wpf.Ui.Abstractions;

namespace Klip.App;

/// <summary>
/// App bootstrap: single instance, DI, database, tray, hotkeys and the clipboard engine.
/// </summary>
public partial class App : Application
{
    private const string MutexName = "Klip.SingleInstance";

    private Mutex? _mutex;
    private ServiceProvider? _services;
    private TaskbarIcon? _tray;
    private SettingsShell? _settingsWindow;
    private HistoryFlyoutWindow? _flyout;
    private PasteService? _pasteService;
    private ClipboardMonitorService? _clipboardMonitor;
    private CaptureController? _captureController;
    private RecordingController? _recordingController;

    // fullscreen game guard. Only polls while a hook scope is alive
    private SystemActivityMonitor? _activityMonitor;

    // ui thread latency probe. Only runs while a hook scope is alive (or with --verbose)
    private UiThreadWatchdog? _uiWatchdog;

    // retention on a pool timer, never a DispatcherTimer (no waking the UI thread
    // of an app that sits idle for hours), skipped while a game is fullscreen
    private System.Threading.Timer? _retentionTimer;

    // one compacting GC after heavy sessions (overlay, editor, recording)
    private MemorySettler? _memory;

    // tray icon follows the taskbar color (light/dark)
    private bool? _trayIconLight;

    // folder of the last recording toast: clicking the balloon opens it
    private string? _recordingToastFolder;

    // file of the last MP4 toast: clicking opens the media editor
    private string? _recordingToastFile;

    // media editor: one window per file, reactivated if already open
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
        // --verbose turns on the chatty log (ingest, hook state) and keeps the ui
        // watchdog running. Off by default: normal use only logs transitions and errors
        StartupLog.VerboseEnabled = e.Args.Contains("--verbose");
        StartupLog.Write("OnStartup: iniciando");

        // o app nasce ocioso na bandeja. EcoQoS pede frequencia eficiente e
        // agendamento em efficient cores INCLUSIVE na tomada (o nivel Low automatico por
        // visibilidade so vale na bateria). Prioridade de memoria baixa faz o gerenciador
        // aparar as NOSSAS paginas antes das dos outros sob pressao - alternativa correta
        // e nao destrutiva ao EmptyWorkingSet.
        PowerEfficiency.SetProcessMemoryPriorityLow();
        PowerEfficiency.EnterProcessEcoQos();
        // Defesa contra dependencia que chame timeBeginPeriod: no Win11 o efeito e por
        // processo e some quando estamos ocultos, mas o custo de troca de contexto fica.
        PowerEfficiency.IgnoreTimerResolutionRequests(true);

        // the LL hook thread now starts at idle (see WarmUpAtIdle), Acquire also starts
        // it on demand. One less thread in the startup path

        var services = new ServiceCollection();
        services.AddSingleton<SettingsService>();
        services.AddSingleton(_ =>
        {
            var db = new Database(AppPaths.DatabaseFile);
            db.Initialize();
            return db;
        });
        services.AddSingleton<ClipboardItemRepository>();
        services.AddSingleton<MediaStore>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<Klip.Core.Storage.IThumbnailGenerator, WpfThumbnailGenerator>();
        services.AddSingleton<OcrService>();
        services.AddSingleton<Klip.Core.Storage.IImageTextExtractor>(sp => sp.GetRequiredService<OcrService>());
        // the OCR engine itself only loads on the first image (OcrService is lazy)
        services.AddSingleton<ClipboardIngestService>(sp => new ClipboardIngestService(
            sp.GetRequiredService<ClipboardItemRepository>(),
            sp.GetRequiredService<MediaStore>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<Klip.Core.Storage.IThumbnailGenerator>(),
            sp.GetRequiredService<Klip.Core.Storage.IImageTextExtractor>(),
            System.IO.Path.Combine(AppPaths.Root, "secrets-pending.txt")));
        services.AddSingleton(_ => new MemorySettler(Dispatcher));
        // thread STA dedicada dona de todo acesso ao clipboard. Precisa vir
        // antes do WriteGuard/Monitor, que a recebem por injecao.
        services.AddSingleton<ClipboardThread>();
        services.AddSingleton<ClipboardWriteGuard>();
        services.AddSingleton<ClipboardMonitorService>();
        services.AddSingleton<ExplorerPasteService>();
        services.AddSingleton<PasteService>();
        services.AddSingleton<PasteQueueService>();
        services.AddSingleton<ScreenCaptureService>();
        services.AddSingleton<PanoramicCaptureService>();
        // recording: concrete interop types only through the factory
        services.AddSingleton(sp => new RecordingController(
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<ClipboardIngestService>(),
            sp.GetRequiredService<HotkeyService>(),
            RecordingInteropFactory.CreateAudioDeviceEnumerator,
            RecordingInteropFactory.CreateMp4Recorder));
        services.AddSingleton<CaptureController>();
        services.AddSingleton<PinnedImageService>();
        services.AddSingleton<AutostartService>();
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<SystemHotkeyService>();
        services.AddSingleton<Klip.Core.Storage.SnippetRepository>();
        services.AddSingleton(_ => new HistoryPause());
        services.AddSingleton<HistoryFlyoutViewModel>();
        services.AddSingleton<HistoryFlyoutWindow>();
        // fullscreen guard and ui latency probe, both only alive with a hook scope
        services.AddSingleton<SystemActivityMonitor>();
        services.AddSingleton(_ => new UiThreadWatchdog(Dispatcher));
        services.AddTransient<EditorWindow>(); // one window per edit
        // tela de Configuracoes: o provider resolve as
        // paginas pelo container; com ele ligado o NavigationCacheMode vira codigo
        // morto e o ciclo de vida passa a ser do DI - Singleton = construida na
        // primeira navegacao e reusada, sem reconstruir ao voltar.
        services.AddSingleton<INavigationViewPageProvider, NavigationPageProvider>();
        services.AddSingleton<SettingsShell>();
        services.AddSingleton<GeneralPage>();
        services.AddSingleton<HotkeysPage>();
        services.AddSingleton<ClipboardPage>();
        services.AddSingleton<CapturePage>();
        services.AddSingleton<RecordingPage>();
        services.AddSingleton<PrivacyPage>();
        services.AddSingleton<MaintenancePage>();
        services.AddSingleton<AboutPage>();
        // plain container, nothing here needs config, logging or hosted services
        _services = services.BuildServiceProvider();
        StartupLog.Write("OnStartup: container pronto");

        // the watchdog measures ui thread latency: a long block there is what freezes
        // system input while a LL hook is installed. The activity monitor polls for
        // fullscreen games (Windows has no event for it). Both only matter while a
        // hook scope is alive, so at idle the process has no periodic timer at all
        _uiWatchdog = _services.GetRequiredService<UiThreadWatchdog>();
        _activityMonitor = _services.GetRequiredService<SystemActivityMonitor>();
        _activityMonitor.StateChanged += state =>
            Dispatcher.BeginInvoke(new Action(() => OnActivityStateChanged(state)));
        if (StartupLog.VerboseEnabled)
            _uiWatchdog.Start();
        LowLevelHookHost.Shared.ScopeActiveChanged += OnHookScopeActiveChanged;
        _memory = _services.GetRequiredService<MemorySettler>();

        var settings = _services.GetRequiredService<SettingsService>();

        // resolve language before any window shows up (default: Windows)
        Localization.Loc.Initialize(settings.Current.Language);
        ThemeManager.ApplyAtStartup(settings.Current.Theme);

        // autostart following the saved setting
        try
        {
            _services.GetRequiredService<AutostartService>().SetEnabled(settings.Current.StartWithWindows);
        }
        catch (Exception ex)
        {
            // no registry access is not fatal here
            StartupLog.WriteException("Autostart", ex);
        }

        // a tela de Configuracoes NAO e construida no startup. A antiga
        // MainWindow montava ~247 elementos e 31 cards mesmo com o app iniciando
        // minimizado - o UiThreadWatchdog media 793 ms de UI thread travada por causa
        // disso. Agora a janela nasce na primeira abertura (ver ShowMainWindow).
        // O banco continua sendo inicializado aqui perto, pelo ClipboardMonitorService.

        // clipboard engine + flyout
        _pasteService = _services.GetRequiredService<PasteService>();
        // warn when paste fails (item stays copied at least)
        _pasteService.PasteFailed += () =>
            _tray?.ShowNotification("Klip", Loc.PasteFailedToast);
        _flyout = _services.GetRequiredService<HistoryFlyoutWindow>();
        // o flyout fecha por varios caminhos (Esc, clique fora, colar,
        // hotkey). Ao inves de espalhar EnterProcessEcoQos por todos eles, reage a
        // visibilidade: sem janela visivel, o app volta ao estado de repouso.
        _flyout.IsVisibleChanged += (_, _) => ApplyIdleQosIfNoWindowVisible();
        _clipboardMonitor = _services.GetRequiredService<ClipboardMonitorService>();
        StartupLog.Write("OnStartup: clipboard monitor ativo");

        // timed pause and "ignore the next copy" feed the monitor; the one-shot timer lives in HistoryPause
        var monitor = _clipboardMonitor;
        var historyPause = _services.GetRequiredService<HistoryPause>();
        monitor.SkipCopy = historyPause.TryConsumeIgnoreNext;
        historyPause.Changed += () => monitor.IsPaused = historyPause.IsPaused;
        _services.GetRequiredService<HistoryFlyoutViewModel>().OpenSettingsRequested += ShowMainWindow;
        Views.Controls.PasteAsFileSettings.SettingsSource = settings;
        _services.GetRequiredService<ExplorerPasteService>().Start();

        // indicator for the sequential paste queue
        var pasteQueue = _services.GetRequiredService<PasteQueueService>();
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
        _captureController = _services.GetRequiredService<CaptureController>();
        _captureController.CaptureCompleted += message =>
        {
            _recordingToastFolder = null; // o clique volta a abrir o editor
            _recordingToastFile = null;
            _tray?.ShowNotification("Klip", message);
        };
        // the editor gets the bitmap straight from memory, no reread of the png
        _captureController.EditImageRequested += OpenEditor;

        // gravacao GIF/MP4: toasts + estado no tray
        _recordingController = _services.GetRequiredService<RecordingController>();
        _recordingController.RecordingToast += (message, folder, editorFile) =>
        {
            // estado do toast anterior nao vaza para o clique deste (limpa e
            // reatribui a cada toast de gravacao)
            _recordingToastFolder = folder;
            _recordingToastFile = editorFile; // MP4: clique abre o editor de midia
            _tray?.ShowNotification("Klip", message);
        };
        // tooltip do tray com o timer enquanto grava (tick de 1 s)
        var wasRecording = false;
        _recordingController.StateChanged += () =>
        {
            if (_recordingController is null)
                return;
            // recording just ended: frames, encoder buffers and D3D go back with a GC
            if (wasRecording && !_recordingController.IsActive)
                _memory?.Schedule();
            wasRecording = _recordingController.IsActive;
            if (_tray is null)
                return;
            _tray.ToolTipText = _recordingController.IsActive
                ? string.Format(Loc.TrayRecordingTooltip, _recordingController.Elapsed.ToString(@"mm\:ss"))
                : "Klip";
        };

        // editor: opened from the flyout and from the toast
        _services.GetRequiredService<HistoryFlyoutViewModel>().OpenInEditorRequested += OpenEditor;

        // editor de midia: registra o gateway para historico/toasts
        MediaEditorGateway.Opener = path => Dispatcher.BeginInvoke(() => OpenMediaEditor(path));

        RegisterHotkeys(settings);
        CreateTrayIcon();
        StartupLog.Write("OnStartup: tray e hotkeys prontos");
        // em idle nenhum hook LL pode estar instalado
        StartupLog.Write($"OnStartup: {HookHealth.FormatSummary()}");

        RunRetentionInBackground(settings);

        // secrets a crashed run left behind (SecretKeepSeconds) go away now
        var ingest = _services.GetRequiredService<ClipboardIngestService>();
        _ = Task.Run(() =>
        {
            try
            {
                var wiped = ingest.PurgeSecretLeftovers();
                if (wiped > 0)
                    StartupLog.Write($"Segredos: {wiped} pendente(s) da sessao anterior apagado(s)");
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("SecretLeftovers", ex);
            }
        });

        WarmUpAtIdle();

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
            // --settings abre direto as Configuracoes (util para suporte e atalhos)
            if (e.Args.Contains("--settings"))
                Dispatcher.BeginInvoke(new Action(ShowMainWindow),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            else if (!startMinimized)
                ShowMainWindow();
        }
    }

    private void HandleElevatedRegistryOperation(string operation)
    {
        // este processo faz uma operacao e morre. O Update do SettingsService
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
        // mesmo motivo do metodo acima - o processo morre logo em seguida.
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
    // text, repeat region and record video: empty by default, registered only when set
    private readonly List<int> _optionalHotkeyIds = [];

    /// <summary>(Re)applies the global hotkeys from settings.</summary>
    public bool ApplyHotkeys(SettingsService settings)
    {
        var hotkeys = _services!.GetRequiredService<HotkeyService>();

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
        foreach (var id in _optionalHotkeyIds)
            hotkeys.Unregister(id);
        _optionalHotkeyIds.Clear();

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

        var current = settings.Current;
        allOk &= TryRegisterOptional(hotkeys, current.HotkeyCaptureText, () => StartCaptureIn(CaptureMode.Text));
        allOk &= TryRegisterOptional(hotkeys, current.HotkeyRepeatRegion, RepeatLastRegion);
        allOk &= TryRegisterOptional(hotkeys, current.HotkeyRecordVideo, () => StartCaptureIn(CaptureMode.Mp4));

        return allOk;
    }

    private bool TryRegisterOptional(HotkeyService hotkeys, string text, Action callback)
    {
        if (!HotkeyGesture.TryParse(text, out var gesture))
            return true; // not set, nothing to register
        if (hotkeys.TryRegister(gesture, callback, out var id))
        {
            _optionalHotkeyIds.Add(id);
            return true;
        }
        NotifyHotkeyConflict(gesture);
        return false;
    }

    /// <summary>
    /// "Take over Win+V" flow: merge into the registry, restart Explorer,
    /// then retry the registration. Returns a status message for the UI.
    /// </summary>
    public async Task<string> TakeoverWinVAsync()
    {
        var settings = _services!.GetRequiredService<SettingsService>();
        var registry = _services!.GetRequiredService<SystemHotkeyService>();

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
        var settings = _services!.GetRequiredService<SettingsService>();

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
        var settings = _services!.GetRequiredService<SettingsService>();
        var registry = _services!.GetRequiredService<SystemHotkeyService>();

        registry.SetPrintScreenFreed(true);
        settings.UpdateAndFlush(s => s.HotkeyCapture = "PrintScreen");
        return ApplyHotkeys(settings) ? "ok" : "conflito";
    }

    /// <summary>Win+Shift+S route (desativa o Win+S junto, avisar antes).</summary>
    public async Task<string> TakeoverWinShiftSAsync()
    {
        var settings = _services!.GetRequiredService<SettingsService>();
        var registry = _services!.GetRequiredService<SystemHotkeyService>();

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
        var settings = _services!.GetRequiredService<SettingsService>();
        var registry = _services!.GetRequiredService<SystemHotkeyService>();

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
        var hotkeys = _services!.GetRequiredService<HotkeyService>();
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
    private void StartCapture() => StartCaptureIn(null);

    private void RepeatLastRegion()
    {
        if (IsExiting)
            return;
        _flyout?.HideFlyout();
        _captureController?.RepeatLastRegion();
    }

    private void StartCaptureIn(CaptureMode? mode)
    {
        // Aqui NAO ha bloqueio por jogo em tela cheia de proposito. Capturar um jogo ou
        // um video em tela cheia e caso de uso primario de uma ferramenta de captura;
        // recusar a hotkey seria uma regressao muito pior do que o custo de composicao
        // de um overlay que o usuario acabou de pedir. O que a auto-suspensao corta e o
        // custo em REPOUSO, nao acao explicita do usuario.
        if (_activityMonitor?.State == SystemActivityState.Suspended)
            StartupLog.WriteVerbose("Captura sobre app em tela cheia (a pedido do usuario)");

        _flyout?.HideFlyout(); // overlay cant capture the flyout while its open
        PowerEfficiency.EnterProcessHighQos(); // UI a frente, sai do Eco
        _captureController?.StartCapture(mode);
    }

    /// <summary>
    /// Reage a entrada e saida de jogo em tela cheia. O
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

    /// <summary>Abre a pasta de gravacoes no Explorer (acao do toast).</summary>
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
    /// Devolve o processo ao EcoQoS quando nenhuma janela do Klip esta
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

    /// <summary>Opens the editor with an image from history (or the toast).</summary>
    private void OpenEditor(ClipboardItem item)
    {
        if (item.FilePath is null || _services is null)
            return;
        var path = _services.GetRequiredService<MediaStore>().ToAbsolute(item.FilePath);
        ShowEditor(editor => editor.OpenFromHistory(path));
    }

    /// <summary>Opens the editor with a capture that is still in memory.</summary>
    private void OpenEditor(System.Windows.Media.Imaging.BitmapSource image) =>
        ShowEditor(editor => editor.OpenImage(image));

    private void ShowEditor(Action<EditorWindow> load)
    {
        if (_services is null)
            return;
        try
        {
            PowerEfficiency.EnterProcessHighQos(); // janela a frente
            var editor = _services.GetRequiredService<EditorWindow>();
            load(editor);
            // base image + undo bitmaps only go back with a GC
            editor.Closed += (_, _) => _memory?.Schedule();
            editor.Show();
            editor.Activate();
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("OpenEditor", ex);
        }
    }

    /// <summary>
    /// Abre o editor de midia: uma janela por arquivo; se ja existe
    /// uma para o mesmo caminho, apenas reativa.
    /// </summary>
    private void OpenMediaEditor(string filePath)
    {
        if (_services is null)
            return;
        try
        {
            PowerEfficiency.EnterProcessHighQos(); // janela a frente
            if (_mediaEditors.TryGetValue(filePath, out var existing))
            {
                existing.Show();
                if (existing.WindowState == WindowState.Minimized)
                    existing.WindowState = WindowState.Normal;
                existing.Activate();
                return;
            }

            var window = new MediaEditorWindow(
                _services.GetRequiredService<SettingsService>(),
                _services.GetRequiredService<Klip.Core.Clipboard.ClipboardIngestService>());
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
                _memory?.Schedule(); // preview frame cache is big
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
        _services?.GetRequiredService<PasteQueueService>().Cancel();
        if (_flyout.IsVisible)
        {
            _flyout.HideFlyout();
            PowerEfficiency.EnterProcessEcoQos(); // sem UI visivel, volta ao Eco
            return;
        }
        // HighQoS ANTES de montar/mostrar a janela, senao o primeiro layout
        // e a composicao Mica rodam num efficient core e o painel abre devagar.
        PowerEfficiency.EnterProcessHighQos();
        _pasteService?.CaptureForegroundTarget(_flyout.Hwnd);
        _flyout.ShowFlyout();
    }

    /// <summary>
    /// Retencao em timer de background (nao DispatcherTimer) e pulada
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
        // nao disputar CPU e IO com um jogo; o proximo tick pega. Avaliado aqui, na
        // hora, ja que o monitor de atividade nao faz polling em repouso
        if (IsExiting || SystemFullscreenDetector.Evaluate() == SystemActivityState.Suspended)
            return;

        var sp = _services!;
        var repository = sp.GetRequiredService<ClipboardItemRepository>();
        var mediaStore = sp.GetRequiredService<MediaStore>();
        var database = sp.GetRequiredService<Database>();
        // prioridade de CPU, IO e memoria baixas nesta varredura
        PowerEfficiency.RunAsBackgroundIo(() =>
        {
            try
            {
                var s = settings.Current;
                var orphans = repository.ApplyRetention(new RetentionLimits(
                    s.RetentionMaxItems, s.RetentionMaxAgeDays, s.RetentionMaxTotalBytes,
                    s.RetentionMaxImages, s.RetentionMaxMediaBytes));
                mediaStore.DeleteFiles(orphans);
                if (orphans.Count > 0)
                    StartupLog.Write($"Retencao: {orphans.Count} arquivo(s) removido(s)");

                // manutencao periodica junto da retencao, na mesma
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
        _tray = new TaskbarIcon { ToolTipText = "Klip" };
        RefreshTrayIcon();
        BuildTrayMenu();
        // the taskbar color can change at runtime; the theme watcher of any open
        // window raises this, and the menu/flyout paths check again (cheap reg read)
        Wpf.Ui.Appearance.ApplicationThemeManager.Changed += (_, _) => RefreshTrayIcon();
        _tray.TrayLeftMouseUp += (_, _) =>
        {
            if (IsExiting)
                return; // shutdown pumpeando o dispatcher: sem reentrancia via tray
            RefreshTrayIcon();
            // durante uma gravacao o clique no tray PARA a gravacao
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

    /// <summary>Swaps the tray .ico when the taskbar switched between light and dark.</summary>
    private void RefreshTrayIcon()
    {
        if (_tray is null)
            return;
        try
        {
            var light = TrayIconFactory.TaskbarIsLight();
            if (_trayIconLight == light)
                return;
            _trayIconLight = light;
            var old = _tray.Icon;
            _tray.Icon = TrayIconFactory.Create(light);
            old?.Dispose();
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("TrayIcon", ex);
        }
    }

    private void BuildTrayMenu()
    {
        if (_tray is null)
            return;

        var menu = new ContextMenu();
        menu.Opened += (_, _) => RefreshTrayIcon();
        menu.Items.Add(CreateMenuItem(Loc.TrayOpenHistory, "\uE81C", (_, _) => ToggleHistoryFlyout()));
        menu.Items.Add(CreateMenuItem(Loc.TrayCapture, "\uE722", (_, _) => StartCapture()));
        menu.Items.Add(CreateMenuItem(Loc.TrayRepeatRegion, "\uE72C", (_, _) => RepeatLastRegion()));
        menu.Items.Add(BuildPinnedMenuItem());
        menu.Items.Add(CreateMenuItem(Loc.TraySettings, "\uE713", (_, _) => ShowMainWindow()));
        menu.Items.Add(new Separator());

        // pause (15 min, 1 h, until resumed) and ignore the next copy. While paused the
        // submenu gives way to a single "resume" item
        var pause = _services?.GetService<HistoryPause>();
        var pauseItem = CreateMenuItem(Loc.TrayPauseHistory, "", (_, _) => { });
        pauseItem.ToolTip = Loc.TrayPauseTooltip;
        pauseItem.Items.Add(CreateMenuItem(Loc.TrayPause15, "", (_, _) => pause?.Pause(TimeSpan.FromMinutes(15))));
        pauseItem.Items.Add(CreateMenuItem(Loc.TrayPause60, "", (_, _) => pause?.Pause(TimeSpan.FromHours(1))));
        pauseItem.Items.Add(CreateMenuItem(Loc.TrayPauseUntil, "", (_, _) => pause?.Pause(null)));
        var resumeItem = CreateMenuItem(Loc.TrayResumeHistory, "", (_, _) => pause?.Resume());
        var ignoreItem = CreateMenuItem(Loc.TrayIgnoreNext, "", (_, _) =>
        {
            if (pause is null)
                return;
            if (pause.IsIgnoringNext)
                pause.CancelIgnoreNext();
            else
                pause.IgnoreNext();
        });
        menu.Opened += (_, _) =>
        {
            var paused = pause?.IsPaused == true;
            pauseItem.Visibility = paused ? Visibility.Collapsed : Visibility.Visible;
            resumeItem.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
            ((TextBlock)ignoreItem.Icon).Text = pause?.IsIgnoringNext == true ? "" : "";
        };
        menu.Items.Add(pauseItem);
        menu.Items.Add(resumeItem);
        menu.Items.Add(ignoreItem);

        menu.Items.Add(CreateMenuItem(Loc.TrayExit, "\uE7E8", (_, _) => ExitApplication()));

        _tray.ContextMenu = menu;
    }

    private MenuItem? _pinnedMenuItem;
    private bool _pinnedHooked;

    /// <summary>"Pinned images" submenu, only visible while something is pinned.</summary>
    private MenuItem BuildPinnedMenuItem()
    {
        var pinned = _services!.GetRequiredService<PinnedImageService>();
        var item = CreateMenuItem(Loc.TrayPinnedImages, "\uE718", (_, _) => { });
        item.Items.Add(CreateMenuItem(Loc.TrayPinnedCloseAll, "\uE711", (_, _) => pinned.CloseAll()));
        item.Items.Add(CreateMenuItem(Loc.TrayPinnedStopClickThrough, "\uE7C9", (_, _) => pinned.StopClickThrough()));
        item.Visibility = pinned.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _pinnedMenuItem = item;

        // hooked once: the menu is rebuilt on language changes, the service is not
        if (!_pinnedHooked)
        {
            _pinnedHooked = true;
            pinned.Changed += () => Dispatcher.BeginInvoke(() =>
            {
                if (_pinnedMenuItem is not null)
                    _pinnedMenuItem.Visibility = pinned.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            });
        }
        return item;
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
    /// Cria a janela de Configuracoes sob demanda. O shell so monta a si
    /// mesmo e a pagina ativa (as outras nascem na primeira navegacao),
    /// entao o custo e uma fracao da tela monolitica anterior.
    /// </summary>
    private void ShowMainWindow()
    {
        if (_services is null || IsExiting)
            return;
        // sai do Eco antes de montar/mostrar (senao o layout roda em E-core)
        PowerEfficiency.EnterProcessHighQos();

        if (_settingsWindow is null)
        {
            _settingsWindow = _services.GetRequiredService<SettingsShell>();
            _settingsWindow.IsVisibleChanged += (_, _) => ApplyIdleQosIfNoWindowVisible();
            StartupLog.Write("Configuracoes: janela criada sob demanda");
        }

        _settingsWindow.RefreshStatus();
        _settingsWindow.Show();
        if (_settingsWindow.WindowState == WindowState.Minimized)
            _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    private void NotifyHotkeyConflict(HotkeyGesture gesture)
    {
        _tray?.ShowNotification("Klip", string.Format(Loc.NotifyHotkeyConflict, gesture));
    }

    /// <summary>
    /// Warm-ups that used to sit in the startup path, now at idle: the flyout first
    /// (first Win+V), then the LL hook thread, then a few seconds later the capture
    /// path (GDI/WIC/JIT cost ~300 ms on the first Win+Shift+S after boot).
    /// </summary>
    private void WarmUpAtIdle()
    {
        // warm up the flyout at idle so the first Win+V opens instantly (pre-pays
        // the first layout and the Mica composition without the user seeing it)
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _flyout?.WarmUp();
            try
            {
                LowLevelHookHost.Shared.Start();
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("HookHostStart", ex);
            }
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        var captureWarm = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(5),
        };
        captureWarm.Tick += (_, _) =>
        {
            captureWarm.Stop(); // one shot
            if (!IsExiting)
                _captureController?.WarmUp();
        };
        captureWarm.Start();
    }

    /// <summary>
    /// First hook scope opened / last one closed. The watchdog and the fullscreen
    /// poller only make sense while a hook is installed; at idle both are stopped.
    /// </summary>
    private void OnHookScopeActiveChanged(bool active)
    {
        if (IsExiting)
            return;
        if (active)
        {
            _uiWatchdog?.Start();
            _activityMonitor?.Start();
            return;
        }
        if (!StartupLog.VerboseEnabled)
            _uiWatchdog?.Stop();
        _activityMonitor?.Stop();
    }

    private void PurgePendingSecrets()
    {
        try
        {
            var ingest = _services?.GetService<ClipboardIngestService>();
            var wiped = ingest?.Secrets.PurgeAll() ?? 0;
            if (wiped > 0)
                StartupLog.Write($"Segredos: {wiped} apagado(s) ao sair");
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("PurgeSecrets", ex);
        }
    }

    // finalizacao graciosa no encerramento do app - o Sair do tray
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
    /// Shutdown/logoff do Windows nao espera awaits da UI - melhor
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
        // shutdown do Windows nao espera - persiste o pendente agora
        _services?.GetService<SettingsService>()?.Flush();
        PurgePendingSecrets();
        StartupLog.Flush();
    }

    /// <summary>Espera a finalizacao da gravacao sem bloquear o dispatcher.</summary>
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
                var hotkeys = _services?.GetService<HotkeyService>();
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
                    foreach (var id in _optionalHotkeyIds)
                        hotkeys.Unregister(id);
                    _optionalHotkeyIds.Clear();
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

        // rede de seguranca para saidas que nao passaram pelo
        // ExitApplication (ex.: Shutdown() direto) - mesmo melhor esforco
        // do SessionEnding, com o teto de 30 s do fluxo normal
        if (_recordingController?.IsBusy == true && !_recordingFinalizeHandled)
        {
            _recordingFinalizeHandled = true;
            PumpRecordingFinalize(TimeSpan.FromSeconds(30));
        }

        _tray?.Dispose();

        // stop the timers before the container goes away
        LowLevelHookHost.Shared.ScopeActiveChanged -= OnHookScopeActiveChanged;
        _retentionTimer?.Dispose();
        _activityMonitor?.Dispose();
        _uiWatchdog?.Dispose();

        _clipboardMonitor?.Dispose();
        PurgePendingSecrets();
        if (_services is not null)
        {
            // limpa o historico ao sair (mantem os fixados/favoritos)
            try
            {
                var settings = _services.GetService<SettingsService>();
                if (settings?.Current.ClearHistoryOnExit == true)
                {
                    var repo = _services.GetService<ClipboardItemRepository>();
                    repo?.ClearAll();
                    // incremental: gives the pages back without rewriting the whole file on exit
                    repo?.ReclaimSpace();
                }
                // grava o que o debounce ainda nao persistiu
                settings?.Flush();
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("ClearOnExit", ex);
            }

            _services.GetService<PasteQueueService>()?.Dispose();
            _services.GetService<HotkeyService>()?.Dispose();
            _services.GetService<ExplorerPasteService>()?.Dispose();
            // a thread do clipboard vai DEPOIS do monitor, que precisa dela
            // viva para remover o listener na mesma thread que o registrou.
            _services.GetService<ClipboardThread>()?.Dispose();
            _services.GetService<Database>()?.Dispose();
            _services.Dispose();
        }
        // derruba o pump e o worker dos hooks LL
        LowLevelHookHost.Shared.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);

        // por ultimo - qualquer Write depois disso cai no append sincrono
        StartupLog.Flush();
        StartupLog.Shutdown();
    }
}
