using Klip.Core.Diagnostics;
using Klip.Core.Input;

namespace Klip.Interop.Input;

/// <summary>
/// Estado consultado dentro do callback do hook LL. Toda leitura precisa ser O(1)
/// e sem alocacao: o callback roda no caminho critico de input do sistema inteiro.
/// <para>
/// ADR-P.04: nada aqui pode tomar lock, alocar ou chamar COM. Os conjuntos de teclas
/// sao bitmaps de 256 bits (<see cref="VirtualKeyMask"/>) e os flags sao inteiros lidos
/// com <see cref="Volatile"/>. O custo total de uma consulta e um punhado de leituras.
/// </para>
/// <para>
/// O estado e estatico de proposito: os callbacks sao <c>[UnmanagedCallersOnly]</c> e,
/// portanto, estaticos - nao ha instancia para carregar ate eles sem um handle pinado.
/// </para>
/// </summary>
public static class HookPolicy
{
    // Backing fields int porque C# nao aceita auto-property volatile; a semantica
    // de leitura/escrita e a mesma via Volatile.Read/Write.
    private static int _keyboardActive;
    private static int _mouseActive;
    private static int _ctrlVArmed;

    // Guard de "colagem em voo": equivalente ao _pasteInFlight do PasteQueueService,
    // so que resolvido no callback com um unico CAS, sem hop para a UI thread.
    private static int _pasteInFlight;

    /// <summary>
    /// Quando true, o hook de teclado publica as teclas no ring e aplica os conjuntos
    /// de descarte. Quando false, o hook (se instalado) so deixa passar.
    /// </summary>
    public static bool KeyboardActive
    {
        get => Volatile.Read(ref _keyboardActive) != 0;
        set => Volatile.Write(ref _keyboardActive, value ? 1 : 0);
    }

    /// <summary>Quando true, o hook de mouse publica os cliques (botao pressionado) no ring.</summary>
    public static bool MouseActive
    {
        get => Volatile.Read(ref _mouseActive) != 0;
        set => Volatile.Write(ref _mouseActive, value ? 1 : 0);
    }

    /// <summary>Teclas engolidas sempre que KeyboardActive. Substitui o conjunto inteiro.</summary>
    public static void SetSwallowKeys(ReadOnlySpan<int> virtualKeys) => SwallowKeys.Set(virtualKeys);

    /// <summary>Teclas engolidas apenas com Ctrl pressionado.</summary>
    public static void SetSwallowKeysWithCtrl(ReadOnlySpan<int> virtualKeys) => SwallowKeysWithCtrl.Set(virtualKeys);

    public static void ClearSwallowKeys()
    {
        SwallowKeys.Clear();
        SwallowKeysWithCtrl.Clear();
    }

    /// <summary>Fila de colagem armada: Ctrl+V vira evento KlipInputMessages.CtrlV.</summary>
    public static bool CtrlVArmed
    {
        get => Volatile.Read(ref _ctrlVArmed) != 0;
        set
        {
            Volatile.Write(ref _ctrlVArmed, value ? 1 : 0);

            // Desarmar tambem devolve o guard: se a fila foi cancelada no meio de uma
            // colagem em voo, ninguem mais chamaria ReleaseCtrlV e o proximo arme
            // nasceria bloqueado.
            if (!value)
                Volatile.Write(ref _pasteInFlight, 0);
        }
    }

    /// <summary>Libera o guard de colagem em voo. Chamado pelo consumidor apos tratar o evento.</summary>
    public static void ReleaseCtrlV() => Volatile.Write(ref _pasteInFlight, 0);

    /// <summary>Metricas do caminho quente (compartilhado com o host).</summary>
    public static HookMetrics Metrics { get; } = new();

    // ----- Uso interno do LowLevelHookHost (caminho quente) -----

    /// <summary>Conjunto consultado direto pelo callback: leitura O(1), lock-free.</summary>
    internal static VirtualKeyMask SwallowKeys { get; } = new();

    /// <summary>Conjunto condicionado a Ctrl, consultado direto pelo callback.</summary>
    internal static VirtualKeyMask SwallowKeysWithCtrl { get; } = new();

    /// <summary>
    /// Tenta marcar uma colagem como em voo. Retorna true apenas para o primeiro
    /// Ctrl+V; os repetidos sao ignorados ate <see cref="ReleaseCtrlV"/>. Um unico CAS,
    /// sem alocacao - seguro dentro do callback.
    /// </summary>
    internal static bool TryBeginCtrlV() => Interlocked.CompareExchange(ref _pasteInFlight, 1, 0) == 0;
}
