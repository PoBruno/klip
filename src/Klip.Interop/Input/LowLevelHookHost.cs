using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Klip.Core.Diagnostics;
using Klip.Core.Input;

namespace Klip.Interop.Input;

/// <summary>Qual hook de baixo nivel. O valor inteiro indexa as contagens de referencia.</summary>
public enum LowLevelHookKind
{
    Keyboard = 0,
    Mouse = 1,
}

/// <summary>
/// ADR-P.01: unico dono de hooks de baixo nivel do Klip. Os hooks vivem numa thread
/// dedicada (<c>Klip.LowLevelHookPump</c>) com message loop proprio, NUNCA na UI thread
/// do WPF - um callback preso na fila do Dispatcher congela o input do Windows inteiro
/// e derruba o frame time de jogos em tela cheia.
/// <para>
/// ADR-P.02: os hooks sao instalados sob demanda por escopo contado
/// (<see cref="Acquire"/>) e REMOVIDOS do sistema quando o ultimo escopo morre. Um flag
/// "Active" nao substitui isso: um hook instalado continua sendo chamado pela Raw Input
/// Thread a cada tecla e a cada movimento de mouse, mesmo que o callback so retorne.
/// </para>
/// <para>
/// ADR-P.04: o callback e O(1). Le o struct por ponteiro (<see cref="Unsafe.AsRef{T}(void*)"/>),
/// decide com bitmap de 256 bits, publica num ring SPSC pre-alocado e retorna. Todo o
/// trabalho de verdade acontece na thread worker. Orcamento: p99 abaixo de 50 us. O
/// disjuntor do sistema (<c>LowLevelHooksTimeout</c>, 300 ms por padrao) REMOVE o hook em
/// silencio quando estourado - desde o Windows 7 nao ha aviso nenhum.
/// </para>
/// </summary>
public sealed unsafe class LowLevelHookHost : IDisposable
{
    // Comandos aceitos pelo message loop da thread de pump. Sao mensagens de thread
    // (hwnd nulo): install/uninstall PRECISAM rodar la, porque SetWindowsHookEx amarra
    // o hook a thread chamadora.
    private const uint WM_KLIP_INSTALL = NativeMethods.WM_APP + 1;
    private const uint WM_KLIP_UNINSTALL = NativeMethods.WM_APP + 2;

    private const int VK_V = 0x56;

    /// <summary>Teto da espera por install/uninstall. Nunca deve ser atingido: o pump so bombeia mensagens.</summary>
    private const int CommandTimeoutMilliseconds = 250;

    /// <summary>Teto da espera pela thread de pump criar a fila de mensagens e pagar o warm-up.</summary>
    private const int StartTimeoutMilliseconds = 2000;

    /// <summary>Espera pela thread de pump encerrar apos o WM_QUIT.</summary>
    private const int ShutdownTimeoutMilliseconds = 2000;

    /// <summary>Fatia de espera do worker: sai do WaitOne periodicamente para reavaliar o cancelamento.</summary>
    private const int WorkerWaitMilliseconds = 250;

    // 1024 entradas: um mouse gamer a 8000 Hz enche 512 numa rajada de 64 ms se o worker
    // for preemptado. Cheio, a fila DESCARTA (RF-P1.02) - perder um clique e infinitamente
    // melhor do que segurar a Raw Input Thread.
    private static readonly InputEventRing Ring = new(1024);

    private readonly object _sync = new();

    // Acordado pela thread de pump depois de processar cada comando de install/uninstall.
    private readonly ManualResetEventSlim _commandSignal = new(false);
    private readonly ManualResetEventSlim _pumpReady = new(false);

    // Indexadas por (int)LowLevelHookKind. Mutadas sempre sob _sync.
    private readonly int[] _refCounts = new int[2];

    // Handles dos hooks: escritos SO na thread de pump, lidos de qualquer thread.
    private nint _keyboardHook;
    private nint _mouseHook;

    private Thread? _pumpThread;
    private Thread? _workerThread;
    private CancellationTokenSource? _workerCts;
    private uint _pumpThreadId;
    private int _started;
    private int _suspended;
    private int _disposed;

    /// <summary>
    /// Instancia unica. O ring e SPSC (um produtor, um consumidor): duas instancias
    /// competindo pelo mesmo ring quebrariam a invariante, por isso o construtor e privado.
    /// </summary>
    public static LowLevelHookHost Shared { get; } = new();

    private LowLevelHookHost()
    {
    }

    /// <summary>
    /// Disparado na thread WORKER, nunca na UI thread. O consumidor e responsavel
    /// por fazer o proprio Dispatcher.BeginInvoke.
    /// </summary>
    public event Action<InputEvent>? Observed;

    /// <summary>Sobe a thread de hooks e a thread worker. Idempotente.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (Volatile.Read(ref _started) != 0)
            return;

        lock (_sync)
        {
            if (Volatile.Read(ref _started) != 0)
                return;

            var pump = new Thread(PumpThreadMain, maxStackSize: 256 * 1024)
            {
                Name = "Klip.LowLevelHookPump",
                IsBackground = true,
                // AboveNormal, NAO Highest: a thread precisa ganhar do trabalho comum do
                // app, mas nao pode competir com o render loop de um jogo.
                Priority = ThreadPriority.AboveNormal,
            };
            // STA porque a thread hospeda um message loop e pode acabar recebendo
            // mensagens de janelas/shell entregues pelo sistema.
            pump.SetApartmentState(ApartmentState.STA);
            pump.Start();

            // A thread so se declara pronta depois de criar a fila de mensagens e pagar
            // o warm-up. Sem isso, PostThreadMessage abaixo se perderia em silencio.
            _pumpReady.Wait(StartTimeoutMilliseconds);
            _pumpThread = pump;

            // Worker sobe DEPOIS do warm-up para nao consumir o evento falso do pre-JIT.
            var cts = new CancellationTokenSource();
            _workerCts = cts;
            var worker = new Thread(() => WorkerThreadMain(cts.Token))
            {
                Name = "Klip.LowLevelHookWorker",
                IsBackground = true,
                Priority = ThreadPriority.Normal,
            };
            worker.Start();
            _workerThread = worker;

            Volatile.Write(ref _started, 1);
        }
    }

    /// <summary>
    /// Instala o hook enquanto o escopo viver (contado por referencia).
    /// Descartar o ultimo escopo REMOVE o hook do sistema (nao apenas desativa).
    ///
    /// RF-P1.06: instala MESMO com o host suspenso. Todo Acquire e disparado por acao
    /// explicita do usuario (abrir o flyout, armar a fila de colagem), e a suspensao
    /// existe para cortar custo em REPOUSO, nao para bloquear o que o usuario pediu.
    /// Recusar o install aqui deixaria o flyout aberto sem navegacao por teclado sempre
    /// que houvesse um video ou apresentacao em tela cheia em primeiro plano.
    /// Sair da suspensao e responsabilidade do chamador (ver SystemActivityMonitor).
    /// </summary>
    public IDisposable Acquire(LowLevelHookKind kind)
    {
        Start();

        lock (_sync)
        {
            int index = (int)kind;
            _refCounts[index]++;
            if (_refCounts[index] == 1)
                PostCommand(WM_KLIP_INSTALL, kind);
        }

        return new HookScope(this, kind);
    }

    public bool IsInstalled(LowLevelHookKind kind) => kind == LowLevelHookKind.Keyboard
        ? Volatile.Read(ref _keyboardHook) != nint.Zero
        : Volatile.Read(ref _mouseHook) != nint.Zero;

    /// <summary>
    /// Auto-suspensao (jogo em tela cheia): remove TODOS os hooks agora.
    /// Escopos vivos continuam validos e sao reinstalados por <see cref="ResumeAll"/>.
    ///
    /// Com ADR-P.02 em vigor, em repouso nao existe hook nenhum instalado - entao na
    /// pratica isto e uma rede de seguranca contra escopo vazado, e nao o mecanismo
    /// principal de economia.
    /// </summary>
    public void SuspendAll()
    {
        lock (_sync)
        {
            if (Volatile.Read(ref _suspended) != 0)
                return;
            Volatile.Write(ref _suspended, 1);

            // RF-P1.06: nao arrancar o hook debaixo de um escopo VIVO. Um refCount > 0
            // significa que o usuario esta com o flyout aberto ou com a fila de colagem
            // armada agora; remover o hook ai quebraria a navegacao por teclado sem
            // nenhum aviso. Escopos vivos sao curtos por construcao (ADR-P.02) e serao
            // removidos sozinhos ao fechar.
            if (_refCounts[(int)LowLevelHookKind.Keyboard] == 0)
                PostCommand(WM_KLIP_UNINSTALL, LowLevelHookKind.Keyboard);
            if (_refCounts[(int)LowLevelHookKind.Mouse] == 0)
                PostCommand(WM_KLIP_UNINSTALL, LowLevelHookKind.Mouse);
        }
    }

    public void ResumeAll()
    {
        lock (_sync)
        {
            if (Volatile.Read(ref _suspended) == 0)
                return;
            Volatile.Write(ref _suspended, 0);

            // Reinstala apenas o que ainda tem escopo vivo.
            if (_refCounts[(int)LowLevelHookKind.Keyboard] > 0)
                PostCommand(WM_KLIP_INSTALL, LowLevelHookKind.Keyboard);
            if (_refCounts[(int)LowLevelHookKind.Mouse] > 0)
                PostCommand(WM_KLIP_INSTALL, LowLevelHookKind.Mouse);
        }
    }

    public bool IsSuspended => Volatile.Read(ref _suspended) != 0;

    /// <summary>RF-P1.04: amostra e zera o pior caso da janela. Chamar de um timer de background.</summary>
    public HookSample SampleMetrics() => HookPolicy.Metrics.Sample();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Thread? pump;
        Thread? worker;

        lock (_sync)
        {
            _refCounts[0] = 0;
            _refCounts[1] = 0;

            pump = _pumpThread;
            worker = _workerThread;
            _pumpThread = null;
            _workerThread = null;

            uint threadId = Volatile.Read(ref _pumpThreadId);
            if (threadId != 0)
                NativeMethods.PostThreadMessageW(threadId, NativeMethods.WM_QUIT, 0, nint.Zero);

            Volatile.Write(ref _started, 0);
        }

        // A propria thread de pump desinstala os dois hooks ao sair do loop: um
        // UnhookWindowsHookEx daqui rodaria na thread errada.
        pump?.Join(ShutdownTimeoutMilliseconds);

        _workerCts?.Cancel();
        Ring.Signal();
        worker?.Join(ShutdownTimeoutMilliseconds);
        _workerCts?.Dispose();
        _workerCts = null;

        _pumpReady.Dispose();
        _commandSignal.Dispose();
    }

    // ================= Escopo contado por referencia (ADR-P.02) =================

    private void Release(LowLevelHookKind kind)
    {
        lock (_sync)
        {
            int index = (int)kind;
            if (_refCounts[index] == 0)
                return;

            _refCounts[index]--;
            // RF-P1.06: desinstala SEMPRE, inclusive com o host suspenso. A condicao
            // anterior (so desinstalar fora da suspensao) vazava exatamente o cenario
            // que este trabalho existe para eliminar: fechar o flyout durante um jogo
            // em tela cheia deixava o hook LL vivo no sistema indefinidamente.
            if (_refCounts[index] == 0)
                PostCommand(WM_KLIP_UNINSTALL, kind);
        }
    }

    private sealed class HookScope(LowLevelHookHost host, LowLevelHookKind kind) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            // Idempotente: um Dispose duplicado nao pode zerar a contagem de outro escopo.
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            host.Release(kind);
        }
    }

    // ================= Thread de pump =================

    /// <summary>Sempre chamado sob _sync, entao ha no maximo um comando em voo.</summary>
    private void PostCommand(uint message, LowLevelHookKind kind)
    {
        uint threadId = Volatile.Read(ref _pumpThreadId);
        if (threadId == 0)
            return;

        _commandSignal.Reset();
        if (!NativeMethods.PostThreadMessageW(threadId, message, (nuint)(int)kind, nint.Zero))
            return;

        // O chamador precisa que o hook ja esteja de pe (ou ja removido) quando
        // Acquire/Dispose retornarem, senao o primeiro evento se perde. A espera e
        // curta e limitada, e a condicao de parada e o estado REAL do handle - um
        // sinal atrasado de um comando anterior so faz o laco reavaliar.
        long deadline = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * CommandTimeoutMilliseconds / 1000);
        bool wantInstalled = message == WM_KLIP_INSTALL;

        while (IsInstalled(kind) != wantInstalled)
        {
            long remainingTicks = deadline - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
                break;

            int remainingMs = (int)(remainingTicks * 1000 / Stopwatch.Frequency);
            if (!_commandSignal.Wait(remainingMs > 0 ? remainingMs : 1))
                break;

            _commandSignal.Reset();
        }
    }

    private void PumpThreadMain()
    {
        try
        {
            // Cuidado 3: PostThreadMessage antes de a fila existir e perdido em silencio.
            // PeekMessage forca a criacao da fila de mensagens desta thread.
            NativeMethods.PeekMessageW(out _, nint.Zero, 0, 0, NativeMethods.PM_NOREMOVE);
            Volatile.Write(ref _pumpThreadId, NativeMethods.GetCurrentThreadId());
            Warmup();
        }
        catch
        {
            // Nunca deixar a thread morrer sem liberar quem espera em Start().
        }
        finally
        {
            _pumpReady.Set();
        }

        while (true)
        {
            int result = NativeMethods.GetMessageW(out var msg, nint.Zero, 0, 0);
            if (result is 0 or -1) // 0 = WM_QUIT, -1 = erro
                break;

            switch (msg.message)
            {
                case WM_KLIP_INSTALL:
                    InstallOnPumpThread((LowLevelHookKind)(int)msg.wParam);
                    _commandSignal.Set();
                    break;

                case WM_KLIP_UNINSTALL:
                    UninstallOnPumpThread((LowLevelHookKind)(int)msg.wParam);
                    _commandSignal.Set();
                    break;

                default:
                    NativeMethods.TranslateMessage(ref msg);
                    NativeMethods.DispatchMessageW(ref msg);
                    break;
            }
        }

        // Saida limpa: os dois hooks saem do sistema pela thread que os instalou.
        UninstallOnPumpThread(LowLevelHookKind.Keyboard);
        UninstallOnPumpThread(LowLevelHookKind.Mouse);
        Volatile.Write(ref _pumpThreadId, 0);
        _commandSignal.Set();
    }

    /// <summary>
    /// RF-P1.02: pre-JIT do caminho quente. Sem isso o PRIMEIRO evento depois de instalar
    /// o hook paga 1 a 5 ms de compilacao - dentro do orcamento de 300 ms, mas visivel como
    /// engasgo na primeira tecla.
    /// <para>
    /// Os callbacks sao chamados com nCode = -1, que passa direto por CallNextHookEx sem
    /// tocar no lParam. Isso ja basta para compilar o metodo INTEIRO (o JIT trabalha por
    /// metodo, nao por ramo), mas nao compila os callees que aquele ramo nao invoca - por
    /// isso cada um deles e aquecido explicitamente abaixo. O ring leva um evento falso,
    /// drenado aqui mesmo (o worker so sobe depois do warm-up).
    /// </para>
    /// <para>
    /// Custo colateral: 2 amostras a mais em Metrics.TotalCallbacks. RecordDrop fica de fora
    /// de proposito - aquece-lo deixaria um descarte fantasma no contador para sempre.
    /// </para>
    /// </summary>
    private static void Warmup()
    {
        var keyboardProc = (delegate* unmanaged<int, nint, nint, nint>)&KeyboardProc;
        var mouseProc = (delegate* unmanaged<int, nint, nint, nint>)&MouseProc;

        // [UnmanagedCallersOnly] proibe chamada direta pelo nome; pelo ponteiro e permitido.
        _ = keyboardProc(-1, nint.Zero, nint.Zero);
        _ = mouseProc(-1, nint.Zero, nint.Zero);

        // Callees que o ramo de nCode = -1 nao alcanca.
        _ = HookPolicy.CtrlVArmed;
        _ = HookPolicy.KeyboardActive;
        _ = HookPolicy.MouseActive;
        _ = HookPolicy.SwallowKeys.Contains(0);
        _ = HookPolicy.SwallowKeysWithCtrl.Contains(0);
        _ = IsCtrlDown();

        // Roda com a fila desarmada (Start acontece antes de qualquer arme), entao o
        // guard e tomado e devolvido sem efeito observavel.
        if (HookPolicy.TryBeginCtrlV())
            HookPolicy.ReleaseCtrlV();

        Ring.TryEnqueue(0, 0, 0, 0);
        Ring.TryDequeue(out _);
        Ring.Reset(); // descarta o sinal pendente do evento falso
    }

    private void InstallOnPumpThread(LowLevelHookKind kind)
    {
        // Cuidado 1: SetWindowsHookEx precisa ser chamado na thread que vai receber os
        // callbacks. Chamado de outra thread ele registra e nunca dispara.
        if (kind == LowLevelHookKind.Keyboard)
        {
            if (_keyboardHook != nint.Zero)
                return;

            nint handle = NativeMethods.SetWindowsHookExW(
                NativeMethods.WH_KEYBOARD_LL,
                (nint)(delegate* unmanaged<int, nint, nint, nint>)&KeyboardProc,
                nint.Zero, // WH_KEYBOARD_LL nao injeta DLL: hMod pode ser 0
                0);
            Volatile.Write(ref _keyboardHook, handle);
        }
        else
        {
            if (_mouseHook != nint.Zero)
                return;

            nint handle = NativeMethods.SetWindowsHookExW(
                NativeMethods.WH_MOUSE_LL,
                (nint)(delegate* unmanaged<int, nint, nint, nint>)&MouseProc,
                nint.Zero,
                0);
            Volatile.Write(ref _mouseHook, handle);
        }
    }

    private void UninstallOnPumpThread(LowLevelHookKind kind)
    {
        if (kind == LowLevelHookKind.Keyboard)
        {
            nint handle = _keyboardHook;
            if (handle == nint.Zero)
                return;

            NativeMethods.UnhookWindowsHookEx(handle);
            Volatile.Write(ref _keyboardHook, nint.Zero);
        }
        else
        {
            nint handle = _mouseHook;
            if (handle == nint.Zero)
                return;

            NativeMethods.UnhookWindowsHookEx(handle);
            Volatile.Write(ref _mouseHook, nint.Zero);
        }
    }

    // ================= Thread worker =================

    private void WorkerThreadMain(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Ring.WaitForWork(WorkerWaitMilliseconds);

            while (Ring.TryDequeue(out var e))
            {
                if (token.IsCancellationRequested)
                    return;

                var handler = Observed;
                if (handler is null)
                    continue;

                try
                {
                    handler(e);
                }
                catch
                {
                    // Um assinante que lance nao pode matar o worker: o proximo evento
                    // (e a proxima abertura do flyout) tem que continuar funcionando.
                }
            }
        }
    }

    // ================= Callbacks (caminho critico) =================

    /// <summary>
    /// Estado do modificador via <c>GetAsyncKeyState</c> - uma syscall barata, sem
    /// alocacao. O estado assincrono do evento CORRENTE ainda nao foi atualizado pelo
    /// Windows quando o hook LL roda, mas modificadores SEGURADOS de eventos anteriores
    /// (o Ctrl de um Ctrl+V) ja estao la, que e exatamente o caso de uso.
    /// VK_CONTROL agrega VK_LCONTROL e VK_RCONTROL: uma chamada basta.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsCtrlDown() => (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;

    [UnmanagedCallersOnly]
    private static nint KeyboardProc(int nCode, nint wParam, nint lParam)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            if (nCode != NativeMethods.HC_ACTION)
                return NativeMethods.CallNextHookEx(nint.Zero, nCode, wParam, lParam);

            uint message = (uint)wParam;
            if (message != NativeMethods.WM_KEYDOWN && message != NativeMethods.WM_SYSKEYDOWN)
                return NativeMethods.CallNextHookEx(nint.Zero, nCode, wParam, lParam);

            // ADR-P.04: leitura por ponteiro. Marshal.PtrToStructure aloca e passa pelo
            // marshaller generico A CADA TECLA do sistema inteiro - proibido aqui.
            ref readonly var data = ref Unsafe.AsRef<NativeMethods.KBDLLHOOKSTRUCT>((void*)lParam);
            int vk = (int)data.vkCode;
            int scan = (int)data.scanCode;
            uint time = data.time;

            // 1) Fila de colagem armada: Ctrl+V vira evento sintetico. NUNCA engolido -
            //    o Ctrl+V precisa chegar ao app alvo para a colagem acontecer de verdade.
            if (HookPolicy.CtrlVArmed && vk == VK_V && IsCtrlDown() && HookPolicy.TryBeginCtrlV())
            {
                if (!Ring.TryEnqueue(KlipInputMessages.CtrlV, vk, scan, time))
                {
                    HookPolicy.Metrics.RecordDrop();
                    // Ninguem vai tratar o evento perdido, entao devolve o guard aqui
                    // mesmo - senao a fila travaria no primeiro descarte.
                    HookPolicy.ReleaseCtrlV();
                }
            }

            // 2) Flyout aberto: publica a tecla e decide o descarte em O(1).
            if (HookPolicy.KeyboardActive)
            {
                bool swallow = HookPolicy.SwallowKeys.Contains(vk)
                    || (HookPolicy.SwallowKeysWithCtrl.Contains(vk) && IsCtrlDown());

                if (!Ring.TryEnqueue(message, vk, scan, time))
                    HookPolicy.Metrics.RecordDrop();

                if (swallow)
                    return 1; // consumida: nao chega ao app abaixo
            }

            return NativeMethods.CallNextHookEx(nint.Zero, nCode, wParam, lParam);
        }
        catch
        {
            // Uma excecao escapando daqui congelaria a digitacao global (e derrubaria o
            // processo, por ser fronteira nao gerenciada). Engole tudo, sem alocar.
            return NativeMethods.CallNextHookEx(nint.Zero, nCode, wParam, lParam);
        }
        finally
        {
            HookPolicy.Metrics.Record(Stopwatch.GetTimestamp() - start);
        }
    }

    [UnmanagedCallersOnly]
    private static nint MouseProc(int nCode, nint wParam, nint lParam)
    {
        // WM_MOUSEMOVE e 99% do volume (1000 a 8000 eventos/s num mouse gamer). Descartado
        // ANTES de qualquer outro trabalho, inclusive antes de ler o relogio: medir o que
        // custa uma comparacao dominaria as proprias metricas.
        if ((uint)wParam == NativeMethods.WM_MOUSEMOVE)
            return NativeMethods.CallNextHookEx(nint.Zero, nCode, wParam, lParam);

        long start = Stopwatch.GetTimestamp();
        try
        {
            if (nCode != NativeMethods.HC_ACTION)
                return NativeMethods.CallNextHookEx(nint.Zero, nCode, wParam, lParam);

            uint message = (uint)wParam;
            if (message != NativeMethods.WM_LBUTTONDOWN
                && message != NativeMethods.WM_RBUTTONDOWN
                && message != NativeMethods.WM_MBUTTONDOWN)
            {
                return NativeMethods.CallNextHookEx(nint.Zero, nCode, wParam, lParam);
            }

            if (HookPolicy.MouseActive)
            {
                // Geometria sempre em pixels fisicos: MSLLHOOKSTRUCT ja entrega assim.
                ref readonly var data = ref Unsafe.AsRef<NativeMethods.MSLLHOOKSTRUCT>((void*)lParam);
                if (!Ring.TryEnqueue(message, data.x, data.y, data.time))
                    HookPolicy.Metrics.RecordDrop();
            }

            // O hook de mouse NUNCA engole: so observa o clique de fora para fechar o flyout.
            return NativeMethods.CallNextHookEx(nint.Zero, nCode, wParam, lParam);
        }
        catch
        {
            return NativeMethods.CallNextHookEx(nint.Zero, nCode, wParam, lParam);
        }
        finally
        {
            HookPolicy.Metrics.Record(Stopwatch.GetTimestamp() - start);
        }
    }
}
