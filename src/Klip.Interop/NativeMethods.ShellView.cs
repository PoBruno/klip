using System.Runtime.InteropServices;

namespace Klip.Interop;

/// <summary>
/// Shell view COM: find the folder an Explorer window shows and select an item in it.
/// Classic ComImport RCWs (LibraryImport does not do COM interfaces). Only the vtable
/// slots up to the last method we call are real; the rest are placeholders in order.
/// </summary>
public static partial class NativeMethods
{
    public static readonly Guid CLSID_ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    public static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    public static readonly Guid IID_IShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    public static readonly Guid IID_IShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    public static readonly Guid IID_IDispatch = new("00020400-0000-0000-C000-000000000046");

    public const uint SVGIO_BACKGROUND = 0;

    public const int SWC_DESKTOP = 8;
    public const int SWFO_NEEDDISPATCH = 1;
    public const int CSIDL_DESKTOP = 0;

    public const uint SVSI_SELECT = 0x1;
    public const uint SVSI_DESELECTOTHERS = 0x4;
    public const uint SVSI_ENSUREVISIBLE = 0x8;
    public const uint SVSI_FOCUSED = 0x10;

    public const uint SIGDN_FILESYSPATH = 0x80058000;

    public const uint GA_ROOT = 2;

    public const int SHCNE_CREATE = 0x00000002;
    public const uint SHCNF_PATHW = 0x0005;
    public const uint SHCNF_FLUSHNOWAIT = 0x3000;

    [ComImport]
    [Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IShellWindows
    {
        int Count { get; }

        [return: MarshalAs(UnmanagedType.IDispatch)]
        object? Item([MarshalAs(UnmanagedType.Struct)] object index);

        [return: MarshalAs(UnmanagedType.IUnknown)]
        object _NewEnum();

        void Register([MarshalAs(UnmanagedType.IDispatch)] object pid, int hwnd, int swClass, out int plCookie);
        void RegisterPending(int lThreadId, [MarshalAs(UnmanagedType.Struct)] ref object pvarloc, [MarshalAs(UnmanagedType.Struct)] ref object pvarlocRoot, int swClass, out int plCookie);
        void Revoke(int lCookie);
        void OnNavigate(int lCookie, [MarshalAs(UnmanagedType.Struct)] ref object pvarLoc);
        void OnActivated(int lCookie, [MarshalAs(UnmanagedType.VariantBool)] bool fActive);

        [return: MarshalAs(UnmanagedType.IDispatch)]
        object? FindWindowSW([MarshalAs(UnmanagedType.Struct)] ref object pvarLoc, [MarshalAs(UnmanagedType.Struct)] ref object pvarLocRoot, int swClass, out int phwnd, int swfwOptions);
    }

    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IOleServiceProvider
    {
        [PreserveSig]
        int QueryService(in Guid guidService, in Guid riid, [MarshalAs(UnmanagedType.Interface)] out object? ppvObject);
    }

    [ComImport]
    [Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellBrowser
    {
        // IOleWindow
        [PreserveSig] int GetWindow(out nint phwnd);
        [PreserveSig] int ContextSensitiveHelp(int fEnterMode);

        [PreserveSig] int InsertMenusSB(nint hmenuShared, nint lpMenuWidths);
        [PreserveSig] int SetMenuSB(nint hmenuShared, nint holemenuRes, nint hwndActiveObject);
        [PreserveSig] int RemoveMenusSB(nint hmenuShared);
        [PreserveSig] int SetStatusTextSB(nint pszStatusText);
        [PreserveSig] int EnableModelessSB(int fEnable);
        [PreserveSig] int TranslateAcceleratorSB(nint pmsg, ushort wID);
        [PreserveSig] int BrowseObject(nint pidl, uint wFlags);
        [PreserveSig] int GetViewStateStream(uint grfMode, out nint ppStrm);
        [PreserveSig] int GetControlWindow(uint id, out nint phwnd);
        [PreserveSig] int SendControlMsg(uint id, uint uMsg, nint wParam, nint lParam, out nint pret);
        [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out IShellView? ppshv);
    }

    [ComImport]
    [Guid("000214E3-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellView
    {
        // IOleWindow
        [PreserveSig] int GetWindow(out nint phwnd);
        [PreserveSig] int ContextSensitiveHelp(int fEnterMode);

        [PreserveSig] int TranslateAccelerator(nint pmsg);
        [PreserveSig] int EnableModeless(int fEnable);
        [PreserveSig] int UIActivate(uint uState);
        [PreserveSig] int Refresh();
        [PreserveSig] int CreateViewWindow(nint psvPrevious, nint pfs, nint psb, nint prcView, out nint phWnd);
        [PreserveSig] int DestroyViewWindow();
        [PreserveSig] int GetCurrentInfo(nint pfs);
        [PreserveSig] int AddPropertySheetPages(uint dwReserved, nint pfn, nint lparam);
        [PreserveSig] int SaveViewState();
        [PreserveSig] int SelectItem(nint pidlItem, uint uFlags);
        [PreserveSig] int GetItemObject(uint uItem, in Guid riid, [MarshalAs(UnmanagedType.Interface)] out object? ppv);
    }

    [ComImport]
    [Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IFolderView
    {
        [PreserveSig] int GetCurrentViewMode(out uint pViewMode);
        [PreserveSig] int SetCurrentViewMode(uint viewMode);
        [PreserveSig] int GetFolder(in Guid riid, [MarshalAs(UnmanagedType.Interface)] out object? ppv);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem
    {
        [PreserveSig] int BindToHandler(nint pbc, in Guid bhid, in Guid riid, out nint ppv);
        [PreserveSig] int GetParent(out nint ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName, out nint ppszName);
    }

    [LibraryImport("user32.dll")]
    public static partial nint GetAncestor(nint hwnd, uint gaFlags);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindWindowEx(nint hWndParent, nint hWndChildAfter, string? lpszClass, string? lpszWindow);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial void SHChangeNotify(int wEventId, uint uFlags, string? dwItem1, nint dwItem2);

    // ----- focus inside another thread -----

    // GUITHREADINFO / GetGUIThreadInfo live in NativeMethods.Caret.cs

    /// <summary>Window with keyboard focus inside the thread that owns <paramref name="topLevel"/>.</summary>
    public static nint GetFocusedChild(nint topLevel)
    {
        var tid = GetWindowThreadProcessId(topLevel, out _);
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        return tid != 0 && GetGUIThreadInfo(tid, ref info) ? info.hwndFocus : 0;
    }

    /// <summary>
    /// Replays a Ctrl+V we swallowed with a hotkey. If the user is still holding Ctrl only
    /// V is sent, so the physical Ctrl state stays in sync.
    /// </summary>
    public static void ReplayCtrlV()
    {
        var ctrlHeld = IsKeyDown(VK_CONTROL);
        var inputs = new List<INPUT>(4);
        if (!ctrlHeld)
            inputs.Add(VkInput(VK_CONTROL, up: false));
        inputs.Add(VkInput(VK_V, up: false));
        inputs.Add(VkInput(VK_V, up: true));
        if (!ctrlHeld)
            inputs.Add(VkInput(VK_CONTROL, up: true));
        SendInput((uint)inputs.Count, [.. inputs], Marshal.SizeOf<INPUT>());
    }

    private static INPUT VkInput(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = (ushort)MapVirtualKeyW(vk, MAPVK_VK_TO_VSC),
                dwFlags = up ? KEYEVENTF_KEYUP : 0,
            },
        },
    };
}
