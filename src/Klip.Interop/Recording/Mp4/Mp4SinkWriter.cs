using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Klip.Interop.Recording;

/// <summary>
/// Encapsula o IMFSinkWriter do arquivo MP4 (RF-F3.08..11): atributos de
/// hardware/fMP4, streams H.264 (input ARGB32 em GPU) e AAC (input PCM 16-bit),
/// alocador de samples de video (RF-M2.01) e a escrita de samples. NAO e
/// thread-safe: todas as chamadas devem vir da task unica de escrita do
/// <see cref="Mp4Recorder"/> (excecao: <see cref="TryAllocateVideoSample"/> e
/// <see cref="GetVideoSampleTexture"/>, chamados do callback do engine - o
/// alocador do MF e thread-safe por contrato).
/// Tuning de encoder e faststart (RF-M2.06/08) adaptados dos mecanismos do
/// sskodje/ScreenRecorderLib (MIT) - ver THIRD-PARTY-NOTICES.md.
/// </summary>
internal sealed class Mp4SinkWriter : IDisposable
{
    // IID de ID3D11Texture2D para o IMFDXGIBuffer.GetResource
    private static readonly Guid D3D11Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    // IID de IMFVideoSampleAllocatorEx (mfidl.h) para o MFCreateVideoSampleAllocatorEx
    private static readonly Guid VideoSampleAllocatorExIid = new("545B3A48-3283-4F62-866F-A62D8F598F9F");

    /// <summary>MF_E_SAMPLEALLOCATOR_EMPTY: pool esgotado = backpressure (RF-M2.02).</summary>
    public const int SampleAllocatorEmpty = unchecked((int)0xC00D4A3E);

    // RF-M2.01: profundidade do pool do alocador (4 iniciais, 10 maximo) - o
    // tracking de lifetime e do proprio MF (sample volta ao pool quando o
    // pipeline solta a ultima referencia), substituindo o round-robin heuristico
    private const int AllocatorInitialSamples = 4;
    private const int AllocatorMaximumSamples = 10;

    // ----- GUIDs CODECAPI do ENCODER_CONFIG (RF-M2.06) -----
    private static readonly Guid CodecApiRateControlMode = new("1C0608E9-370C-4710-8A58-CB6181C42423");
    private static readonly Guid CodecApiMeanBitRate = new("F7222374-2144-4815-B550-A37F8E12EE52");
    private static readonly Guid CodecApiMaxBitRate = new("9651EAE4-39B9-4EBF-85EF-D7F444EC7465");
    private static readonly Guid CodecApiCommonQuality = new("FCBF57A3-7EA5-4B0C-9644-69B40C39C391");
    private static readonly Guid CodecApiQualityVsSpeed = new("98332DF8-03CD-476B-89FA-3F9E442DEC9F");
    private static readonly Guid CodecApiGopSize = new("95F31B26-95A4-41AA-9303-246A7FC6EEF1");
    private static readonly Guid CodecApiBPictureCount = new("8D390AAC-DC5C-4200-B57F-814D04BABAB2");
    private static readonly Guid CodecApiScenarioInfo = new("B28A6E64-3FF9-446A-8A4B-0D7A53413236");

    private const uint RateControlQuality = 3;            // eAVEncCommonRateControlMode_Quality
    private const uint RateControlPeakConstrainedVbr = 1; // eAVEncCommonRateControlMode_PeakConstrainedVBR
    private const uint ScenarioDisplayRemoting = 1;       // eAVScenarioInfo_DisplayRemoting

    private readonly IMFSinkWriter _writer;
    private readonly IMFDXGIDeviceManager _dxgiManager;
    private IMFVideoSampleAllocatorEx? _sampleAllocator;
    private readonly int _videoStreamIndex;
    private readonly int _audioStreamIndex = -1;
    private bool _disposed;

    public int Width { get; }

    public int Height { get; }

    public Mp4SinkWriter(
        string outputPath,
        ID3D11Device device,
        int width,
        int height,
        int fps,
        int bitrateKbps,
        bool qualityMode,
        bool fragmentedMp4,
        bool withAudio)
    {
        Width = width;
        Height = height;

        // O SinkWriter (Video Processor MFT + encoder) e o recorder compartilham
        // o immediate context do device da captura -> protecao multithread
        // obrigatoria (RF-F3.08)
        using (var multithread = device.QueryInterfaceOrNull<ID3D11Multithread>())
            multithread?.SetMultithreadProtected(true);

        _dxgiManager = MediaFactory.MFCreateDXGIDeviceManager();
        try
        {
            _dxgiManager.ResetDevice(device).CheckError();

            using IMFAttributes attributes = MediaFactory.MFCreateAttributes(6);
            // RF-F3.08: encoder de hardware. Throttling do SinkWriter DESLIGADO
            // (regressao dos pipocos de audio): com throttling ligado o
            // WriteSample de video BLOQUEIA quando a fila do encoder enche e,
            // como video e audio compartilham o mesmo write gate no Mp4Recorder,
            // o consumidor de audio era esfomeado ate o canal dropar blocos
            // (cada bloco perdido = descontinuidade de 10 ms = pipoco). A
            // protecao contra reuso de textura vem do IMFVideoSampleAllocatorEx
            // (RF-M2.01): o MF rastreia o lifetime de cada sample e so devolve a
            // textura ao pool quando o pipeline solta a ultima referencia.
            // LowLatency tambem fica de fora (bug #4): em gravacao para arquivo
            // ele degrada a qualidade (desabilita B-frames/lookahead).
            attributes.Set(SinkWriterAttributeKeys.DisableThrottling, true).CheckError();
            attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, true).CheckError();
            attributes.Set(SinkWriterAttributeKeys.D3DManager, _dxgiManager).CheckError();
            if (fragmentedMp4)
            {
                // RF-F3.10: fMP4 - crash preserva o gravado ate o ultimo fragmento
                attributes.Set(TranscodeAttributeKeys.TranscodeContainertype, TranscodeContainerTypeGuids.Fmpeg4).CheckError();
            }
            else
            {
                // RF-M2.08: MP4 classico com moov ANTES do mdat (faststart) -
                // playback via streaming comeca sem baixar o arquivo inteiro
                attributes.Set(Mpeg4MediaSinkAttributeKeys.MoovBeforeMdat, 1u).CheckError();
            }

            // RF-M2.06 (D-M2.4): tuning do encoder H.264 para screen content via
            // IPropertyStore. Tolerante a falhas: encoders variam no suporte -
            // qualquer erro aqui apenas loga e a gravacao segue com defaults.
            ApplyEncoderConfig(attributes, fps, bitrateKbps, qualityMode);

            _writer = MediaFactory.MFCreateSinkWriterFromURL(outputPath, null, attributes);
        }
        catch
        {
            _dxgiManager.Dispose();
            throw;
        }

        try
        {
            _videoStreamIndex = AddVideoStream(fps, bitrateKbps);
            if (withAudio)
                _audioStreamIndex = AddAudioStream();
            _writer.BeginWriting();
        }
        catch
        {
            _sampleAllocator?.Dispose();
            _writer.Dispose();
            _dxgiManager.Dispose();
            throw;
        }
    }

    /// <summary>
    /// RF-M2.01/02: aloca um sample de video do pool do MF. Retorna null quando
    /// o pool esta esgotado (MF_E_SAMPLEALLOCATOR_EMPTY = encoder atrasado);
    /// o chamador decide entre retry curto e drop contabilizado.
    /// </summary>
    public IMFSample? TryAllocateVideoSample()
    {
        try
        {
            return _sampleAllocator!.AllocateSample();
        }
        catch (SharpGen.Runtime.SharpGenException ex) when (ex.ResultCode.Code == SampleAllocatorEmpty)
        {
            return null;
        }
    }

    /// <summary>
    /// Textura D3D11 por tras do sample alocado (RF-M2.01): buffer 0 ->
    /// IMFDXGIBuffer -> ID3D11Texture2D. O wrapper retornado deve ser
    /// descartado pelo chamador (solta apenas a referencia COM local).
    /// </summary>
    public ID3D11Texture2D GetVideoSampleTexture(IMFSample sample)
    {
        using IMFMediaBuffer buffer = sample.GetBufferByIndex(0);
        using IMFDXGIBuffer dxgiBuffer = buffer.QueryInterface<IMFDXGIBuffer>();
        nint texturePtr = dxgiBuffer.GetResource(D3D11Texture2DIid);
        return new ID3D11Texture2D(texturePtr);
    }

    /// <summary>
    /// Envia um sample de video ja alocado/copiado ao encoder (RF-M2.01). O
    /// chamador descarta o sample apos o retorno (a referencia do pipeline
    /// mantem a textura viva ate o encoder terminar de le-la).
    /// </summary>
    public void WriteVideoSample(IMFSample sample)
    {
        // O buffer DXGI do allocator vem com CurrentLength = 0; o encoder exige
        // o tamanho contiguo preenchido (E_INVALIDARG sem isso - mesmo requisito
        // documentado no ScreenRecorderLib: GetContiguousLength -> SetCurrentLength).
        using (IMFMediaBuffer buffer = sample.GetBufferByIndex(0))
        {
            if (buffer.CurrentLength == 0)
            {
                using IMF2DBuffer buffer2D = buffer.QueryInterface<IMF2DBuffer>();
                buffer.CurrentLength = buffer2D.ContiguousLength;
            }
        }

        _writer.WriteSample(_videoStreamIndex, sample);
    }

    /// <summary>
    /// Envia um bloco PCM 16-bit 48 kHz estereo mixado (RF-F3.11).
    /// <paramref name="byteOffset"/> permite ao muxer aparar o inicio de blocos
    /// sobrepostos ao silencio ja injetado (RF-M2.03).
    /// </summary>
    public void WriteAudioSample(byte[] pcm, int byteOffset, int byteCount, long timestampTicks, long durationTicks)
    {
        if (_audioStreamIndex < 0)
            return;

        using IMFMediaBuffer buffer = MediaFactory.MFCreateMemoryBuffer(byteCount);
        buffer.Lock(out nint data, out _, out _);
        try
        {
            Marshal.Copy(pcm, byteOffset, data, byteCount);
        }
        finally
        {
            buffer.Unlock();
        }

        buffer.CurrentLength = byteCount;

        using IMFSample sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = timestampTicks;
        sample.SampleDuration = durationTicks;
        _writer.WriteSample(_audioStreamIndex, sample);
    }

    /// <summary>RF-M2.12: estatisticas do stream de video (backlog do encoder, fila em bytes).</summary>
    public SinkWriterStatistics GetVideoStatistics() => _writer.GetStatistics(_videoStreamIndex);

    /// <summary>
    /// Finaliza o container (moov / ultimo fragmento). Pode levar segundos
    /// (RF-F3.16) - chamar fora da UI thread.
    /// </summary>
    public void FinalizeFile() => _writer.Finalize();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _writer.Dispose();
        if (_sampleAllocator is not null)
        {
            try
            {
                _sampleAllocator.UninitializeSampleAllocator();
            }
            catch (Exception)
            {
                // teardown nunca lanca
            }

            _sampleAllocator.Dispose();
            _sampleAllocator = null;
        }

        _dxgiManager.Dispose();
    }

    // ----- ENCODER_CONFIG (RF-M2.06) -----

    /// <summary>
    /// Monta o IPropertyStore com as propriedades CODECAPI e o anexa como
    /// MF_SINK_WRITER_ENCODER_CONFIG. Adaptado do mecanismo de configuracao de
    /// encoder do ScreenRecorderLib (MIT). Nunca lanca: falha vira Trace e a
    /// gravacao segue com os defaults do encoder.
    /// </summary>
    private static void ApplyEncoderConfig(IMFAttributes attributes, int fps, int bitrateKbps, bool qualityMode)
    {
        try
        {
            var store = NativeMethods.PSCreateMemoryPropertyStore(typeof(NativeMethods.IPropertyStore).GUID);
            try
            {
                if (qualityMode)
                {
                    // bitrate automatico -> modo Quality puro (screen content nitido)
                    SetStoreValue(store, CodecApiRateControlMode, RateControlQuality);
                }
                else
                {
                    // bitrate explicito nas settings -> PeakConstrainedVBR com a
                    // media pedida e pico 2x (RF-M2.06)
                    SetStoreValue(store, CodecApiRateControlMode, RateControlPeakConstrainedVbr);
                    SetStoreValue(store, CodecApiMeanBitRate, (uint)(bitrateKbps * 1000));
                    SetStoreValue(store, CodecApiMaxBitRate, (uint)(bitrateKbps * 2000));
                }

                SetStoreValue(store, CodecApiCommonQuality, 80);
                SetStoreValue(store, CodecApiQualityVsSpeed, 80);
                SetStoreValue(store, CodecApiGopSize, (uint)(fps * 2));
                SetStoreValue(store, CodecApiBPictureCount, 0);
                SetStoreValue(store, CodecApiScenarioInfo, ScenarioDisplayRemoting);

                // aplicado em LOTE: o SinkWriter repassa o store inteiro ao
                // encoder; propriedades nao suportadas sao ignoradas por ele
                nint unknown = Marshal.GetIUnknownForObject(store);
                using var wrapper = new SharpGen.Runtime.ComObject(unknown);
                attributes.Set(SinkWriterAttributeKeys.EncoderConfig, wrapper).CheckError();
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        catch (Exception ex)
        {
            // RF-M2.06: o tuning nunca impede o start da gravacao
            Trace.WriteLine($"Mp4SinkWriter: ENCODER_CONFIG indisponivel, seguindo com defaults do encoder. ({ex.Message})");
        }
    }

    /// <summary>Escreve um VT_UI4 no store; PROPERTYKEY = {fmtid = GUID CODECAPI, pid = 0} (RF-M2.06).</summary>
    private static void SetStoreValue(NativeMethods.IPropertyStore store, Guid codecApiGuid, uint value)
    {
        try
        {
            var key = new NativeMethods.PROPERTYKEY { fmtid = codecApiGuid, pid = 0 };
            var variant = new NativeMethods.PROPVARIANT { vt = NativeMethods.VT_UI4, ulVal = value };
            store.SetValue(in key, in variant);
        }
        catch (Exception)
        {
            // propriedade individual falhou: ignorar (encoders variam, RF-M2.06)
        }
    }

    // ----- streams -----

    private int AddVideoStream(int fps, int bitrateKbps)
    {
        using IMFMediaType output = MediaFactory.MFCreateMediaType();
        output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
        output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
        output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)(bitrateKbps * 1000)).CheckError();
        output.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive).CheckError();
        MediaFactory.MFSetAttributeSize(output, MediaTypeAttributeKeys.FrameSize, (uint)Width, (uint)Height).CheckError();
        MediaFactory.MFSetAttributeRatio(output, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1).CheckError();
        MediaFactory.MFSetAttributeRatio(output, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1).CheckError();
        // bug #5 da auditoria: sem perfil explicito os encoders caem em Baseline
        output.Set(MediaTypeAttributeKeys.Mpeg2Profile, 100u).CheckError(); // eAVEncH264VProfile_High
        // bug #6 da auditoria (RF-M2.07): colorimetria BT.709 explicita na
        // conversao RGB->NV12; sem isso regioes < 720p podem sair BT.601
        output.Set(MediaTypeAttributeKeys.YuvMatrix, 1u).CheckError();         // MFVideoTransferMatrix_BT709 = 1
        output.Set(MediaTypeAttributeKeys.VideoNominalRange, 2u).CheckError(); // MFNominalRange_16_235
        output.Set(MediaTypeAttributeKeys.VideoPrimaries, 2u).CheckError();    // MFVideoPrimaries_BT709 = 2
        output.Set(MediaTypeAttributeKeys.TransferFunction, 5u).CheckError();  // MFVideoTransFunc_709
        int index = _writer.AddStream(output);

        // input ARGB32 (memoria BGRA, igual a textura B8G8R8A8 da F2); a
        // conversao de cor acontece no Video Processor MFT em GPU (RF-F3.09)
        using IMFMediaType input = MediaFactory.MFCreateMediaType();
        input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
        input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Argb32).CheckError();
        input.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive).CheckError();
        // bug #3 da auditoria: o default RGB do MF e bottom-up; stride positivo
        // explicito garante top-down no caminho de software
        input.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(Width * 4)).CheckError();
        // bug #6 (RF-M2.07): descreve a fonte RGB da captura (full range,
        // primarias/curva BT.709); a matriz YUV e propriedade do lado NV12
        input.Set(MediaTypeAttributeKeys.VideoNominalRange, 1u).CheckError();  // MFNominalRange_0_255
        input.Set(MediaTypeAttributeKeys.VideoPrimaries, 2u).CheckError();     // MFVideoPrimaries_BT709 = 2
        input.Set(MediaTypeAttributeKeys.TransferFunction, 5u).CheckError();   // MFVideoTransFunc_709
        MediaFactory.MFSetAttributeSize(input, MediaTypeAttributeKeys.FrameSize, (uint)Width, (uint)Height).CheckError();
        MediaFactory.MFSetAttributeRatio(input, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1).CheckError();
        MediaFactory.MFSetAttributeRatio(input, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1).CheckError();
        _writer.SetInputMediaType(index, input, null);

        CreateSampleAllocator(input);
        return index;
    }

    /// <summary>
    /// RF-M2.01 (D-M2.1): IMFVideoSampleAllocatorEx com o MESMO
    /// IMFDXGIDeviceManager do SinkWriter e o media type de INPUT do stream de
    /// video. O MF rastreia o lifetime de cada sample (a textura so volta ao
    /// pool quando o pipeline solta a ultima referencia), eliminando a
    /// heuristica de profundidade do pool round-robin anterior.
    /// </summary>
    private void CreateSampleAllocator(IMFMediaType inputMediaType)
    {
        nint allocatorPtr = MediaFactory.MFCreateVideoSampleAllocatorEx(VideoSampleAllocatorExIid);
        var allocator = new IMFVideoSampleAllocatorEx(allocatorPtr);
        try
        {
            allocator.SetDirectXManager(_dxgiManager);

            using IMFAttributes allocatorAttributes = MediaFactory.MFCreateAttributes(2);
            // MF_SA_D3D11_USAGE = D3D11_USAGE_DEFAULT; MF_SA_D3D11_BINDFLAGS =
            // RENDER_TARGET | SHADER_RESOURCE (CopyResource como destino +
            // leitura pelo Video Processor MFT)
            allocatorAttributes.Set(TransformAttributeKeys.D3D11Usage, (uint)ResourceUsage.Default).CheckError();
            allocatorAttributes.Set(
                TransformAttributeKeys.D3D11Bindflags,
                (uint)(BindFlags.RenderTarget | BindFlags.ShaderResource)).CheckError();

            allocator.InitializeSampleAllocatorEx(
                AllocatorInitialSamples,
                AllocatorMaximumSamples,
                allocatorAttributes,
                inputMediaType);
        }
        catch
        {
            allocator.Dispose();
            throw;
        }

        _sampleAllocator = allocator;
    }

    private int AddAudioStream()
    {
        using IMFMediaType output = MediaFactory.MFCreateMediaType();
        output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
        output.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac).CheckError();
        output.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, 48000u).CheckError();
        output.Set(MediaTypeAttributeKeys.AudioNumChannels, 2u).CheckError();
        output.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u).CheckError();
        // RF-F3.11: AAC 192 kbps; o encoder AAC da MF so aceita AVG_BYTES_PER_SECOND
        // em {12000, 16000, 20000, 24000}
        output.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 24000u).CheckError();
        int index = _writer.AddStream(output);

        using IMFMediaType input = MediaFactory.MFCreateMediaType();
        input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
        input.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm).CheckError();
        input.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, 48000u).CheckError();
        input.Set(MediaTypeAttributeKeys.AudioNumChannels, 2u).CheckError();
        input.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u).CheckError();
        input.Set(MediaTypeAttributeKeys.AudioBlockAlignment, 4u).CheckError();
        input.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 192000u).CheckError();
        _writer.SetInputMediaType(index, input, null);
        return index;
    }
}
