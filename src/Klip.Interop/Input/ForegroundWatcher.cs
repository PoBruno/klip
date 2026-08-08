using System.Runtime.InteropServices;

namespace Klip.Interop.Input;

/// <summary>
/// ADR-P.01 / RF-P1.05: alternativa barata ao <c>WH_MOUSE_LL</c> para detectar "clique
/// fora" quando a janela nao pode receber foco (<c>WS_EX_NOACTIVATE</c>). Em vez de
/// interceptar todo evento de mouse do sistema, observa apenas a troca de janela em
/// primeiro plano.
/// <para>
/// A diferenca de custo e estrutural, nao de constante. Um hook LL e SINCRONO: a Raw
/// Input Thread do Windows fica bloqueada em cada evento ate o callback retornar (ou ate
/// estourar <c>LowLevelHooksTimeout</c>), e um mouse gamer gera de 1000 a 8000 eventos por
/// segundo. Um WinEvent registrado com <c>WINEVENT_OUTOFCONTEXT</c> e ASSINCRONO e
/// ENFILEIRADO: o sistema posta o evento na fila de mensagens do assinante e segue em
/// frente sem esperar. Ninguem trava se o callback demorar, e o volume cai de milhares por
/// segundo para alguns por minuto (so muda de janela ativa).
/// </para>
/// <para>
/// Contrapartida do modelo assincrono: o evento chega DEPOIS da troca de foreground ja ter
/// acontecido, e pode chegar fora de ordem sob carga. Serve para reagir ("o usuario saiu
/// daqui, feche o flyout"), nao para decidir se um evento passa ou nao.
/// </para>
/// <para>
/// <c>Start</c> precisa ser chamado de uma thread com message loop (a UI thread ou a thread
/// de pump do <see cref="LowLevelHookHost"/>): o callback e entregue pela fila dessa thread.
/// Sem loop de mensagens, nenhum evento chega. <c>WINEVENT_SKIPOWNPROCESS</c> filtra as
/// janelas do proprio Klip na origem.
/// </para>
/// </summary>
public sealed unsafe class ForegroundWatcher : IDisposable
{
    // O proc e [UnmanagedCallersOnly], logo estatico: nao ha instancia para alcancar sem
    // um handle pinado. O destino fica num campo estatico, publicado com Volatile.
    private static Action<nint>? s_handler;

    private Action<nint>? _handler;
    private nint _hook;

    /// <summary>
    /// Registra o WinEvent de troca de foreground. Idempotente por instancia.
    /// <paramref name="onForegroundChanged"/> recebe o HWND que virou primeiro plano e roda
    /// na thread que chamou <c>Start</c>.
    /// </summary>
    public void Start(Action<nint> onForegroundChanged)
    {
        ArgumentNullException.ThrowIfNull(onForegroundChanged);

        if (_hook != nint.Zero)
            return;

        _handler = onForegroundChanged;
        Volatile.Write(ref s_handler, onForegroundChanged);

        nint hook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            nint.Zero, // sem DLL: WINEVENT_OUTOFCONTEXT nao injeta nada em processo nenhum
            (nint)(delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void>)&WinEventProc,
            0, // todos os processos
            0, // todas as threads
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        if (hook == nint.Zero)
        {
            // Falhou: nao deixa um handler orfao respondendo por um hook que nao existe.
            Interlocked.CompareExchange(ref s_handler, null, onForegroundChanged);
            _handler = null;
            return;
        }

        _hook = hook;
    }

    /// <summary>Remove o WinEvent. Deve rodar na mesma thread que chamou <c>Start</c>.</summary>
    public void Stop()
    {
        if (_hook != nint.Zero)
        {
            NativeMethods.UnhookWinEvent(_hook);
            _hook = nint.Zero;
        }

        var handler = _handler;
        if (handler is not null)
        {
            // So limpa se o destino ainda for o nosso: outra instancia pode ter assumido.
            Interlocked.CompareExchange(ref s_handler, null, handler);
            _handler = null;
        }
    }

    public void Dispose() => Stop();

    [UnmanagedCallersOnly]
    private static void WinEventProc(
        nint hWinEventHook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint idEventThread,
        uint dwmsEventTime)
    {
        try
        {
            if (eventType != NativeMethods.EVENT_SYSTEM_FOREGROUND || hwnd == nint.Zero)
                return;

            // idObject != OBJID_WINDOW (0) vem de filhos/controles: nao e troca de janela.
            if (idObject != 0 || idChild != 0)
                return;

            Volatile.Read(ref s_handler)?.Invoke(hwnd);
        }
        catch
        {
            // Fronteira nao gerenciada: uma excecao escapando daqui derruba o processo.
        }
    }
}
