using System.Diagnostics;
using System.Globalization;
using System.Windows.Threading;
using Klip.App.Services;

namespace Klip.App.Diagnostics;

/// <summary>
/// RF-P0.02: mede quanto tempo a UI thread demora para atender uma mensagem.
/// Um bloqueio longo na UI thread do Klip e o que congela o input do sistema
/// quando ha hook LL instalado - transformar isso em evidencia rastreavel.
/// <para>
/// Criterio de aceite CA-P2.1: nenhum bloqueio acima de 50 ms durante 30 min de
/// uso intenso.
/// </para>
/// </summary>
public sealed class UiThreadWatchdog : IDisposable
{
    /// <summary>
    /// Teto de 1 registro a cada 10 s. Um travamento de 60 s com sonda por segundo
    /// escreveria 60 linhas identicas e afogaria o log logo quando ele importa.
    /// </summary>
    private const int LogThrottleSeconds = 10;

    private static readonly long LogThrottleTicks = Stopwatch.Frequency * LogThrottleSeconds;

    private readonly Dispatcher _uiDispatcher;
    private readonly int _intervalMilliseconds;
    private readonly int _thresholdMilliseconds;
    private readonly object _sync = new();

    private System.Threading.Timer? _timer;
    private int _probeInFlight;
    private long _worstTicks;
    private long _stalls;

    // 0 = nunca registrou; qualquer outro valor e um timestamp de Stopwatch.
    private long _lastLogTimestamp;

    private int _disposed;

    /// <param name="uiDispatcher">Dispatcher da UI thread que sera sondada.</param>
    /// <param name="intervalMilliseconds">Periodo entre sondas.</param>
    /// <param name="thresholdMilliseconds">A partir daqui a latencia vira registro no log.</param>
    public UiThreadWatchdog(Dispatcher uiDispatcher, int intervalMilliseconds = 1000, int thresholdMilliseconds = 100)
    {
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        ArgumentOutOfRangeException.ThrowIfLessThan(intervalMilliseconds, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(thresholdMilliseconds, 1);

        _uiDispatcher = uiDispatcher;
        _intervalMilliseconds = intervalMilliseconds;
        _thresholdMilliseconds = thresholdMilliseconds;
    }

    /// <summary>Quantas sondas passaram do limiar desde o inicio do processo.</summary>
    public long StallCount => Interlocked.Read(ref _stalls);

    /// <summary>
    /// Sobe o watchdog. Idempotente.
    /// <para>
    /// RF-P0.02: <see cref="System.Threading.Timer"/>, nunca DispatcherTimer - a
    /// sonda precisa nascer FORA da UI thread, senao ela mediria a si mesma e
    /// jamais detectaria um bloqueio.
    /// </para>
    /// </summary>
    public void Start()
    {
        lock (_sync)
        {
            if (Volatile.Read(ref _disposed) != 0 || _timer is not null)
                return;

            _timer = new System.Threading.Timer(
                OnTick,
                state: null,
                dueTime: _intervalMilliseconds,
                period: _intervalMilliseconds);
        }
    }

    public void Stop()
    {
        System.Threading.Timer? timer;
        lock (_sync)
        {
            timer = _timer;
            _timer = null;
        }

        timer?.Dispose();
    }

    /// <summary>Pior latencia observada desde o ultimo Sample, em ms. Zera ao amostrar.</summary>
    public double SampleWorstMilliseconds()
    {
        long ticks = Interlocked.Exchange(ref _worstTicks, 0);
        return ticks * 1000.0 / Stopwatch.Frequency;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Stop();
    }

    // ================= Sonda =================

    private void OnTick(object? _)
    {
        bool owned = false;
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            // Dispatcher encerrando: a fila nao vai mais ser servida, entao a
            // medicao nao teria significado (e o BeginInvoke lancaria).
            if (_uiDispatcher.HasShutdownStarted || _uiDispatcher.HasShutdownFinished)
                return;

            // Uma sonda por vez: durante um travamento de 30 s, sondas por segundo
            // enfileirariam 30 operacoes que executariam todas juntas ao destravar,
            // e o proprio watchdog viraria carga extra na UI thread.
            if (Interlocked.CompareExchange(ref _probeInFlight, 1, 0) != 0)
                return;

            owned = true;
            long start = Stopwatch.GetTimestamp();

            // DispatcherPriority.Send e a maior prioridade da fila: o que se mede e
            // o tempo ate a UI thread conseguir ATENDER a fila, nao a espera atras
            // de trabalho de menor prioridade legitimamente enfileirado.
            _uiDispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => CompleteProbe(start)));

            // A partir daqui quem devolve o guard e CompleteProbe.
            owned = false;
        }
        catch (Exception)
        {
            // Excecao nao tratada no callback de System.Threading.Timer derruba o
            // PROCESSO. Caso tipico: o dispatcher encerra entre o teste acima e o
            // BeginInvoke. Sem log aqui de proposito - o log escreve na fila de
            // outra thread e este caminho roda em pleno shutdown.
        }
        finally
        {
            if (owned)
                Volatile.Write(ref _probeInFlight, 0);
        }
    }

    /// <summary>Executa NA UI thread: o tempo ate chegar aqui e a latencia medida.</summary>
    private void CompleteProbe(long startTimestamp)
    {
        try
        {
            long elapsedTicks = Stopwatch.GetTimestamp() - startTimestamp;
            RecordWorst(elapsedTicks);

            double elapsedMilliseconds = elapsedTicks * 1000.0 / Stopwatch.Frequency;
            if (elapsedMilliseconds < _thresholdMilliseconds)
                return;

            long stalls = Interlocked.Increment(ref _stalls);
            if (!TryTakeLogSlot())
                return;

            StartupLog.Write(string.Format(
                CultureInfo.InvariantCulture,
                "[watchdog] UI thread demorou {0:0} ms para atender (limiar {1} ms, travamentos {2})",
                elapsedMilliseconds,
                _thresholdMilliseconds,
                stalls));
        }
        catch (Exception)
        {
            // Roda na UI thread: uma excecao aqui viraria DispatcherUnhandledException.
            // O watchdog nunca pode ser a causa de um crash.
        }
        finally
        {
            Volatile.Write(ref _probeInFlight, 0);
        }
    }

    /// <summary>CAS em laco: so escreve quando a nova medicao supera o pior caso corrente.</summary>
    private void RecordWorst(long elapsedTicks)
    {
        long current = Volatile.Read(ref _worstTicks);
        while (elapsedTicks > current)
        {
            long observed = Interlocked.CompareExchange(ref _worstTicks, elapsedTicks, current);
            if (observed == current)
                return;
            current = observed;
        }
    }

    /// <summary>Rate limit do log: no maximo um registro a cada 10 s.</summary>
    private bool TryTakeLogSlot()
    {
        long now = Stopwatch.GetTimestamp();
        long last = Volatile.Read(ref _lastLogTimestamp);

        // O primeiro travamento sempre e registrado (_lastLogTimestamp nasce em 0).
        if (last != 0 && now - last < LogThrottleTicks)
            return false;

        return Interlocked.CompareExchange(ref _lastLogTimestamp, now, last) == last;
    }
}
