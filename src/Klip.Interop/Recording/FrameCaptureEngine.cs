using System.Diagnostics;
using System.Runtime.InteropServices;
using Klip.Core.Recording;
using Microsoft.Win32.SafeHandles;
using SharpGen.Runtime;
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
/// Motor de captura de frames via Windows.Graphics.Capture (spec F2).
/// Captura o monitor que contem a regiao, recorta na GPU para a regiao pedida
/// (dimensoes PARES, RF-F2.03) e entrega frames por dois caminhos:
/// <list type="bullet">
/// <item><see cref="GpuFrameArrived"/>: textura D3D11 no <see cref="Device"/>
/// compartilhado, para o encoder MP4 (D-F2.3).</item>
/// <item><see cref="CpuFrameArrived"/>: BGRA na CPU em cadencia CFR, apenas
/// quando <see cref="FrameCaptureOptions.FixedFps"/> esta definido (modo GIF).</item>
/// </list>
/// Backpressure por construcao (RF-F2.04): no maximo <see cref="GpuPoolSize"/>
/// texturas em voo; se o consumidor nao devolveu (Dispose), frames novos sao
/// descartados. Os handlers dos eventos rodam em threads do pool free-threaded
/// e devem ser rapidos (enfileirar e voltar).
/// </summary>
public sealed class FrameCaptureEngine : IAsyncDisposable
{
    // RF-F2.04: teto de texturas em voo (drop controlado acima disso)
    private const int GpuPoolSize = 3;
    private const int FramePoolBuffers = 2; // RF-F2.02

    // Teto de catch-up por tick do loop CFR: um atraso maior que 4 slots
    // (~133 ms a 30 fps) vira "slots perdidos" (hold inevitavel no player) em
    // vez de uma rajada longa de duplicados. 4 cobre o pior caso realista de
    // jitter de scheduling/GC sem estourar o pool de texturas (emissao e
    // sequencial: o consumidor devolve a textura antes do proximo aluguel).
    private const int MaxCatchUpFrames = 4;

    private readonly object _lock = new();

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private SizeInt32 _poolSize;

    // geometria do crop em px fisicos RELATIVOS a origem do monitor (rcMonitor)
    private int _cropLeft;
    private int _cropTop;
    private int _cropWidth;
    private int _cropHeight;

    private ID3D11Texture2D?[] _texturePool = [];
    private bool[] _textureInUse = [];

    // modo CFR (RF-F2.05): ultimo frame vivo + staging para readback
    private ID3D11Texture2D? _cfrLatest;
    private ID3D11Texture2D? _staging;
    private bool _cfrHasFrame;
    private CancellationTokenSource? _cfrCts;
    private Task? _cfrLoop;

    // diagnostico do catch-up CFR (microstutter no MP4): duplicados emitidos
    // para preencher indices atrasados e slots alem do teto (perdidos = hold)
    private long _cfrCatchUpDuplicates;
    private long _cfrLostGridSlots;

    // fonte preta pre-alocada: limpa o alvo quando o box clampado < crop
    private ID3D11Texture2D? _blackTexture;

    // buffer BGRA unico reutilizado entre CpuFrames (evita ~8 MB/frame de GC)
    private byte[]? _cpuBuffer;

    // portao de emissao durante a recuperacao de device (RF-F2.08): loop CFR e
    // ProcessFrame checam sob _lock; handlers em voo sao contados para o
    // recovery aguardar antes de descartar o device antigo.
    private bool _emitPaused;
    private int _activeEmits;

    private FrameCaptureOptions _options = new();
    private bool _running;
    private bool _deviceRecoveryAttempted;
    private bool _disposed;

    // RF-M2.10: cursor ao vivo - estado desejado sobrevive a recriacao de
    // sessao (recovery) e um re-sync defensivo roda no proximo FrameArrived
    private bool _cursorCaptureDesired = true;
    private volatile bool _cursorResyncPending;

    // RF-M2.13: QPC do ultimo FrameArrived do WGC (watchdog do consumidor).
    // WGC so entrega frames quando o conteudo MUDA - tela estatica zera a
    // cadencia legitimamente, por isso o watchdog e so log, nunca reinicio.
    private long _lastWgcFrameTimestamp;

    /// <summary>
    /// True quando o SO suporta Windows.Graphics.Capture (17763+ no nosso piso)
    /// e a sessao de captura esta habilitada.
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
                return false; // WinRT indisponivel (ex.: Server Core)
            }
        }
    }

    /// <summary>
    /// Device D3D11 (BGRA support) usado pela captura, exposto para o encoder
    /// MP4 compartilhar texturas. Valido apos <see cref="StartAsync"/>. Pode ser
    /// SUBSTITUIDO apos uma recuperacao de device lost - ver <see cref="DeviceRecreated"/>.
    /// </summary>
    public ID3D11Device Device =>
        _device ?? throw new InvalidOperationException("Device disponivel apenas apos StartAsync.");

    /// <summary>
    /// Caminho MP4: frame recortado na GPU. Posse da textura e do consumidor
    /// (Dispose devolve ao pool). Em modo CFR os timestamps ficam SEMPRE na
    /// grade <c>n * (1/FPS)</c> ancorada no relogio real desde o primeiro tick.
    /// Ticks atrasados sao compensados com DUPLICADOS do ultimo frame nos
    /// indices intermediarios (ate <see cref="MaxCatchUpFrames"/> por tick;
    /// acima disso os slots mais antigos sao perdidos - ver
    /// <see cref="CfrLostGridSlots"/>). Timestamps nunca saem da grade (RF-F2.05).
    /// </summary>
    public event Action<GpuFrame>? GpuFrameArrived;

    /// <summary>
    /// Caminho GIF: frame BGRA na CPU, cadencia CFR (so com FixedFps != null).
    /// Invocado sincronamente; <see cref="CpuFrame.Bgra"/> e um buffer
    /// REUTILIZADO entre frames - valido apenas durante o handler, copie o que
    /// precisar antes de retornar. Timestamps na grade <c>n * (1/FPS)</c>
    /// ancorada no relogio real; indices podem ser pulados (o delay do GIF vem
    /// da diferenca de timestamps, entao pulo nao gera travada), timestamps
    /// nunca saem da grade (RF-F2.05).
    /// </summary>
    public event Action<CpuFrame>? CpuFrameArrived;

    /// <summary>
    /// Frames DUPLICADOS emitidos pelo catch-up do loop CFR (preenchem indices
    /// atrasados da grade para evitar hold no player). Diagnostico do
    /// microstutter no MP4; valido durante e apos a sessao.
    /// </summary>
    public long CfrCatchUpDuplicates => Volatile.Read(ref _cfrCatchUpDuplicates);

    /// <summary>
    /// Slots da grade CFR perdidos (atraso maior que o teto de catch-up ou pool
    /// de texturas esgotado alem do alcancavel). Cada slot perdido e um hold de
    /// 1/FPS no arquivo final.
    /// </summary>
    public long CfrLostGridSlots => Volatile.Read(ref _cfrLostGridSlots);

    /// <summary>Falha irrecuperavel (RF-F2.08). O consumidor deve finalizar o arquivo e chamar StopAsync.</summary>
    public event Action<FrameCaptureError>? Failed;

    /// <summary>
    /// Disparado quando o device foi recriado apos device lost (1 tentativa,
    /// RF-F2.08). <see cref="Device"/> passa a apontar para o device novo;
    /// texturas de <see cref="GpuFrame"/> antigas ficam invalidas para copia.
    /// O encoder deve reinicializar recursos compartilhados ou tratar como fatal.
    /// </summary>
    public event Action? DeviceRecreated;

    /// <summary>RF-M2.10: estado desejado do cursor na captura (getter para a toolbar).</summary>
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

    /// <summary>
    /// RF-M2.13: tempo desde o ultimo FrameArrived do WGC. Zero antes do
    /// primeiro frame da sessao (evita falso positivo no startup).
    /// </summary>
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
    /// RF-M2.10 (D-M2.6): liga/desliga o cursor AO VIVO via
    /// GraphicsCaptureSession.IsCursorCaptureEnabled. O desejo e guardado e
    /// re-aplicado defensivamente no proximo FrameArrived (cobre sessao
    /// recriada por recovery); try/catch cobre builds sem a propriedade.
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

    /// <summary>Aplica o cursor desejado a sessao corrente (sob _lock). Nunca lanca.</summary>
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
            // build sem a propriedade/sessao encerrando: re-sync tenta de novo
            // no proximo FrameArrived enquanto _cursorResyncPending estiver set
            return;
        }

        _cursorResyncPending = false;
    }

    /// <summary>
    /// Inicia a captura no monitor que contem o centro da regiao (RF-F2.09).
    /// </summary>
    /// <param name="region">Regiao em px fisicos, coords de desktop virtual; recortada ao monitor.</param>
    /// <param name="options">Cursor, borda e pacing (VFR/CFR).</param>
    public async Task StartAsync(RecordingRegion region, FrameCaptureOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(options);
        if (region.Width < 2 || region.Height < 2)
            throw new ArgumentException("Regiao minima de 2x2 px fisicos.", nameof(region));
        if (options.FixedFps is <= 0)
            throw new ArgumentException("FixedFps deve ser positivo ou null (VFR).", nameof(options));
        if (!IsSupported)
            throw new PlatformNotSupportedException("Windows.Graphics.Capture indisponivel neste sistema.");

        // D3D11CreateDevice (com fallback WARP) pode levar segundos: setup
        // pesado fora da thread chamadora (UI). GraphicsCaptureItem e
        // Direct3D11CaptureFramePool sao free-threaded, nao exigem a thread.
        GraphicsCaptureSession session = await Task.Run(() =>
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_running)
                    throw new InvalidOperationException("Captura ja iniciada.");

                _options = options;
                _deviceRecoveryAttempted = false;
                _emitPaused = false;
                _cursorCaptureDesired = options.CaptureCursor; // RF-M2.10: estado inicial
                _cursorResyncPending = false;
                Volatile.Write(ref _lastWgcFrameTimestamp, 0); // RF-M2.13
                Volatile.Write(ref _cfrCatchUpDuplicates, 0);
                Volatile.Write(ref _cfrLostGridSlots, 0);

                // RF-F2.09: monitor que contem o centro da regiao; crop relativo a rcMonitor
                var hMonitor = ResolveMonitor(region, out var monitorRect);
                ComputeCrop(region, monitorRect);

                CreateDevice();
                _item = CaptureInterop.CreateItemForMonitor(hMonitor);
                _item.Closed += OnItemClosed;

                CreateFramePool();
                CreateCropResources();
                _session = _framePool!.CreateCaptureSession(_item);
                _running = true;
                return _session;
            }
        }).ConfigureAwait(false);

        // fora do lock: RequestAccessAsync e assincrono (RF-F2.07)
        await ConfigureSessionAsync(session, options, options.CaptureCursor).ConfigureAwait(false);

        // Revalidar sob o lock: StopAsync pode ter corrido durante os awaits;
        // sem isso o loop CFR ficaria orfao rodando para sempre.
        lock (_lock)
        {
            if (!_running || !ReferenceEquals(_session, session))
                return; // Stop ja limpou a sessao -> abortar sem iniciar nada

            session.StartCapture();

            if (options.FixedFps is int fps)
            {
                _cfrCts = new CancellationTokenSource();
                var token = _cfrCts.Token;
                // LongRunning: o loop CFR bloqueia em WaitAny no waitable timer
                // de alta resolucao (jitter alvo < 2 ms); thread dedicada evita
                // roubar/pendurar threads do pool durante a gravacao inteira.
                _cfrLoop = Task.Factory.StartNew(
                    () => CfrLoop(fps, token),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }
        }
    }

    /// <summary>Encerra a sessao e libera os recursos de captura (device permanece ate DisposeAsync).</summary>
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
                // encerramento normal do loop CFR
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
            _winrtDevice?.Dispose();
            _winrtDevice = null;
            _context?.Dispose();
            _context = null;
            _device?.Dispose();
            _device = null;
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
            throw new InvalidOperationException("Nao foi possivel resolver o monitor da regiao.");
        monitorRect = info.rcMonitor;
        return hMonitor;
    }

    private void ComputeCrop(RecordingRegion region, NativeMethods.RECT monitor)
    {
        int monWidth = monitor.right - monitor.left;
        int monHeight = monitor.bottom - monitor.top;

        // clamp ao monitor (RF-F2.09: regiao limitada a 1 monitor)
        int left = Math.Clamp(region.Left - monitor.left, 0, Math.Max(0, monWidth - 2));
        int top = Math.Clamp(region.Top - monitor.top, 0, Math.Max(0, monHeight - 2));
        int width = Math.Min(region.Width, monWidth - left);
        int height = Math.Min(region.Height, monHeight - top);

        // RF-F2.03: dimensoes PARES (arredonda para baixo, exigencia NV12 do encoder)
        _cropLeft = left;
        _cropTop = top;
        _cropWidth = Math.Max(2, width & ~1);
        _cropHeight = Math.Max(2, height & ~1);
    }

    private void CreateDevice()
    {
        // VideoSupport e obrigatorio para o IMFDXGIDeviceManager habilitar MFTs
        // de hardware; sem ele o encoder MP4 cai silenciosamente para software.
        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        var result = D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, null, out ID3D11Device? device);
        if (result.Failure || device is null)
        {
            // GPU indisponivel (RDP, driver quebrado): WARP mantem a captura funcional
            D3D11.D3D11CreateDevice(null, DriverType.Warp, flags, null, out device).CheckError();
        }

        _device = device!;
        _context = _device.ImmediateContext;

        // O immediate context e compartilhado por 3 threads (WGC, loop CFR,
        // encoder) desde o primeiro frame: proteger AQUI, na criacao do device,
        // e nao apenas quando o Mp4SinkWriter for construido.
        using (var multithread = _context.QueryInterfaceOrNull<ID3D11Multithread>())
            multithread?.SetMultithreadProtected(true);

        _winrtDevice = CaptureInterop.CreateWinRtDevice(_device);
    }

    private void CreateFramePool()
    {
        _poolSize = _item!.Size;
        // RF-F2.02: free-threaded (nao amarra na UI thread), BGRA, 2 buffers
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winrtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            FramePoolBuffers,
            _poolSize);
        _framePool.FrameArrived += OnFrameArrived;
    }

    private void CreateCropResources()
    {
        // RF-F2.11: pool B8G8R8A8 -> o sistema entrega conteudo SDR mesmo em
        // monitor HDR (tone map do compositor); pipeline FP16 fica como evolucao.
        var desc = new Texture2DDescription
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

        // Fonte preta pre-alocada (criada UMA vez): limpa o alvo antes do crop
        // quando o box clampado ficou menor que o crop (conteudo encolheu apos
        // mudanca de modo), evitando lixo de textura reciclada nas bordas.
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

            // buffer unico reutilizado entre CpuFrames (contrato: consumidor
            // copia dentro do handler; ver doc de CpuFrame.Bgra)
            _cpuBuffer = new byte[stride * _cropHeight];
        }
    }

    private static async Task ConfigureSessionAsync(
        GraphicsCaptureSession session,
        FrameCaptureOptions options,
        bool captureCursor)
    {
        // RF-F2.06: cursor configuravel (19041+; piso do projeto e 17763).
        // RF-M2.10: o valor vem do estado DESEJADO (pode ter sido alternado ao
        // vivo antes de uma recriacao de sessao), nao das options originais.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            try
            {
                session.IsCursorCaptureEnabled = captureCursor;
            }
            catch
            {
                // sem suporte -> cursor fica no default do sistema
            }
        }

        // RF-F2.07 / Q-F2.1: remover borda amarela exige consentimento (20348+).
        // Se negado ou se a API lancar (app nao-empacotado), seguimos com borda.
        if (options.RequestBorderless && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
        {
            try
            {
                // RequestAccessAsync passa por broker e pode pendurar: timeout
                // curto para nunca atrasar o inicio da gravacao.
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
                // consentimento indisponivel -> conviver com a borda
            }
        }

        // RF-F2.05: em 24H2+ limita a taxa na fonte (menos frames para descartar)
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
                // feature-detection falhou -> pacing fica so no consumidor
            }
        }
    }

    // ----- pipeline de frames -----

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
            // TryGetNextFrame sob o _lock: fora dele corre com _framePool.Dispose
            // no StopCore/recovery. Pool antigo (pos-recovery) e ignorado.
            if (!_running || _emitPaused || _context is null || !ReferenceEquals(sender, _framePool))
                return;

            // RF-M2.13: marca a chegada de frame do WGC para o watchdog
            Volatile.Write(ref _lastWgcFrameTimestamp, Stopwatch.GetTimestamp());

            // RF-M2.10: re-sync defensivo do cursor (toggle que falhou ou
            // sessao recriada desde o pedido)
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
                    // RF-F2.08: resolucao/rotacao do monitor mudou -> Recreate do pool
                    _poolSize = contentSize;
                    _framePool!.Recreate(
                        _winrtDevice,
                        DirectXPixelFormat.B8G8R8A8UIntNormalized,
                        FramePoolBuffers,
                        contentSize);
                    return; // proximo frame ja vem no tamanho novo
                }

                using var sourceTexture = CaptureInterop.GetTexture(frame.Surface);
                var box = ComputeSourceBox(contentSize, out bool boxSmallerThanCrop);
                if (box is null)
                    return; // regiao ficou fora do monitor apos mudanca de modo

                if (_options.FixedFps is not null)
                {
                    // CFR: so atualiza o "ultimo frame"; emissao e do loop de pacing (RF-F2.05)
                    if (boxSmallerThanCrop)
                        _context.CopyResource(_cfrLatest!, _blackTexture!); // limpa lixo reciclado
                    _context.CopySubresourceRegion(_cfrLatest!, 0, 0, 0, 0, sourceTexture, 0, box.Value);
                    _cfrHasFrame = true;
                }
                else
                {
                    // VFR: crop GPU->GPU (RF-F2.03) com backpressure (RF-F2.04).
                    // Delegate capturado ANTES de alugar a textura: desassinar
                    // entre a checagem e o invoke nao pode vazar textura do pool.
                    gpuHandler = GpuFrameArrived;
                    if (gpuHandler is null || !TryAcquireTexture(out var target))
                        return; // sem consumidor ou consumidor atrasado -> drop controlado
                    if (boxSmallerThanCrop)
                        _context.CopyResource(target, _blackTexture!); // limpa lixo reciclado
                    _context.CopySubresourceRegion(target, 0, 0, 0, 0, sourceTexture, 0, box.Value);
                    emit = new GpuFrame(target, frame.SystemRelativeTime, ReturnTexture);
                }
            }
            finally
            {
                // RF-F2.02: devolver o buffer ao frame pool imediatamente
                frame.Dispose();
            }

            if (emit is not null)
                Interlocked.Increment(ref _activeEmits); // recovery aguarda handlers em voo
        }

        if (emit is null)
            return;

        try
        {
            gpuHandler!.Invoke(emit);
        }
        catch
        {
            emit.Dispose(); // devolve a textura; OnFrameArrived reporta o erro
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _activeEmits);
        }
    }

    /// <summary>
    /// Box do crop clampado ao conteudo atual (defensivo pos-resize).
    /// <paramref name="smallerThanCrop"/> indica que o conteudo encolheu e o
    /// alvo precisa ser limpo antes da copia (textura reciclada tem lixo).
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
    /// Loop de pacing CFR (RF-F2.05). Corre numa thread dedicada e bloqueia num
    /// waitable timer de ALTA RESOLUCAO (CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
    /// 1803+): o PeriodicTimer anterior herdava a resolucao do timer de sistema
    /// (~15,6 ms default) e, no Windows 11, timeBeginPeriod(1) e IGNORADO quando
    /// o processo esta minimizado/oculto - exatamente o estado tipico do Klip
    /// durante uma gravacao (hipotese #1 do microstutter: tick atrasado > 1
    /// periodo => indice pulado => hold de 2/fps no player). O waitable timer
    /// high-res nao depende da resolucao global nem do estado da janela e
    /// entrega jitter sub-ms.
    /// </summary>
    private void CfrLoop(int fps, CancellationToken token)
    {
        var interval = TimeSpan.FromSeconds(1.0 / fps);

        // Periodo em ms arredondado para BAIXO: ticks levemente adiantados caem
        // em "grid <= lastGrid" (skip barato); arredondar para cima criaria
        // indices atrasados sistematicos que o catch-up teria que preencher.
        int periodMs = Math.Max(1, (int)interval.TotalMilliseconds);

        using var timerHandle = CreateTickTimer(interval, periodMs);
        using var timerWait = timerHandle is not null ? new NativeWaitHandle(timerHandle) : null;
        var waitHandles = timerWait is not null
            ? new WaitHandle[] { timerWait, token.WaitHandle }
            : null;

        // RF-F2.05: a grade vem do relogio real (Stopwatch ancorado no primeiro
        // tick), nunca da contagem de ticks - contar ticks encurtaria a timeline
        // (video "acelerado", A/V dessync) porque waits podem coalescer.
        Stopwatch? clock = null;
        long lastGrid = -1;

        while (!token.IsCancellationRequested)
        {
            if (waitHandles is not null)
            {
                if (WaitHandle.WaitAny(waitHandles) == 1)
                    return; // cancelado (StopAsync)
            }
            else if (token.WaitHandle.WaitOne(periodMs))
            {
                return; // fallback raro sem waitable timer: espera de baixa
                        // resolucao; indices atrasados sao cobertos pelo catch-up
            }

            clock ??= Stopwatch.StartNew();
            long grid = (long)Math.Round(clock.Elapsed.Ticks / (double)interval.Ticks);
            if (grid <= lastGrid)
                continue; // tick adiantado: nunca reemitir um slot da grade

            // Hipotese #1 do microstutter: um indice pulado deixava o frame
            // anterior segurado por 2+ slots no player (hold de >= 66 ms a
            // 30 fps). Catch-up: preenche os indices intermediarios com
            // DUPLICADOS do ultimo frame (_cfrLatest), timestamps consecutivos
            // na grade - encode extra barato (frame identico) em troca de
            // timeline sem buracos. Teto de MaxCatchUpFrames por tick; atraso
            // maior vira slots perdidos contabilizados (hold inevitavel).
            long firstIndex = lastGrid < 0 ? grid : lastGrid + 1;
            long span = grid - firstIndex + 1;
            if (span > MaxCatchUpFrames)
            {
                Interlocked.Add(ref _cfrLostGridSlots, span - MaxCatchUpFrames);
                firstIndex = grid - MaxCatchUpFrames + 1;
            }

            // Emissao SEQUENCIAL (um aluguel de textura por vez): o consumidor
            // MP4 copia e devolve a textura dentro do handler, entao a rajada
            // de ate 4 duplicados cabe no pool de 3. Se o aluguel falhar
            // (consumidor segurando texturas), EmitCfrGridSlot retorna false
            // SEM avancar lastGrid e os slots restantes ficam para os ticks
            // seguintes com o conteudo entao corrente (catch-up espacado).
            for (long index = firstIndex; index <= grid; index++)
            {
                if (!EmitCfrGridSlot(index, isLatest: index == grid, interval, ref lastGrid))
                    break;
            }
        }
    }

    /// <summary>
    /// Cria o waitable timer periodico do loop CFR. Tenta alta resolucao
    /// (1803+); cai para timer comum em builds antigos; null se indisponivel
    /// (o loop usa espera de baixa resolucao no token como ultimo recurso).
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

        long dueTime = -interval.Ticks; // negativo = relativo, unidades de 100 ns
        if (!NativeMethods.SetWaitableTimer(handle, in dueTime, periodMs, 0, 0, false))
        {
            handle.Dispose();
            return null;
        }

        return handle;
    }

    /// <summary>Adapta um SafeWaitHandle nativo (waitable timer) ao WaitHandle gerenciado.</summary>
    private sealed class NativeWaitHandle : WaitHandle
    {
        public NativeWaitHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }

    /// <summary>
    /// Emite o slot <paramref name="index"/> da grade CFR (GPU sempre que ha
    /// consumidor; CPU apenas no indice mais recente - o delay do GIF vem da
    /// diferenca de timestamps, duplicar la so inflaria arquivo e readback).
    /// Retorna false para interromper a rajada do tick atual SEM avancar
    /// <paramref name="lastGrid"/> alem do ja emitido.
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
            // lastGrid fica intacto aqui: durante recovery (_emitPaused) ou
            // antes do primeiro frame nao ha o que emitir; quando a emissao
            // reabrir, o catch-up preenche ate MaxCatchUpFrames slots e o
            // excedente vira slots perdidos (nunca uma rajada gigante).
            if (_disposed || !_running || _emitPaused || !_cfrHasFrame || _context is null)
                return false;

            try
            {
                // Delegates capturados em locais: desassinar entre a checagem e
                // o invoke nao pode vazar textura nem invocar handler nulo.
                gpuHandler = GpuFrameArrived;
                if (gpuHandler is not null)
                {
                    if (!TryAcquireTexture(out var target))
                        return false; // pool esgotado -> catch-up espacado no proximo tick

                    _context.CopyResource(target, _cfrLatest!);
                    gpuFrame = new GpuFrame(target, timestamp, ReturnTexture);
                    if (!isLatest)
                        Interlocked.Increment(ref _cfrCatchUpDuplicates);
                }

                cpuHandler = CpuFrameArrived;
                if (isLatest && cpuHandler is not null && _staging is not null)
                {
                    // D-F2.3: copia GPU->staging sob o lock; Map/readback ficam
                    // FORA (senao a thread do WGC bloqueia enquanto a GPU drena)
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
                Interlocked.Increment(ref _activeEmits); // recovery aguarda handlers em voo
        }

        if (gpuFrame is null && !readCpu)
            return true; // sem consumidores: slot marcado como emitido mesmo assim

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
                    gpuFrame.Dispose(); // devolve a textura ao pool
                    HandlePipelineError(ex);
                }
            }

            if (readCpu)
            {
                try
                {
                    // Map do staging fora do _lock: staging e exclusiva
                    // deste loop e o context e multithread-protected.
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
            // buffer unico reutilizado (contrato de CpuFrame.Bgra: valido so
            // durante o handler; consumidor copia o que precisar)
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

    // ----- pool de texturas (RF-F2.04) -----

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

            // pool ja foi recriado/descartado: a posse ficou orfa, liberar direto
            texture.Dispose();
        }
    }

    // ----- robustez de sessao longa (RF-F2.08) -----

    private void OnItemClosed(GraphicsCaptureItem sender, object? args)
    {
        // monitor desconectado -> encerrar graciosamente (arquivo preservado, F3)
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
            // RF-F2.08: device lost -> tentar recriar 1x; segunda falha e fatal
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
                    // Reabrir a emissao SOMENTE depois de DeviceRecreated: o
                    // encoder precisa reinicializar antes de ver texturas do
                    // device novo. Em falha, _running ja e false.
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
        // Fecha o portao de emissao ANTES de descartar o device antigo: loop
        // CFR e ProcessFrame checam _emitPaused sob o lock e param de emitir.
        lock (_lock)
        {
            if (!_running || _disposed || _item is null)
                return;
            _emitPaused = true;
        }

        // Aguarda handlers em voo (consumidor pode estar num CopyResource com
        // textura do device antigo). Spin-wait curto com teto de ~1 s.
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

            DisposeFrameResources();
            _winrtDevice?.Dispose();
            _context?.Dispose();
            _device?.Dispose();

            CreateDevice();
            CreateFramePool();
            CreateCropResources();
            _session = _framePool!.CreateCaptureSession(_item);
            session = _session;
            cursorDesired = _cursorCaptureDesired; // RF-M2.10: sobrevive ao recovery
        }

        await ConfigureSessionAsync(session, _options, cursorDesired).ConfigureAwait(false);
        session.StartCapture();
        // _emitPaused e reaberto pelo chamador apos disparar DeviceRecreated
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

        // cleanup completo fica para o StopAsync do consumidor (evita dispose
        // reentrante a partir da thread de callback do pool)
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
            // texturas em voo sao liberadas quando o consumidor chamar
            // GpuFrame.Dispose (ReturnTexture nao as encontra mais no pool)
            if (!_textureInUse[i])
                _texturePool[i]?.Dispose();
            _texturePool[i] = null;
        }

        _texturePool = [];
        _textureInUse = [];

        _blackTexture?.Dispose();
        _blackTexture = null;

        _cfrLatest?.Dispose();
        _cfrLatest = null;
        _cfrHasFrame = false;
        _staging?.Dispose();
        _staging = null;
        _cpuBuffer = null;
    }
}
