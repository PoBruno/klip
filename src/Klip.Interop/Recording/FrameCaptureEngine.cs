using System.Diagnostics;
using System.Runtime.InteropServices;
using Klip.Core.Recording;
using Microsoft.Win32.SafeHandles;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace Klip.Interop.Recording;

/// <summary>
/// Frame capture engine on top of Windows.Graphics.Capture. Captures the monitor that
/// holds the region, crops on the gpu (even sizes) and hands frames out two ways:
/// <list type="bullet">
/// <item><see cref="GpuFrameArrived"/>: D3D11 texture on the shared <see cref="Device"/>,
/// for the mp4 encoder. BGRA at the crop size, or NV12 at the output size when
/// <see cref="FrameCaptureOptions.Nv12Output"/> is on and the gpu video processor works.</item>
/// <item><see cref="CpuFrameArrived"/>: BGRA on the cpu at a fixed rate, only with
/// <see cref="FrameCaptureOptions.FixedFps"/> (gif mode).</item>
/// </list>
/// Backpressure by construction: at most <see cref="GpuPoolSize"/> textures in flight;
/// if the consumer did not give them back (Dispose), new frames are dropped. Event
/// handlers run on free threaded pool threads and must be quick.
/// </summary>
public sealed class FrameCaptureEngine : IAsyncDisposable
{
    private const int GpuPoolSize = 3;
    private const int FramePoolBuffers = 2;

    // the wgc frame pool only has 2 surfaces, so a handful of cached views is plenty
    private const int MaxCachedInputViews = 8;

    private readonly object _lock = new();

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private SizeInt32 _poolSize;
    private nint _hMonitor;

    // crop in physical px RELATIVE to the monitor origin (rcMonitor)
    private int _cropLeft;
    private int _cropTop;
    private int _cropWidth;
    private int _cropHeight;

    // size of what goes out to the gpu consumer (crop size in BGRA mode)
    private int _outWidth;
    private int _outHeight;
    private bool _nv12;

    private ID3D11Texture2D?[] _texturePool = [];
    private bool[] _textureInUse = [];

    // cfr mode: last live frame + staging for the cpu readback
    private ID3D11Texture2D? _cfrLatest;
    private ID3D11Texture2D? _staging;
    private bool _cfrHasFrame;
    private CancellationTokenSource? _cfrCts;
    private Task? _cfrLoop;
    private int _maxCatchUpFrames = 4;

    // catch-up diagnostics: duplicates emitted to fill late grid slots, and slots
    // beyond the cap (each one is a visible hold in the player)
    private long _cfrCatchUpDuplicates;
    private long _cfrLostGridSlots;

    // pre-made black source to clear the target when the clamped box < crop (BGRA mode)
    private ID3D11Texture2D? _blackTexture;

    // single BGRA buffer reused between CpuFrames (saves ~8 MB of garbage per frame)
    private byte[]? _cpuBuffer;

    // video processor (NV12 mode): one blt does crop + scale + BGRA -> NV12
    private ID3D11VideoDevice? _videoDevice;
    private ID3D11VideoContext1? _videoContext;
    private ID3D11VideoProcessorEnumerator? _vpEnumerator;
    private ID3D11VideoProcessor? _videoProcessor;
    private ID3D11VideoProcessorOutputView? _vpOutputView;
    private readonly Dictionary<nint, ID3D11VideoProcessorInputView> _vpInputViews = [];

    // emission gate during device recovery: cfr loop and ProcessFrame check it under
    // _lock; handlers in flight are counted so recovery waits before dropping the device
    private bool _emitPaused;
    private int _activeEmits;

    private FrameCaptureOptions _options = new();
    private bool _running;
    private bool _deviceRecoveryAttempted;
    private bool _disposed;

    // live cursor toggle: the wish survives a session rebuild and gets re-applied on
    // the next FrameArrived
    private bool _cursorCaptureDesired = true;
    private volatile bool _cursorResyncPending;

    // QPC of the last wgc FrameArrived (consumer watchdog). wgc only delivers frames
    // when the content CHANGES, so a still screen legitimately goes quiet
    private long _lastWgcFrameTimestamp;

    /// <summary>
    /// True when the OS supports Windows.Graphics.Capture (17763+ on our floor) and
    /// capture sessions are enabled.
    /// </summary>
    public static bool IsSupported
    {
        get
        {
            try
            {
                return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)
                    && GraphicsCaptureSession.IsSupported();
            }
            catch
            {
                return false; // no WinRT (server core and friends)
            }
        }
    }

    /// <summary>
    /// D3D11 device used by the capture, shared with the mp4 encoder. Valid after
    /// <see cref="StartAsync"/>. Can be REPLACED after a device lost recovery, see
    /// <see cref="DeviceRecreated"/>.
    /// </summary>
    public ID3D11Device Device =>
        _device ?? throw new InvalidOperationException("Device only available after StartAsync.");

    /// <summary>True when gpu frames are NV12 at the requested output size.</summary>
    public bool IsNv12Output
    {
        get
        {
            lock (_lock)
                return _nv12;
        }
    }

    /// <summary>Size of the gpu frames (valid after start).</summary>
    public VideoSize OutputSize
    {
        get
        {
            lock (_lock)
                return new VideoSize(_outWidth, _outHeight);
        }
    }

    /// <summary>
    /// Mp4 path: cropped frame on the gpu. The consumer owns the texture (Dispose gives it
    /// back to the pool). In CFR mode timestamps ALWAYS sit on the <c>n * (1/FPS)</c> grid
    /// anchored on the real clock. Late ticks are filled with DUPLICATES of the last frame
    /// (up to ~130 ms worth per tick, see <see cref="VideoEncodingMath.MaxCatchUpFrames"/>);
    /// beyond that the oldest slots are lost, see <see cref="CfrLostGridSlots"/>.
    /// </summary>
    public event Action<GpuFrame>? GpuFrameArrived;

    /// <summary>
    /// Gif path: BGRA frame on the cpu at a fixed rate (only with FixedFps set and BGRA
    /// output). Invoked synchronously; <see cref="CpuFrame.Bgra"/> is REUSED between
    /// frames, copy what you need before returning. Grid indexes may be skipped (the gif
    /// delay comes from the timestamp difference, so a skip is not a stall).
    /// </summary>
    public event Action<CpuFrame>? CpuFrameArrived;

    /// <summary>Duplicates emitted by the cfr catch-up. Valid during and after the session.</summary>
    public long CfrCatchUpDuplicates => Volatile.Read(ref _cfrCatchUpDuplicates);

    /// <summary>
    /// Cfr grid slots lost (delay above the catch-up cap, or texture pool exhausted).
    /// Each one is a 1/FPS hold in the final file.
    /// </summary>
    public long CfrLostGridSlots => Volatile.Read(ref _cfrLostGridSlots);

    /// <summary>Unrecoverable failure. The consumer should finalize the file and call StopAsync.</summary>
    public event Action<FrameCaptureError>? Failed;

    /// <summary>
    /// Raised after the device was rebuilt following a device lost (one attempt).
    /// <see cref="Device"/> now points to the new one; old <see cref="GpuFrame"/>
    /// textures are no good for copies anymore.
    /// </summary>
    public event Action? DeviceRecreated;

    /// <summary>Wished cursor state (getter for the toolbar).</summary>
    public bool IsCursorCaptureEnabled
    {
        get
        {
            lock (_lock)
            {
                return _cursorCaptureDesired;
            }
        }
    }

    /// <summary>Time since the last wgc FrameArrived. Zero before the first frame.</summary>
    public TimeSpan TimeSinceLastWgcFrame
    {
        get
        {
            long last = Volatile.Read(ref _lastWgcFrameTimestamp);
            return last == 0
                ? TimeSpan.Zero
                : Stopwatch.GetElapsedTime(last);
        }
    }

    /// <summary>
    /// Size the engine will crop for <paramref name="region"/> (clamped to its monitor,
    /// rounded down to even), without starting anything.
    /// </summary>
    public static VideoSize MeasureCrop(RecordingRegion region)
    {
        ResolveMonitor(region, out var monitorRect);
        var crop = ComputeCrop(region, monitorRect);
        return new VideoSize(crop.Width, crop.Height);
    }

    /// <summary>
    /// Live cursor on/off through GraphicsCaptureSession.IsCursorCaptureEnabled. The wish
    /// is stored and re-applied on the next FrameArrived (covers a session rebuilt by
    /// recovery); try/catch covers builds without the property.
    /// </summary>
    public void SetCursorCaptureEnabled(bool enabled)
    {
        lock (_lock)
        {
            _cursorCaptureDesired = enabled;
            _cursorResyncPending = true;
            ApplyCursorCaptureLocked();
        }
    }

    private void ApplyCursorCaptureLocked()
    {
        var session = _session;
        if (session is null || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            return;

        try
        {
            session.IsCursorCaptureEnabled = _cursorCaptureDesired;
        }
        catch (Exception)
        {
            // session going away or no property: retried on the next FrameArrived
            return;
        }

        _cursorResyncPending = false;
    }

    /// <summary>Starts capturing the monitor that holds the center of the region.</summary>
    /// <param name="region">Region in physical px, virtual desktop coords; clamped to the monitor.</param>
    /// <param name="options">Cursor, border, pacing and output format.</param>
    public async Task StartAsync(RecordingRegion region, FrameCaptureOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(options);
        if (region.Width < 2 || region.Height < 2)
            throw new ArgumentException("Region must be at least 2x2 physical px.", nameof(region));
        if (options.FixedFps is <= 0)
            throw new ArgumentException("FixedFps must be positive or null (VFR).", nameof(options));
        if (!IsSupported)
            throw new PlatformNotSupportedException("Windows.Graphics.Capture is not available on this system.");

        // D3D11CreateDevice (with the WARP fallback) can take seconds: heavy setup off the
        // calling (ui) thread. GraphicsCaptureItem and the frame pool are free threaded.
        GraphicsCaptureSession session = await Task.Run(() =>
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_running)
                    throw new InvalidOperationException("Capture already started.");

                _options = options;
                _deviceRecoveryAttempted = false;
                _emitPaused = false;
                _cursorCaptureDesired = options.CaptureCursor;
                _cursorResyncPending = false;
                _maxCatchUpFrames = VideoEncodingMath.MaxCatchUpFrames(options.FixedFps ?? 30);
                Volatile.Write(ref _lastWgcFrameTimestamp, 0);
                Volatile.Write(ref _cfrCatchUpDuplicates, 0);
                Volatile.Write(ref _cfrLostGridSlots, 0);

                // the item comes from the monitor today; a window target only needs a
                // different item and a crop equal to the content size
                _hMonitor = ResolveMonitor(region, out var monitorRect);
                (_cropLeft, _cropTop, _cropWidth, _cropHeight) = ComputeCrop(region, monitorRect);

                CreateDevice(_hMonitor);
                _item = CaptureInterop.CreateItemForMonitor(_hMonitor);
                _item.Closed += OnItemClosed;

                CreateFramePool();
                ChooseOutputFormat();
                CreateCropResources();
                _session = _framePool!.CreateCaptureSession(_item);
                _running = true;
                return _session;
            }
        }).ConfigureAwait(false);

        // outside the lock: RequestAccessAsync is async
        await ConfigureSessionAsync(session, options, options.CaptureCursor).ConfigureAwait(false);

        // StopAsync may have run during the awaits; without this check the cfr loop
        // would be left running forever
        lock (_lock)
        {
            if (!_running || !ReferenceEquals(_session, session))
                return;

            session.StartCapture();

            if (options.FixedFps is int fps)
            {
                _cfrCts = new CancellationTokenSource();
                var token = _cfrCts.Token;
                // LongRunning: the loop blocks on a high resolution waitable timer for
                // the whole recording; a dedicated thread keeps the pool free
                _cfrLoop = Task.Factory.StartNew(
                    () => CfrLoop(fps, token),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }
        }
    }

    /// <summary>Ends the session and frees capture resources (the device lives until DisposeAsync).</summary>
    public async Task StopAsync()
    {
        Task? loop;
        lock (_lock)
        {
            if (!_running && _cfrLoop is null && _session is null)
                return;
            _running = false;
            _cfrCts?.Cancel();
            loop = _cfrLoop;
            _cfrLoop = null;
        }

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        lock (_lock)
        {
            StopCore();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        await StopAsync().ConfigureAwait(false);
        lock (_lock)
        {
            _disposed = true;
            DisposeDevice();
        }
    }

    // ----- setup -----

    private static nint ResolveMonitor(RecordingRegion region, out NativeMethods.RECT monitorRect)
    {
        var center = new NativeMethods.POINT
        {
            x = region.Left + region.Width / 2,
            y = region.Top + region.Height / 2,
        };
        nint hMonitor = NativeMethods.MonitorFromPoint(center, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (hMonitor == 0 || !NativeMethods.GetMonitorInfo(hMonitor, ref info))
            throw new InvalidOperationException("Could not resolve the monitor of the region.");
        monitorRect = info.rcMonitor;
        return hMonitor;
    }

    private static (int Left, int Top, int Width, int Height) ComputeCrop(RecordingRegion region, NativeMethods.RECT monitor)
    {
        int monWidth = monitor.right - monitor.left;
        int monHeight = monitor.bottom - monitor.top;

        // a region never spans monitors: clamp to this one
        int left = Math.Clamp(region.Left - monitor.left, 0, Math.Max(0, monWidth - 2));
        int top = Math.Clamp(region.Top - monitor.top, 0, Math.Max(0, monHeight - 2));
        int width = Math.Min(region.Width, monWidth - left);
        int height = Math.Min(region.Height, monHeight - top);

        // even sizes, rounded down (NV12 needs it)
        return (left, top, Math.Max(2, width & ~1), Math.Max(2, height & ~1));
    }

    private void CreateDevice(nint hMonitor)
    {
        // VideoSupport is required for the IMFDXGIDeviceManager to enable hardware MFTs
        // and for the video processor; without it the encoder quietly goes software
        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;

        // device on the adapter that drives the monitor: on hybrid laptops the default
        // adapter may be the other gpu, which costs a cross adapter copy per frame
        ID3D11Device? device = null;
        using (var adapter = FindAdapterForMonitor(hMonitor))
        {
            if (adapter is not null)
            {
                var result = D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, flags, null, out device);
                if (result.Failure)
                    device = null;
            }
        }

        if (device is null)
        {
            var result = D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, null, out device);
            if (result.Failure || device is null)
            {
                // no gpu (rdp, broken driver): WARP keeps capture working
                D3D11.D3D11CreateDevice(null, DriverType.Warp, flags, null, out device).CheckError();
            }
        }

        _device = device!;
        _context = _device.ImmediateContext;

        if (_options?.HighPriority == true)
            RaiseGpuThreadPriority(_device);

        // the immediate context is shared by 3 threads (wgc, cfr loop, encoder) from
        // the first frame on, so protect it right here
        using (var multithread = _context.QueryInterfaceOrNull<ID3D11Multithread>())
            multithread?.SetMultithreadProtected(true);

        _winrtDevice = CaptureInterop.CreateWinRtDevice(_device);
    }

    // +2 out of -7..7: enough to win ties against a game at normal priority without
    // starving it. No privilege needed for this range, and a failure changes nothing
    private const int GpuThreadPriority = 2;

    private static void RaiseGpuThreadPriority(ID3D11Device device)
    {
        try
        {
            using var dxgiDevice = device.QueryInterfaceOrNull<IDXGIDevice>();
            if (dxgiDevice is null)
                return;
            var result = dxgiDevice.SetGPUThreadPriority(GpuThreadPriority);
            Trace.WriteLine($"FrameCaptureEngine: SetGPUThreadPriority({GpuThreadPriority}) = 0x{result.Code:X8}");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"FrameCaptureEngine: SetGPUThreadPriority falhou: {ex.Message}");
        }
    }

    private static IDXGIAdapter1? FindAdapterForMonitor(nint hMonitor)
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1 adapter).Success; i++)
            {
                for (uint j = 0; adapter.EnumOutputs(j, out IDXGIOutput output).Success; j++)
                {
                    using (output)
                    {
                        if (output.Description.Monitor == hMonitor)
                            return adapter;
                    }
                }

                adapter.Dispose();
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"FrameCaptureEngine: adapter lookup failed, using the default one. ({ex.Message})");
        }

        return null;
    }

    private void CreateFramePool()
    {
        _poolSize = _item!.Size;
        // free threaded (not tied to the ui thread), BGRA, 2 buffers
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winrtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            FramePoolBuffers,
            _poolSize);
        _framePool.FrameArrived += OnFrameArrived;
    }

    /// <summary>
    /// NV12 needs cfr mode and a working video processor; anything missing keeps the
    /// BGRA path at the crop size, which is what recordings always used.
    /// </summary>
    private void ChooseOutputFormat()
    {
        _nv12 = false;
        _outWidth = _cropWidth;
        _outHeight = _cropHeight;

        if (!_options.Nv12Output || _options.FixedFps is null)
            return;

        var size = _options.OutputSize ?? new VideoSize(_cropWidth, _cropHeight);
        int width = Math.Max(2, Math.Min(size.Width, _cropWidth) & ~1);
        int height = Math.Max(2, Math.Min(size.Height, _cropHeight) & ~1);

        try
        {
            CreateVideoProcessor(width, height, _options.FixedFps.Value);
            _nv12 = true;
            _outWidth = width;
            _outHeight = height;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"FrameCaptureEngine: gpu video processor unavailable, keeping the BGRA path. ({ex.Message})");
            DisposeVideoProcessor();
        }
    }

    private void CreateVideoProcessor(int width, int height, int fps)
    {
        _videoDevice = _device!.QueryInterface<ID3D11VideoDevice>();
        _videoContext = _context!.QueryInterface<ID3D11VideoContext1>();

        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational((uint)fps, 1),
            InputWidth = (uint)Math.Max(_poolSize.Width, _cropWidth),
            InputHeight = (uint)Math.Max(_poolSize.Height, _cropHeight),
            OutputFrameRate = new Rational((uint)fps, 1),
            OutputWidth = (uint)width,
            OutputHeight = (uint)height,
            Usage = VideoUsage.OptimalSpeed,
        };
        _videoDevice.CreateVideoProcessorEnumerator(ref content, out _vpEnumerator).CheckError();

        _vpEnumerator!.CheckVideoProcessorFormat(Format.B8G8R8A8_UNorm, out var inputSupport).CheckError();
        _vpEnumerator.CheckVideoProcessorFormat(Format.NV12, out var outputSupport).CheckError();
        if ((inputSupport & VideoProcessorFormatSupport.Input) == 0 || (outputSupport & VideoProcessorFormatSupport.Output) == 0)
            throw new NotSupportedException("Video processor cannot do BGRA -> NV12 here.");

        _videoDevice.CreateVideoProcessor(_vpEnumerator, 0, out _videoProcessor).CheckError();

        var vp = _videoProcessor!;
        _videoContext.VideoProcessorSetStreamFrameFormat(vp, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamAutoProcessingMode(vp, 0, false);
        _videoContext.VideoProcessorSetStreamOutputRate(vp, 0, VideoProcessorOutputRate.Normal, false, null);
        // desktop is full range rgb; the file is tagged bt.709 limited range
        _videoContext.VideoProcessorSetStreamColorSpace1(vp, 0, ColorSpaceType.RgbFullG22NoneP709);
        _videoContext.VideoProcessorSetOutputColorSpace1(vp, ColorSpaceType.YcbcrStudioG22LeftP709);
        // letterbox bars
        _videoContext.VideoProcessorSetOutputBackgroundColor(vp, false, new VideoColor
        {
            Rgba = new VideoColorRgba { R = 0, G = 0, B = 0, A = 1 },
        });
    }

    private void CreateCropResources()
    {
        // B8G8R8A8 pool: on an HDR monitor the system hands over SDR content
        var desc = _nv12
            ? new Texture2DDescription
            {
                Width = (uint)_outWidth,
                Height = (uint)_outHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            }
            : new Texture2DDescription
            {
                Width = (uint)_cropWidth,
                Height = (uint)_cropHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            };

        _texturePool = new ID3D11Texture2D?[GpuPoolSize];
        _textureInUse = new bool[GpuPoolSize];
        for (int i = 0; i < GpuPoolSize; i++)
            _texturePool[i] = _device!.CreateTexture2D(desc);

        if (_nv12)
        {
            // the processor writes straight into the "latest" texture; background
            // color takes care of the bars, no black source needed
            _cfrLatest = _device!.CreateTexture2D(desc);
            _cfrHasFrame = false;
            var viewDesc = new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
            };
            _videoDevice!.CreateVideoProcessorOutputView(_cfrLatest, _vpEnumerator, viewDesc, out _vpOutputView).CheckError();
            return;
        }

        // black source made ONCE: clears the target before the crop when the clamped
        // box got smaller than the crop (content shrank after a mode change)
        int stride = _cropWidth * 4;
        var zero = new byte[stride * _cropHeight];
        var pin = GCHandle.Alloc(zero, GCHandleType.Pinned);
        try
        {
            var init = new SubresourceData(pin.AddrOfPinnedObject(), (uint)stride);
            _blackTexture = _device!.CreateTexture2D(desc, [init]);
        }
        finally
        {
            pin.Free();
        }

        if (_options.FixedFps is not null)
        {
            _cfrLatest = _device!.CreateTexture2D(desc);
            _cfrHasFrame = false;

            var stagingDesc = desc with
            {
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
            };
            _staging = _device!.CreateTexture2D(stagingDesc);
            _cpuBuffer = new byte[stride * _cropHeight];
        }
    }

    private static async Task ConfigureSessionAsync(
        GraphicsCaptureSession session,
        FrameCaptureOptions options,
        bool captureCursor)
    {
        // cursor needs 19041+ (our floor is 17763). The value is the WISHED state, which
        // may have been toggled live before a session rebuild
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            try
            {
                session.IsCursorCaptureEnabled = captureCursor;
            }
            catch
            {
            }
        }

        // dropping the yellow border needs consent (20348+). Denied or throwing (not
        // packaged) = live with the border
        if (options.RequestBorderless && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
        {
            try
            {
                // RequestAccessAsync goes through a broker and can hang: short timeout
                var accessTask = GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask();
                var completed = await Task.WhenAny(accessTask, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
                if (ReferenceEquals(completed, accessTask)
                    && await accessTask.ConfigureAwait(false) == AppCapabilityAccessStatus.Allowed)
                {
                    session.IsBorderRequired = false;
                }
            }
            catch
            {
            }
        }

        // 24H2+: limit the rate at the source (fewer frames to throw away on 144 Hz+)
        if (options.FixedFps is int fps && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
        {
            try
            {
                if (ApiInformation.IsPropertyPresent(
                        "Windows.Graphics.Capture.GraphicsCaptureSession", "MinUpdateInterval"))
                {
                    session.MinUpdateInterval = TimeSpan.FromSeconds(1.0 / fps);
                }
            }
            catch
            {
            }
        }
    }

    // ----- frame pipeline -----

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object? args)
    {
        try
        {
            ProcessFrame(sender);
        }
        catch (Exception ex)
        {
            HandlePipelineError(ex);
        }
    }

    private void ProcessFrame(Direct3D11CaptureFramePool sender)
    {
        GpuFrame? emit = null;
        Action<GpuFrame>? gpuHandler = null;

        lock (_lock)
        {
            // TryGetNextFrame under _lock: outside it races with _framePool.Dispose in
            // StopCore/recovery. An old pool (after recovery) is ignored
            if (!_running || _emitPaused || _context is null || !ReferenceEquals(sender, _framePool))
                return;

            Volatile.Write(ref _lastWgcFrameTimestamp, Stopwatch.GetTimestamp());

            if (_cursorResyncPending)
                ApplyCursorCaptureLocked();

            var frame = sender.TryGetNextFrame();
            if (frame is null)
                return;

            try
            {
                var contentSize = frame.ContentSize;
                if (contentSize.Width != _poolSize.Width || contentSize.Height != _poolSize.Height)
                {
                    // monitor resolution/rotation changed -> recreate the pool
                    _poolSize = contentSize;
                    ClearInputViews();
                    _framePool!.Recreate(
                        _winrtDevice,
                        DirectXPixelFormat.B8G8R8A8UIntNormalized,
                        FramePoolBuffers,
                        contentSize);
                    return; // next frame already comes at the new size
                }

                using var sourceTexture = CaptureInterop.GetTexture(frame.Surface);
                var box = ComputeSourceBox(contentSize, out bool boxSmallerThanCrop);
                if (box is null)
                    return; // region ended up outside the monitor after a mode change

                if (_options.FixedFps is not null)
                {
                    // cfr: only refresh the "latest" frame; the pacing loop emits
                    if (_nv12)
                    {
                        if (!TryBlitNv12(sourceTexture, box.Value))
                            return;
                    }
                    else
                    {
                        if (boxSmallerThanCrop)
                            _context.CopyResource(_cfrLatest!, _blackTexture!);
                        _context.CopySubresourceRegion(_cfrLatest!, 0, 0, 0, 0, sourceTexture, 0, box.Value);
                    }

                    _cfrHasFrame = true;
                }
                else
                {
                    // vfr: gpu crop with backpressure. Grab the delegate BEFORE renting a
                    // texture: unsubscribing in between must not leak a pool texture
                    gpuHandler = GpuFrameArrived;
                    if (gpuHandler is null || !TryAcquireTexture(out var target))
                        return;
                    if (boxSmallerThanCrop)
                        _context.CopyResource(target, _blackTexture!);
                    _context.CopySubresourceRegion(target, 0, 0, 0, 0, sourceTexture, 0, box.Value);
                    emit = new GpuFrame(target, frame.SystemRelativeTime, ReturnTexture);
                }
            }
            finally
            {
                // give the buffer back to the frame pool right away
                frame.Dispose();
            }

            if (emit is not null)
                Interlocked.Increment(ref _activeEmits);
        }

        if (emit is null)
            return;

        try
        {
            gpuHandler!.Invoke(emit);
        }
        catch
        {
            emit.Dispose();
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _activeEmits);
        }
    }

    /// <summary>
    /// One video processor pass: source rect = crop box, dest rect = letterboxed area
    /// of the output, BGRA -> NV12. Under _lock. If the very first blt fails (driver
    /// says no to the wgc surface, for example) the engine drops back to BGRA before any
    /// frame went out; after that a failure is a pipeline error like any other.
    /// </summary>
    private bool TryBlitNv12(ID3D11Texture2D source, Box box)
    {
        try
        {
            var view = GetInputView(source);
            int boxWidth = box.Right - box.Left;
            int boxHeight = box.Bottom - box.Top;
            var dest = VideoEncodingMath.Letterbox(boxWidth, boxHeight, _outWidth, _outHeight);

            var vp = _videoProcessor!;
            _videoContext!.VideoProcessorSetStreamSourceRect(vp, 0, true,
                new RawRect(box.Left, box.Top, box.Right, box.Bottom));
            _videoContext.VideoProcessorSetStreamDestRect(vp, 0, true,
                new RawRect(dest.Left, dest.Top, dest.Left + dest.Width, dest.Top + dest.Height));

            var stream = new VideoProcessorStream
            {
                Enable = true,
                OutputIndex = 0,
                InputFrameOrField = 0,
                InputSurface = view,
            };
            _videoContext.VideoProcessorBlt(vp, _vpOutputView, 0, 1, [stream]).CheckError();
            return true;
        }
        catch (Exception ex) when (!_cfrHasFrame && !IsDeviceRemoved(ex))
        {
            Trace.WriteLine($"FrameCaptureEngine: video processor blt failed on the first frame, switching to BGRA. ({ex.Message})");
            FallBackToBgraLocked();
            return false;
        }
    }

    private ID3D11VideoProcessorInputView GetInputView(ID3D11Texture2D source)
    {
        // the pool surfaces are stable, so their views are too; the view holds its own
        // reference, which keeps the pointer from being reused while cached
        if (_vpInputViews.TryGetValue(source.NativePointer, out var cached))
            return cached;

        if (_vpInputViews.Count >= MaxCachedInputViews)
            ClearInputViews();

        var desc = new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
        };
        _videoDevice!.CreateVideoProcessorInputView(source, _vpEnumerator, desc, out var view).CheckError();
        _vpInputViews[source.NativePointer] = view!;
        return view!;
    }

    private void FallBackToBgraLocked()
    {
        // nothing was emitted yet (cfr emits only after _cfrHasFrame), so swapping the
        // textures under the lock is safe
        DisposeFrameResources();
        DisposeVideoProcessor();
        _nv12 = false;
        _outWidth = _cropWidth;
        _outHeight = _cropHeight;
        CreateCropResources();
    }

    /// <summary>
    /// Crop box clamped to the current content (defensive after a resize).
    /// <paramref name="smallerThanCrop"/> = content shrank and the target must be
    /// cleared first (recycled texture has garbage).
    /// </summary>
    private Box? ComputeSourceBox(SizeInt32 content, out bool smallerThanCrop)
    {
        int left = Math.Min(_cropLeft, Math.Max(0, content.Width - 2));
        int top = Math.Min(_cropTop, Math.Max(0, content.Height - 2));
        int right = Math.Min(_cropLeft + _cropWidth, content.Width);
        int bottom = Math.Min(_cropTop + _cropHeight, content.Height);
        smallerThanCrop = right - left < _cropWidth || bottom - top < _cropHeight;
        if (right - left < 2 || bottom - top < 2)
            return null;
        return new Box(left, top, 0, right, bottom, 1);
    }

    /// <summary>
    /// Cfr pacing loop on a dedicated thread, blocking on a HIGH RESOLUTION waitable
    /// timer. A PeriodicTimer inherits the system timer resolution (~15.6 ms) and on
    /// Windows 11 timeBeginPeriod(1) is IGNORED while the process is minimized or hidden,
    /// which is exactly how Klip sits during a recording. The high res timer does not
    /// care and gives sub ms jitter.
    /// </summary>
    private void CfrLoop(int fps, CancellationToken token)
    {
        var interval = TimeSpan.FromSeconds(1.0 / fps);

        // period rounded DOWN: slightly early ticks fall on "grid <= lastGrid" (cheap
        // skip); rounding up would create late slots the catch-up has to fill all the time
        int periodMs = Math.Max(1, (int)interval.TotalMilliseconds);
        int maxCatchUp = _maxCatchUpFrames;

        // recorder process only: MMCSS keeps the pacing thread on time when a game
        // loads every core. Failing here is fine, the loop just runs at normal priority
        nint mmcss = 0;
        if (_options?.HighPriority == true)
        {
            uint taskIndex = 0;
            mmcss = NativeMethods.AvSetMmThreadCharacteristicsW("Capture", ref taskIndex);
        }

        try
        {
            RunCfrTicks(interval, periodMs, maxCatchUp, token);
        }
        finally
        {
            if (mmcss != 0)
                NativeMethods.AvRevertMmThreadCharacteristics(mmcss);
        }
    }

    private void RunCfrTicks(TimeSpan interval, int periodMs, int maxCatchUp, CancellationToken token)
    {
        using var timerHandle = CreateTickTimer(interval, periodMs);
        using var timerWait = timerHandle is not null ? new NativeWaitHandle(timerHandle) : null;
        var waitHandles = timerWait is not null
            ? new WaitHandle[] { timerWait, token.WaitHandle }
            : null;

        // the grid comes from the real clock (Stopwatch anchored on the first tick),
        // never from counting ticks: waits can coalesce and the timeline would shrink
        Stopwatch? clock = null;
        long lastGrid = -1;

        while (!token.IsCancellationRequested)
        {
            if (waitHandles is not null)
            {
                if (WaitHandle.WaitAny(waitHandles) == 1)
                    return;
            }
            else if (token.WaitHandle.WaitOne(periodMs))
            {
                return; // rare fallback without a waitable timer
            }

            clock ??= Stopwatch.StartNew();
            long grid = (long)Math.Round(clock.Elapsed.Ticks / (double)interval.Ticks);
            if (grid <= lastGrid)
                continue; // early tick: never re-emit a slot

            // catch-up: a skipped index used to leave the previous frame on screen for
            // 2+ slots. Fill the missing indexes with DUPLICATES of the latest frame
            // (cheap to encode). Above the cap the oldest slots are counted as lost
            long firstIndex = lastGrid < 0 ? grid : lastGrid + 1;
            long span = grid - firstIndex + 1;
            if (span > maxCatchUp)
            {
                Interlocked.Add(ref _cfrLostGridSlots, span - maxCatchUp);
                firstIndex = grid - maxCatchUp + 1;
            }

            // SEQUENTIAL emission (one texture rented at a time): the mp4 consumer copies
            // and returns the texture inside the handler, so a burst fits in the pool. If
            // renting fails the remaining slots are left for the next ticks
            for (long index = firstIndex; index <= grid; index++)
            {
                if (!EmitCfrGridSlot(index, isLatest: index == grid, interval, ref lastGrid))
                    break;
            }
        }
    }

    /// <summary>
    /// High resolution periodic timer for the cfr loop (1803+), plain timer on older
    /// builds, null if neither works.
    /// </summary>
    private static SafeWaitHandle? CreateTickTimer(TimeSpan interval, int periodMs)
    {
        var handle = NativeMethods.CreateWaitableTimerExW(
            0, null, NativeMethods.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, NativeMethods.TIMER_ALL_ACCESS);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            handle = NativeMethods.CreateWaitableTimerExW(0, null, 0, NativeMethods.TIMER_ALL_ACCESS);
        }

        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        long dueTime = -interval.Ticks; // negative = relative, 100 ns units
        if (!NativeMethods.SetWaitableTimer(handle, in dueTime, periodMs, 0, 0, false))
        {
            handle.Dispose();
            return null;
        }

        return handle;
    }

    private sealed class NativeWaitHandle : WaitHandle
    {
        public NativeWaitHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }

    /// <summary>
    /// Emits grid slot <paramref name="index"/> (gpu whenever there is a consumer; cpu
    /// only for the latest index, duplicating there would just bloat the gif). Returns
    /// false to stop the burst WITHOUT moving <paramref name="lastGrid"/> past what went out.
    /// </summary>
    private bool EmitCfrGridSlot(long index, bool isLatest, TimeSpan interval, ref long lastGrid)
    {
        var timestamp = TimeSpan.FromTicks(index * interval.Ticks);
        GpuFrame? gpuFrame = null;
        bool readCpu = false;
        Action<GpuFrame>? gpuHandler = null;
        Action<CpuFrame>? cpuHandler = null;

        lock (_lock)
        {
            // during recovery or before the first frame there is nothing to emit; the
            // catch-up cap keeps the reopening from turning into a huge burst
            if (_disposed || !_running || _emitPaused || !_cfrHasFrame || _context is null)
                return false;

            try
            {
                gpuHandler = GpuFrameArrived;
                if (gpuHandler is not null)
                {
                    if (!TryAcquireTexture(out var target))
                        return false;

                    _context.CopyResource(target, _cfrLatest!);
                    gpuFrame = new GpuFrame(target, timestamp, ReturnTexture);
                    if (!isLatest)
                        Interlocked.Increment(ref _cfrCatchUpDuplicates);
                }

                cpuHandler = CpuFrameArrived;
                if (isLatest && cpuHandler is not null && _staging is not null)
                {
                    // copy to staging under the lock; Map/readback happen OUTSIDE it,
                    // otherwise the wgc thread waits while the gpu drains
                    _context.CopyResource(_staging, _cfrLatest!);
                    readCpu = true;
                }

                lastGrid = index;
            }
            catch (Exception ex)
            {
                gpuFrame?.Dispose();
                HandlePipelineError(ex);
                return false;
            }

            if (gpuFrame is not null || readCpu)
                Interlocked.Increment(ref _activeEmits);
        }

        if (gpuFrame is null && !readCpu)
            return true;

        try
        {
            if (gpuFrame is not null)
            {
                try
                {
                    gpuHandler!.Invoke(gpuFrame);
                }
                catch (Exception ex)
                {
                    gpuFrame.Dispose();
                    HandlePipelineError(ex);
                }
            }

            if (readCpu)
            {
                try
                {
                    // staging belongs to this loop and the context is multithread protected
                    cpuHandler!.Invoke(ReadStaging(timestamp));
                }
                catch (Exception ex)
                {
                    HandlePipelineError(ex);
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeEmits);
        }

        return true;
    }

    private CpuFrame ReadStaging(TimeSpan timestamp)
    {
        var mapped = _context!.Map(_staging!, 0, MapMode.Read);
        try
        {
            int stride = _cropWidth * 4;
            var data = _cpuBuffer!;
            nint source = mapped.DataPointer;
            int rowPitch = (int)mapped.RowPitch;
            for (int y = 0; y < _cropHeight; y++)
                Marshal.Copy(source + (nint)y * rowPitch, data, y * stride, stride);
            return new CpuFrame(data, _cropWidth, _cropHeight, timestamp);
        }
        finally
        {
            _context.Unmap(_staging!, 0);
        }
    }

    // ----- texture pool -----

    private bool TryAcquireTexture(out ID3D11Texture2D texture)
    {
        for (int i = 0; i < _texturePool.Length; i++)
        {
            if (!_textureInUse[i] && _texturePool[i] is not null)
            {
                _textureInUse[i] = true;
                texture = _texturePool[i]!;
                return true;
            }
        }

        texture = null!;
        return false;
    }

    private void ReturnTexture(ID3D11Texture2D texture)
    {
        lock (_lock)
        {
            for (int i = 0; i < _texturePool.Length; i++)
            {
                if (ReferenceEquals(_texturePool[i], texture))
                {
                    _textureInUse[i] = false;
                    return;
                }
            }

            // pool was rebuilt or dropped: the texture is an orphan now, free it
            texture.Dispose();
        }
    }

    // ----- long session robustness -----

    private void OnItemClosed(GraphicsCaptureItem sender, object? args)
    {
        RaiseFailure(new FrameCaptureError(
            FrameCaptureErrorKind.MonitorLost,
            "O monitor capturado foi desconectado."));
    }

    private void HandlePipelineError(Exception ex)
    {
        bool deviceRemoved = IsDeviceRemoved(ex);
        bool attemptRecovery;
        lock (_lock)
        {
            attemptRecovery = deviceRemoved && !_deviceRecoveryAttempted && _running && !_disposed;
            if (attemptRecovery)
                _deviceRecoveryAttempted = true;
        }

        if (attemptRecovery)
        {
            // device lost -> one rebuild; a second failure is fatal
            _ = Task.Run(async () =>
            {
                try
                {
                    await RecoverDeviceAsync().ConfigureAwait(false);
                    DeviceRecreated?.Invoke();
                }
                catch (Exception recoveryEx)
                {
                    RaiseFailure(new FrameCaptureError(
                        FrameCaptureErrorKind.DeviceLost,
                        "GPU perdida e a recuperacao falhou.",
                        recoveryEx));
                }
                finally
                {
                    // reopen emission only AFTER DeviceRecreated: the encoder has to
                    // reinitialize before it sees textures of the new device
                    lock (_lock)
                        _emitPaused = false;
                }
            });
            return;
        }

        RaiseFailure(new FrameCaptureError(
            deviceRemoved ? FrameCaptureErrorKind.DeviceLost : FrameCaptureErrorKind.Unknown,
            deviceRemoved ? "GPU perdida (device removed)." : "Falha no pipeline de captura.",
            ex));
    }

    private static bool IsDeviceRemoved(Exception ex)
    {
        const int DXGI_ERROR_DEVICE_REMOVED = unchecked((int)0x887A0005);
        const int DXGI_ERROR_DEVICE_RESET = unchecked((int)0x887A0007);
        const int DXGI_ERROR_DEVICE_HUNG = unchecked((int)0x887A0006);

        int hresult = ex switch
        {
            SharpGenException sharpGen => sharpGen.ResultCode.Code,
            COMException com => com.HResult,
            _ => ex.HResult,
        };
        return hresult is DXGI_ERROR_DEVICE_REMOVED or DXGI_ERROR_DEVICE_RESET or DXGI_ERROR_DEVICE_HUNG;
    }

    private async Task RecoverDeviceAsync()
    {
        // close the emission gate BEFORE dropping the old device
        lock (_lock)
        {
            if (!_running || _disposed || _item is null)
                return;
            _emitPaused = true;
        }

        // wait for handlers in flight (a consumer may be mid CopyResource with a texture
        // of the old device). Short spin, ~1 s cap
        var spin = new SpinWait();
        long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        while (Volatile.Read(ref _activeEmits) > 0 && Stopwatch.GetTimestamp() < deadline)
            spin.SpinOnce();

        GraphicsCaptureSession session;
        bool cursorDesired;
        lock (_lock)
        {
            if (!_running || _disposed || _item is null)
                return;

            _session?.Dispose();
            _session = null;
            if (_framePool is not null)
            {
                _framePool.FrameArrived -= OnFrameArrived;
                _framePool.Dispose();
                _framePool = null;
            }

            bool wasNv12 = _nv12;
            DisposeFrameResources();
            DisposeDevice();

            CreateDevice(_hMonitor);
            CreateFramePool();
            // same format as before: the encoder was set up for it
            if (wasNv12)
                CreateVideoProcessor(_outWidth, _outHeight, _options.FixedFps ?? 30);
            CreateCropResources();
            _session = _framePool!.CreateCaptureSession(_item);
            session = _session;
            cursorDesired = _cursorCaptureDesired;
        }

        await ConfigureSessionAsync(session, _options, cursorDesired).ConfigureAwait(false);
        session.StartCapture();
    }

    private void RaiseFailure(FrameCaptureError error)
    {
        bool wasRunning;
        lock (_lock)
        {
            wasRunning = _running;
            _running = false;
            _cfrCts?.Cancel();
        }

        // full cleanup is left to the consumer StopAsync (no reentrant dispose from the
        // pool callback thread)
        if (wasRunning)
            Failed?.Invoke(error);
    }

    // ----- teardown -----

    private void StopCore()
    {
        _cfrCts?.Dispose();
        _cfrCts = null;

        if (_session is not null)
        {
            _session.Dispose();
            _session = null;
        }

        if (_framePool is not null)
        {
            _framePool.FrameArrived -= OnFrameArrived;
            _framePool.Dispose();
            _framePool = null;
        }

        if (_item is not null)
        {
            _item.Closed -= OnItemClosed;
            _item = null;
        }

        DisposeFrameResources();
    }

    private void DisposeFrameResources()
    {
        for (int i = 0; i < _texturePool.Length; i++)
        {
            // textures in flight are freed when the consumer calls GpuFrame.Dispose
            // (ReturnTexture will not find them in the pool anymore)
            if (!_textureInUse[i])
                _texturePool[i]?.Dispose();
            _texturePool[i] = null;
        }

        _texturePool = [];
        _textureInUse = [];

        _blackTexture?.Dispose();
        _blackTexture = null;

        ClearInputViews();
        _vpOutputView?.Dispose();
        _vpOutputView = null;

        _cfrLatest?.Dispose();
        _cfrLatest = null;
        _cfrHasFrame = false;
        _staging?.Dispose();
        _staging = null;
        _cpuBuffer = null;
    }

    private void ClearInputViews()
    {
        foreach (var view in _vpInputViews.Values)
            view.Dispose();
        _vpInputViews.Clear();
    }

    private void DisposeVideoProcessor()
    {
        ClearInputViews();
        _vpOutputView?.Dispose();
        _vpOutputView = null;
        _videoProcessor?.Dispose();
        _videoProcessor = null;
        _vpEnumerator?.Dispose();
        _vpEnumerator = null;
        _videoContext?.Dispose();
        _videoContext = null;
        _videoDevice?.Dispose();
        _videoDevice = null;
    }

    private void DisposeDevice()
    {
        DisposeVideoProcessor();
        _winrtDevice?.Dispose();
        _winrtDevice = null;
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
    }
}
