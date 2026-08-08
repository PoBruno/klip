using System.Diagnostics;

namespace Klip.Core.Diagnostics;

/// <summary>Fotografia da janela de medicao do hook, produzida por <see cref="HookMetrics.Sample"/>.</summary>
public readonly record struct HookSample(
    double EventsPerSecond,
    double WorstMicroseconds,
    double BudgetPercent,
    long TotalCallbacks,
    long Dropped);

/// <summary>
/// RF-P1.04: instrumentacao do caminho quente do hook. O callback tem um orcamento
/// duro (LowLevelHooksTimeout, 300 ms por padrao); se estourar, o Windows remove o
/// hook silenciosamente. Aqui so ha incremento atomico e um CAS no pior caso -
/// nenhuma alocacao, nenhum lock, nenhuma lista de amostras.
/// </summary>
public sealed class HookMetrics
{
    private long _totalCallbacks;
    private long _dropped;
    private long _worstTicks;

    // Referencias da ultima amostragem, usadas para derivar a taxa por segundo.
    private long _lastSampleTimestamp;
    private long _lastTotalCallbacks;

    public HookMetrics()
    {
        // Primeira chamada a Sample() mede a janela desde a construcao.
        _lastSampleTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>Orcamento do callback em ms (LowLevelHooksTimeout efetivo). Default 300.</summary>
    public int BudgetMilliseconds { get; set; } = 300;

    /// <summary>Chamado do callback do hook. O(1), sem alocacao.</summary>
    /// <param name="elapsedTicks">Duracao do callback em ticks de <see cref="Stopwatch"/>.</param>
    public void Record(long elapsedTicks)
    {
        Interlocked.Increment(ref _totalCallbacks);

        // CAS em laco: so escreve quando o novo valor supera o pior caso corrente.
        long current = Volatile.Read(ref _worstTicks);
        while (elapsedTicks > current)
        {
            long observed = Interlocked.CompareExchange(ref _worstTicks, elapsedTicks, current);
            if (observed == current)
                break;
            current = observed;
        }
    }

    /// <summary>Conta um evento descartado por buffer cheio.</summary>
    public void RecordDrop() => Interlocked.Increment(ref _dropped);

    /// <summary>Amostra e ZERA o pior caso. Chamar de um timer de background.</summary>
    public HookSample Sample()
    {
        long now = Stopwatch.GetTimestamp();
        long previousTimestamp = Interlocked.Exchange(ref _lastSampleTimestamp, now);
        long worstTicks = Interlocked.Exchange(ref _worstTicks, 0);

        long total = Interlocked.Read(ref _totalCallbacks);
        long previousTotal = Interlocked.Exchange(ref _lastTotalCallbacks, total);
        long dropped = Interlocked.Read(ref _dropped);

        double seconds = (now - previousTimestamp) / (double)Stopwatch.Frequency;
        double eventsPerSecond = seconds > 0.0 ? (total - previousTotal) / seconds : 0.0;

        double worstMicroseconds = worstTicks * 1_000_000.0 / Stopwatch.Frequency;
        double budgetMicroseconds = BudgetMilliseconds * 1000.0;
        double budgetPercent = budgetMicroseconds > 0.0
            ? worstMicroseconds / budgetMicroseconds * 100.0
            : 0.0;

        return new HookSample(eventsPerSecond, worstMicroseconds, budgetPercent, total, dropped);
    }
}
