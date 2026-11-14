using Klip.Interop;
using Klip.Interop.Input;

namespace Klip.App.Services;

/// <summary>
/// Rede de seguranca contra interferencia com jogos. O Windows NAO
/// notifica entrada/saida de app em tela cheia (doc oficial de
/// SHQueryUserNotificationState), entao e obrigatorio fazer polling.
/// <para>
/// Ao entrar em tela cheia os hooks de baixo nivel sao REMOVIDOS do
/// sistema e o processo desce para EcoQoS + prioridade de memoria baixa. Um hook
/// instalado continua sendo chamado pela Raw Input Thread a cada tecla e a cada
/// movimento de mouse mesmo que o callback so retorne - por isso desativar nao
/// basta, tem que desinstalar.
/// </para>
/// <para>
/// So faz polling enquanto existe escopo de hook vivo (o App liga e desliga nas
/// transicoes). Em repouso nao ha hook para proteger, entao nao ha timer.
/// </para>
/// </summary>
public sealed class SystemActivityMonitor : IDisposable
{
    /// <summary>
    /// Polling folgado: o custo de perder 3 s para reagir e zero, o de acordar a
    /// maquina com frequencia num app que fica horas ocioso e alto.
    /// </summary>
    private const int PollIntervalMilliseconds = 3000;

    // Serializa a aplicacao das transicoes: EvaluateNow (UI) e o tick do timer
    // podem correr juntos e nao podem aplicar suspensao/retomada fora de ordem.
    private readonly object _sync = new();

    private System.Threading.Timer? _timer;

    // while suspended with no hook alive there is no polling, so this one-shot looks
    // again every so often; otherwise the eco/low memory state could stay for hours
    private System.Threading.Timer? _recheck;
    private const int RecheckMilliseconds = 30_000;

    // (int)SystemActivityState - int para permitir leitura com Volatile.
    private int _state = (int)SystemActivityState.Normal;

    private int _tickInFlight;
    private int _disposed;

    /// <summary>Ultimo estado avaliado. Nasce em <see cref="SystemActivityState.Normal"/>.</summary>
    public SystemActivityState State => (SystemActivityState)Volatile.Read(ref _state);

    /// <summary>Disparado na thread do timer (background), nao na UI.</summary>
    public event Action<SystemActivityState>? StateChanged;

    /// <summary>
    /// Sobe o polling. Idempotente.
    /// <para>
    /// <see cref="System.Threading.Timer"/>, NUNCA DispatcherTimer - um
    /// DispatcherTimer acordaria a UI thread a cada 3 s pelo resto da sessao num
    /// app que passa horas ocioso no tray.
    /// </para>
    /// </summary>
    public void Start()
    {
        lock (_sync)
        {
            if (Volatile.Read(ref _disposed) != 0 || _timer is not null)
                return;

            // dueTime igual ao period: o primeiro tick tambem espera 3 s. O startup
            // ja e o trecho mais concorrido do processo e ninguem entra em tela
            // cheia nos primeiros 3 s de vida do app.
            _timer = new System.Threading.Timer(
                OnTick,
                state: null,
                dueTime: PollIntervalMilliseconds,
                period: PollIntervalMilliseconds);
        }
    }

    /// <summary>
    /// Para o polling. Stopping is not leaving the state, so a suspension in course is
    /// evaluated once more right away (off the caller's thread, the shell query can
    /// block) and, while it lasts, again every 30 s until the fullscreen app is gone.
    /// </summary>
    public void Stop()
    {
        System.Threading.Timer? timer;
        lock (_sync)
        {
            timer = _timer;
            _timer = null;
        }

        timer?.Dispose();
        RecheckSoon();
    }

    /// <summary>
    /// Asks for one evaluation off the calling thread when the state is suspended
    /// (unlock, a hotkey, polling just stopped). No-op in the normal state.
    /// </summary>
    public void RecheckSoon()
    {
        if (State != SystemActivityState.Suspended || Volatile.Read(ref _disposed) != 0)
            return;
        lock (_sync)
        {
            _recheck ??= new System.Threading.Timer(OnRecheck, null, Timeout.Infinite, Timeout.Infinite);
            _recheck.Change(0, Timeout.Infinite);
        }
    }

    private void OnRecheck(object? _)
    {
        try
        {
            if (EvaluateCore() != SystemActivityState.Suspended)
                return;
            lock (_sync)
            {
                // the polling is back (a hook came up): it takes over from here
                if (_timer is null && Volatile.Read(ref _disposed) == 0)
                    _recheck?.Change(RecheckMilliseconds, Timeout.Infinite);
            }
        }
        catch (Exception ex)
        {
            try
            {
                StartupLog.WriteException("SystemActivityMonitor.OnRecheck", ex);
            }
            catch (Exception)
            {
                // a pool timer callback must never throw
            }
        }
    }

    /// <summary>Forca uma avaliacao agora (ex.: antes de abrir o overlay de captura).</summary>
    /// <remarks>
    /// Roda na thread do chamador (pode ser a UI). De proposito NAO marca a thread
    /// como Eco: rebaixar a UI thread para E-core seria exatamente o oposto do que
    /// se quer no caminho de abertura de janela.
    /// </remarks>
    public SystemActivityState EvaluateNow() => EvaluateCore();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Stop();
        lock (_sync)
        {
            _recheck?.Dispose();
            _recheck = null;
        }

        // LowLevelHookHost.Shared e um singleton de processo: um monitor descartado
        // nao pode deixar os hooks suspensos para sempre. Devolve o estado normal
        // sem tocar em QoS (o processo esta encerrando ou voltando ao idle).
        lock (_sync)
        {
            if ((SystemActivityState)_state != SystemActivityState.Suspended)
                return;
            Volatile.Write(ref _state, (int)SystemActivityState.Normal);
        }

        try
        {
            LowLevelHookHost.Shared.ResumeAll();
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("SystemActivityMonitor.Dispose", ex);
        }
    }

    // ================= Polling =================

    private void OnTick(object? _)
    {
        // Reentrancia: um tick lento (shell travado respondendo a
        // SHQueryUserNotificationState) nao pode ser empilhado pelo proximo.
        if (Interlocked.CompareExchange(ref _tickInFlight, 1, 0) != 0)
            return;

        try
        {
            EvaluateCore();
        }
        catch (Exception ex)
        {
            // Excecao nao tratada no callback de System.Threading.Timer derruba o
            // PROCESSO (nao ha handler acima do thread pool): nada escapa daqui.
            try
            {
                StartupLog.WriteException("SystemActivityMonitor.OnTick", ex);
            }
            catch (Exception)
            {
                // Nem o log pode derrubar o tick.
            }
        }
        finally
        {
            Volatile.Write(ref _tickInFlight, 0);
        }
    }

    private SystemActivityState EvaluateCore()
    {
        var next = SystemFullscreenDetector.Evaluate();

        bool changed;
        lock (_sync)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return (SystemActivityState)_state;

            changed = (SystemActivityState)_state != next;
            if (changed)
            {
                Volatile.Write(ref _state, (int)next);
                ApplyTransition(next);
            }
        }

        if (changed)
            RaiseStateChanged(next);

        return next;
    }

    /// <summary>Aplica a politica da transicao. Sempre chamado sob <c>_sync</c>.</summary>
    private static void ApplyTransition(SystemActivityState state)
    {
        if (state == SystemActivityState.Suspended)
        {
            // o item mais importante da suspensao. Remove FISICAMENTE os
            // hooks LL - um hook instalado e chamado pela Raw Input Thread a cada
            // evento do sistema inteiro, e e isso que derruba o frame time do jogo.
            // (SuspendAll preserva escopo vivo: nao arranca o hook de um flyout aberto.)
            LowLevelHookHost.Shared.SuspendAll();

            // enquanto o jogo tem a maquina, o Klip vai para frequencia
            // eficiente/E-cores e diz ao gerenciador de memoria "apare as minhas
            // paginas antes das dos outros".
            //
            // Excecao: se o usuario esta interagindo agora (flyout aberto ou fila de
            // colagem armada), rebaixar para EcoQoS deixaria a UI que ele acabou de
            // abrir lenta - o flyout e NOACTIVATE, entao o app em tela cheia continua
            // sendo o foreground e a heuristica de tela cheia continua valendo.
            if (LowLevelHookHost.Shared.IsInstalled(LowLevelHookKind.Keyboard) ||
                LowLevelHookHost.Shared.IsInstalled(LowLevelHookKind.Mouse))
            {
                StartupLog.Write(
                    $"SystemActivityMonitor: Normal -> Suspended ({DescribeSuspendReason()}); " +
                    "QoS mantida (usuario interagindo com o painel)");
                return;
            }

            PowerEfficiency.EnterProcessEcoQos();
            PowerEfficiency.SetProcessMemoryPriorityLow();

            StartupLog.Write(
                $"SystemActivityMonitor: Normal -> Suspended ({DescribeSuspendReason()}); " +
                "hooks LL removidos, EcoQoS + prioridade de memoria baixa");
            return;
        }

        LowLevelHookHost.Shared.ResumeAll();

        // back to the normal rest state: memory priority normal and qos handed to the
        // system (not HighQoS, that one is asked for by the window that opens). staying
        // low/eco here is what left the first Win+V after a long game paging back in.
        PowerEfficiency.SetProcessMemoryPriorityNormal();
        PowerEfficiency.ResetProcessQosToSystem();
        StartupLog.Write(
            "SystemActivityMonitor: Suspended -> Normal; hooks LL reinstalados, " +
            "QoS do sistema + prioridade de memoria normal");
    }

    /// <summary>
    /// Motivo da suspensao para o log. Segunda leitura, best effort: a
    /// decisao e sempre de <see cref="SystemFullscreenDetector.Evaluate"/> - aqui so
    /// se pergunta qual sinal a explica. Quando o estado de notificacao nao justifica
    /// a suspensao, o que restou foi a heuristica geometrica (borderless fullscreen).
    /// </summary>
    private static string DescribeSuspendReason() =>
        SystemFullscreenDetector.QueryNotificationState() switch
        {
            QUERY_USER_NOTIFICATION_STATE.QUNS_NOT_PRESENT => "QUNS_NOT_PRESENT",
            QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY => "QUNS_BUSY",
            QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN => "QUNS_RUNNING_D3D_FULL_SCREEN",
            QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE => "QUNS_PRESENTATION_MODE",
            _ => "borderless fullscreen",
        };

    /// <summary>
    /// Notifica os assinantes FORA de qualquer lock e com try/catch por assinante:
    /// um handler que lance (ou que faca Dispatcher.Invoke numa UI travada) nao pode
    /// matar a thread do timer nem impedir os demais de receber a transicao.
    /// </summary>
    private void RaiseStateChanged(SystemActivityState state)
    {
        var handler = StateChanged;
        if (handler is null)
            return;

        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                ((Action<SystemActivityState>)subscriber)(state);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("SystemActivityMonitor.StateChanged", ex);
            }
        }
    }
}
