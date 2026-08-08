using System.Globalization;
using Klip.Core.Diagnostics;
using Klip.Interop.Input;
using Microsoft.Win32;

namespace Klip.App.Diagnostics;

/// <summary>
/// RF-P0.01: telemetria do caminho critico de input. Criterio de aceite CA-P1.2:
/// pior caso &lt; 50 us e menos de 2% do orcamento do LowLevelHooksTimeout.
/// <para>
/// O disjuntor do sistema remove o hook em SILENCIO quando o callback estoura o
/// timeout - desde o Windows 7 nao ha aviso nenhum. Medir a folga e a unica forma
/// de saber que ainda ha folga.
/// </para>
/// </summary>
public static class HookHealth
{
    /// <summary>Valor assumido pelo Windows quando a chave nao existe.</summary>
    private const int DefaultTimeoutMilliseconds = 300;

    /// <summary>Teto real: Win10 1709+ e Win11 clampam o valor lido do registro.</summary>
    private const int MaxTimeoutMilliseconds = 1000;

    private const string DesktopKeyPath = @"Control Panel\Desktop";
    private const string TimeoutValueName = "LowLevelHooksTimeout";

    /// <summary>
    /// A linha vai para o log e para a tela de diagnostico e e comparada com o
    /// exemplo da spec, entao a formatacao numerica e FIXA em pt-BR em vez de
    /// seguir a cultura da maquina - dois relatorios de campo tem que ser
    /// comparaveis linha a linha.
    /// </summary>
    private static readonly CultureInfo ReportCulture = CultureInfo.GetCultureInfo("pt-BR");

    // 0 = ainda nao lido do registro.
    private static int _cachedTimeout;

    /// <summary>
    /// Le HKCU\Control Panel\Desktop\LowLevelHooksTimeout. Ausente =&gt; 300 ms.
    /// Acima de 1000 =&gt; clampado (Win10 1709+ e Win11).
    /// <para>
    /// O valor e lido uma unica vez e propagado para
    /// <see cref="HookMetrics.BudgetMilliseconds"/>: <see cref="HookPolicy.Metrics"/>
    /// nasce com 300 ms fixos e so quem le o registro sabe o valor real da maquina.
    /// </para>
    /// </summary>
    public static int EffectiveTimeoutMilliseconds()
    {
        int cached = Volatile.Read(ref _cachedTimeout);
        if (cached != 0)
            return cached;

        int value = ReadTimeoutFromRegistry();

        // Quem vencer a corrida publica o orcamento; os demais reusam o valor dele
        // (identico na pratica, mas assim HookMetrics recebe uma unica escrita).
        int published = Interlocked.CompareExchange(ref _cachedTimeout, value, 0);
        if (published != 0)
            return published;

        HookPolicy.Metrics.BudgetMilliseconds = value;
        return value;
    }

    /// <summary>Amostra e zera o pior caso. Chamar de um timer de background.</summary>
    public static HookSample Sample()
    {
        // Garante que o orcamento ja reflete o registro antes da primeira amostra,
        // senao BudgetPercent sairia calculado contra os 300 ms de fabrica.
        _ = EffectiveTimeoutMilliseconds();
        return LowLevelHookHost.Shared.SampleMetrics();
    }

    /// <summary>
    /// Linha pronta para a tela de diagnostico e para o log.
    /// <para>
    /// ATENCAO: consome uma amostra (<see cref="Sample"/> zera o pior caso), entao
    /// nao chamar junto com <see cref="Sample"/> na mesma janela de medicao.
    /// </para>
    /// </summary>
    public static string FormatSummary()
    {
        var sample = Sample();
        int budget = EffectiveTimeoutMilliseconds();

        return string.Format(
            ReportCulture,
            "Hooks: teclado={0}, mouse={1} | {2:0} ev/s | pior {3:0.0} us ({4:0.00}% de {5} ms) | descartes {6}",
            Describe(LowLevelHookKind.Keyboard),
            Describe(LowLevelHookKind.Mouse),
            sample.EventsPerSecond,
            sample.WorstMicroseconds,
            sample.BudgetPercent,
            budget,
            sample.Dropped);
    }

    /// <summary>
    /// True quando os hooks LL nao estao instalados (estado ideal em idle, CA-P1.3).
    /// Um hook instalado e chamado pela Raw Input Thread a cada evento do sistema
    /// inteiro, mesmo com o callback vazio - "instalado" e o que custa, nao "ativo".
    /// </summary>
    public static bool IsIdle =>
        !LowLevelHookHost.Shared.IsInstalled(LowLevelHookKind.Keyboard)
        && !LowLevelHookHost.Shared.IsInstalled(LowLevelHookKind.Mouse);

    private static string Describe(LowLevelHookKind kind) =>
        LowLevelHookHost.Shared.IsInstalled(kind) ? "sim" : "nao";

    private static int ReadTimeoutFromRegistry()
    {
        try
        {
            // Microsoft.Win32.Registry e API gerenciada, nao P/Invoke: pode viver no
            // App sem violar a regra de manter todo DllImport no Klip.Interop.
            using var key = Registry.CurrentUser.OpenSubKey(DesktopKeyPath);
            object? raw = key?.GetValue(TimeoutValueName);
            if (raw is null)
                return DefaultTimeoutMilliseconds;

            // Normalmente REG_DWORD, mas maquinas com politica antiga trazem REG_SZ.
            int value = raw switch
            {
                int number => number,
                long number => number > MaxTimeoutMilliseconds ? MaxTimeoutMilliseconds : (int)number,
                string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) => parsed,
                _ => DefaultTimeoutMilliseconds,
            };

            if (value <= 0)
                return DefaultTimeoutMilliseconds;

            // O proprio sistema clampa em 1000 ms: registrar 5000 nao compra folga.
            return value > MaxTimeoutMilliseconds ? MaxTimeoutMilliseconds : value;
        }
        catch (Exception)
        {
            // Registro indisponivel (politica, perfil corrompido, sandbox): assume o
            // padrao do sistema. Diagnostico nunca pode ser fonte de falha.
            return DefaultTimeoutMilliseconds;
        }
    }
}
