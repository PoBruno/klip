using System.Runtime.InteropServices;

namespace Klip.Interop;

/// <summary>
/// Low level keyboard hook (WH_KEYBOARD_LL) that intercepts Ctrl+V and feeds
/// the sequential paste queue.
/// </summary>
public sealed partial class LowLevelKeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int VK_V = 0x56;
    private const int VK_CONTROL = 0x11;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;

    private readonly HookProc _proc;
    private nint _hookHandle;

    /// <summary>
    /// Fires when Ctrl+V is seen with the queue armed. Return true to LET the
    /// Ctrl+V go through (after prepping the clipboard). Runs on the hook thread.
    /// </summary>
    public Func<bool>? OnCtrlV { get; set; }

    private delegate nint HookProc(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    public LowLevelKeyboardHook()
    {
        _proc = HookCallback;
    }

    public void Install()
    {
        if (_hookHandle != nint.Zero)
            return;
        // WH_KEYBOARD_LL doesnt need a module/injection, hMod can be 0
        _hookHandle = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, nint.Zero, 0);
    }

    public void Uninstall()
    {
        if (_hookHandle != nint.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = nint.Zero;
        }
    }

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && OnCtrlV is not null)
        {
            var msg = (int)wParam;
            if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (data.vkCode == VK_V && IsCtrlDown())
                {
                    // prep the next queue item, if it takes it we let Ctrl+V pass
                    try
                    {
                        OnCtrlV.Invoke();
                    }
                    catch
                    {
                        // never let an exception here freeze global typing
                    }
                }
            }
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private static bool IsCtrlDown() =>
        (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 ||
        (GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0 ||
        (GetAsyncKeyState(VK_RCONTROL) & 0x8000) != 0;

    public void Dispose() => Uninstall();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetWindowsHookExW(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(nint hhk);

    [LibraryImport("user32.dll")]
    private static partial nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vKey);
}
