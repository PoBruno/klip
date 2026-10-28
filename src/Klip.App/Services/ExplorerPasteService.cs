using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Klip.Core.Clipboard;
using Klip.Core.Common;
using Klip.Core.Settings;
using Klip.Interop;
using Klip.Interop.Input;
using Klip.Interop.Shell;

namespace Klip.App.Services;

/// <summary>
/// Ctrl+V on an Explorer folder (or the desktop) with an image on the clipboard saves
/// it as a png in that folder. The clipboard of other apps is never rewritten.
/// <para>
/// Cost model: nothing runs while the clipboard has no image. With an image there, one
/// out of context WinEvent watches foreground changes (a few events per minute), and
/// Ctrl+V is registered as a hotkey only while an Explorer window is in front. Everything
/// lives on the clipboard thread; the COM talk with Explorer and the file write go to a
/// short lived STA thread so neither the UI nor the clipboard thread waits on Explorer.
/// </para>
/// </summary>
public sealed class ExplorerPasteService : IDisposable
{
    private const int HotkeyId = 1;

    // long enough for the replayed key to get through the input queue before the
    // hotkey comes back, short enough that a quick second Ctrl+V still hits us
    private static readonly TimeSpan RearmDelay = TimeSpan.FromMilliseconds(150);

    private readonly ClipboardMonitorService _monitor;
    private readonly ClipboardThread _clipboardThread;
    private readonly SettingsService _settings;

    // clipboard thread only from here down
    private readonly ForegroundWatcher _foreground = new();
    private HwndSource? _window;
    private DispatcherTimer? _rearm;
    private ClipboardImageState _state = ClipboardImageState.None;
    private bool _hotkeyRegistered;
    private bool _enabled;
    private bool _registerFailedLogged;
    private int _busy;
    private bool _disposed;

    public ExplorerPasteService(ClipboardMonitorService monitor, ClipboardThread clipboardThread, SettingsService settings)
    {
        _monitor = monitor;
        _clipboardThread = clipboardThread;
        _settings = settings;
    }

    /// <summary>Hooks into the monitor. Cheap: nothing is armed until an image shows up.</summary>
    public void Start()
    {
        _enabled = _settings.Current.PasteImageAsFileInExplorer;
        _monitor.ImageStateChanged += OnImageStateChanged;
        _settings.Changed += OnSettingsChanged;
        // the clipboard may already hold an image from before Klip started
        _monitor.RequestImageStateRefresh();
    }

    private void OnSettingsChanged(AppSettings s)
    {
        var enabled = s.PasteImageAsFileInExplorer;
        _clipboardThread.BeginInvoke(() =>
        {
            if (_enabled == enabled)
                return;
            _enabled = enabled;
            if (enabled)
                _monitor.RequestImageStateRefresh(); // the monitor skips the probes while off
            else
                UpdateArming();
        });
    }

    private void OnImageStateChanged(ClipboardImageState state)
    {
        _state = state;
        UpdateArming();
    }

    private bool ShouldWatch => !_disposed && _enabled && _state.IsCandidate && !_monitor.IsPaused;

    private void UpdateArming()
    {
        if (!ShouldWatch)
        {
            _foreground.Stop();
            SetHotkey(false);
            return;
        }

        if (!_foreground.IsRunning)
            _foreground.Start(OnForegroundChanged, skipOwnProcess: false);
        OnForegroundChanged(NativeMethods.GetForegroundWindow());
    }

    private void OnForegroundChanged(nint hwnd)
    {
        if (!ShouldWatch)
            return;
        SetHotkey(ExplorerPastePolicy.IsFolderWindowClass(NativeMethods.GetClassNameSafe(hwnd)));
    }

    private void SetHotkey(bool on)
    {
        if (on == _hotkeyRegistered)
            return;

        if (!on)
        {
            _rearm?.Stop();
            if (_window is not null)
                NativeMethods.UnregisterHotKey(_window.Handle, HotkeyId);
            _hotkeyRegistered = false;
            return;
        }

        _window ??= CreateWindow();
        if (NativeMethods.RegisterHotKey(_window.Handle, HotkeyId,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_NOREPEAT, NativeMethods.VK_V))
        {
            _hotkeyRegistered = true;
        }
        else if (!_registerFailedLogged)
        {
            // some other tool owns Ctrl+V; Explorer keeps its normal paste
            _registerFailedLogged = true;
            StartupLog.Write("[AVISO] Ctrl+V no Explorer: RegisterHotKey falhou");
        }
    }

    private HwndSource CreateWindow()
    {
        var source = new HwndSource(new HwndSourceParameters("KlipExplorerPaste")
        {
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
            ParentWindow = new nint(-3), // HWND_MESSAGE
        });
        source.AddHook(WndProc);
        return source;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam == HotkeyId)
        {
            handled = true;
            try
            {
                OnHotkey();
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("ExplorerPaste", ex);
                PassThrough(rearm: true);
            }
        }
        return 0;
    }

    private void OnHotkey()
    {
        var fg = NativeMethods.GetForegroundWindow();
        if (!ExplorerPastePolicy.IsFolderWindowClass(NativeMethods.GetClassNameSafe(fg)))
        {
            // the foreground event has not caught up yet
            PassThrough(rearm: false);
            return;
        }

        // rename box, address bar, search: they get a normal Ctrl+V
        var focus = NativeMethods.GetFocusedChild(fg);
        if (!ExplorerPastePolicy.IsItemViewFocusClass(NativeMethods.GetClassNameSafe(focus)))
        {
            PassThrough(rearm: true);
            return;
        }

        // the state is up to 150 ms old (monitor debounce): Klip itself may have just put
        // text or a file there, so look again with the cheap probes
        var image = ReadClipboardImage();
        if (image is null)
        {
            PassThrough(rearm: true);
            return;
        }

        if (Interlocked.Exchange(ref _busy, 1) == 1)
            return; // still writing the previous one, swallow the repeat

        var state = _state;
        var template = _settings.Current.ImageFileNameTemplate;
        var worker = new Thread(() => SaveIntoFolder(fg, image.Value, state, template))
        {
            IsBackground = true,
            Name = "Klip.ExplorerPaste",
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    /// <summary>PNG bytes or a frozen bitmap from the clipboard, or null if it is not an image any more.</summary>
    private static (byte[]? Png, BitmapSource? Bitmap)? ReadClipboardImage()
    {
        if (NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_HDROP) ||
            NativeMethods.IsClipboardFormatAvailable(Klip.Interop.Clipboard.VirtualFileDataObject.CfFileGroupDescriptorW))
            return null;

        var hasPng = NativeMethods.IsClipboardFormatAvailable(Klip.Interop.Clipboard.VirtualFileDataObject.CfPng);
        var hasBitmap = NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_DIB) ||
                        NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_DIBV5) ||
                        NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_BITMAP);
        if (!hasPng && !hasBitmap)
            return null;

        try
        {
            var data = System.Windows.Clipboard.GetDataObject();
            if (data is null)
                return null;
            if (hasPng && data.GetData("PNG") is MemoryStream ms && ms.Length > 0)
                return (ms.ToArray(), null);
            if (data.GetData(DataFormats.Bitmap, autoConvert: true) is BitmapSource bmp)
            {
                if (bmp.CanFreeze && !bmp.IsFrozen)
                    bmp.Freeze();
                return (null, bmp);
            }
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("ExplorerPasteRead", ex);
        }
        return null;
    }

    /// <summary>STA worker: find the folder, write the png, select it.</summary>
    private void SaveIntoFolder(nint fg, (byte[]? Png, BitmapSource? Bitmap) image, ClipboardImageState state, string template)
    {
        try
        {
            using var view = ExplorerFolderView.FromWindow(fg);
            if (view is null)
            {
                // This PC, Home, Gallery, a zip...: let Explorer do whatever it does
                _clipboardThread.BeginInvoke(() => PassThrough(rearm: true));
                return;
            }

            var png = image.Png ?? Encode(image.Bitmap!);
            var name = FileNameTemplate.ToFileName(template,
                state.IsCandidate ? state.CopiedAt : DateTime.Now, state.SourceApp);
            var path = WriteUnique(view.Path, name, png);
            StartupLog.WriteVerbose($"ExplorerPaste: {path}");
            view.Select(path);
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("ExplorerPasteSave", ex);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private static string WriteUnique(string folder, string name, byte[] png)
    {
        for (var attempt = 0; ; attempt++)
        {
            var unique = FileNameTemplate.MakeUnique(name, n => File.Exists(Path.Combine(folder, n)));
            var path = Path.Combine(folder, unique);
            try
            {
                // CreateNew: never overwrite, even if the name got taken a moment ago
                using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                fs.Write(png);
                return path;
            }
            catch (IOException) when (attempt < 3 && File.Exists(path))
            {
                // lost a race for the name, pick the next one
            }
        }
    }

    private static byte[] Encode(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Gives the Ctrl+V back to Explorer: drop the hotkey, replay, re-arm a bit later.</summary>
    private void PassThrough(bool rearm)
    {
        SetHotkey(false);
        NativeMethods.ReplayCtrlV();
        if (!rearm)
            return;

        _rearm ??= CreateRearmTimer();
        _rearm.Stop();
        _rearm.Start();
    }

    private DispatcherTimer CreateRearmTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, _clipboardThread.Dispatcher) { Interval = RearmDelay };
        timer.Tick += (_, _) =>
        {
            timer.Stop(); // one shot
            if (ShouldWatch)
                OnForegroundChanged(NativeMethods.GetForegroundWindow());
        };
        return timer;
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _monitor.ImageStateChanged -= OnImageStateChanged;
        try
        {
            // hotkey and WinEvent belong to the clipboard thread
            _clipboardThread.Invoke(() =>
            {
                _disposed = true;
                _foreground.Stop();
                SetHotkey(false);
                _rearm?.Stop();
                if (_window is { } window)
                {
                    window.RemoveHook(WndProc);
                    window.Dispose();
                    _window = null;
                }
            });
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("ExplorerPasteDispose", ex);
        }
    }
}
