using System.Runtime.InteropServices;

namespace Klip.Interop;

/// <summary>
/// ADR-P.01: P/Invoke dos hooks de baixo nivel (WH_KEYBOARD_LL / WH_MOUSE_LL) e do
/// message loop da thread dedicada que os hospeda.
/// <para>
/// Tres regras do Win32 comandam este arquivo inteiro:
/// (1) <c>SetWindowsHookEx</c> precisa ser chamado na MESMA thread que vai receber
/// os callbacks - registrar de outra thread compila, registra e nunca dispara;
/// (2) essa thread precisa bombear mensagens, porque o Windows entrega o callback
/// pela fila de mensagens dela;
/// (3) <c>PostThreadMessage</c> emitido antes de a fila existir e perdido em silencio,
/// por isso a thread chama <c>PeekMessageW(PM_NOREMOVE)</c> antes de se declarar pronta.
/// </para>
/// <para>
/// ADR-P.04: <c>SetWindowsHookExW</c> recebe <c>nint</c> (function pointer vindo de
/// <c>[UnmanagedCallersOnly]</c>), nunca um delegate gerenciado - o GC pode mover ou
/// coletar o delegate e o resultado e o <c>ExecutionEngineException</c> classico no
/// meio da digitacao do usuario.
/// </para>
/// </summary>
public static partial class NativeMethods
{
    // ----- Tipos de hook -----

    public const int WH_KEYBOARD_LL = 13;
    public const int WH_MOUSE_LL = 14;

    /// <summary>Unico nCode em que o hook LL pode inspecionar/alterar o evento.</summary>
    public const int HC_ACTION = 0;

    // ----- Mensagens observadas dentro do callback -----

    public const int WM_KEYDOWN = 0x0100;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_MOUSEMOVE = 0x0200;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_MBUTTONDOWN = 0x0207;

    // ----- Message loop da thread de hooks -----

    public const int WM_QUIT = 0x0012;

    /// <summary>Base das mensagens privadas da aplicacao (comandos de install/uninstall).</summary>
    public const int WM_APP = 0x8000;

    public const uint PM_NOREMOVE = 0;

    // ----- WinEvents (alternativa barata ao WH_MOUSE_LL, ver ForegroundWatcher) -----

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;

    /// <summary>Sem injecao de DLL: o callback e entregue de forma assincrona, pela fila do assinante.</summary>
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    public const uint WINEVENT_SKIPOWNTHREAD = 0x0001;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    // ----- Estruturas -----

    /// <summary>lParam do WH_KEYBOARD_LL. Lido com Unsafe.AsRef, nunca com Marshal.PtrToStructure.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    /// <summary>lParam do WH_MOUSE_LL. Lido com Unsafe.AsRef, nunca com Marshal.PtrToStructure.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public int x;
        public int y;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
    }

    // ----- Hooks -----

    /// <summary>
    /// ADR-P.04: <paramref name="lpfn"/> e um function pointer cru
    /// (<c>(nint)(delegate* unmanaged&lt;int, nint, nint, nint&gt;)&amp;Metodo</c>).
    /// Passar delegate gerenciado aqui e o bug que trava a digitacao global.
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint SetWindowsHookExW(int idHook, nint lpfn, nint hMod, uint dwThreadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWindowsHookEx(nint hhk);

    /// <summary>hhk e ignorado desde o Windows XP: passar 0 e o uso normal dentro do callback.</summary>
    [LibraryImport("user32.dll")]
    public static partial nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    // ----- Message loop -----

    /// <summary>Retorna 0 no WM_QUIT e -1 em erro; por isso o retorno e int, nao bool.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int GetMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PeekMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(ref MSG lpMsg);

    [LibraryImport("user32.dll")]
    public static partial nint DispatchMessageW(ref MSG lpMsg);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostThreadMessageW(uint idThread, uint Msg, nuint wParam, nint lParam);

    // ----- WinEvents -----

    /// <summary>
    /// <paramref name="pfnWinEventProc"/> tambem e function pointer
    /// (<c>delegate* unmanaged&lt;nint, uint, nint, int, int, uint, uint, void&gt;</c>).
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint hmodWinEventProc,
        nint pfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWinEvent(nint hWinEventHook);

    // GetCurrentThreadId vive em NativeMethods.User32.cs e GetAsyncKeyState em
    // NativeMethods.Input.cs: reusados aqui em vez de duplicar a declaracao.
}
