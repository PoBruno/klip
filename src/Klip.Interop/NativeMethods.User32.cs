using System.Runtime.InteropServices;

namespace Klip.Interop;

/// <summary>Assorted user32 P/Invoke (windows, icons).</summary>
public static partial class NativeMethods
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint hIcon);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial nint GetShellWindow();

    // ----- Focus fallback for pasting -----

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    /// <summary>
    /// Brings a window to the foreground the hard way: if SetForegroundWindow
    /// refuses (Windows foreground-lock rules), attach our thread input queue to
    /// the target window and try again.
    /// </summary>
    public static bool ForceForeground(nint hWnd)
    {
        if (hWnd == nint.Zero)
            return false;
        if (SetForegroundWindow(hWnd))
            return true;

        var targetThread = GetWindowThreadProcessId(hWnd, out _);
        var currentThread = GetCurrentThreadId();
        if (targetThread == 0 || targetThread == currentThread)
            return false;

        AttachThreadInput(currentThread, targetThread, true);
        try
        {
            return SetForegroundWindow(hWnd);
        }
        finally
        {
            AttachThreadInput(currentThread, targetThread, false);
        }
    }
}
