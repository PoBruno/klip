using System.IO;
using System.Windows;
using Klip.App.Windows;
using Klip.Core.Clipboard;
using Klip.Core.Hotkeys;
using Klip.Core.Media.Gif;
using Klip.Core.Recording;
using Klip.Core.Settings;
using Klip.Interop;
using Klip.Interop.Recording;

namespace Klip.App.Services;

/// <summary>Tipo de gravacao pedida no overlay.</summary>
public enum RecordingKind
{
    Gif,
    Mp4,
}

/// <summary>Estados da sessao de gravacao (specs F3/F4).</summary>
public enum RecordingState
{
    Idle,
    PreRecord,   // legado: painel de audio do MP4 (hoje as opcoes vem do submenu do overlay)
    Countdown,   // 3 s sobre a regiao
    Recording,
    Paused,      // so MP4 (RF-F3.13)
    Finalizing,  // Convertendo (GIF) / Finalizando (MP4, RF-F3.16)
}

/// <summary>
/// Orquestra a UX de gravacao: contagem, borda + toolbar (excluidas da
/// captura), hotkey global de parar (RF-F3.05, so durante a sessao), fluxo
/// GIF completo (engine CFR -> GifFramePipeline -> GifFrameBuffer ->
/// GifEncoder -> arquivo -> historico) e fluxo MP4 via IMp4Recorder. As
/// opcoes (FPS/escala do GIF; audio/bitrate do MP4) vem de AppSettings,
/// configuradas no submenu inline da toolbar do overlay (UX submenu de
/// gravacao) - o painel pre-gravacao (RecordingSetupWindow) saiu do fluxo
/// padrao. Uma gravacao por vez. Todos os pontos de entrada rodam na UI
/// thread; callbacks do engine/recorder chegam nas threads deles - apenas os
/// UPDATES DE UI sao remarshalados via Post (o frame GIF e consumido na
/// propria thread de captura e escoado pelo worker do pipeline).
/// </summary>
public sealed class RecordingController(
    SettingsService settings,
    ClipboardIngestService ingest,
    HotkeyService hotkeys,
    Func<IAudioDeviceEnumerator> audioEnumeratorFactory,
    Func<IMp4Recorder> mp4RecorderFactory)
{
    // RF-F4.04: teto de retencao em RAM (~500 MB) antes do spill em disco
    private const long GifMaxRamBytes = 500L * 1024 * 1024;

    // RF-F4.04: teto total (RAM + spill); atingiu -> conclui com o gravado
    private const long GifMaxTotalBytes = 4L * 1024 * 1024 * 1024;

    private RecordingState _state = RecordingState.Idle;
    private RecordingKind _kind;
    private NativeMethods.RECT _monitorBounds;
    private int _stopHotkeyId;

    // janelas da sessao
    private RegionFrameWindow? _border;
    private RecordingToolbarWindow? _toolbar;
    private RecordingProgressWindow? _progress;
    private System.Windows.Threading.DispatcherTimer? _tick;

    // contagem regressiva cancelavel (RequestStop/saida do app)
    private CancellationTokenSource? _countdownCts;

    // GIF
    private FrameCaptureEngine? _gifEngine;
    private GifFrameBuffer? _gifBuffer;
    private GifFramePipeline? _gifPipeline;
    private int _gifDelayMs;
    private int _gifScalePercent;
    private System.Diagnostics.Stopwatch? _gifWatch;
    private volatile bool _gifAccepting;
    private volatile bool _gifLimitHit; // setado na thread do worker do pipeline

    // MP4
    private IMp4Recorder? _recorder;

    // gesto pausar/retomar em curso: ignora novos cliques ate o await concluir
    private bool _pauseBusy;

    // finalizacao em curso (RF-F3.16): permite o shutdown aguardar o StopAsync
    private Task? _stopTask;

    // Bug 2a: avisos nao-fatais do Mp4Recorder (diagnostico de drops, falha de
    // finalize) chegam inclusive DENTRO do StopAsync; um toast imediato
    // aparecia ANTES do toast de conclusao e o clique no balao abria o fMP4
    // ainda em finalizacao (o MediaElement lia so os fragmentos ja flushados).
    // Os avisos sao enfileirados e consolidados como linhas secundarias do
    // toast final (conclusao ou erro), nunca antes dele. Acesso apenas na UI
    // thread (handlers chegam via Post).
    private readonly List<string> _pendingWarnings = [];

    public RecordingState State => _state;

    public bool IsActive => _state != RecordingState.Idle;

    /// <summary>Sessao com conteudo em risco se o app fechar agora (RF-F3.16).</summary>
    public bool IsBusy => _state is RecordingState.Recording or RecordingState.Paused or RecordingState.Finalizing;

    /// <summary>Tempo gravado (exclui pausas no MP4, pelo proprio recorder).</summary>
    public TimeSpan Elapsed => _kind == RecordingKind.Mp4
        ? _recorder?.Elapsed ?? TimeSpan.Zero
        : _gifWatch?.Elapsed ?? TimeSpan.Zero;

    /// <summary>
    /// Toast na bandeja: (mensagem, pasta para abrir no clique ou null,
    /// arquivo para abrir no editor de midia no clique ou null - RF-F5.16).
    /// </summary>
    public event Action<string, string?, string?>? RecordingToast;

    /// <summary>Estado/timer mudou - o tray atualiza tooltip (RF-F3.04).</summary>
    public event Action? StateChanged;

    /// <summary>
    /// Entrada unica a partir do overlay. Sessao unica: se ja ha gravacao,
    /// apenas chama atencao para a toolbar (parar continua pelos controles).
    /// </summary>
    public void Start(RecordingRegion region, RecordingKind kind, NativeMethods.RECT monitorBounds)
    {
        if (IsActive)
        {
            // toolbar e NOACTIVATE (Activate seria no-op): realce visual rapido
            _toolbar?.FlashAttention();
            return;
        }
        if (region.Width < 2 || region.Height < 2)
            return; // regiao minima do motor de captura

        _kind = kind;
        _monitorBounds = monitorBounds;
        // UX submenu de gravacao: MP4 tambem vai direto (selecao -> countdown
        // -> grava); as fontes de audio/bitrate ja foram escolhidas no submenu
        // inline do overlay e persistidas em AppSettings
        if (kind == RecordingKind.Mp4)
            _ = RunMp4Async(region);
        else
            _ = RunGifAsync(region);
    }

    /// <summary>Pedido de parar vindo do hotkey global (RF-F3.05) ou do tray (RF-F3.04).</summary>
    public void RequestStop()
    {
        switch (_state)
        {
            case RecordingState.Recording or RecordingState.Paused:
                Observe(StopAsync());
                break;
            case RecordingState.Countdown:
                // contagem cancelavel: fecha a janela e volta a Idle
                _countdownCts?.Cancel();
                break;
        }
    }

    // ----- MP4 (spec F3) -----

    // Fluxo antigo (fallback): a etapa RecordingSetupWindow (painel de audio
    // pre-gravacao, RF-F3.02) foi substituida pelo submenu inline do overlay
    // (UX submenu de gravacao). A classe RecordingSetupWindow permanece no
    // repo; para reativar o painel, roteie Start() para um ShowSetupAsync que
    // enumere microfones fora da UI thread e chame RunMp4Async no Iniciar.

    private async Task RunMp4Async(RecordingRegion region)
    {
        try
        {
            // UX submenu de gravacao: valida os microfones salvos contra os
            // dispositivos ativos (id ausente e ignorado); enumeracao WASAPI
            // pode bloquear - roda fora da UI thread, em paralelo com a
            // contagem. Falha e engolida aqui dentro (segue sem microfones e
            // nada fica como excecao nao-observada se a contagem cancelar).
            var savedMicIds = settings.Current.Mp4MicrophoneIds;
            var micTask = savedMicIds.Count == 0
                ? Task.FromResult<IReadOnlyList<string>>([])
                : Task.Run<IReadOnlyList<string>>(() =>
                {
                    try
                    {
                        var active = audioEnumeratorFactory().GetActiveMicrophones();
                        return savedMicIds.Where(id => active.Any(m => m.Id == id)).ToList();
                    }
                    catch (Exception ex)
                    {
                        // enumeracao WASAPI indisponivel: segue sem microfones
                        StartupLog.WriteException("RecordingAudioEnum", ex);
                        return [];
                    }
                });

            if (!await RunCountdownAsync(region))
                return; // contagem cancelada (RequestStop/saida do app)

            var micIds = await micTask;
            var systemAudio = settings.Current.Mp4CaptureSystemAudio;

            var folder = RecordingPaths.Resolve(settings.Current.RecordingsFolder);
            Directory.CreateDirectory(folder); // RF-F3.06: criada sob demanda
            var path = RecordingPaths.BuildOutputPath(folder, DateTime.Now, "mp4");

            var recorder = mp4RecorderFactory();
            _recorder = recorder;
            _pendingWarnings.Clear(); // aviso de sessao anterior nunca vaza
            // RF-F3.14/RF-F3.15 + Bug 2a: avisos nao-fatais NAO viram toast
            // imediato - ficam na fila e saem consolidados no toast final
            recorder.Warning += message => Post(() => _pendingWarnings.Add(message));
            recorder.Failed += failure => Post(() => OnRecorderFailed(failure));
            // RF-F3.15: parada espontanea com arquivo valido (ex.: disco cheio)
            // conclui a sessao como um stop manual - sem prender a maquina de estados
            recorder.AutoStopped += result => Post(() => OnRecorderAutoStopped(result));

            await recorder.StartAsync(new Mp4RecordingOptions
            {
                Region = region,
                OutputPath = path,
                Fps = 30,
                BitrateKbps = settings.Current.Mp4BitrateKbps, // 0 = preset automatico (RF-F3.09)
                FragmentedMp4 = true,                          // RF-F3.10
                CaptureCursor = true,
                CaptureSystemAudio = systemAudio,
                MicrophoneDeviceIds = micIds,
            });

            BeginRecordingUi(region,
                canPause: true,
                showMicIndicator: micIds.Count > 0,
                showSystemIndicator: systemAudio);
            SetState(RecordingState.Recording);
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("RecordingMp4Start", ex);
            RecordingToast?.Invoke(ComposeWithWarnings(
                string.Format(Localization.Loc.NotifyRecordingFailed, ex.Message)), null, null);
            await DisposeRecorderAsync();
            TeardownRecordingUi();
            SetState(RecordingState.Idle);
        }
    }

    /// <summary>Falha fatal do recorder: encerra graciosamente (arquivo pode existir parcial com fMP4).</summary>
    private void OnRecorderFailed(RecordingFailure failure)
    {
        StartupLog.Write($"Gravacao MP4 falhou: {failure.Message}");
        if (_state is RecordingState.Recording or RecordingState.Paused)
        {
            RecordingToast?.Invoke(
                string.Format(Localization.Loc.NotifyRecordingFailed, failure.Message), null, null);
            Observe(StopAsync());
        }
    }

    /// <summary>
    /// O recorder parou por conta propria com arquivo valido (ex.: guarda-corpo
    /// de disco, RF-F3.15): mesmo fluxo de conclusao do stop manual (fecha
    /// borda/toolbar, desregistra hotkey, ingest + toast), sem chamar
    /// StopAsync do recorder de novo. Idempotente com o stop manual: se a
    /// finalizacao ja comecou (Finalizing), nao finaliza duas vezes.
    /// </summary>
    private void OnRecorderAutoStopped(Mp4RecordingResult result)
    {
        if (_state is not (RecordingState.Recording or RecordingState.Paused))
            return;
        _stopTask = AutoStopCoreAsync(result);
        Observe(_stopTask);
    }

    private async Task AutoStopCoreAsync(Mp4RecordingResult result)
    {
        SetState(RecordingState.Finalizing);
        try
        {
            TeardownRecordingUi();
            IngestAndToast(result.OutputPath);
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("RecordingAutoStop", ex);
            RecordingToast?.Invoke(ComposeWithWarnings(
                string.Format(Localization.Loc.NotifyRecordingFailed, ex.Message)), null, null);
        }
        finally
        {
            await DisposeRecorderAsync();
            _stopTask = null;
            SetState(RecordingState.Idle);
        }
    }

    private async void OnPauseToggled()
    {
        // gesto em curso: cliques repetidos durante o await nao reentram
        if (_pauseBusy || _recorder is not { } recorder)
            return;
        _pauseBusy = true;
        try
        {
            if (_state == RecordingState.Recording)
            {
                await recorder.PauseAsync();
                if (_state == RecordingState.Recording) // stop pode ter comecado no await
                {
                    SetState(RecordingState.Paused);
                    _toolbar?.SetPaused(true);
                }
            }
            else if (_state == RecordingState.Paused)
            {
                await recorder.ResumeAsync();
                if (_state == RecordingState.Paused)
                {
                    SetState(RecordingState.Recording);
                    _toolbar?.SetPaused(false);
                }
            }
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("RecordingPause", ex);
        }
        finally
        {
            _pauseBusy = false;
        }
    }

    // ----- GIF (spec F4) -----

    private async Task RunGifAsync(RecordingRegion region)
    {
        try
        {
            if (!await RunCountdownAsync(region))
                return; // contagem cancelada (RequestStop/saida do app)

            var fps = settings.Current.GifFps is 10 or 15 or 20 ? settings.Current.GifFps : 15;
            _gifDelayMs = GifRecordingMath.FrameDelayMs(fps); // Q-F4.2: delay fixo, FPS efetivo na UI
            _gifScalePercent = settings.Current.GifScalePercent is 50 or 75 ? settings.Current.GifScalePercent : 100;
            _gifLimitHit = false;

            var spillDir = Path.Combine(Path.GetTempPath(), "Klip", "gif-spill");
            _gifBuffer = new GifFrameBuffer(GifMaxRamBytes, spillDir);

            // pipeline pooled: o handler do frame so copia/enfileira; o worker
            // faz dedupe/spill e calcula o delay real pelo timestamp CFR -
            // evita alocacao LOH por frame e pausas de GC no app (RF-F4.04)
            var pipeline = new GifFramePipeline(_gifBuffer, _gifDelayMs, _gifScalePercent);
            pipeline.FrameRetained += OnGifFrameRetained;
            _gifPipeline = pipeline;

            var engine = new FrameCaptureEngine();
            _gifEngine = engine;
            engine.CpuFrameArrived += OnGifFrame;
            engine.Failed += error => Post(() => OnGifEngineFailed(error));

            _gifAccepting = true;
            await engine.StartAsync(region, new FrameCaptureOptions
            {
                FixedFps = fps, // D-F4.4: GIF forca CFR
                CaptureCursor = true,
            });

            _gifWatch = System.Diagnostics.Stopwatch.StartNew();
            BeginRecordingUi(region, canPause: false, showMicIndicator: false, showSystemIndicator: false);
            SetState(RecordingState.Recording);
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("RecordingGifStart", ex);
            RecordingToast?.Invoke(
                string.Format(Localization.Loc.NotifyRecordingFailed, ex.Message), null, null);
            _gifAccepting = false;
            await DisposeGifCaptureAsync();
            await DisposeGifPipelineAsync();
            _gifBuffer?.Dispose();
            _gifBuffer = null;
            TeardownRecordingUi();
            SetState(RecordingState.Idle);
        }
    }

    /// <summary>
    /// Chega na thread do loop CFR do engine (sequencial). frame.Bgra e
    /// REUTILIZADO pelo engine - so vale durante o handler: o pipeline copia
    /// (com downscale RF-F4.03) para um buffer pooled ainda aqui e o resto
    /// (dedupe, spill, retencao) roda no worker dedicado.
    /// </summary>
    private void OnGifFrame(CpuFrame frame)
    {
        if (!_gifAccepting || _gifPipeline is not { } pipeline)
            return;
        try
        {
            pipeline.Post(frame.Bgra, frame.Width, frame.Height, frame.Timestamp);
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("RecordingGifFrame", ex);
        }
    }

    /// <summary>
    /// Thread do worker do pipeline. RF-F4.04: teto total atingido -> conclui
    /// automaticamente com o gravado (mesmo padrao do scroll capture: nunca
    /// descartar conteudo).
    /// </summary>
    private void OnGifFrameRetained(long totalBytes)
    {
        if (totalBytes >= GifMaxTotalBytes && !_gifLimitHit)
        {
            _gifLimitHit = true;
            _gifAccepting = false;
            Post(() => Observe(StopAsync()));
        }
    }

    /// <summary>RF-F2.08: falha do motor conclui com o que foi retido (nunca descarta).</summary>
    private void OnGifEngineFailed(FrameCaptureError error)
    {
        StartupLog.Write($"Gravacao GIF: engine falhou ({error.Kind}): {error.Message}");
        if (_state == RecordingState.Recording)
        {
            RecordingToast?.Invoke(
                string.Format(Localization.Loc.NotifyRecordingFailed, error.Message), null, null);
            Observe(StopAsync());
        }
    }

    // ----- parada e finalizacao -----

    /// <summary>
    /// RF-F3.16: finalizacao graciosa no encerramento do app. Para a sessao
    /// ativa (ou adota a finalizacao ja em curso) e aguarda: MP4 com teto de
    /// seguranca (o fMP4 preserva o que ja foi fragmentado se estourar), GIF
    /// ate o fim do encode (unico jeito de nao perder o buffer). Chamar na
    /// UI thread; o dispatcher precisa continuar bombeando durante o await.
    /// </summary>
    public async Task StopAndFinalizeAsync(TimeSpan mp4Timeout)
    {
        var finalize = _state switch
        {
            RecordingState.Recording or RecordingState.Paused => StopAsync(),
            RecordingState.Finalizing => _stopTask ?? Task.CompletedTask,
            _ => Task.CompletedTask,
        };

        if (_kind == RecordingKind.Mp4)
            await Task.WhenAny(finalize, Task.Delay(mp4Timeout));
        else
            await finalize;
    }

    private Task StopAsync()
    {
        if (_state is not (RecordingState.Recording or RecordingState.Paused))
            return _stopTask ?? Task.CompletedTask;
        _stopTask = StopCoreAsync();
        return _stopTask;
    }

    private async Task StopCoreAsync()
    {
        SetState(RecordingState.Finalizing);
        _gifAccepting = false;

        try
        {
            TeardownRecordingUi();

            // dentro do try: se o ctor/Show falhar, o finally reseta o estado
            _progress = new RecordingProgressWindow(_kind == RecordingKind.Gif
                ? Localization.Loc.RecordingConverting
                : Localization.Loc.RecordingFinalizing); // RF-F3.16
            _progress.Show();

            if (_kind == RecordingKind.Mp4)
                await FinishMp4Async();
            else
                await FinishGifAsync();
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("RecordingFinish", ex);
            // Bug 2b: finalize falhou -> toast de ERRO unico (com os avisos
            // pendentes consolidados), nunca um toast de sucesso em paralelo
            RecordingToast?.Invoke(ComposeWithWarnings(
                string.Format(Localization.Loc.NotifyRecordingFailed, ex.Message)), null, null);
        }
        finally
        {
            _progress?.Close();
            _progress = null;
            _gifWatch = null;
            _stopTask = null;
            SetState(RecordingState.Idle);
        }
    }

    private async Task FinishMp4Async()
    {
        if (_recorder is not { } recorder)
            return;
        try
        {
            var result = await recorder.StopAsync();
            IngestAndToast(result.OutputPath);
        }
        finally
        {
            await DisposeRecorderAsync();
        }
    }

    private async Task FinishGifAsync()
    {
        await DisposeGifCaptureAsync(); // garante que o loop CFR terminou (nao chegam mais frames)

        // drena o worker do pipeline ANTES de tocar no buffer: frames ainda na
        // fila viram retencao; falha de ingestao nao descarta o ja retido
        var pipeline = _gifPipeline;
        _gifPipeline = null;
        if (pipeline is not null)
        {
            try
            {
                await pipeline.CompleteAsync();
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("RecordingGifPipeline", ex);
            }
        }

        var buffer = _gifBuffer;
        _gifBuffer = null;
        if (buffer is null)
            return;

        try
        {
            if (buffer.Count == 0)
                return;

            var folder = RecordingPaths.Resolve(settings.Current.RecordingsFolder);
            Directory.CreateDirectory(folder); // RF-F3.06 (vale para GIF tambem)
            var path = RecordingPaths.BuildOutputPath(folder, DateTime.Now, "gif");

            // encode two-pass fora da UI thread (RF-F4.07/08)
            await Task.Run(() =>
            {
                var frames = buffer.Snapshot();
                using var stream = File.Create(path);
                new GifEncoder().Encode(stream, frames, new GifEncodeOptions());
            });

            IngestAndToast(path);
            if (_gifLimitHit)
                RecordingToast?.Invoke(Localization.Loc.NotifyRecordingLimit, null, null);
        }
        finally
        {
            buffer.Dispose(); // apaga o spill em %TEMP%
        }
    }

    /// <summary>
    /// RF-F3.07/RF-F5.15/16: item no historico apontando para o arquivo + toast.
    /// MP4: o clique no toast abre o editor de midia (em vez da pasta).
    /// GIF: alem do toast, abre o editor de midia automaticamente (RF-F4.01).
    /// Bug 2b/2c: este metodo so roda APOS o StopAsync do recorder retornar
    /// (arquivo finalizado em disco), entao o toast de conclusao - o UNICO
    /// clicavel para abrir no editor - nunca aponta para um fMP4 em
    /// finalizacao. A guarda contra toast duplicado e a checagem de estado:
    /// OnRecorderAutoStopped ignora se ja esta Finalizing (stop manual em
    /// curso) e StopAsync retorna o _stopTask existente - um unico caminho de
    /// conclusao por sessao. A ordem dos toasts (conclusao primeiro, avisos
    /// consolidados nele) e a garantia documentada de que nenhum clique abre
    /// o arquivo antes da finalizacao (Q resolvida por ordem, sem gate extra
    /// no MediaEditorGateway).
    /// </summary>
    private void IngestAndToast(string path)
    {
        Core.Storage.ClipboardItem? item = null;
        try
        {
            item = ingest.Ingest(new ClipboardSnapshot
            {
                Files = [path],
                SourceApp = "Klip",
                SourceTitle = Localization.Loc.RecordingItemTitle,
                Origin = Core.Storage.ClipboardItemOrigin.Recording,
            });
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("RecordingIngest", ex);
        }

        StartupLog.Write($"Gravacao salva: {path}, item {item?.Id}");
        var isGif = path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
        if (isGif)
        {
            RecordingToast?.Invoke(ComposeWithWarnings(
                string.Format(Localization.Loc.NotifyRecordingSaved, Path.GetFileName(path)) +
                ". " + Localization.Loc.NotifyRecordingOpenFolder),
                Path.GetDirectoryName(path), null);
            // RF-F4.01: GIF recem-convertido abre direto no editor de midia
            MediaEditorGateway.Open(path);
        }
        else
        {
            RecordingToast?.Invoke(ComposeWithWarnings(
                string.Format(Localization.Loc.NotifyRecordingSaved, Path.GetFileName(path)) +
                ". " + Localization.Loc.NotifyOpenInMediaEditor),
                null, path);
        }
    }

    /// <summary>
    /// Bug 2a: anexa os avisos enfileirados como linhas secundarias da
    /// mensagem final e limpa a fila - um unico toast por conclusao, com o
    /// aviso DEPOIS (nunca antes) da informacao de conclusao/erro.
    /// </summary>
    private string ComposeWithWarnings(string message)
    {
        if (_pendingWarnings.Count == 0)
            return message;
        var composed = message + Environment.NewLine +
            string.Join(Environment.NewLine, _pendingWarnings);
        _pendingWarnings.Clear();
        return composed;
    }

    // ----- UI da sessao (borda, toolbar, hotkey, timer) -----

    private void BeginRecordingUi(RecordingRegion region, bool canPause, bool showMicIndicator, bool showSystemIndicator)
    {
        RegisterStopHotkey();

        // RF-F3.04/RF-T2.08: modo reuniao (so MP4) - nada visivel; tray indica e para
        var hidden = _kind == RecordingKind.Mp4 && settings.Current.HideRecordingBorder;
        if (!hidden)
        {
            // reuso do frame do scroll capture: click-through, NOACTIVATE,
            // excluido da captura, borda accent FORA da area gravada
            _border = new RegionFrameWindow();
            _border.ShowAround(region.Left, region.Top, region.Width, region.Height);

            // RF-T2.05: estado inicial dos toggles = opcoes da sessao (cursor
            // sempre inicia capturado - CaptureCursor: true nos dois fluxos;
            // mic/sistema iniciam ativos; borda inicia visivel)
            _toolbar = new RecordingToolbarWindow(new RecordingToolbarOptions
            {
                CanPause = canPause,
                ShowMicToggle = showMicIndicator,
                ShowSystemToggle = showSystemIndicator,
                CursorCaptureEnabled = true,
                BorderVisible = true,
            });
            _toolbar.StopRequested += () => Observe(StopAsync());
            _toolbar.PauseToggled += OnPauseToggled;
            // RF-T2.05: toggles ao vivo - mic/sistema no recorder (ganho no
            // mixer, captura nunca para); cursor no recorder (MP4) ou no
            // engine CFR da sessao GIF; borda = Hide/Show da moldura (nunca
            // Close durante a sessao, RF-T2.04)
            _toolbar.MicMuteToggled += muted => _recorder?.SetMicrophoneMuted(muted);
            _toolbar.SystemMuteToggled += muted => _recorder?.SetSystemAudioMuted(muted);
            _toolbar.CursorToggled += OnCursorToggled;
            _toolbar.BorderToggled += OnBorderToggled;
            // RF-T2.02: fim do arraste -> persiste px fisicos ("x,y,w,h")
            _toolbar.PositionChanged += rect => settings.Update(s =>
                s.RecordingToolbarPosition = RecordingToolbarWindow.FormatPosition(rect));
            _toolbar.ShowOutside(region, _monitorBounds, settings.Current.RecordingToolbarPosition);
        }

        _tick = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _tick.Tick += (_, _) =>
        {
            _toolbar?.UpdateElapsed(Elapsed);
            StateChanged?.Invoke(); // tray atualiza o tooltip com o timer
        };
        _tick.Start();
    }

    /// <summary>RF-T2.05/RF-M2.10: cursor ao vivo - MP4 via recorder, GIF via engine da sessao.</summary>
    private void OnCursorToggled(bool visible)
    {
        if (_kind == RecordingKind.Mp4)
            _recorder?.SetCursorCaptureEnabled(visible);
        else
            _gifEngine?.SetCursorCaptureEnabled(visible);
    }

    /// <summary>RF-T2.05: moldura da regiao via Hide/Show - a afinidade WDA persiste no HWND (RF-T2.04).</summary>
    private void OnBorderToggled(bool visible)
    {
        if (_border is not { } border)
            return;
        if (visible)
            border.Show();
        else
            border.Hide();
    }

    private void TeardownRecordingUi()
    {
        UnregisterStopHotkey();
        _tick?.Stop();
        _tick = null;
        _toolbar?.Close();
        _toolbar = null;
        _border?.Close();
        _border = null;
    }

    /// <summary>RF-F3.05: hotkey de parar registrado SO durante a gravacao.</summary>
    private void RegisterStopHotkey()
    {
        if (_stopHotkeyId != 0)
            return;
        if (HotkeyGesture.TryParse(settings.Current.StopRecordingHotkey, out var gesture) &&
            hotkeys.TryRegister(gesture, RequestStop, out var id))
        {
            _stopHotkeyId = id;
        }
        // conflito: segue sem hotkey - toolbar/tray continuam parando
    }

    private void UnregisterStopHotkey()
    {
        if (_stopHotkeyId == 0)
            return;
        hotkeys.Unregister(_stopHotkeyId);
        _stopHotkeyId = 0;
    }

    // ----- infra -----

    /// <summary>
    /// Contagem de 3 s cancelavel via RequestStop/saida do app. Retorna false
    /// (e volta a Idle) se foi cancelada - o chamador nao liga engine/recorder.
    /// </summary>
    private async Task<bool> RunCountdownAsync(RecordingRegion region)
    {
        SetState(RecordingState.Countdown);
        var cts = new CancellationTokenSource();
        _countdownCts = cts;
        try
        {
            await RecordingCountdownWindow.RunAsync(region, cts.Token);
        }
        catch (OperationCanceledException)
        {
            SetState(RecordingState.Idle);
            return false;
        }
        finally
        {
            _countdownCts = null;
        }

        if (cts.IsCancellationRequested)
        {
            // cancelado na fresta entre o fim da contagem e a continuation
            SetState(RecordingState.Idle);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Observa rotas fire-and-forget da parada: falha nao tratada e logada e o
    /// estado volta a Idle (senao a sessao ficaria presa em Finalizing com a
    /// janela de progresso aberta).
    /// </summary>
    private void Observe(Task task) =>
        task.ContinueWith(
            t => Post(() =>
            {
                StartupLog.WriteException("RecordingStopUnobserved", t.Exception!.GetBaseException());
                try
                {
                    TeardownRecordingUi();
                    _progress?.Close();
                }
                catch (Exception ex)
                {
                    StartupLog.WriteException("RecordingStopReset", ex);
                }
                _progress = null;
                _stopTask = null;
                SetState(RecordingState.Idle);
            }),
            TaskContinuationOptions.OnlyOnFaulted);

    private async Task DisposeGifCaptureAsync()
    {
        var engine = _gifEngine;
        _gifEngine = null;
        if (engine is null)
            return;
        try
        {
            await engine.DisposeAsync(); // inclui StopAsync
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("RecordingGifEngineDispose", ex);
        }
    }

    private async Task DisposeGifPipelineAsync()
    {
        var pipeline = _gifPipeline;
        _gifPipeline = null;
        if (pipeline is null)
            return;
        try
        {
            await pipeline.DisposeAsync(); // devolve buffers pendentes ao pool
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("RecordingGifPipelineDispose", ex);
        }
    }

    private async Task DisposeRecorderAsync()
    {
        var recorder = _recorder;
        _recorder = null;
        if (recorder is null)
            return;
        try
        {
            await recorder.DisposeAsync();
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("RecordingRecorderDispose", ex);
        }
    }

    private void SetState(RecordingState state)
    {
        _state = state;
        StateChanged?.Invoke();
    }

    private static void Post(Action action) =>
        Application.Current?.Dispatcher.BeginInvoke(action);
}
