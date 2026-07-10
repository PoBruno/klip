using System.Runtime.InteropServices;

namespace Klip.Interop;

/// <summary>P/Invoke do clipboard. Listener moderno + sequence number.</summary>
public static partial class NativeMethods
{
    public const int WM_CLIPBOARDUPDATE = 0x031D;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AddClipboardFormatListener(nint hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RemoveClipboardFormatListener(nint hwnd);

    /// <summary>anti-loop - número incrementado a cada mudança do clipboard.</summary>
    [LibraryImport("user32.dll")]
    public static partial uint GetClipboardSequenceNumber();

    [LibraryImport("user32.dll")]
    public static partial nint GetClipboardOwner();

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetWindowText(nint hWnd, [Out] char[] lpString, int nMaxCount);

    public static string GetWindowTextSafe(nint hWnd)
    {
        if (hWnd == nint.Zero)
            return "";
        var buffer = new char[512];
        var len = GetWindowText(hWnd, buffer, buffer.Length);
        return len > 0 ? new string(buffer, 0, len) : "";
    }
}
