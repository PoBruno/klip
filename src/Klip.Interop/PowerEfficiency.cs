using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Klip.Interop;

/// <summary>
/// Controle de QoS de energia do Windows (RF-P3.03): EcoQoS por processo e por
/// thread, prioridade de memoria e background IO mode para trabalho de manutencao.
/// <para>
/// Nenhum metodo lanca. Quando o Windows nao expoe a API (builds antigos do
/// Windows 10), a chamada devolve false, o resultado e cacheado e o app continua
/// funcionando com o comportamento padrao do sistema.
/// </para>
/// </summary>
public static class PowerEfficiency
{
    private static readonly uint ThrottlingStateSize =
        (uint)Marshal.SizeOf<NativeMethods.POWER_THROTTLING_STATE>();

    private static readonly uint MemoryPriorityInfoSize =
        (uint)Marshal.SizeOf<NativeMethods.MEMORY_PRIORITY_INFORMATION>();

    // Cache de indisponibilidade: uma vez que o export falta, nao paga mais o
    // custo de montar/lancar a excecao a cada chamada.
    private static bool _processApiMissing;
    private static bool _threadApiMissing;
    private static bool _threadPriorityApiMissing;

    /// <summary>
    /// Marca o PROCESSO como EcoQoS: frequencia eficiente e E-cores, inclusive na
    /// tomada. Chamar quando todas as janelas estiverem escondidas.
    /// ControlMask = POWER_THROTTLING_EXECUTION_SPEED, StateMask = POWER_THROTTLING_EXECUTION_SPEED.
    /// </summary>
    public static bool EnterProcessEcoQos() => SetProcessThrottling(
        controlMask: NativeMethods.POWER_THROTTLING_EXECUTION_SPEED,
        stateMask: NativeMethods.POWER_THROTTLING_EXECUTION_SPEED);

    /// <summary>
    /// HighQoS: desliga o throttling. Chamar ANTES de montar o visual tree do painel
    /// ou iniciar uma captura - caso contrario o primeiro layout roda em E-core lento.
    /// ControlMask = POWER_THROTTLING_EXECUTION_SPEED, StateMask = 0 (mecanismo desligado).
    /// </summary>
    public static bool EnterProcessHighQos() => SetProcessThrottling(
        controlMask: NativeMethods.POWER_THROTTLING_EXECUTION_SPEED,
        stateMask: 0);

    /// <summary>
    /// Devolve o controle ao sistema (heuristicas por visibilidade/audio).
    /// ControlMask = 0, StateMask = 0.
    /// </summary>
    public static bool ResetProcessQosToSystem() => SetProcessThrottling(
        controlMask: 0,
        stateMask: 0);

    /// <summary>
    /// Ignora requests de timer resolution deste processo - defesa contra dependencias
    /// que chamem timeBeginPeriod. A operacao e reversivel: ao desligar, o sistema
    /// lembra e volta a honrar o request anterior.
    /// </summary>
    public static bool IgnoreTimerResolutionRequests(bool ignore) => SetProcessThrottling(
        controlMask: NativeMethods.PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
        stateMask: ignore ? NativeMethods.PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION : 0);

    /// <summary>
    /// Prioridade de memoria BAIXA: "apare as minhas paginas antes das dos outros".
    /// Alternativa correta e nao destrutiva ao EmptyWorkingSet.
    /// </summary>
    public static bool SetProcessMemoryPriorityLow() =>
        SetProcessMemoryPriority(NativeMethods.MEMORY_PRIORITY_LOW);

    /// <summary>Restaura a prioridade de memoria padrao do processo.</summary>
    public static bool SetProcessMemoryPriorityNormal() =>
        SetProcessMemoryPriority(NativeMethods.MEMORY_PRIORITY_NORMAL);

    /// <summary>
    /// Marca a THREAD ATUAL como EcoQoS. Use em threads de manutencao (retencao,
    /// thumbnails, VACUUM): vao para os E-cores sem afetar a UI thread.
    /// </summary>
    public static bool MarkCurrentThreadEco()
    {
        if (_threadApiMissing)
            return false;

        var state = new NativeMethods.POWER_THROTTLING_STATE
        {
            Version = NativeMethods.THREAD_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = NativeMethods.POWER_THROTTLING_EXECUTION_SPEED,
            StateMask = NativeMethods.POWER_THROTTLING_EXECUTION_SPEED,
        };

        try
        {
            return NativeMethods.SetThreadInformation(
                NativeMethods.GetCurrentThread(),
                NativeMethods.ThreadPowerThrottling,
                ref state,
                ThrottlingStateSize);
        }
        catch (EntryPointNotFoundException)
        {
            _threadApiMissing = true;
            return false;
        }
        catch (DllNotFoundException)
        {
            _threadApiMissing = true;
            return false;
        }
    }

    /// <summary>
    /// Executa com prioridade de CPU, IO e memoria baixas + E-cores. Reverte no finally.
    /// <para>
    /// AVISO OFICIAL: "a thread in background processing mode should minimize sharing
    /// resources such as critical sections, heaps, and handles with other threads in the
    /// process, otherwise priority inversions can occur". Ou seja: NAO segure locks
    /// compartilhados com a UI (conexao SQLite, caches) dentro deste escopo - a thread
    /// rebaixada segura o lock, a UI pede o mesmo lock e o painel trava por inversao
    /// de prioridade.
    /// </para>
    /// <para>
    /// THREAD_MODE_BACKGROUND_BEGIN/END so valem para a thread que chama. O END so e
    /// emitido se o BEGIN teve sucesso: a API falha com ERROR_THREAD_MODE_ALREADY_BACKGROUND
    /// quando a thread ja esta no modo, e nesse caso quem entrou e responsavel por sair.
    /// </para>
    /// </summary>
    public static void RunAsBackgroundIo(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        var thread = NativeMethods.GetCurrentThread();
        var entered = TryBeginBackgroundIo(thread);
        try
        {
            MarkCurrentThreadEco();
            work();
        }
        finally
        {
            if (entered)
                TryEndBackgroundIo(thread);
        }
    }

    /// <summary>
    /// Versao assincrona de <see cref="RunAsBackgroundIo(Action)"/>, com os mesmos
    /// avisos de inversao de prioridade.
    /// <para>
    /// O trabalho roda numa thread dedicada com um SynchronizationContext de thread
    /// unica: as continuacoes do await voltam para a mesma thread que entrou em
    /// background mode, que e a unica onde o END pode ser emitido. Cria uma thread
    /// por chamada - use para manutencao, nunca em caminho quente.
    /// </para>
    /// </summary>
    public static Task RunAsBackgroundIoAsync(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => PumpBackgroundIo(work, completion))
        {
            IsBackground = true,
            Name = "Klip.BackgroundIo",
        };
        thread.Start();
        return completion.Task;
    }

    private static void PumpBackgroundIo(Func<Task> work, TaskCompletionSource completion)
    {
        var previousContext = SynchronizationContext.Current;
        var pump = new SingleThreadSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(pump);

        var thread = NativeMethods.GetCurrentThread();
        var entered = TryBeginBackgroundIo(thread);

        Task? pending = null;
        Exception? failure = null;
        try
        {
            MarkCurrentThreadEco();
            pending = work();
            if (pending is not null)
            {
                // encerra a bomba quando o trabalho terminar, seja qual for o desfecho
                pending.ContinueWith(
                    static (_, state) => ((SingleThreadSynchronizationContext)state!).Complete(),
                    pump,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                pump.Drain();
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            pump.Complete();
            if (entered)
                TryEndBackgroundIo(thread);
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        if (failure is not null)
            completion.TrySetException(failure);
        else if (pending is null)
            completion.TrySetResult();
        else if (pending.IsCanceled)
            completion.TrySetCanceled();
        else if (pending.Exception is { } aggregate)
            completion.TrySetException(aggregate.InnerExceptions);
        else
            completion.TrySetResult();
    }

    private static bool SetProcessThrottling(uint controlMask, uint stateMask)
    {
        if (_processApiMissing)
            return false;

        var state = new NativeMethods.POWER_THROTTLING_STATE
        {
            Version = NativeMethods.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = controlMask,
            StateMask = stateMask,
        };

        try
        {
            return NativeMethods.SetProcessInformation(
                NativeMethods.GetCurrentProcess(),
                NativeMethods.ProcessPowerThrottling,
                ref state,
                ThrottlingStateSize);
        }
        catch (EntryPointNotFoundException)
        {
            _processApiMissing = true;
            return false;
        }
        catch (DllNotFoundException)
        {
            _processApiMissing = true;
            return false;
        }
    }

    private static bool SetProcessMemoryPriority(uint priority)
    {
        if (_processApiMissing)
            return false;

        var info = new NativeMethods.MEMORY_PRIORITY_INFORMATION { MemoryPriority = priority };

        try
        {
            return NativeMethods.SetProcessInformation(
                NativeMethods.GetCurrentProcess(),
                NativeMethods.ProcessMemoryPriority,
                ref info,
                MemoryPriorityInfoSize);
        }
        catch (EntryPointNotFoundException)
        {
            _processApiMissing = true;
            return false;
        }
        catch (DllNotFoundException)
        {
            _processApiMissing = true;
            return false;
        }
    }

    private static bool TryBeginBackgroundIo(nint thread)
    {
        if (_threadPriorityApiMissing)
            return false;

        try
        {
            // rebaixa CPU, IO e memoria de uma vez; falha se a thread ja esta no modo
            return NativeMethods.SetThreadPriority(thread, NativeMethods.THREAD_MODE_BACKGROUND_BEGIN);
        }
        catch (EntryPointNotFoundException)
        {
            _threadPriorityApiMissing = true;
            return false;
        }
        catch (DllNotFoundException)
        {
            _threadPriorityApiMissing = true;
            return false;
        }
    }

    private static void TryEndBackgroundIo(nint thread)
    {
        try
        {
            NativeMethods.SetThreadPriority(thread, NativeMethods.THREAD_MODE_BACKGROUND_END);
        }
        catch (EntryPointNotFoundException)
        {
            _threadPriorityApiMissing = true;
        }
        catch (DllNotFoundException)
        {
            _threadPriorityApiMissing = true;
        }
    }

    /// <summary>
    /// Bomba de mensagens minima de thread unica: mantem as continuacoes do await
    /// na mesma thread que entrou em background IO mode.
    /// </summary>
    private sealed class SingleThreadSynchronizationContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            try
            {
                _queue.Add((d, state));
            }
            catch (InvalidOperationException)
            {
                // bomba ja encerrada: continuacao tardia vai para o pool em vez de sumir
                ThreadPool.QueueUserWorkItem(
                    static item => item.Callback(item.State),
                    (Callback: d, State: state),
                    preferLocal: false);
            }
        }

        public void Drain()
        {
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
                callback(state);
        }

        public void Complete()
        {
            try
            {
                _queue.CompleteAdding();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
