using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Klip.Core.Recording;

namespace Klip.Interop.Recording;

/// <summary>
/// Mixa as fontes WASAPI (loopback + N microfones) numa unica timeline PCM
/// 16-bit 48 kHz estereo (RF-F3.11). O relogio-mestre e o tempo ativo da
/// gravacao (ancorado no 1o frame de video, excluindo pausas); os timestamps
/// dos blocos emitidos sao derivados da CONTAGEM de samples ja emitidos -
/// monotonicos por construcao (RF-F3.12/13).
/// </summary>
internal sealed class AudioMixer : IDisposable
{
    private const int TickMilliseconds = 10;

    // jitter buffer: o pump emite ate (relogio - 100 ms), absorvendo a cadencia
    // de entrega do WASAPI sem furar blocos (RF-F3.12). Atrasos de 10-30 ms
    // sao NORMAIS no WASAPI event-driven; alem disso o due e limitado ao que
    // as fontes REALMENTE entregaram (ver PumpLoopAsync) - atraso maior vira
    // espera, nunca zeros. 100 ms de latencia e irrelevante em gravacao para
    // arquivo (nao e ao vivo). Compartilhado com o RealignHeadroomFrames do
    // AudioCaptureSource (devem andar juntos).
    internal const int JitterFrames = AudioCaptureSource.TargetSampleRate / 10;

    // teto de recuperacao por tick (1 s) - limita rajadas apos hiccup de scheduling
    private const int MaxBurstFrames = AudioCaptureSource.TargetSampleRate;

    // buffer do cliente WASAPI: o padrao de 100 ms do NAudio fazia o ENGINE
    // descartar pacotes DE VERDADE quando a thread de captura ficava sem CPU
    // por mais que isso (GC/carga) - perda irrecuperavel que nenhum buffering
    // a jusante resolve. 250 ms cobre stalls tipicos; nao subir alem disso no
    // loopback: em polling o NAudio entrega a cada ~buffer/2, e essa cadencia
    // precisa ficar folgada sob o GapToleranceFrames da fonte (400 ms).
    private const int CaptureBufferMilliseconds = 250;

    // RF-M2.09 (D-M2.5): rampa linear de ~8 ms ao alternar mute 0<->1 - a
    // captura nunca para, so o ganho muda, sem click nem descontinuidade
    private const int MuteRampFrames = AudioCaptureSource.TargetSampleRate * 8 / 1000;

    private readonly List<AudioCaptureSource> _sources;
    private readonly Func<byte[], int, long, long, ValueTask> _emitBlock;
    private readonly Action<string> _onWarning;
    private readonly Stopwatch _clock = new();
    private readonly Stopwatch _pauseClock = new();
    private readonly float[] _mix = new float[MaxBurstFrames * AudioCaptureSource.TargetChannels];

    // RF-M2.09: ganho POR FONTE (loopback vs mics) com rampa; targets sao
    // escritos de qualquer thread (Set*Muted), current/blocos so pelo pump
    private readonly float[] _systemGainBlock = new float[MaxBurstFrames];
    private readonly float[] _microphoneGainBlock = new float[MaxBurstFrames];
    private float _systemGainCurrent;
    private float _microphoneGainCurrent;
    private volatile float _systemGainTarget;
    private volatile float _microphoneGainTarget;

    private TimeSpan _pausedTotal;
    private long _emittedFrames;
    private volatile bool _paused;
    private CancellationTokenSource? _cts;
    private Task? _pumpTask;
    private bool _disposed;

    private AudioMixer(
        List<AudioCaptureSource> sources,
        Func<byte[], int, long, long, ValueTask> emitBlock,
        Action<string> onWarning,
        bool microphoneMuted,
        bool systemAudioMuted)
    {
        _sources = sources;
        _emitBlock = emitBlock;
        _onWarning = onWarning;

        // RF-M2.11: estado inicial dos toggles - sem rampa (nada foi emitido)
        _microphoneGainTarget = microphoneMuted ? 0f : 1f;
        _systemGainTarget = systemAudioMuted ? 0f : 1f;
        _microphoneGainCurrent = _microphoneGainTarget;
        _systemGainCurrent = _systemGainTarget;
    }

    /// <summary>
    /// Cria as fontes pedidas nas opcoes. Retorna null quando nenhuma fonte
    /// pode ser criada (gravacao segue sem trilha de audio). Fontes que falham
    /// individualmente geram <paramref name="onWarning"/> e sao puladas.
    /// </summary>
    public static AudioMixer? TryCreate(
        Mp4RecordingOptions options,
        Func<byte[], int, long, long, ValueTask> emitBlock,
        Action<string> onWarning,
        bool microphoneMuted = false,
        bool systemAudioMuted = false)
    {
        var sources = new List<AudioCaptureSource>();

        if (options.CaptureSystemAudio)
        {
            try
            {
                sources.Add(new AudioCaptureSource(
                    new BufferedLoopbackCapture(), ownedDevice: null, "Som do sistema", onWarning,
                    isSystemAudio: true));
            }
            catch (Exception)
            {
                onWarning("Nao foi possivel capturar o som do sistema; a gravacao segue sem ele.");
            }
        }

        if (options.MicrophoneDeviceIds.Count > 0)
        {
            MMDeviceEnumerator? enumerator = null;
            try
            {
                enumerator = new MMDeviceEnumerator();
            }
            catch (Exception)
            {
                onWarning("Servico de audio indisponivel; a gravacao segue sem microfones.");
            }

            if (enumerator is not null)
            {
                using (enumerator)
                {
                    foreach (string id in options.MicrophoneDeviceIds)
                    {
                        MMDevice? device = null;
                        try
                        {
                            device = enumerator.GetDevice(id);
                            if (device.State != DeviceState.Active)
                                throw new InvalidOperationException("Dispositivo inativo.");
                            sources.Add(new AudioCaptureSource(
                                new WasapiCapture(device, useEventSync: true, CaptureBufferMilliseconds),
                                device, device.FriendlyName, onWarning));
                        }
                        catch (Exception)
                        {
                            device?.Dispose();
                            onWarning("Um microfone selecionado nao esta disponivel; a gravacao segue sem ele.");
                        }
                    }
                }
            }
        }

        return sources.Count == 0
            ? null
            : new AudioMixer(sources, emitBlock, onWarning, microphoneMuted, systemAudioMuted);
    }

    /// <summary>
    /// RF-M2.09: muta/desmuta os microfones via alvo de ganho (rampa aplicada
    /// pelo pump). Thread-safe; chamavel a qualquer momento da sessao.
    /// </summary>
    public void SetMicrophoneMuted(bool muted) => _microphoneGainTarget = muted ? 0f : 1f;

    /// <summary>RF-M2.09: muta/desmuta o som do sistema (loopback) via alvo de ganho.</summary>
    public void SetSystemAudioMuted(bool muted) => _systemGainTarget = muted ? 0f : 1f;

    /// <summary>Inicia as capturas e a task de mixagem (aguardando o relogio).</summary>
    public void Start()
    {
        for (int i = _sources.Count - 1; i >= 0; i--)
        {
            try
            {
                _sources[i].Start();
            }
            catch (Exception)
            {
                // startup falhou (dispositivo sumiu entre criar e iniciar)
                _sources[i].Dispose();
                _sources.RemoveAt(i);
            }
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _pumpTask = Task.Run(() => PumpAsync(token));
    }

    /// <summary>
    /// Ancora o T0 do relogio de audio no 1o frame de video (as duas timelines
    /// comecam em 0 juntas). Antes disso o pump descarta o acumulado.
    /// </summary>
    public void StartClock()
    {
        if (_clock.IsRunning)
            return;
        foreach (var source in _sources)
            source.ResetForResume(0);
        _clock.Start();
    }

    public void Pause()
    {
        if (_paused)
            return;
        _pauseClock.Restart();
        _paused = true;
    }

    public void Resume()
    {
        if (!_paused)
            return;
        _pausedTotal += _pauseClock.Elapsed;
        _pauseClock.Reset();

        // RF-F3.13: audio chegado durante a pausa e descartado e a contagem de
        // cada fonte realinha ao total ja emitido (ring vazio = nada entregue
        // alem do lido; realinhar ao relogio criava underflow pos-resume)
        foreach (var source in _sources)
            source.ResetForResume(_emittedFrames);
        _paused = false;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_pumpTask is not null)
        {
            try
            {
                await _pumpTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        foreach (var source in _sources)
            source.Stop();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        foreach (var source in _sources)
            source.Dispose();
    }

    private TimeSpan ActiveElapsed =>
        _clock.Elapsed - _pausedTotal - (_paused ? _pauseClock.Elapsed : TimeSpan.Zero);

    private long ClockFrames() =>
        (long)(ActiveElapsed.TotalSeconds * AudioCaptureSource.TargetSampleRate);

    /// <summary>Total de frames emitidos na sessao (ler apos StopAsync).</summary>
    public long EmittedFrames => _emittedFrames;

    /// <summary>Frames de silencio inseridos por gap, somados das fontes.</summary>
    public long SilenceInsertedFrames => SumSources(static s => s.SilenceInsertedFrames);

    /// <summary>Frames descartados por deriva para frente, somados das fontes.</summary>
    public long DriftDroppedFrames => SumSources(static s => s.DriftDroppedFrames);

    /// <summary>Zeros defensivos por underflow do ring, somados das fontes.</summary>
    public long UnderflowZeroFrames => SumSources(static s => s.UnderflowZeroFrames);

    private long SumSources(Func<AudioCaptureSource, long> selector)
    {
        long total = 0;
        foreach (var source in _sources)
            total += selector(source);
        return total;
    }

    private async Task PumpAsync(CancellationToken token)
    {
        try
        {
            await PumpLoopAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // encerramento normal
        }
        catch (Exception ex)
        {
            // audio nunca derruba a gravacao: o video segue e as fontes
            // silenciam (mesmo espirito do RF-F3.14)
            _onWarning($"O pipeline de audio falhou; o restante da gravacao pode ficar sem som. ({ex.Message})");
        }
    }

    private async Task PumpLoopAsync(CancellationToken token)
    {
        // O tick de 10 ms pode chegar com jitter alto (resolucao do timer do
        // Windows ~15,6 ms sem timeBeginPeriod), mas isso NAO cria
        // descontinuidade: a emissao e integralmente compensada pelo relogio -
        // cada tick emite tudo ate (relogio - jitter buffer), entao um tick
        // atrasado apenas emite um bloco maior, com timestamps derivados da
        // contagem de samples (continuos por construcao).
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickMilliseconds));
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            if (!_clock.IsRunning || _paused)
                continue;

            long clockFrames = ClockFrames();

            // fontes vivas apenas reportam o quanto ja entregaram; fontes
            // mortas (RF-F3.14) ou em gap real (sem pacotes alem da tolerancia,
            // RF-F3.12) ganham silencio continuo dentro do EnsureProgress e
            // nunca seguram o pump
            long minDelivered = long.MaxValue;
            foreach (var source in _sources)
                minDelivered = Math.Min(minDelivered, source.EnsureProgress(clockFrames));

            // o due avanca ate o MENOR entre (relogio - jitter buffer) e o que
            // TODAS as fontes tem de fato: atraso de entrega dentro da
            // tolerancia e resolvido esperando (emitir menos agora e mais
            // depois - o relogio absorve), nunca com zeros+descarte, que
            // picava o audio sob jitter sustentado (GC, move-loop do drag da
            // toolbar, carga de CPU). Timestamps por contagem de samples
            // continuam continuos por construcao.
            long target = Math.Min(clockFrames - JitterFrames, minDelivered);
            int due = (int)Math.Clamp(target - _emittedFrames, 0, MaxBurstFrames);
            if (due == 0)
                continue;

            int sampleCount = due * AudioCaptureSource.TargetChannels;
            Array.Clear(_mix, 0, sampleCount);

            // RF-M2.09: rampa de ganho POR GRUPO calculada uma vez por bloco -
            // todas as fontes do grupo compartilham a mesma trajetoria; o
            // estado avanca exatamente `due` frames por tick
            FillGainBlock(_systemGainBlock, ref _systemGainCurrent, _systemGainTarget, due);
            FillGainBlock(_microphoneGainBlock, ref _microphoneGainCurrent, _microphoneGainTarget, due);
            foreach (var source in _sources)
                source.ReadInto(_mix, due, source.IsSystemAudio ? _systemGainBlock : _microphoneGainBlock);

            // soma com clamp [-1,1] -> PCM 16-bit little-endian (RF-F3.11)
            byte[] pcm = new byte[sampleCount * sizeof(short)];
            for (int i = 0; i < sampleCount; i++)
            {
                float value = Math.Clamp(_mix[i], -1f, 1f);
                short sample = (short)MathF.Round(value * short.MaxValue);
                pcm[2 * i] = (byte)sample;
                pcm[2 * i + 1] = (byte)((ushort)sample >> 8);
            }

            // RF-F3.12: timestamp por contagem de samples emitidos (monotonico)
            long timestampTicks = _emittedFrames * TimeSpan.TicksPerSecond / AudioCaptureSource.TargetSampleRate;
            long durationTicks = (long)due * TimeSpan.TicksPerSecond / AudioCaptureSource.TargetSampleRate;
            _emittedFrames += due;

            await _emitBlock(pcm, pcm.Length, timestampTicks, durationTicks).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Preenche <paramref name="gain"/> com a rampa linear de ~8 ms (D-M2.5) do
    /// ganho corrente em direcao ao alvo, um fator por frame. Sem transicao
    /// pendente o preenchimento e constante (caminho comum).
    /// </summary>
    private static void FillGainBlock(float[] gain, ref float current, float target, int frames)
    {
        if (current == target)
        {
            // caminho comum: ganho estavel no bloco inteiro
            for (int i = 0; i < frames; i++)
                gain[i] = current;
            return;
        }

        float step = 1f / MuteRampFrames;
        float value = current;
        for (int i = 0; i < frames; i++)
        {
            value = target > value
                ? MathF.Min(value + step, target)
                : MathF.Max(value - step, target);
            gain[i] = value;
        }

        current = value;
    }

    /// <summary>
    /// Loopback do sistema com buffer WASAPI de <see cref="CaptureBufferMilliseconds"/>
    /// (o WasapiLoopbackCapture do NAudio fixa 100 ms sem expor o parametro).
    /// Mesmo dispositivo/flags do original: device de render padrao + flag
    /// AUDCLNT_STREAMFLAGS_LOOPBACK, em polling (event sync nao acorda em
    /// loopback sem um stream de render proprio).
    /// </summary>
    private sealed class BufferedLoopbackCapture() : WasapiCapture(
        WasapiLoopbackCapture.GetDefaultLoopbackCaptureDevice(),
        useEventSync: false,
        CaptureBufferMilliseconds)
    {
        protected override AudioClientStreamFlags GetAudioClientStreamFlags() =>
            AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
    }
}
