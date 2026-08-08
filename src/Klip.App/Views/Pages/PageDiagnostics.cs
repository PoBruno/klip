using System.Diagnostics;
using Klip.App.Services;

namespace Klip.App.Views.Pages;

/// <summary>
/// ADR-S.03: instrumentacao que prova o ciclo de vida das paginas. Com as paginas
/// registradas como <c>AddSingleton</c> e um <c>INavigationViewPageProvider</c>
/// ligado, cada pagina tem que ser construida UMA unica vez - navegar de ida e
/// volta nao pode reconstruir nada.
/// <para>
/// O metodo e <see cref="ConditionalAttribute"/>("DEBUG"): em Release a chamada
/// some do call site, sem <c>#if</c> espalhado pelos construtores.
/// </para>
/// </summary>
internal static class PageDiagnostics
{
    private static readonly Dictionary<Type, int> Counts = [];

    /// <summary>Conta e loga a construcao de uma pagina. Chamar no construtor.</summary>
    [Conditional("DEBUG")]
    public static void TrackConstruction(object page)
    {
        var type = page.GetType();
        lock (Counts)
        {
            Counts.TryGetValue(type, out var count);
            Counts[type] = ++count;
            StartupLog.Write($"SettingsPage: {type.Name} construida ({count}x)");
        }
    }

    /// <summary>Resumo "Pagina=N" de tudo que ja foi construido.</summary>
    public static string FormatSummary()
    {
        lock (Counts)
        {
            return Counts.Count == 0
                ? "(nenhuma pagina construida)"
                : string.Join(", ", Counts.Select(pair => $"{pair.Key.Name}={pair.Value}"));
        }
    }
}
