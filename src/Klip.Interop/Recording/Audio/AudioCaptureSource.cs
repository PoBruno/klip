using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace Klip.Interop.Recording;

/// <summary>
/// Uma fonte de audio da gravacao (loopback do sistema ou um microfone),
/// capturada via WASAPI no formato nativo do dispositivo e convertida aqui
/// para float 48 kHz estereo (WdlResampler quando a taxa difere). Mantem um
/// buffer circular com contabilidade de samples entregues vs esperados pelo
/// relogio. Atraso de ENTREGA dentro da tolerancia e resolvido por buffering
/// (o mixer espera o dado; ver <see cref="EnsureProgress"/>); silencio so e
/// inserido em gap real da fonte (loopback sem pacotes, RF-F3.12) ou fonte
/// morta (RF-F3.14), e deriva de clock para frente e contida por descarte.
/// </summary>
internal sealed class AudioCaptureSource : IDisposable
{
    /// <summary>Taxa alvo do pipeline de audio (RF-F3.11).</summary>
    public const int TargetSampleRate = 48000;

    /// <summary>Canais do pipeline (downmix/upmix para estereo, RF-F3.11).</summary>
    public const int TargetChannels = 2;

    // ~1 s de teto no buffer: cobre stalls do pump de centenas de ms (o dado
    // fica esperando aqui, sem perda) e limita a latencia por drop-oldest se
    // o pump ficar preso por mais de 1 s (falha real, nao jitter).
    private const int MaxBufferedFrames = TargetSampleRate;

    // RF-F3.12: fonte viva sem pacotes ha mais que isto = gap REAL (loopback
    // nao entrega pacotes quando o sistema esta em silencio) -> inserir
    // silencio. Tolerancia GENEROSA de proposito: atraso de entrega ate aqui
    // e resolvido por buffering (o mixer espera; ver EnsureProgress) - o
    // limiar antigo de 100 ms igualava o jitter buffer do mixer, entao
    // qualquer jitter sustentado acima disso (GC, move-loop do drag da
    // toolbar, carga de CPU) inseria zeros no meio do stream e deslocava a
    // chegada real -> audio picotado (pipocos continuos). Dimensionamento:
    // buffer do engine WASAPI (250 ms) + cadencia de polling do loopback
    // (~buffer/2 = 125 ms) + folga de um tick - deficit maior que isso so
    // existe quando o engine JA descartou pacotes (gap real, silencio
    // correto). Fica bem abaixo do MuxStarvationThresholdFrames do
    // Mp4Recorder (600 ms): espera legitima nunca e starvation. O silencio de
    // um gap real cobre o deficit INTEIRO retroativamente, entao a tolerancia
    // maior nao desloca conteudo - so adia a emissao (latencia, nao ao vivo).
    private const int GapToleranceFrames = TargetSampleRate * 400 / 1000;

    // folga deixada apos inserir silencio de gap (= jitter buffer do mixer:
    // a fonte volta a ficar exatamente na borda de leitura, sem furo)
    private const int RealignHeadroomFrames = AudioMixer.JitterFrames;

    // deriva "para frente" (dispositivo entregando mais que o relogio) tolerada
    // antes do descarte duro. 150 ms: o WASAPI event-driven entrega em rajadas
    // (pacotes de 10 ms podem chegar agrupados), entao o excesso instantaneo
    // oscila dezenas de ms acima do relogio; os 40 ms originais da spec
    // (RF-F3.12) disparavam descarte de audio VALIDO em blocos -> pops.
    // Deriva real de clock leva horas para acumular 150 ms; o descarte e
    // feito de uma vez so (nunca repetidamente) e contabilizado.
    private const int ForwardDriftFrames = TargetSampleRate * 150 / 1000;

    private readonly object _lock = new();
    private readonly IWaveIn _capture;
    private readonly MMDevice? _device;
    private readonly Action<string> _onWarning;

    // formato de entrada (fixado na criacao; WASAPI shared mode nao muda em voo)
    private readonly int _sourceRate;
    private readonly int _sourceChannels;
    private readonly SampleEncoding _sourceEncoding;
    private readonly int _sourceBytesPerSample;
    private readonly WdlResampler? _resampler;

    // buffer circular de floats intercalados (frames * 2)
    private readonly float[] _ring = new float[MaxBufferedFrames * TargetChannels];
    private int _ringStart;
    private int _ringCount;

    private long _deliveredFrames; // total produzido (dados reais + silencio inserido)

    // gap confirmado (fonte viva sem pacotes alem da tolerancia): enquanto
    // ativo o silencio e CONTINUO a cada tick (sem alternancia zeros/espera);
    // limpo na proxima chegada de dados reais
    private bool _gapPadding;

    // fim de gap: a rajada que volta pode conter audio RETIDO no engine
    // durante o stall (ate o buffer WASAPI de 250 ms) que sobrepoe zeros ja
    // emitidos; o excedente sobre o relogio e aparado de uma vez no tick
    // seguinte - senao ou o descarte por deriva dispara no limiar (click
    // tardio) ou o conteudo fica com lag permanente ate o fim da gravacao
    private bool _gapRealignPending;

    // pos-reset (StartClock/resume): o buffer do engine WASAPI (250 ms) pode
    // conter audio capturado ANTES do relogio (re)iniciar; esse backlog chega
    // no 1o read e empurraria delivered alem do relogio, disparando descarte
    // por "deriva" no meio da gravacao. O excesso medido no 1o tick apos a 1a
    // chegada real e exatamente esse backlog pre-T0 - aparado uma unica vez.
    private bool _baselineTrimPending = true;
    private bool _realDataSinceReset;

    // diagnostico dos pipocos: cada frame descartado/inserido e uma potencial
    // descontinuidade audivel; agregados pelo AudioMixer e logados no stop
    private long _silenceInsertedFrames; // gap sem pacotes -> zeros (normal em loopback silencioso)
    private long _driftDroppedFrames;    // descarte para realinhar ao relogio (deriva/fim de gap; deve ser raro)
    private long _underflowZeroFrames;   // zeros defensivos por ring curto no ReadInto (nao deve ocorrer)

    private float[] _stereoScratch = [];
    private float[] _resampleScratch = [];
    private volatile bool _dead;
    private bool _stopping;
    private bool _warned;
    private bool _disposed;

    private enum SampleEncoding
    {
        Float32,
        Pcm16,
        Pcm24,
        Pcm32,
    }

    public AudioCaptureSource(
        IWaveIn capture,
        MMDevice? ownedDevice,
        string displayName,
        Action<string> onWarning,
        bool isSystemAudio = false)
    {
        _capture = capture;
        _device = ownedDevice;
        DisplayName = displayName;
        IsSystemAudio = isSystemAudio;
        _onWarning = onWarning;

        var format = capture.WaveFormat;
        _sourceRate = format.SampleRate;
        _sourceChannels = Math.Max(1, format.Channels);
        _sourceEncoding = ResolveEncoding(format);
        _sourceBytesPerSample = format.BitsPerSample / 8;

        if (_sourceRate != TargetSampleRate)
        {
            _resampler = new WdlResampler();
            _resampler.SetMode(true, 2, false, 0, 0);
            _resampler.SetFilterParms();
            _resampler.SetFeedMode(true); // input driven: alimentamos o que o WASAPI entrega
            _resampler.SetRates(_sourceRate, TargetSampleRate);
        }

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
    }

    public string DisplayName { get; }

    /// <summary>
    /// RF-M2.09: true para o loopback do sistema, false para microfones -
    /// define qual rampa de ganho do <see cref="AudioMixer"/> se aplica.
    /// </summary>
    public bool IsSystemAudio { get; }

    /// <summary>Frames de silencio inseridos por gap de pacotes (diagnostico).</summary>
    public long SilenceInsertedFrames
    {
        get
        {
            lock (_lock)
            {
                return _silenceInsertedFrames;
            }
        }
    }

    /// <summary>Frames descartados por deriva para frente (diagnostico).</summary>
    public long DriftDroppedFrames
    {
        get
        {
            lock (_lock)
            {
                return _driftDroppedFrames;
            }
        }
    }

    /// <summary>Zeros defensivos mixados por ring curto no ReadInto (diagnostico).</summary>
    public long UnderflowZeroFrames
    {
        get
        {
            lock (_lock)
            {
                return _underflowZeroFrames;
            }
        }
    }

    public void Start() => _capture.StartRecording();

    /// <summary>
    /// Chamado a cada tick do pump ANTES da leitura. Insere silencio apenas em
    /// falha/gap REAL da fonte: dispositivo morto (RF-F3.14) ou fonte viva sem
    /// pacotes alem da tolerancia generosa (loopback silencioso, RF-F3.12) -
    /// nesses casos o silencio e continuo, sem alternancia. Atraso de entrega
    /// DENTRO da tolerancia nao gera zeros: o mixer limita o due ao minimo
    /// retornado aqui e espera o dado (zeros+descarte nesse caso era o que
    /// picava o audio sob jitter sustentado). Tambem contem deriva de clock
    /// para frente descartando o excedente de uma vez.
    /// </summary>
    /// <param name="clockFrames">Samples esperados pelo relogio ativo (exclui pausas).</param>
    /// <returns>Total entregue (dados reais + silencio) para o mixer limitar o due.</returns>
    public long EnsureProgress(long clockFrames)
    {
        lock (_lock)
        {
            // apara o backlog pre-T0 do engine WASAPI (ver _baselineTrimPending):
            // e audio de ANTES da sessao/trecho - descartar aqui e correto e
            // nao conta como estalo (nada dele foi ou sera emitido)
            if (_baselineTrimPending && _realDataSinceReset)
            {
                _baselineTrimPending = false;
                long preClock = _deliveredFrames - clockFrames;
                if (preClock > 0)
                {
                    int trim = (int)Math.Min(preClock, _ringCount / TargetChannels);
                    ConsumeLocked(trim);
                    _deliveredFrames -= trim;
                }
            }

            long deficit = clockFrames - _deliveredFrames;

            // fonte morta ou gap ja confirmado: silencio continuo ate a borda
            // de leitura (relogio - jitter buffer); fonte viva so entra em
            // gap depois da tolerancia de buffering
            long padThreshold = _dead || _gapPadding ? RealignHeadroomFrames : GapToleranceFrames;
            if (deficit > padThreshold)
            {
                int silence = (int)Math.Min(deficit - RealignHeadroomFrames, MaxBufferedFrames);
                if (silence > 0)
                {
                    _gapPadding = true;
                    _silenceInsertedFrames += silence;
                    AppendLocked(null, 0, silence);
                }
            }

            // fim de gap (ver _gapRealignPending): apara de uma vez o audio
            // retido no engine que sobrepoe os zeros ja emitidos - o episodio
            // vira UMA emenda, sem lag residual de conteudo
            if (_gapRealignPending)
            {
                _gapRealignPending = false;
                long staleExcess = _deliveredFrames - clockFrames;
                if (staleExcess > 0)
                {
                    int drop = (int)Math.Min(staleExcess, _ringCount / TargetChannels);
                    _driftDroppedFrames += drop;
                    ConsumeLocked(drop);
                    _deliveredFrames -= drop;
                }
            }

            long excess = _deliveredFrames - clockFrames;
            if (excess > ForwardDriftFrames)
            {
                // dispositivo adiantado alem da tolerancia de rajada: descartar
                // o excedente mais antigo DE UMA VEZ realinha a timeline
                // (correcao dura; deriva real de clock leva horas - se este
                // contador crescer, o relogio de referencia esta errado)
                int drop = (int)Math.Min(excess, _ringCount / TargetChannels);
                _driftDroppedFrames += drop;
                ConsumeLocked(drop);
                _deliveredFrames -= drop;
            }

            return _deliveredFrames;
        }
    }

    /// <summary>
    /// Soma ate <paramref name="frames"/> frames desta fonte em <paramref name="mix"/>,
    /// aplicando o ganho POR FRAME de <paramref name="gain"/> (RF-M2.09: rampa de
    /// mute calculada pelo mixer; mesmo fator nos dois canais do frame).
    /// O mixer limita o pedido ao minimo reportado por <see cref="EnsureProgress"/>,
    /// entao o ring cobre tudo em operacao normal; um furo residual sai como
    /// zeros e avanca a contagem para nao deslocar a timeline (diagnostico).
    /// </summary>
    public void ReadInto(float[] mix, int frames, float[] gain)
    {
        lock (_lock)
        {
            int take = Math.Min(frames, _ringCount / TargetChannels);
            for (int i = 0; i < take * TargetChannels; i++)
                mix[i] += _ring[(_ringStart + i) % _ring.Length] * gain[i / TargetChannels];
            ConsumeLocked(take);

            // defensivo (nao deve ocorrer): frames que faltaram sairam como
            // zeros no mix; avancar delivered mantem a chegada futura alinhada
            // aos slots ja emitidos em vez de desloca-la
            int shortfall = frames - take;
            if (shortfall > 0)
            {
                _underflowZeroFrames += shortfall;
                _deliveredFrames += shortfall;
            }
        }
    }

    /// <summary>
    /// RF-F3.13: descarta o acumulado da pausa e realinha a contagem ao total
    /// ja emitido pelo mixer. Realinhar ao relogio (como antes) declarava o
    /// jitter buffer como "entregue" com o ring VAZIO - o ReadInto mixava
    /// zeros por underflow logo apos cada resume.
    /// </summary>
    public void ResetForResume(long emittedFrames)
    {
        lock (_lock)
        {
            _ringStart = 0;
            _ringCount = 0;
            _gapPadding = false;
            _gapRealignPending = false;
            _baselineTrimPending = true;
            _realDataSinceReset = false;
            _deliveredFrames = emittedFrames;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _stopping = true;
        }

        try
        {
            _capture.StopRecording();
        }
        catch (Exception)
        {
            // dispositivo ja removido: nada a parar
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        _capture.Dispose();
        _device?.Dispose();
    }

    // ----- callbacks WASAPI (thread de captura do NAudio) -----

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        int frameBytes = _sourceBytesPerSample * _sourceChannels;
        if (_dead || frameBytes == 0 || e.BytesRecorded < frameBytes)
            return;

        int framesIn = e.BytesRecorded / frameBytes;
        EnsureCapacity(ref _stereoScratch, framesIn * TargetChannels);
        ConvertToStereoFloat(e.Buffer, framesIn, _stereoScratch);

        float[] output = _stereoScratch;
        int outputFrames = framesIn;
        if (_resampler is not null)
        {
            outputFrames = Resample(_stereoScratch, framesIn);
            output = _resampleScratch;
        }

        if (outputFrames <= 0)
            return;

        lock (_lock)
        {
            if (_gapPadding)
            {
                _gapPadding = false;        // pacotes voltaram: fim do gap real
                _gapRealignPending = true;  // proximo tick apara o excedente retido
            }

            _realDataSinceReset = true; // habilita o trim de backlog pre-T0
            AppendLocked(output, 0, outputFrames);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        bool intentional;
        lock (_lock)
        {
            intentional = _stopping;
        }

        _dead = true;
        if (!intentional && !_warned)
        {
            _warned = true;
            // RF-F3.14: dispositivo removido -> silencio (via EnsureProgress) + aviso
            _onWarning($"A fonte de audio \"{DisplayName}\" parou de responder; o restante da gravacao usara silencio nessa fonte.");
        }
    }

    // ----- conversao de formato -----

    private static SampleEncoding ResolveEncoding(WaveFormat format)
    {
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible extensible
                && extensible.SubFormat.Equals(NAudio.MediaFoundation.AudioSubtypes.MFAudioFormat_Float));

        if (isFloat)
        {
            return format.BitsPerSample == 32
                ? SampleEncoding.Float32
                : throw new NotSupportedException($"Formato float de {format.BitsPerSample} bits nao suportado.");
        }

        return format.BitsPerSample switch
        {
            16 => SampleEncoding.Pcm16,
            24 => SampleEncoding.Pcm24,
            32 => SampleEncoding.Pcm32,
            _ => throw new NotSupportedException($"Formato PCM de {format.BitsPerSample} bits nao suportado."),
        };
    }

    /// <summary>Converte frames intercalados do formato nativo para float estereo (canais 0/1; mono duplicado).</summary>
    private void ConvertToStereoFloat(byte[] buffer, int frames, float[] destination)
    {
        int channels = _sourceChannels;
        int bytesPerSample = _sourceBytesPerSample;
        int frameBytes = bytesPerSample * channels;

        for (int frame = 0; frame < frames; frame++)
        {
            int frameOffset = frame * frameBytes;
            float left = ReadSample(buffer, frameOffset);
            float right = channels >= 2 ? ReadSample(buffer, frameOffset + bytesPerSample) : left;
            destination[frame * TargetChannels] = left;
            destination[frame * TargetChannels + 1] = right;
        }
    }

    private float ReadSample(byte[] buffer, int offset) => _sourceEncoding switch
    {
        SampleEncoding.Float32 => BitConverter.ToSingle(buffer, offset),
        SampleEncoding.Pcm16 => BitConverter.ToInt16(buffer, offset) / 32768f,
        SampleEncoding.Pcm24 => (buffer[offset] << 8 | buffer[offset + 1] << 16 | buffer[offset + 2] << 24) / 2147483648f,
        SampleEncoding.Pcm32 => BitConverter.ToInt32(buffer, offset) / 2147483648f,
        _ => 0f,
    };

    /// <summary>Resample push (feed mode) do WdlResampler; retorna frames produzidos em <see cref="_resampleScratch"/>.</summary>
    private int Resample(float[] stereo, int framesIn)
    {
        int maxOut = (int)((long)framesIn * TargetSampleRate / _sourceRate) + 16;
        EnsureCapacity(ref _resampleScratch, maxOut * TargetChannels);

        int toFeed = _resampler!.ResamplePrepare(framesIn, TargetChannels, out float[] inBuffer, out int inOffset);
        Array.Copy(stereo, 0, inBuffer, inOffset, toFeed * TargetChannels);
        return _resampler.ResampleOut(_resampleScratch, 0, toFeed, maxOut, TargetChannels);
    }

    private static void EnsureCapacity(ref float[] buffer, int samples)
    {
        if (buffer.Length < samples)
            buffer = new float[samples];
    }

    // ----- buffer circular (sempre sob _lock) -----

    /// <param name="data">Samples intercalados ou null para silencio.</param>
    /// <param name="offset">Offset em frames.</param>
    /// <param name="frames">Quantidade de frames.</param>
    private void AppendLocked(float[]? data, int offset, int frames)
    {
        if (frames <= 0)
            return;

        _deliveredFrames += frames;

        // overflow: mantem os mais recentes (drop-oldest limita a latencia)
        int buffered = _ringCount / TargetChannels;
        if (buffered + frames > MaxBufferedFrames)
            ConsumeLocked(buffered + frames - MaxBufferedFrames);

        int writeIndex = (_ringStart + _ringCount) % _ring.Length;
        int baseIndex = offset * TargetChannels;
        for (int i = 0; i < frames * TargetChannels; i++)
        {
            _ring[writeIndex] = data is null ? 0f : data[baseIndex + i];
            writeIndex = (writeIndex + 1) % _ring.Length;
        }

        _ringCount += frames * TargetChannels;
    }

    private void ConsumeLocked(int frames)
    {
        int samples = Math.Min(frames * TargetChannels, _ringCount);
        _ringStart = (_ringStart + samples) % _ring.Length;
        _ringCount -= samples;
    }
}
