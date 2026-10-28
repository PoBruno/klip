using System.Diagnostics;
using System.Runtime.InteropServices;
using Klip.Core.Recording;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Klip.Interop.Recording;

/// <summary>
/// Wraps the IMFSinkWriter of the mp4 file: hardware/fMP4 attributes, the video stream
/// (H.264/HEVC/AV1 fed with NV12 or ARGB32 gpu textures), the AAC stream (16-bit PCM
/// in), the video sample allocator and sample writes. NOT thread safe: every call comes
/// from the single writer task of <see cref="Mp4Recorder"/>, except
/// <see cref="TryAllocateVideoSample"/> and <see cref="GetVideoSampleTexture"/>, called
/// from the engine callback (the MF allocator is thread safe by contract).
/// Encoder tuning and faststart adapted from sskodje/ScreenRecorderLib (MIT), see
/// THIRD-PARTY-NOTICES.md.
/// </summary>
internal sealed class Mp4SinkWriter : IDisposable
{
    private static readonly Guid D3D11Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
    private static readonly Guid VideoSampleAllocatorExIid = new("545B3A48-3283-4F62-866F-A62D8F598F9F");

    /// <summary>MF_E_SAMPLEALLOCATOR_EMPTY: pool exhausted = encoder backpressure.</summary>
    public const int SampleAllocatorEmpty = unchecked((int)0xC00D4A3E);

    // MF tracks each sample lifetime (it goes back to the pool when the pipeline drops
    // the last reference). 60 fps needs a deeper pool than 30 to ride encoder hiccups
    private const int AllocatorInitialSamples = 4;

    // ----- CODECAPI guids -----
    private static readonly Guid CodecApiRateControlMode = new("1C0608E9-370C-4710-8A58-CB6181C42423");
    private static readonly Guid CodecApiMeanBitRate = new("F7222374-2144-4815-B550-A37F8E12EE52");
    private static readonly Guid CodecApiMaxBitRate = new("9651EAE4-39B9-4EBF-85EF-D7F444EC7465");
    private static readonly Guid CodecApiQualityVsSpeed = new("98332DF8-03CD-476B-89FA-3F9E442DEC9F");
    private static readonly Guid CodecApiGopSize = new("95F31B26-95A4-41AA-9303-246A7FC6EEF1");
    private static readonly Guid CodecApiBPictureCount = new("8D390AAC-DC5C-4200-B57F-814D04BABAB2");
    private static readonly Guid CodecApiScenarioInfo = new("B28A6E64-3FF9-446A-8A4B-0D7A53413236");

    private const uint RateControlPeakConstrainedVbr = 1; // eAVEncCommonRateControlMode_PeakConstrainedVBR
    private const uint ScenarioDisplayRemoting = 1;       // eAVScenarioInfo_DisplayRemoting
    private const uint ScenarioArchive = 3;               // eAVScenarioInfo_Archive

    private readonly IMFSinkWriter _writer;
    private readonly IMFDXGIDeviceManager _dxgiManager;
    private IMFVideoSampleAllocatorEx? _sampleAllocator;
    private readonly int _videoStreamIndex;
    private readonly int _audioStreamIndex = -1;
    private readonly bool _nv12Input;
    private bool _disposed;

    public int Width { get; }

    public int Height { get; }

    /// <param name="outputPath">Target .mp4.</param>
    /// <param name="device">Capture device (textures are shared with it).</param>
    /// <param name="width">Frame width; input and output have the same size.</param>
    /// <param name="height">Frame height.</param>
    /// <param name="nv12Input">true = NV12 textures straight into the encoder; false = ARGB32
    /// and MF inserts its own color converter.</param>
    /// <param name="plan">Codec, fps, bitrate and tuning.</param>
    /// <param name="fragmentedMp4">fMP4 (crash safe) or classic mp4 with moov first.</param>
    /// <param name="withAudio">Add the AAC track.</param>
    public Mp4SinkWriter(
        string outputPath,
        ID3D11Device device,
        int width,
        int height,
        bool nv12Input,
        VideoEncodingPlan plan,
        bool fragmentedMp4,
        bool withAudio)
    {
        Width = width;
        Height = height;
        _nv12Input = nv12Input;

        // the SinkWriter and the recorder share the immediate context of the capture
        // device, so multithread protection is a must
        using (var multithread = device.QueryInterfaceOrNull<ID3D11Multithread>())
            multithread?.SetMultithreadProtected(true);

        _dxgiManager = MediaFactory.MFCreateDXGIDeviceManager();
        try
        {
            _dxgiManager.ResetDevice(device).CheckError();

            using IMFAttributes attributes = MediaFactory.MFCreateAttributes(6);
            // SinkWriter throttling OFF: with it on, the video WriteSample BLOCKS when the
            // encoder queue fills and, since audio and video share one write gate in
            // Mp4Recorder, audio starved until the channel dropped blocks (each lost block
            // = a 10 ms pop). Texture reuse is covered by the sample allocator instead.
            // LowLatency stays off too: for files it costs quality.
            attributes.Set(SinkWriterAttributeKeys.DisableThrottling, true).CheckError();
            attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, true).CheckError();
            attributes.Set(SinkWriterAttributeKeys.D3DManager, _dxgiManager).CheckError();
            if (fragmentedMp4)
            {
                attributes.Set(TranscodeAttributeKeys.TranscodeContainertype, TranscodeContainerTypeGuids.Fmpeg4).CheckError();
            }
            else if (plan.Codec == VideoCodec.H264)
            {
                // classic mp4 with moov BEFORE mdat (faststart). Not for hevc: the sink
                // fails the Finalize with it and leaves a file without moov
                attributes.Set(Mpeg4MediaSinkAttributeKeys.MoovBeforeMdat, 1u).CheckError();
            }

            ApplyEncoderConfig(attributes, plan);

            _writer = MediaFactory.MFCreateSinkWriterFromURL(outputPath, null, attributes);
        }
        catch
        {
            _dxgiManager.Dispose();
            throw;
        }

        try
        {
            _videoStreamIndex = AddVideoStream(plan);
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
    /// A video sample from the MF pool, or null when the pool is empty (encoder behind);
    /// the caller picks between a short retry and a counted drop.
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
    /// D3D11 texture behind an allocated sample: buffer 0 -> IMFDXGIBuffer ->
    /// ID3D11Texture2D. Dispose the wrapper (only drops the local COM reference).
    /// </summary>
    public ID3D11Texture2D GetVideoSampleTexture(IMFSample sample)
    {
        using IMFMediaBuffer buffer = sample.GetBufferByIndex(0);
        using IMFDXGIBuffer dxgiBuffer = buffer.QueryInterface<IMFDXGIBuffer>();
        nint texturePtr = dxgiBuffer.GetResource(D3D11Texture2DIid);
        return new ID3D11Texture2D(texturePtr);
    }

    /// <summary>Sends an allocated and filled video sample to the encoder.</summary>
    public void WriteVideoSample(IMFSample sample)
    {
        // the allocator hands out buffers with CurrentLength = 0 and the encoder wants the
        // contiguous size (E_INVALIDARG otherwise, same as ScreenRecorderLib notes)
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
    /// Sends a mixed 16-bit 48 kHz stereo PCM block. <paramref name="byteOffset"/> lets
    /// the muxer trim the start of blocks that overlap silence already injected.
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

    /// <summary>Video stream statistics (encoder backlog, queued bytes).</summary>
    public SinkWriterStatistics GetVideoStatistics() => _writer.GetStatistics(_videoStreamIndex);

    /// <summary>Finalizes the container. Can take seconds, keep it off the ui thread.</summary>
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
            }

            _sampleAllocator.Dispose();
            _sampleAllocator = null;
        }

        _dxgiManager.Dispose();
    }

    // ----- encoder config -----

    /// <summary>
    /// Builds the IPropertyStore with the CODECAPI values and attaches it as
    /// MF_SINK_WRITER_ENCODER_CONFIG. Never throws: encoders differ in what they accept,
    /// a failure just leaves the encoder defaults.
    /// </summary>
    private static void ApplyEncoderConfig(IMFAttributes attributes, VideoEncodingPlan plan)
    {
        try
        {
            var store = NativeMethods.PSCreateMemoryPropertyStore(typeof(NativeMethods.IPropertyStore).GUID);
            try
            {
                // capped vbr: pure CQ had no ceiling and blew up files on high motion
                SetStoreValue(store, CodecApiRateControlMode, RateControlPeakConstrainedVbr);
                SetStoreValue(store, CodecApiMeanBitRate, (uint)plan.BitrateKbps * 1000);
                SetStoreValue(store, CodecApiMaxBitRate, (uint)plan.PeakBitrateKbps * 1000);

                var tuning = plan.Tuning;
                SetStoreValue(store, CodecApiQualityVsSpeed, (uint)tuning.QualityVsSpeed);
                SetStoreValue(store, CodecApiGopSize, (uint)tuning.GopFrames);
                SetStoreValue(store, CodecApiBPictureCount, (uint)tuning.BFrames);
                SetStoreValue(store, CodecApiScenarioInfo, tuning.ScreenContent ? ScenarioDisplayRemoting : ScenarioArchive);

                // applied as a BATCH: the SinkWriter hands the whole store to the encoder,
                // which ignores what it does not support
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
            Trace.WriteLine($"Mp4SinkWriter: ENCODER_CONFIG unavailable, going with encoder defaults. ({ex.Message})");
        }
    }

    /// <summary>Writes a VT_UI4; PROPERTYKEY = {fmtid = CODECAPI guid, pid = 0}.</summary>
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
            // one property failed: skip it
        }
    }

    // ----- streams -----

    private int AddVideoStream(VideoEncodingPlan plan)
    {
        int fps = plan.Fps;

        using IMFMediaType output = MediaFactory.MFCreateMediaType();
        output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
        output.Set(MediaTypeAttributeKeys.Subtype, VideoEncoderCatalog.SubtypeOf(plan.Codec)).CheckError();
        output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)plan.BitrateKbps * 1000).CheckError();
        output.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive).CheckError();
        MediaFactory.MFSetAttributeSize(output, MediaTypeAttributeKeys.FrameSize, (uint)Width, (uint)Height).CheckError();
        MediaFactory.MFSetAttributeRatio(output, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1).CheckError();
        MediaFactory.MFSetAttributeRatio(output, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1).CheckError();
        // without an explicit profile some encoders drop to h264 Baseline
        if (plan.Codec == VideoCodec.H264)
            output.Set(MediaTypeAttributeKeys.Mpeg2Profile, 100u).CheckError(); // eAVEncH264VProfile_High
        else if (plan.Codec == VideoCodec.Hevc)
            output.Set(MediaTypeAttributeKeys.Mpeg2Profile, 1u).CheckError();   // eAVEncH265VProfile_Main_420_8
        // explicit bt.709 limited range; without it small regions may come out bt.601
        output.Set(MediaTypeAttributeKeys.YuvMatrix, 1u).CheckError();         // MFVideoTransferMatrix_BT709
        output.Set(MediaTypeAttributeKeys.VideoNominalRange, 2u).CheckError(); // MFNominalRange_16_235
        output.Set(MediaTypeAttributeKeys.VideoPrimaries, 2u).CheckError();    // MFVideoPrimaries_BT709
        output.Set(MediaTypeAttributeKeys.TransferFunction, 5u).CheckError();  // MFVideoTransFunc_709
        int index = _writer.AddStream(output);

        using IMFMediaType input = MediaFactory.MFCreateMediaType();
        input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
        input.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive).CheckError();
        if (_nv12Input)
        {
            // already converted on the gpu: same colorimetry as the output, no MF converter
            input.Set(MediaTypeAttributeKeys.Subtype, VideoEncoderCatalog.Nv12Subtype).CheckError();
            input.Set(MediaTypeAttributeKeys.DefaultStride, (uint)Width).CheckError();
            input.Set(MediaTypeAttributeKeys.YuvMatrix, 1u).CheckError();
            input.Set(MediaTypeAttributeKeys.VideoNominalRange, 2u).CheckError();
        }
        else
        {
            // ARGB32 (BGRA in memory); MF puts its video processor in front of the encoder.
            // The MF default for rgb is bottom-up, a positive stride keeps it top-down
            input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Argb32).CheckError();
            input.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(Width * 4)).CheckError();
            input.Set(MediaTypeAttributeKeys.VideoNominalRange, 1u).CheckError(); // MFNominalRange_0_255
        }

        input.Set(MediaTypeAttributeKeys.VideoPrimaries, 2u).CheckError();
        input.Set(MediaTypeAttributeKeys.TransferFunction, 5u).CheckError();
        MediaFactory.MFSetAttributeSize(input, MediaTypeAttributeKeys.FrameSize, (uint)Width, (uint)Height).CheckError();
        MediaFactory.MFSetAttributeRatio(input, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1).CheckError();
        MediaFactory.MFSetAttributeRatio(input, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1).CheckError();
        _writer.SetInputMediaType(index, input, null);

        CreateSampleAllocator(input, Math.Max(10, fps / 3));
        return index;
    }

    /// <summary>
    /// IMFVideoSampleAllocatorEx on the SAME device manager as the SinkWriter and the
    /// input media type of the video stream.
    /// </summary>
    private void CreateSampleAllocator(IMFMediaType inputMediaType, int maximumSamples)
    {
        // render target + shader resource works everywhere for BGRA; some drivers refuse
        // it for NV12, then the allocator gets no bind flags (we only copy into it)
        var bindings = new List<uint> { (uint)(BindFlags.RenderTarget | BindFlags.ShaderResource) };
        if (_nv12Input)
            bindings.Add(0);

        Exception? last = null;
        foreach (var bindFlags in bindings)
        {
            nint allocatorPtr = MediaFactory.MFCreateVideoSampleAllocatorEx(VideoSampleAllocatorExIid);
            var allocator = new IMFVideoSampleAllocatorEx(allocatorPtr);
            try
            {
                allocator.SetDirectXManager(_dxgiManager);

                using IMFAttributes allocatorAttributes = MediaFactory.MFCreateAttributes(2);
                allocatorAttributes.Set(TransformAttributeKeys.D3D11Usage, (uint)ResourceUsage.Default).CheckError();
                if (bindFlags != 0)
                    allocatorAttributes.Set(TransformAttributeKeys.D3D11Bindflags, bindFlags).CheckError();

                allocator.InitializeSampleAllocatorEx(
                    AllocatorInitialSamples,
                    maximumSamples,
                    allocatorAttributes,
                    inputMediaType);
                _sampleAllocator = allocator;
                return;
            }
            catch (Exception ex)
            {
                allocator.Dispose();
                last = ex;
            }
        }

        throw last!;
    }

    private int AddAudioStream()
    {
        using IMFMediaType output = MediaFactory.MFCreateMediaType();
        output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
        output.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac).CheckError();
        output.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, 48000u).CheckError();
        output.Set(MediaTypeAttributeKeys.AudioNumChannels, 2u).CheckError();
        output.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u).CheckError();
        // AAC 192 kbps; the MF AAC encoder only takes AVG_BYTES_PER_SECOND in
        // {12000, 16000, 20000, 24000}
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
