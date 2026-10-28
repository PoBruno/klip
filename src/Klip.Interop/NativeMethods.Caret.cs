using System.Runtime.InteropServices;

namespace Klip.Interop;

/// <summary>Where the text caret is, so the history panel can open next to it like the native Win+V.</summary>
public static partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public nint hwndActive;
        public nint hwndFocus;
        public nint hwndCapture;
        public nint hwndMenuOwner;
        public nint hwndMoveSize;
        public nint hwndCaret;
        public RECT rcCaret;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO pgui);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClientToScreen(nint hWnd, ref POINT lpPoint);

    private const int OBJID_CARET = unchecked((int)0xFFFFFFF8);
    private static readonly Guid IID_IAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");

    // classic COM marshaling on purpose: it hands back a typed IAccessible (LibraryImport can't)
    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(nint hwnd, int dwObjectId, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IAccessibleCaret? ppvObject);

    // only accLocation is called; the slots before it just keep the vtable in order
    [ComImport]
    [Guid("618736E0-3C3D-11CF-810C-00AA00389B71")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IAccessibleCaret
    {
        [DispId(-5002)] object accParent { get; }
        [DispId(-5001)] int accChildCount { get; }
        [DispId(-5000)] object accChild(object varChild);
        [DispId(-5003)] string accName(object varChild);
        [DispId(-5004)] string accValue(object varChild);
        [DispId(-5005)] string accDescription(object varChild);
        [DispId(-5006)] object accRole(object varChild);
        [DispId(-5007)] object accState(object varChild);
        [DispId(-5008)] string accHelp(object varChild);
        [DispId(-5009)] int accHelpTopic(out string pszHelpFile, object varChild);
        [DispId(-5010)] string accKeyboardShortcut(object varChild);
        [DispId(-5011)] object accFocus { get; }
        [DispId(-5012)] object accSelection { get; }
        [DispId(-5013)] string accDefaultAction(object varChild);
        [DispId(-5014)] void accSelect(int flagsSelect, object varChild);
        [DispId(-5015)] void accLocation(out int pxLeft, out int pyTop, out int pcxWidth, out int pcyHeight, object varChild);
    }

    /// <summary>
    /// Screen rect (physical px) of the text caret in the foreground app. Tries the
    /// system caret first (Win32 edits, Office, most native apps), then the MSAA caret
    /// object (Chromium, Electron, Firefox draw their own caret but expose it there).
    /// False when there is no caret to be found.
    /// </summary>
    public static bool TryGetCaretRect(out RECT screenRect)
    {
        screenRect = default;
        var foreground = GetForegroundWindow();
        if (foreground == nint.Zero)
            return false;
        var thread = GetWindowThreadProcessId(foreground, out _);

        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (GetGUIThreadInfo(thread, ref info) && info.hwndCaret != nint.Zero)
        {
            var topLeft = new POINT { x = info.rcCaret.left, y = info.rcCaret.top };
            var bottomRight = new POINT { x = info.rcCaret.right, y = info.rcCaret.bottom };
            if (ClientToScreen(info.hwndCaret, ref topLeft) && ClientToScreen(info.hwndCaret, ref bottomRight) &&
                (topLeft.x != 0 || topLeft.y != 0))
            {
                screenRect = new RECT { left = topLeft.x, top = topLeft.y, right = bottomRight.x, bottom = bottomRight.y };
                return true;
            }
        }

        var target = info.hwndFocus != nint.Zero ? info.hwndFocus : foreground;
        return TryGetAccessibleCaret(target, out screenRect);
    }

    private static bool TryGetAccessibleCaret(nint hwnd, out RECT screenRect)
    {
        screenRect = default;
        IAccessibleCaret? caret = null;
        try
        {
            var iid = IID_IAccessible;
            if (AccessibleObjectFromWindow(hwnd, OBJID_CARET, ref iid, out caret) != 0 || caret is null)
                return false;
            caret.accLocation(out var x, out var y, out var w, out var h, 0);
            // apps without a caret still answer, with an empty rect at 0,0
            if ((x == 0 && y == 0) || (w == 0 && h == 0))
                return false;
            screenRect = new RECT { left = x, top = y, right = x + w, bottom = y + h };
            return true;
        }
        catch (Exception)
        {
            // provider gone, access denied, elevated app... no caret then
            return false;
        }
        finally
        {
            if (caret is not null)
                Marshal.ReleaseComObject(caret);
        }
    }
}
