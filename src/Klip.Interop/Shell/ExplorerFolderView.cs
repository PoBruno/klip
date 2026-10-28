using System.Runtime.InteropServices;
using Klip.Core.Clipboard;

namespace Klip.Interop.Shell;

/// <summary>
/// The folder shown by an Explorer window (or the desktop) plus its view, so we can
/// select a file we just created there.
/// <para>
/// Everything here is cross-process COM into explorer.exe: create and use it on an STA
/// thread that is not the UI thread, and dispose it on the same thread.
/// </para>
/// </summary>
public sealed class ExplorerFolderView : IDisposable
{
    private NativeMethods.IShellView? _view;

    private ExplorerFolderView(string path, NativeMethods.IShellView view)
    {
        Path = path;
        _view = view;
    }

    /// <summary>File system path of the folder. Virtual folders never get here.</summary>
    public string Path { get; }

    /// <summary>
    /// Folder of the Explorer window <paramref name="topLevel"/> (active tab on Win11) or
    /// of the desktop. Null when it is not a file system folder (This PC, Home, Gallery)
    /// or Explorer did not answer.
    /// </summary>
    public static ExplorerFolderView? FromWindow(nint topLevel)
    {
        var type = Type.GetTypeFromCLSID(NativeMethods.CLSID_ShellWindows, throwOnError: false);
        if (type is null || Activator.CreateInstance(type) is not NativeMethods.IShellWindows windows)
            return null;

        try
        {
            var browser = ExplorerPastePolicy.IsDesktopClass(NativeMethods.GetClassNameSafe(topLevel))
                ? DesktopBrowser(windows)
                : WindowBrowser(windows, topLevel);
            if (browser is null)
                return null;

            try
            {
                if (browser.QueryActiveShellView(out var view) < 0 || view is null)
                    return null;
                var path = FolderPath(view);
                if (path is null)
                {
                    Marshal.ReleaseComObject(view);
                    return null;
                }
                return new ExplorerFolderView(path, view);
            }
            finally
            {
                Marshal.ReleaseComObject(browser);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(windows);
        }
    }

    /// <summary>
    /// Selects the file in the view (and gives it focus). The view learns about new files
    /// through change notifications, so it may take a moment: retries a few times.
    /// </summary>
    public bool Select(string fullPath, int attempts = 8, int delayMs = 75)
    {
        if (_view is null)
            return false;

        NativeMethods.SHChangeNotify(NativeMethods.SHCNE_CREATE,
            NativeMethods.SHCNF_PATHW | NativeMethods.SHCNF_FLUSHNOWAIT, fullPath, 0);

        // IShellView.SelectItem takes a raw pidl, which does not survive the trip into
        // explorer.exe here; the automation twin (IShellFolderViewDual) does
        if (_view.GetItemObject(NativeMethods.SVGIO_BACKGROUND, NativeMethods.IID_IDispatch, out var disp) < 0 || disp is null)
            return false;
        try
        {
            dynamic folderView = disp;
            var name = System.IO.Path.GetFileName(fullPath);
            const int flags = (int)(NativeMethods.SVSI_SELECT | NativeMethods.SVSI_DESELECTOTHERS |
                                    NativeMethods.SVSI_ENSUREVISIBLE | NativeMethods.SVSI_FOCUSED);
            for (var i = 0; i < attempts; i++)
            {
                try
                {
                    object? item = folderView.Folder?.ParseName(name);
                    if (item is not null)
                    {
                        folderView.SelectItem(item, flags);
                        return true;
                    }
                }
                catch (COMException)
                {
                    // not in the view yet
                }
                Thread.Sleep(delayMs);
            }
            return false;
        }
        finally
        {
            Marshal.ReleaseComObject(disp);
        }
    }

    public void Dispose()
    {
        if (_view is not null)
        {
            Marshal.ReleaseComObject(_view);
            _view = null;
        }
    }

    private static NativeMethods.IShellBrowser? DesktopBrowser(NativeMethods.IShellWindows windows)
    {
        object loc = NativeMethods.CSIDL_DESKTOP;
        object root = null!; // VT_EMPTY
        var disp = windows.FindWindowSW(ref loc, ref root, NativeMethods.SWC_DESKTOP, out _, NativeMethods.SWFO_NEEDDISPATCH);
        return disp is null ? null : BrowserOf(disp);
    }

    private static NativeMethods.IShellBrowser? WindowBrowser(NativeMethods.IShellWindows windows, nint topLevel)
    {
        // win11 tabs: every tab is a ShellWindows entry under the same frame, and the
        // first ShellTabWindowClass child of the frame is the one being shown
        var activeTab = NativeMethods.FindWindowEx(topLevel, 0, "ShellTabWindowClass", null);
        NativeMethods.IShellBrowser? fallback = null;

        var count = windows.Count;
        for (var i = 0; i < count; i++)
        {
            object? item;
            try
            {
                item = windows.Item(i);
            }
            catch (COMException)
            {
                continue; // window closed while we were looking
            }
            if (item is null)
                continue;

            var browser = BrowserOf(item);
            if (browser is null)
                continue;

            if (browser.GetWindow(out var hwnd) >= 0 && NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT) == topLevel)
            {
                if (activeTab == 0 || hwnd == activeTab)
                {
                    if (fallback is not null)
                        Marshal.ReleaseComObject(fallback);
                    return browser;
                }
                if (fallback is null)
                {
                    fallback = browser;
                    continue;
                }
            }
            Marshal.ReleaseComObject(browser);
        }
        return fallback;
    }

    private static NativeMethods.IShellBrowser? BrowserOf(object dispatch)
    {
        object? obj = null;
        try
        {
            if (dispatch is not NativeMethods.IOleServiceProvider sp)
                return null;
            return sp.QueryService(NativeMethods.SID_STopLevelBrowser, NativeMethods.IID_IShellBrowser, out obj) >= 0
                ? obj as NativeMethods.IShellBrowser
                : null;
        }
        finally
        {
            // same COM identity = same RCW; releasing it would kill the browser we return
            if (Marshal.IsComObject(dispatch) && !ReferenceEquals(dispatch, obj))
                Marshal.ReleaseComObject(dispatch);
        }
    }

    private static string? FolderPath(NativeMethods.IShellView view)
    {
        if (view is not NativeMethods.IFolderView folderView)
            return null;
        if (folderView.GetFolder(NativeMethods.IID_IShellItem, out var obj) < 0 || obj is not NativeMethods.IShellItem item)
            return null;
        try
        {
            if (item.GetDisplayName(NativeMethods.SIGDN_FILESYSPATH, out var psz) < 0 || psz == 0)
                return null;
            try
            {
                var path = Marshal.PtrToStringUni(psz);
                return string.IsNullOrEmpty(path) || !Directory.Exists(path) ? null : path;
            }
            finally
            {
                Marshal.FreeCoTaskMem(psz);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    }
}
