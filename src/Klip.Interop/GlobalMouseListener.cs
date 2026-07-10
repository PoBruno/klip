using System.Runtime.InteropServices;

namespace Klip.Interop;

/// <summary>
/// Global low level mouse hook (WH_MOUSE_LL). The history flyout shows as a
/// no-activate window, so WPF never raises Deactivated when you click away.
/// This hook lets it close on an outside click instead. It only reports the
/// click position; the caller decides if it was inside or outside.
/// </summary>
public sealed partial class GlobalMouseListener : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;

    private readonly HookProc _proc;
    private nint _hookHandle;

    /// <summary>
    /// Fires on any mouse button down while active, with the screen point
    /// (physical px). Runs on the hook thread; keep it fast and exception-safe.
    /// </summary>
    public Action<int, int>? OnButtonDown { get; set; }

    /// <summary>When false, the hook stays installed but reports nothing.</summary>
    public bool Active { get; set; }

    private delegate nint HookProc(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public int x;
        public int y;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    public GlobalMouseListener()
    {
        _proc = HookCallback;
    }

    public void Install()
    {
        if (_hookHandle == nint.Zero)
            _hookHandle = SetWindowsHookExW(WH_MOUSE_LL, _proc, nint.Zero, 0);
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
        if (nCode >= 0 && Active && OnButtonDown is not null)
        {
            var msg = (int)wParam;
            if (msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                try
                {
                    OnButtonDown.Invoke(data.x, data.y);
                }
                catch
                {
                    // never let an exception here freeze global clicking
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
