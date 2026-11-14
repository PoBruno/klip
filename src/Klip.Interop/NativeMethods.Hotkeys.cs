using System.Runtime.InteropServices;

namespace Klip.Interop;

/// <summary>P/Invoke de hotkeys globais (RegisterHotKey).</summary>
public static partial class NativeMethods
{
    public const int WM_HOTKEY = 0x0312;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    public const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    public const uint VK_SNAPSHOT = 0x2C;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint hWnd, int id);

    /// <summary>
    /// Tick count (ms since boot, same clock as Environment.TickCount) at which the
    /// last message pulled by this thread was posted. On WM_HOTKEY that is roughly the
    /// key press, so now minus this is how long the hotkey sat in the queue.
    /// </summary>
    [LibraryImport("user32.dll")]
    public static partial int GetMessageTime();

    /// <summary>Id of a registered window message, e.g. "TaskbarCreated" (broadcast when Explorer starts again).</summary>
    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterWindowMessage(string lpString);
}
