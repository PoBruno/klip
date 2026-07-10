using System.Runtime.InteropServices;

namespace Klip.Interop;

/// <summary>
/// Global keyboard listener (WH_KEYBOARD_LL) used to drive the history flyout
/// while it is open WITHOUT stealing focus from the app underneath. The flyout
/// shows as a no-activate window, so it never gets keyboard focus the normal way;
/// this hook forwards keys to it instead.
///
/// The callback returns true when it consumed the key (so it does NOT reach the
/// app below), false to let it pass through.
/// </summary>
public sealed partial class GlobalKeyboardListener : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private readonly HookProc _proc;
    private nint _hookHandle;

    /// <summary>
    /// Fires for each key down while active. Args: virtual key code. Return true
    /// to swallow the key (block the app below), false to let it through.
    /// Runs on the hook thread; keep it fast and exception-safe.
    /// </summary>
    public Func<int, bool>? OnKeyDown { get; set; }

    /// <summary>When false, the hook stays installed but forwards nothing.</summary>
    public bool Active { get; set; }

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

    public GlobalKeyboardListener()
    {
        _proc = HookCallback;
    }

    public void Install()
    {
        if (_hookHandle == nint.Zero)
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
        if (nCode >= 0 && Active && OnKeyDown is not null)
        {
            var msg = (int)wParam;
            if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                try
                {
                    if (OnKeyDown.Invoke((int)data.vkCode))
                        return 1; // consumed, do not pass to the focused app
                }
                catch
                {
                    // never let an exception here freeze global typing
                }
            }
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose() => Uninstall();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetWindowsHookExW(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(nint hhk);

    [LibraryImport("user32.dll")]
    private static partial nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);
}
