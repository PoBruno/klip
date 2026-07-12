using System.Diagnostics;
using System.Threading.Channels;
using Klip.Core.Recording;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Klip.Interop.Recording;

/// <summary>
/// Gravador MP4 (H.264 + AAC) da spec F3 + motor v2 (spec M2): consome frames
/// GPU do <see cref="FrameCaptureEngine"/> (CFR, crop na GPU), copia cada um
/// para um sample do IMFVideoSampleAllocatorEx (RF-M2.01, lifetime rastreado
/// pelo MF) e os envia ao IMFSinkWriter com encoder de hardware (RF-F3.08/09);
/// audio WASAPI mixado numa trilha AAC (RF-F3.11/12) com mutes ao vivo por
/// rampa de ganho (RF-M2.09). A escrita e serializada por um gate unico; o
/// muxer injeta silencio quando o mixer para de entregar (RF-M2.03) e
/// reconcilia os timestamps de video com o audio entregue (RF-M2.04).
/// Silence padding e reconciliacao adaptados do OutputManager/
/// PrepareAndRenderFrame do sskodje/ScreenRecorderLib (MIT) - ver
/// THIRD-PARTY-NOTICES.md.
/// </summary>
public sealed class Mp4Recorder : IMp4Recorder
{
    // Fila de video com DropOldest (writer atrasado -> drop, sem bloquear).
    // Com o throttling do SinkWriter desabilitado o WriteSample nao bloqueia;
    // a fila so absorve rajadas do catch-up do engine (frames duplicados).
    private const int VideoQueueCapacity = 8;

    private const int AudioQueueCapacity = 256;

    // RF-F3.15: guarda-corpo de disco
    private const long DiskWarningBytes = 2L * 1024 * 1024 * 1024;
    private const long DiskStopBytes = 500L * 1024 * 1024;
    private static readonly TimeSpan DiskCheckInterval = TimeSpan.FromSeconds(10);

    // ----- muxer de audio (RF-M2.03/04), unidades em frames de 48 kHz -----

    private const int AudioSampleRate = AudioCaptureSource.TargetSampleRate;
    private const int AudioBytesPerFrame = AudioCaptureSource.TargetChannels * sizeof(short);

    // lag NOMINAL do audio atras do video: o jitter buffer do mixer (100 ms).
    // O padding do muxer so age quando o atraso passa DISSO + o limiar de
    // starvation - senao estaria injetando silencio sobre audio que o mixer
    // ainda vai entregar (RF-M2.03).
    private const long AudioNominalLagFrames = AudioMixer.JitterFrames;

    // mixer sem entregar alem DISTO (alem do lag nominal) = starvation real
    // (pump morto/travado); ai o muxer assume com silencio (RF-M2.03).
    // DEVE ficar bem acima da espera legitima do mixer: com o due limitado ao
    // que as fontes entregaram, o head de audio pode ficar ate a tolerancia
    // de gap das fontes (400 ms) atras do relogio, MAIS o atraso de scheduling
    // do proprio pump sob carga - tudo isso e recuperado sem perda. Um limiar
    // curto (150 ms na origem) injetava silencio sobre audio que o mixer
    // AINDA IA entregar e o bloco real chegava sob a regiao ja preenchida ->
    // overlap aparado (audio real sacrificado, pipoco). Starvation REAL so
    // existe com o pump morto, entao pagar ~700 ms de latencia de deteccao
    // nesse cenario catastrofico e barato.
    private const long MuxStarvationThresholdFrames = AudioSampleRate * 600 / 1000;

    // silencio gerado em blocos de ate 200 ms (buffer unico reutilizado)
    private const int MuxSilenceChunkFrames = AudioSampleRate / 5;

    // RF-M2.04: teto absoluto do deslocamento de PTS de video (40 ms) e slew
    // maximo por frame (0,5 ms) - ajuste fino continuo, nunca deforma a grade
    // CFR visivelmente nem quebra a monotonicidade (o passo e << 1/fps)
    private const long MaxAvShiftTicks = TimeSpan.TicksPerMillisecond * 40;
    private const long AvShiftSlewTicksPerFrame = TimeSpan.TicksPerMillisecond / 2;

    private readonly object _lock = new();

    private Mp4RecordingOptions? _options;
    private int _fps = 30;
    private long _frameDurationTicks = TimeSpan.TicksPerSecond / 30;
    private bool _mfAcquired;

    private FrameCaptureEngine? _engine;
    private ID3D11Device? _device;            // do engine (nao possui)
    private ID3D11DeviceContext? _context;    // wrapper proprio (dispose obrigatorio)
    private AudioMixer? _audioMixer;
    private volatile Mp4SinkWriter? _writer;  // criado async no 1o frame (nunca no callback)
    private int _writerInitStarted;
    private bool _hasAudio;

    private Channel<VideoItem>? _videoChannel;
    private Channel<AudioItem>? _audioChannel;
    private Task? _writerTask;
    private Task? _diskGuardTask;
    private Task? _telemetryTask;
    private CancellationTokenSource? _sessionCts;
    private TaskCompletionSource<bool> _writerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SemaphoreSlim _writeGate = new(1, 1);

    // timeline de video (thread unica: loop CFR do engine). O engine emite
    // timestamps SEMPRE na grade n*(1e7/fps) ancorada em relogio real e PODE
    // PULAR indices (sem catch-up); a timeline do arquivo e derivada dos
    // INDICES da grade calculados do timestamp de cada frame - nunca de um
    // contador proprio - para tolerar saltos (bug #8 da auditoria)
    private bool _videoStarted;
    private long _videoIndexOffset;
    private long _lastVideoIndex = -1;
    private volatile bool _realignVideo;

    // contadores da sessao, logados no Trace ao parar + Warning unico se drops
    // > 1% dos frames (RF-M2.12 consolida com a telemetria do encoder)
    private long _videoFramesEmitted;    // frames aceitos e enfileirados
    private long _videoAllocatorDrops;   // RF-M2.02: MF_E_SAMPLEALLOCATOR_EMPTY apos retry
    private long _videoChannelDrops;     // DropOldest na fila de video

    // ----- estado do muxer serializado (SEMPRE sob _writeGate) -----
    private long _audioHeadFrames;              // fim (exclusivo) do audio ja escrito
    private bool _audioTimelineStarted;         // 1o bloco real do mixer ja escrito
    private bool _audioWroteRealSinceLastVideo; // excecao do RF-M2.03
    private bool _muxPaddingActive;             // starvation confirmada: padding continuo
    private long _muxSilenceInsertedFrames;     // RF-M2.03 (diagnostico)
    private long _muxOverlapTrimmedFrames;      // chegada tardia sobreposta ao silencio injetado
    private double _avLagFilteredTicks;         // RF-M2.04: EMA do erro de lag A/V
    private long _videoPtsShiftTicks;           // RF-M2.04: deslocamento corrente de PTS
    private byte[]? _muxSilenceBuffer;

    // ----- telemetria (RF-M2.12) e watchdog (RF-M2.13) -----
    private long _maxEncoderBacklog;
    private long _maxQueuedBytes;
    private long _wgcStallEpisodes;

    // ----- toggles ao vivo (RF-M2.09..11) -----
    private volatile bool _micMuted;
    private volatile bool _systemMuted;
    private volatile bool _cursorCaptureEnabled = true;
    private bool _cursorSetExplicitly; // pedido pre-start sobrepoe options.CaptureCursor

    private readonly Stopwatch _clock = new();
    private readonly Stopwatch _pauseClock = new();
    private TimeSpan _pausedTotal;

    private volatile bool _isRecording;
    private volatile bool _isPaused;
    private volatile bool _stopRequested;
    private bool _failed;
    private bool _diskWarned;
    private Task<Mp4RecordingResult>? _stopTask;
    private bool _disposed;

    private readonly record struct VideoItem(IMFSample Sample, long TimestampTicks);

    private sealed record AudioItem(byte[] Pcm, int ByteCount, long TimestampTicks, long DurationTicks);

    /// <inheritdoc />
    public bool IsRecording => _isRecording;

    /// <inheritdoc />
    public bool IsPaused => _isPaused;

    /// <inheritdoc />
    public TimeSpan Elapsed
    {
        get
        {
            lock (_lock)
            {
                var elapsed = _clock.Elapsed - _pausedTotal - (_isPaused ? _pauseClock.Elapsed : TimeSpan.Zero);
                return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
            }
        }
    }

    /// <inheritdoc />
    public event Action<RecordingFailure>? Failed;

    /// <inheritdoc />
    public event Action<string>? Warning;

    /// <inheritdoc />
    public event Action<Mp4RecordingResult>? AutoStopped;

    // ----- toggles ao vivo (RF-M2.09..11) -----

    /// <inheritdoc />
    public void SetMicrophoneMuted(bool muted)
    {
        // RF-M2.09/11: antes do start vira estado inicial (aplicado ao criar o
        // mixer); durante a gravacao ajusta o alvo do ganho (rampa de ~8 ms)
        _micMuted = muted;
        _audioMixer?.SetMicrophoneMuted(muted);
    }

    /// <inheritdoc />
    public bool IsMicrophoneMuted => _micMuted;

    /// <inheritdoc />
    public void SetSystemAudioMuted(bool muted)
    {
        _systemMuted = muted;
        _audioMixer?.SetSystemAudioMuted(muted);
    }

    /// <inheritdoc />
    public bool IsSystemAudioMuted => _systemMuted;

    /// <inheritdoc />
    public void SetCursorCaptureEnabled(bool enabled)
    {
        // RF-M2.10: ao vivo delega ao engine (re-sync defensivo la); antes do
        // start apenas registra o desejo (vira estado inicial no StartCoreAsync)
        _cursorCaptureEnabled = enabled;
        lock (_lock)
        {
            _cursorSetExplicitly = true;
        }

        try
        {
            _engine?.SetCursorCaptureEnabled(enabled);
        }
        catch (Exception)
        {
            // engine encerrando em paralelo: o proximo start aplica o desejado
        }
    }

    /// <inheritdoc />
    public bool IsCursorCaptureEnabled => _cursorCaptureEnabled;

    /// <inheritdoc />
    public async Task StartAsync(Mp4RecordingOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Fps <= 0)
            throw new ArgumentException("Fps deve ser positivo.", nameof(options));

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_isRecording)
                throw new InvalidOperationException("Gravacao ja em andamento.");
            if (_stopTask is { IsCompleted: false })
                throw new InvalidOperationException("A gravacao anterior ainda esta finalizando.");
            ResetSessionState();
            _isRecording = true; // antes do engine: falhas de startup ja roteiam pelo caminho fatal
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            await StartCoreAsync(options).ConfigureAwait(false);
        }
        catch
        {
            _isRecording = false;
            await CleanupResourcesAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public Task PauseAsync()
    {
        lock (_lock)
        {
            if (!_isRecording || _isPaused)
                return Task.CompletedTask;
            _pauseClock.Restart();
            _isPaused = true;         // frames/audio durante a pausa sao descartados (RF-F3.13)
            _audioMixer?.Pause();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ResumeAsync()
    {
        lock (_lock)
        {
            if (!_isRecording || !_isPaused)
                return Task.CompletedTask;
            _pausedTotal += _pauseClock.Elapsed;
            _pauseClock.Reset();
            _audioMixer?.Resume();
            _realignVideo = true;     // proximo frame recalcula o offset (timeline contigua, RF-F3.13)
            _isPaused = false;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<Mp4RecordingResult> StopAsync()
    {
        lock (_lock)
        {
            if (_stopTask is not null)
                return _stopTask;
            if (!_isRecording)
                throw new InvalidOperationException("Nenhuma gravacao em andamento.");
            _stopTask = Task.Run(() => StopCoreAsync(abort: false));
            return _stopTask;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Task<Mp4RecordingResult>? stopTask;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_stopTask is null && _isRecording)
                _stopTask = Task.Run(() => StopCoreAsync(abort: true));
            stopTask = _stopTask;
        }

        if (stopTask is not null)
        {
            try
            {
                await stopTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // teardown nunca lanca
            }
        }

        await CleanupResourcesAsync().ConfigureAwait(false);
        _writeGate.Dispose();
    }

    // ----- inicializacao -----

    private void ResetSessionState()
    {
        _stopTask = null;
        _failed = false;
        _diskWarned = false;
        _stopRequested = false;
        _isPaused = false;
        _videoStarted = false;
        _realignVideo = false;
        _videoIndexOffset = 0;
        _lastVideoIndex = -1;
        _videoFramesEmitted = 0;
        _videoAllocatorDrops = 0;
        _videoChannelDrops = 0;
        _writerInitStarted = 0;
        _audioHeadFrames = 0;
        _audioTimelineStarted = false;
        _audioWroteRealSinceLastVideo = false;
        _muxPaddingActive = false;
        _muxSilenceInsertedFrames = 0;
        _muxOverlapTrimmedFrames = 0;
        _avLagFilteredTicks = 0;
        _videoPtsShiftTicks = 0;
        _maxEncoderBacklog = 0;
        _maxQueuedBytes = 0;
        _wgcStallEpisodes = 0;
        _pausedTotal = TimeSpan.Zero;
        _clock.Reset();
        _pauseClock.Reset();
        _writerReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async Task StartCoreAsync(Mp4RecordingOptions options)
    {
        _options = options;
        _fps = options.Fps;
        _frameDurationTicks = TimeSpan.TicksPerSecond / _fps;

        MediaFoundationRuntime.Acquire(); // RF-F3.17: lanca com orientacao do Media Feature Pack
        _mfAcquired = true;

        string outputDirectory = Path.GetDirectoryName(Path.GetFullPath(options.OutputPath)) ?? string.Empty;
        if (outputDirectory.Length > 0)
            Directory.CreateDirectory(outputDirectory);

        _videoChannel = Channel.CreateBounded<VideoItem>(
            new BoundedChannelOptions(VideoQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            },
            dropped =>
            {
                // drop devolve o sample ao pool do alocador (RF-M2.01); contabilizado
                dropped.Sample.Dispose();
                Interlocked.Increment(ref _videoChannelDrops);
            });
        _audioChannel = Channel.CreateBounded<AudioItem>(
            new BoundedChannelOptions(AudioQueueCapacity)
            {
                // Regressao dos pipocos: DropOldest descartava blocos de 10 ms
                // quando o consumidor de audio atrasava (cada bloco perdido =
                // descontinuidade audivel). Wait nunca perde audio; o deadlock
                // antigo do stop foi resolvido pela ORDEM do StopCoreAsync
                // (_writerReady + TryComplete dos canais ANTES do
                // mixer.StopAsync) e pelo token da sessao no WriteAsync do
                // pump (aborto destrava na hora).
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
            });

        // RF-F3.11: fontes de audio criadas ANTES do engine para (a) decidir se o
        // arquivo tera trilha AAC e (b) garantir StartClock no 1o frame de video.
        // RF-M2.11: mutes pedidos antes do start viram estado inicial do mixer.
        _audioMixer = AudioMixer.TryCreate(
            options,
            EmitAudioBlockAsync,
            message => Warning?.Invoke(message),
            microphoneMuted: _micMuted,
            systemAudioMuted: _systemMuted);
        _hasAudio = _audioMixer is not null;
        _audioMixer?.Start();

        // RF-M2.10/11: cursor pedido explicitamente antes do start vence as options
        bool captureCursor;
        lock (_lock)
        {
            captureCursor = _cursorSetExplicitly ? _cursorCaptureEnabled : options.CaptureCursor;
        }

        _cursorCaptureEnabled = captureCursor;

        _engine = new FrameCaptureEngine();
        _engine.GpuFrameArrived += OnGpuFrameArrived;
        _engine.Failed += OnEngineFailed;
        _engine.DeviceRecreated += OnDeviceRecreated;

        await _engine.StartAsync(options.Region, new FrameCaptureOptions
        {
            CaptureCursor = captureCursor,
            FixedFps = _fps, // CFR: timestamps sinteticos n/fps (RF-F2.05)
        }).ConfigureAwait(false);

        _device = _engine.Device;
        _context = _device.ImmediateContext;

        _sessionCts = new CancellationTokenSource();
        var token = _sessionCts.Token;
        _writerTask = Task.Run(() => WriterLoopAsync(token));
        _diskGuardTask = Task.Run(() => DiskGuardLoopAsync(options.OutputPath, token));
        _telemetryTask = Task.Run(() => TelemetryLoopAsync(token)); // RF-M2.12/13
    }

    // ----- pipeline de video (callback do loop CFR do engine, thread unica) -----

    private void OnGpuFrameArrived(GpuFrame frame)
    {
        try
        {
            if (!_isRecording || _stopRequested || _isPaused)
            {
                frame.Dispose();
                return;
            }

            var channel = _videoChannel;
            var context = _context;
            if (channel is null || context is null)
            {
                frame.Dispose();
                return;
            }

            var writer = _writer;
            if (writer is null)
            {
                // RF-M2.01: o SinkWriter/alocador nascem FORA do callback (a
                // criacao leva dezenas de ms e bloquearia o loop CFR); frames
                // ate ele ficar pronto sao descartados e o T0 ancora no
                // primeiro frame realmente alocado
                BeginWriterInitialization(frame.Texture);
                frame.Dispose();
                return;
            }

            // bug #8 da auditoria: converte o timestamp do frame para o INDICE
            // da grade n*(1e7/fps) (arredondamento ao mais proximo absorve o
            // truncamento de 1e7/fps); indices podem saltar quando o engine
            // pula frames e a matematica abaixo preserva esses saltos
            long rawTicks = frame.Timestamp.Ticks;
            long rawIndex = (rawTicks * _fps + TimeSpan.TicksPerSecond / 2) / TimeSpan.TicksPerSecond;
            if (!_videoStarted)
            {
                // T0 = 1o frame alocavel: timeline do arquivo comeca em 0 e o
                // relogio do audio ancora no mesmo instante (RF-F3.12)
                _videoStarted = true;
                _realignVideo = false;
                _videoIndexOffset = rawIndex;
                lock (_lock)
                {
                    _clock.Start();
                }

                _audioMixer?.StartClock();
            }
            else if (_realignVideo)
            {
                // RF-F3.13: pos-pausa, o 1o frame novo ocupa o slot seguinte ao
                // ultimo escrito (timeline contigua); os frames seguintes
                // preservam a distancia relativa da grade do engine
                _realignVideo = false;
                _videoIndexOffset = rawIndex - (_lastVideoIndex + 1);
            }

            long outputIndex = rawIndex - _videoIndexOffset;
            if (outputIndex <= _lastVideoIndex)
            {
                frame.Dispose();
                return; // defensivo: timestamps SEMPRE monotonicos (RF-F3.09)
            }

            // RF-M2.01/02: sample do pool do MF; pool esgotado = backpressure
            // do encoder -> retry curto e drop contabilizado, nunca bloquear
            IMFSample? sample = AcquireVideoSample(writer);
            if (sample is null)
            {
                frame.Dispose();
                Interlocked.Increment(ref _videoAllocatorDrops);
                return;
            }

            try
            {
                // copia GPU->GPU (context protegido por ID3D11Multithread) para a
                // textura DO SAMPLE e devolve o frame ao pool do engine
                // imediatamente (RF-F2.04); o MF mantem a textura viva ate o
                // encoder soltar a ultima referencia (RF-M2.01)
                using (ID3D11Texture2D target = writer.GetVideoSampleTexture(sample))
                {
                    context.CopyResource(target, frame.Texture);
                }

                frame.Dispose();

                _lastVideoIndex = outputIndex;
                Interlocked.Increment(ref _videoFramesEmitted);
                long timestampTicks = outputIndex * TimeSpan.TicksPerSecond / _fps;
                if (!channel.Writer.TryWrite(new VideoItem(sample, timestampTicks)))
                {
                    sample.Dispose(); // canal fechado (stop em andamento)
                }

                sample = null; // posse transferida (canal ou dispose acima)
            }
            finally
            {
                sample?.Dispose(); // excecao antes da transferencia: volta ao pool
            }
        }
        catch (Exception ex)
        {
            frame.Dispose();
            HandleFatal("Falha ao processar frame de video.", ex);
        }
    }

    /// <summary>
    /// RF-M2.02: aloca um sample de video com retry curto de ate 1 periodo de
    /// frame. O callback roda na thread dedicada do loop CFR; um periodo de
    /// atraso vira no maximo 1 slot da grade, coberto pelo catch-up do engine.
    /// </summary>
    private IMFSample? AcquireVideoSample(Mp4SinkWriter writer)
    {
        IMFSample? sample = writer.TryAllocateVideoSample();
        if (sample is not null)
            return sample;

        long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / _fps;
        while (Stopwatch.GetTimestamp() < deadline && !_stopRequested)
        {
            Thread.Sleep(1);
            sample = writer.TryAllocateVideoSample();
            if (sample is not null)
                return sample;
        }

        return null;
    }

    /// <summary>
    /// Cria o SinkWriter (e o alocador de samples, RF-M2.01) numa task propria
    /// a partir das dimensoes REAIS do 1o frame (o engine arredonda para PAR e
    /// clampa a regiao ao monitor, RF-F2.03/09). O callback nunca bloqueia.
    /// </summary>
    private void BeginWriterInitialization(ID3D11Texture2D template)
    {
        if (Interlocked.Exchange(ref _writerInitStarted, 1) != 0)
            return;

        var description = template.Description;
        int width = (int)description.Width;
        int height = (int)description.Height;
        var options = _options!;
        var device = _device!;
        bool hasAudio = _hasAudio;
        int fps = _fps;

        _ = Task.Run(() =>
        {
            try
            {
                int bitrateKbps = options.BitrateKbps > 0
                    ? options.BitrateKbps
                    : DefaultBitrateKbps(height);

                var writer = new Mp4SinkWriter(
                    options.OutputPath,
                    device,
                    width,
                    height,
                    fps,
                    bitrateKbps,
                    qualityMode: options.BitrateKbps <= 0, // RF-M2.06: auto = Quality(3)
                    options.FragmentedMp4,
                    hasAudio);

                lock (_lock)
                {
                    if (_stopRequested || _disposed)
                    {
                        writer.Dispose();
                        return;
                    }

                    _writer = writer;
                }

                _writerReady.TrySetResult(true);
            }
            catch (Exception ex)
            {
                HandleFatal("Falha ao inicializar o encoder MP4.", ex);
            }
        });
    }

    private void OnEngineFailed(FrameCaptureError error) =>
        HandleFatal(error.Message, error.Exception);

    private void OnDeviceRecreated() =>
        // v1: device novo invalida o D3D manager do SinkWriter -> finalizar o
        // arquivo (fMP4 preserva o gravado) e reportar como fatal
        HandleFatal("A GPU foi redefinida durante a gravacao; o arquivo foi finalizado ate o ponto gravado.", null);

    // ----- pipeline de audio (task do mixer) -----

    private async ValueTask EmitAudioBlockAsync(byte[] pcm, int byteCount, long timestampTicks, long durationTicks)
    {
        var channel = _audioChannel;
        var token = _sessionCts?.Token ?? CancellationToken.None;
        if (channel is null || _stopRequested)
            return;

        try
        {
            // FullMode.Wait: fila cheia bloqueia o pump do mixer (nunca perde
            // audio); o token da sessao garante que um aborto destrava o
            // WriteAsync pendente e o TryComplete do canal (graceful stop)
            // encerra com ChannelClosedException - ambos tratados abaixo
            await channel.Writer.WriteAsync(new AudioItem(pcm, byteCount, timestampTicks, durationTicks), token)
                .ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            // stop em andamento
        }
        catch (OperationCanceledException)
        {
            // aborto da sessao
        }
    }

    // ----- task de escrita (serializa o SinkWriter) -----

    private async Task WriterLoopAsync(CancellationToken token)
    {
        // Cada consumidor captura a PROPRIA falha (HandleFatal -> StopCore
        // completa os canais e cancela o token, destravando o outro consumidor);
        // um WhenAll com excecao pendente aqui deixaria o outro lado preso.
        var videoTask = Task.Run(() => ConsumeVideoAsync(token), CancellationToken.None);
        var audioTask = Task.Run(() => ConsumeAudioAsync(token), CancellationToken.None);
        await Task.WhenAll(videoTask, audioTask).ConfigureAwait(false);
    }

    private async Task ConsumeVideoAsync(CancellationToken token)
    {
        var reader = _videoChannel!.Reader;
        try
        {
            await foreach (var item in reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                try
                {
                    var writer = _writer;
                    if (writer is null)
                        continue; // impossivel em pratica: samples nascem do writer

                    await _writeGate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        // RF-M2.03: silencio do muxer ANTES do sample de video
                        WritePendingSilencePadding(writer, item.TimestampTicks);

                        // RF-M2.04: PTS reconciliado com o audio entregue
                        long pts = ReconcileVideoTimestamp(item.TimestampTicks);
                        item.Sample.SampleTime = pts;
                        item.Sample.SampleDuration = _frameDurationTicks;
                        writer.WriteVideoSample(item.Sample);
                    }
                    finally
                    {
                        _writeGate.Release();
                    }
                }
                finally
                {
                    // devolve o sample ao pool do alocador quando o pipeline
                    // soltar a ultima referencia (RF-M2.01)
                    item.Sample.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // aborto por falha fatal; samples pendentes drenados no cleanup
        }
        catch (Exception ex)
        {
            HandleFatal("Falha na escrita do video MP4.", ex);
        }
    }

    private async Task ConsumeAudioAsync(CancellationToken token)
    {
        bool writerReady;
        try
        {
            writerReady = await _writerReady.Task.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var reader = _audioChannel!.Reader;
        try
        {
            await foreach (var item in reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                var writer = _writer;
                if (!writerReady || writer is null)
                    continue; // gravacao parou antes do 1o frame: descartar audio pendente

                await _writeGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    WriteAudioReconciled(writer, item);
                }
                finally
                {
                    _writeGate.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // aborto por falha fatal
        }
        catch (Exception ex)
        {
            HandleFatal("Falha na escrita do audio MP4.", ex);
        }
    }

    // ----- muxer: silence padding e reconciliacao A/V (RF-M2.03/04) -----
    // Adaptados do OutputManager::RenderFrame / PrepareAndRenderFrame do
    // sskodje/ScreenRecorderLib (MIT), ajustados ao nosso jitter buffer.

    /// <summary>
    /// RF-M2.03 (D-M2.2): se o mixer parou de entregar (pump morto/travado),
    /// escreve silencio PCM do tamanho exato do intervalo pendente ANTES do
    /// video - sem isso o SinkWriter estrangula o video esperando audio para
    /// intercalar. Excecao da spec: se o bloco anterior tinha audio real, nao
    /// injeta (o mixer esta vivo e vai entregar; o lag nominal do jitter
    /// buffer NAO e starvation). Sempre sob _writeGate.
    /// </summary>
    private void WritePendingSilencePadding(Mp4SinkWriter writer, long videoTimestampTicks)
    {
        if (!_hasAudio)
            return;

        bool hadRealAudio = _audioWroteRealSinceLastVideo;
        _audioWroteRealSinceLastVideo = false;

        if (!_audioTimelineStarted)
            return; // startup: o 1o bloco do mixer ainda vai ancorar a trilha

        if (hadRealAudio)
        {
            _muxPaddingActive = false;
            return;
        }

        long expectedHeadFrames = TicksToAudioFrames(videoTimestampTicks) - AudioNominalLagFrames;
        long gapFrames = expectedHeadFrames - _audioHeadFrames;
        if (gapFrames <= 0)
            return;
        if (!_muxPaddingActive && gapFrames < MuxStarvationThresholdFrames)
            return; // dentro da tolerancia: deixa o mixer entregar

        _muxPaddingActive = true;
        WriteMuxSilence(writer, gapFrames);
    }

    /// <summary>Escreve <paramref name="frames"/> de silencio PCM a partir do head corrente (sob _writeGate).</summary>
    private void WriteMuxSilence(Mp4SinkWriter writer, long frames)
    {
        _muxSilenceBuffer ??= new byte[MuxSilenceChunkFrames * AudioBytesPerFrame];
        while (frames > 0)
        {
            int chunk = (int)Math.Min(frames, MuxSilenceChunkFrames);
            long timestamp = AudioFramesToTicks(_audioHeadFrames);
            long duration = AudioFramesToTicks(_audioHeadFrames + chunk) - timestamp;
            writer.WriteAudioSample(_muxSilenceBuffer, 0, chunk * AudioBytesPerFrame, timestamp, duration);
            _audioHeadFrames += chunk;
            _muxSilenceInsertedFrames += chunk;
            frames -= chunk;
        }
    }

    /// <summary>
    /// Escreve um bloco do mixer reconciliado com o head da trilha (sob
    /// _writeGate): sobreposicao com silencio ja injetado (RF-M2.03) e aparada
    /// do inicio do bloco; gap defensivo e preenchido com silencio (o mixer e
    /// contiguo por construcao - isso nao deve ocorrer).
    /// </summary>
    private void WriteAudioReconciled(Mp4SinkWriter writer, AudioItem item)
    {
        long startFrames = TicksToAudioFrames(item.TimestampTicks);
        int frames = item.ByteCount / AudioBytesPerFrame;
        if (frames <= 0)
            return;

        if (!_audioTimelineStarted)
        {
            _audioTimelineStarted = true;
            _audioHeadFrames = startFrames;
        }

        int skipFrames = 0;
        if (startFrames < _audioHeadFrames)
        {
            // chegada tardia sob a regiao ja preenchida com silencio: o audio
            // real sobreposto e sacrificado (mesma troca do ScreenRecorderLib)
            skipFrames = (int)Math.Min(_audioHeadFrames - startFrames, frames);
            _muxOverlapTrimmedFrames += skipFrames;
            if (skipFrames >= frames)
                return;
        }
        else if (startFrames > _audioHeadFrames)
        {
            WriteMuxSilence(writer, startFrames - _audioHeadFrames);
        }

        int writeFrames = frames - skipFrames;
        long writeStart = startFrames + skipFrames;
        long timestamp = AudioFramesToTicks(writeStart);
        long duration = AudioFramesToTicks(writeStart + writeFrames) - timestamp;
        writer.WriteAudioSample(item.Pcm, skipFrames * AudioBytesPerFrame, writeFrames * AudioBytesPerFrame, timestamp, duration);
        _audioHeadFrames = writeStart + writeFrames;
        _audioWroteRealSinceLastVideo = true;
    }

    /// <summary>
    /// RF-M2.04 (D-M2.3): video escravizado ao audio ENTREGUE - o erro entre o
    /// head da trilha de audio (+ lag nominal do jitter buffer) e a grade CFR
    /// e filtrado por EMA e aplicado como deslocamento de PTS com slew de ate
    /// 0,5 ms/frame (teto 40 ms). A grade CFR continua sendo a base (RF-M2.05)
    /// e o realinhamento pos-pausa por indice fica intacto: este ajuste e um
    /// refino continuo de poucos ms por cima dela, monotonico por construcao
    /// (o passo e muito menor que 1/fps). Sempre sob _writeGate.
    /// </summary>
    private long ReconcileVideoTimestamp(long gridTicks)
    {
        if (!_hasAudio || !_audioTimelineStarted)
            return gridTicks;

        long audioHeadTicks = AudioFramesToTicks(_audioHeadFrames);
        long lagErrorTicks = audioHeadTicks + AudioFramesToTicks(AudioNominalLagFrames) - gridTicks;
        _avLagFilteredTicks = 0.95 * _avLagFilteredTicks + 0.05 * lagErrorTicks;

        long target = (long)Math.Clamp(_avLagFilteredTicks, -MaxAvShiftTicks, MaxAvShiftTicks);
        long step = Math.Clamp(target - _videoPtsShiftTicks, -AvShiftSlewTicksPerFrame, AvShiftSlewTicksPerFrame);
        _videoPtsShiftTicks += step;
        return gridTicks + _videoPtsShiftTicks;
    }

    private static long TicksToAudioFrames(long ticks) =>
        (ticks * AudioSampleRate + TimeSpan.TicksPerSecond / 2) / TimeSpan.TicksPerSecond;

    private static long AudioFramesToTicks(long frames) =>
        frames * TimeSpan.TicksPerSecond / AudioSampleRate;

    // RF-F3.09: preset automatico por resolucao (screen content H.264)
    private static int DefaultBitrateKbps(int height) =>
        height >= 1440 ? 14000 : height >= 1080 ? 8000 : 5000;

    // ----- telemetria (RF-M2.12) e watchdog WGC (RF-M2.13) -----

    private async Task TelemetryLoopAsync(CancellationToken token)
    {
        bool stallLogged = false;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                // RF-M2.12: backlog do encoder e fila em bytes a 1 Hz
                var writer = _writer;
                if (writer is not null)
                {
                    try
                    {
                        SinkWriterStatistics stats;
                        await _writeGate.WaitAsync(token).ConfigureAwait(false);
                        try
                        {
                            stats = writer.GetVideoStatistics();
                        }
                        finally
                        {
                            _writeGate.Release();
                        }

                        long backlog = (long)(stats.NumSamplesReceived - stats.NumSamplesEncoded);
                        if (backlog > _maxEncoderBacklog)
                            _maxEncoderBacklog = backlog;
                        if (stats.ByteCountQueued > _maxQueuedBytes)
                            _maxQueuedBytes = stats.ByteCountQueued;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        // writer finalizando: estatisticas deixam de responder
                    }
                }

                // RF-M2.13: watchdog da sessao WGC (log apenas nesta fase - a
                // tela ESTATICA tambem zera FrameArrived legitimamente)
                var engine = _engine;
                if (engine is not null && _isRecording && !_isPaused)
                {
                    TimeSpan sinceLastFrame = engine.TimeSinceLastWgcFrame;
                    if (sinceLastFrame > TimeSpan.FromSeconds(1))
                    {
                        if (!stallLogged)
                        {
                            stallLogged = true;
                            _wgcStallEpisodes++;
                            Trace.WriteLine(
                                $"Mp4Recorder: watchdog WGC (RF-M2.13) - {sinceLastFrame.TotalSeconds:F1} s sem FrameArrived " +
                                "(tela estatica ou sessao de captura travada).");
                        }
                    }
                    else
                    {
                        stallLogged = false;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ----- guarda-corpo de disco (RF-F3.15) -----

    private async Task DiskGuardLoopAsync(string outputPath, CancellationToken token)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(outputPath));
        if (string.IsNullOrEmpty(root))
            return;

        using var timer = new PeriodicTimer(DiskCheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                long freeBytes;
                try
                {
                    freeBytes = new DriveInfo(root).AvailableFreeSpace;
                }
                catch (Exception)
                {
                    continue; // drive de rede/removivel indisponivel: tenta de novo
                }

                if (freeBytes < DiskStopBytes)
                {
                    // RF-F3.15: < 500 MB -> parada graciosa com arquivo valido
                    Warning?.Invoke("Espaco em disco critico (menos de 500 MB livres): a gravacao foi encerrada e o arquivo preservado.");
                    Task<Mp4RecordingResult>? stopTask = null;
                    lock (_lock)
                    {
                        if (_stopTask is null)
                        {
                            _stopTask = Task.Run(() => StopCoreAsync(abort: false));
                            stopTask = _stopTask;
                        }
                    }

                    // bug #7 da auditoria: parada iniciada pelo PROPRIO recorder
                    // notifica AutoStopped com o resultado apos finalizar o
                    // arquivo. StopAsync explicito (stopTask null aqui) nao
                    // dispara; falhas continuam roteando por Failed. A espera
                    // vai numa task desanexada: StopCoreAsync awaita ESTA task
                    // do guard, aguardar aqui fecharia um ciclo de deadlock.
                    if (stopTask is not null)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var result = await stopTask.ConfigureAwait(false);
                                if (!_failed)
                                    AutoStopped?.Invoke(result);
                            }
                            catch (Exception)
                            {
                                // finalizacao falhou: ja reportado via Warning/Failed
                            }
                        });
                    }

                    return;
                }

                if (freeBytes < DiskWarningBytes && !_diskWarned)
                {
                    _diskWarned = true; // aviso unico
                    Warning?.Invoke("Espaco em disco baixo (menos de 2 GB livres) no drive da gravacao.");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ----- parada e falha -----

    private void HandleFatal(string message, Exception? exception)
    {
        lock (_lock)
        {
            if (_failed || !_isRecording)
                return;
            _failed = true;
            _stopTask ??= Task.Run(() => StopCoreAsync(abort: true));
        }

        // finaliza o arquivo primeiro (fMP4 preserva o gravado) e so entao avisa
        _ = Task.Run(async () =>
        {
            try
            {
                await _stopTask!.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            Failed?.Invoke(new RecordingFailure(message, exception));
        });
    }

    private async Task<Mp4RecordingResult> StopCoreAsync(bool abort)
    {
        _stopRequested = true;
        TimeSpan elapsed;
        lock (_lock)
        {
            if (_isPaused)
            {
                _pausedTotal += _pauseClock.Elapsed;
                _pauseClock.Reset();
                _isPaused = false;
            }

            _clock.Stop();
            elapsed = _clock.Elapsed - _pausedTotal;
            _isRecording = false;
        }

        // 1) destrava consumidores e produtores ANTES de parar as fontes
        //    (bug #2 da auditoria): o pump do mixer pode estar aguardando um
        //    WriteAsync no canal de audio e o consumidor de audio aguarda
        //    _writerReady; resolver os dois primeiro evita deadlock com o
        //    _audioMixer.StopAsync() abaixo
        _writerReady.TrySetResult(false); // se nunca houve frame, libera o consumidor de audio
        if (abort)
            _sessionCts?.Cancel();
        _videoChannel?.Writer.TryComplete();
        _audioChannel?.Writer.TryComplete();

        // 2) fontes param de produzir
        long catchUpDuplicates = 0;
        long lostGridSlots = 0;
        long audioEmittedFrames = 0;
        long audioSilenceFrames = 0;
        long audioDriftDropFrames = 0;
        long audioUnderflowFrames = 0;
        if (_engine is not null)
        {
            try
            {
                await _engine.StopAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            // contadores capturados ANTES do CleanupResourcesAsync anular _engine
            catchUpDuplicates = _engine.CfrCatchUpDuplicates;
            lostGridSlots = _engine.CfrLostGridSlots;
        }

        if (_audioMixer is not null)
        {
            try
            {
                await _audioMixer.StopAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            // contadores capturados ANTES do CleanupResourcesAsync anular _audioMixer
            audioEmittedFrames = _audioMixer.EmittedFrames;
            audioSilenceFrames = _audioMixer.SilenceInsertedFrames;
            audioDriftDropFrames = _audioMixer.DriftDroppedFrames;
            audioUnderflowFrames = _audioMixer.UnderflowZeroFrames;
        }

        // 3) drena a fila (ou aborta) e encerra a task de escrita
        if (_writerTask is not null)
        {
            try
            {
                await _writerTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        _sessionCts?.Cancel();
        if (_diskGuardTask is not null)
        {
            try
            {
                await _diskGuardTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        // RF-M2.12: telemetria encerra ANTES do Finalize (GetStatistics nunca
        // corre em paralelo com o teardown do writer)
        if (_telemetryTask is not null)
        {
            try
            {
                await _telemetryTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        // 4) finaliza o container (RF-F3.16: pode levar segundos)
        int width = 0;
        int height = 0;
        string outputPath = _options?.OutputPath ?? string.Empty;
        if (_writer is not null)
        {
            width = _writer.Width;
            height = _writer.Height;
            try
            {
                var writer = _writer;
                await Task.Run(writer.FinalizeFile).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // finalize falhou: com fMP4 o arquivo ainda reproduz ate o
                // ultimo fragmento (RF-F3.10); reportar sem perder o resultado
                Warning?.Invoke("Falha ao finalizar o MP4; com fMP4 o conteudo gravado permanece reproduzivel.");
            }
        }

        await CleanupResourcesAsync().ConfigureAwait(false);

        ReportVideoDiagnostics(catchUpDuplicates, lostGridSlots);
        ReportAudioDiagnostics(audioEmittedFrames, audioSilenceFrames, audioDriftDropFrames, audioUnderflowFrames);

        long fileSize = 0;
        try
        {
            var info = new FileInfo(outputPath);
            if (info.Exists)
                fileSize = info.Length;
        }
        catch (Exception)
        {
        }

        return new Mp4RecordingResult(outputPath, elapsed, width, height, fileSize);
    }

    /// <summary>
    /// Diagnostico de video consolidado (RF-M2.12): contadores da sessao +
    /// telemetria do encoder no Trace sempre; Warning UMA vez ao parar se os
    /// drops (alocador + fila + slots de grade perdidos) passarem de 1% dos
    /// frames. O contrato do Core (Mp4RecordingResult) e congelado - por isso
    /// log, nao API.
    /// </summary>
    private void ReportVideoDiagnostics(long catchUpDuplicates, long lostGridSlots)
    {
        long emitted = Interlocked.Read(ref _videoFramesEmitted);
        long allocatorDrops = Interlocked.Read(ref _videoAllocatorDrops);
        long channelDrops = Interlocked.Read(ref _videoChannelDrops);
        long totalDrops = allocatorDrops + channelDrops + lostGridSlots;
        long totalSlots = emitted + totalDrops;

        Trace.WriteLine(
            $"Mp4Recorder: frames emitidos={emitted}, duplicados de catch-up={catchUpDuplicates}, " +
            $"drops por alocador={allocatorDrops}, drops por fila={channelDrops}, slots de grade perdidos={lostGridSlots}");
        Trace.WriteLine(
            $"Mp4Recorder: telemetria do encoder (RF-M2.12) - backlog maximo={_maxEncoderBacklog} samples, " +
            $"fila maxima={_maxQueuedBytes} bytes, episodios de watchdog WGC={_wgcStallEpisodes}");

        if (totalSlots > 0 && totalDrops * 100 > totalSlots)
        {
            Warning?.Invoke(
                $"A gravacao descartou {totalDrops} de {totalSlots} frames (mais de 1%): " +
                $"{allocatorDrops} por backpressure do encoder, {channelDrops} por fila de escrita e " +
                $"{lostGridSlots} slots de grade perdidos; o video pode apresentar travadinhas.");
        }
    }

    /// <summary>
    /// Diagnostico dos pipocos de audio: descartes por deriva e zeros de
    /// underflow sao descontinuidades audiveis. Contadores no Trace sempre;
    /// Warning UMA vez ao parar se passarem de 0,5% do audio emitido. Silencio
    /// inserido por gap fica fora do Warning (loopback sem som tocando nao
    /// entrega pacotes - silencio ali e o comportamento correto), mas vai ao
    /// Trace para investigacao.
    /// </summary>
    private void ReportAudioDiagnostics(long emittedFrames, long silenceFrames, long driftDropFrames, long underflowFrames)
    {
        if (!_hasAudio)
            return;

        Trace.WriteLine(
            $"Mp4Recorder: audio frames emitidos={emittedFrames}, silencio inserido={silenceFrames}, " +
            $"descartes por deriva={driftDropFrames}, zeros por underflow={underflowFrames}");
        Trace.WriteLine(
            $"Mp4Recorder: muxer (RF-M2.03/04) - silencio injetado={_muxSilenceInsertedFrames} frames, " +
            $"overlap aparado={_muxOverlapTrimmedFrames} frames, deslocamento final de PTS={_videoPtsShiftTicks / 10000.0:F1} ms");

        long discontinuities = driftDropFrames + underflowFrames;
        if (emittedFrames > 0 && discontinuities * 200 > emittedFrames)
        {
            Warning?.Invoke(
                $"A gravacao descartou {discontinuities} de {emittedFrames} amostras de audio (mais de 0,5%): " +
                $"{driftDropFrames} para realinhar ao relogio (deriva/fim de gap) e {underflowFrames} por underflow do buffer; " +
                "o som pode apresentar estalos.");
        }
    }

    private async Task CleanupResourcesAsync()
    {
        _videoChannel?.Writer.TryComplete();
        _audioChannel?.Writer.TryComplete();
        _sessionCts?.Cancel();

        if (_engine is not null)
        {
            _engine.GpuFrameArrived -= OnGpuFrameArrived;
            _engine.Failed -= OnEngineFailed;
            _engine.DeviceRecreated -= OnDeviceRecreated;
            try
            {
                await _engine.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            _engine = null;
        }

        _audioMixer?.Dispose();
        _audioMixer = null;

        // drena samples ainda na fila (voltam ao pool do alocador) antes de
        // descartar o writer/alocador (RF-M2.01)
        if (_videoChannel is not null)
        {
            while (_videoChannel.Reader.TryRead(out var item))
                item.Sample.Dispose();
        }

        _writer?.Dispose();
        _writer = null;

        _context?.Dispose();
        _context = null;
        _device = null; // posse e do engine

        _sessionCts?.Dispose();
        _sessionCts = null;
        _videoChannel = null;
        _audioChannel = null;
        _writerTask = null;
        _diskGuardTask = null;
        _telemetryTask = null;
        _options = null;
        _muxSilenceBuffer = null;

        lock (_lock)
        {
            // RF-M2.10/11: o proximo start volta a honrar options.CaptureCursor,
            // a menos que a toolbar peca de novo antes do StartAsync
            _cursorSetExplicitly = false;
        }

        if (_mfAcquired)
        {
            _mfAcquired = false;
            MediaFoundationRuntime.Release();
        }
    }
}
