using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Klip.Core.Settings;
using Klip.Core.Storage;
using Klip.Interop;

namespace Klip.App.Services;

/// <summary>
/// Pastes a history item into whatever app had focus.
/// The flow is: save focus before the flyout opens, write the clipboard, restore
/// focus (falling back to AttachThreadInput), fire Ctrl+V through SendInput, and
/// finally put the old clipboard back if that option is on.
/// </summary>
public sealed class PasteService(
    ClipboardWriteGuard writeGuard,
    MediaStore mediaStore,
    SettingsService settings)
{
    /// <summary>HWND of the target app, saved before showing the flyout.</summary>
    public nint SavedTargetWindow { get; private set; }

    /// <summary>Fires when the paste fails, so we can show a fallback toast.</summary>
    public event Action? PasteFailed;

    public void CaptureForegroundTarget() =>
        SavedTargetWindow = NativeMethods.GetForegroundWindow();

    /// <summary>
    /// Brings the saved target app back to the front. Used when the flyout took
    /// focus (search box click) and then closed without pasting, so the caret
    /// goes back where the user was.
    /// </summary>
    public void RestoreTargetFocus()
    {
        if (SavedTargetWindow != nint.Zero)
            NativeMethods.ForceForeground(SavedTargetWindow);
    }

    /// <summary>
    /// Writes the item to the clipboard and pastes it into the target. Returns
    /// immediately: all the heavy work (image decode, clipboard write, delays)
    /// runs off the click so the UI and the input hooks never stall.
    /// </summary>
    public void PasteItem(ClipboardItem item, bool asPlainText = false)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var target = SavedTargetWindow;
        var restore = settings.Current.RestoreClipboardAfterPaste;
        // snapshot the current clipboard now if the option is on. cheap for text,
        // and it has to be read on the UI thread anyway (STA).
        var previous = restore ? writeGuard.SnapshotCurrent() : null;

        // decode the image (disk read + full decode) OFF the UI thread; text is cheap
        BitmapSource? bitmap = null;
        byte[]? pngBytes = null;
        var isImage = item.Type == ClipboardItemType.Image && item.FilePath is not null;
        var imagePath = isImage ? mediaStore.ToAbsolute(item.FilePath!) : null;

        _ = Task.Run(() =>
        {
            var ok = true;
            try
            {
                if (imagePath is not null)
                    (pngBytes, bitmap) = ClipboardWriteGuard.DecodeImageFile(imagePath);

                // the actual clipboard write must run on the UI thread (STA)
                dispatcher.Invoke(() =>
                {
                    if (bitmap is not null && pngBytes is not null)
                        writeGuard.WriteImageFromPng(pngBytes, bitmap);
                    else
                        writeGuard.WriteItem(item, plainTextOnly: asPlainText);
                });

                if (target != nint.Zero && !NativeMethods.ForceForeground(target))
                    ok = false;

                WaitForForeground(target);
                NativeMethods.ReleasePressedModifiers();
                Thread.Sleep(20);
                NativeMethods.SendCtrlV();
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("Paste", ex);
                ok = false;
            }

            if (restore && previous is not null)
            {
                Thread.Sleep(150);
                dispatcher.BeginInvoke(() => writeGuard.Restore(previous));
            }

            if (!ok)
                dispatcher.BeginInvoke(() => PasteFailed?.Invoke());
        });
    }

    /// <summary>
    /// Waits (briefly) for the target window to become foreground, instead of a
    /// fixed sleep. Bails out fast so the paste stays snappy.
    /// </summary>
    private static void WaitForForeground(nint target)
    {
        if (target == nint.Zero)
        {
            Thread.Sleep(40);
            return;
        }
        for (var i = 0; i < 12; i++) // up to ~120ms, usually resolves in one or two
        {
            if (NativeMethods.GetForegroundWindow() == target)
                return;
            Thread.Sleep(10);
        }
    }

    /// <summary>Just copy, no paste (Ctrl+click). Image decode runs off the UI thread.</summary>
    public void CopyItemToClipboard(ClipboardItem item, bool asPlainText = false)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        if (item.Type == ClipboardItemType.Image && item.FilePath is not null)
        {
            var path = mediaStore.ToAbsolute(item.FilePath);
            _ = Task.Run(() =>
            {
                var (png, bmp) = ClipboardWriteGuard.DecodeImageFile(path);
                dispatcher.Invoke(() => writeGuard.WriteImageFromPng(png, bmp));
            });
            return;
        }
        writeGuard.WriteItem(item, plainTextOnly: asPlainText);
    }

    /// <summary>Writes plain text and pastes it into the target (used by the emoji panel).</summary>
    public void PasteText(string text)
    {
        var restore = settings.Current.RestoreClipboardAfterPaste;
        var previous = restore ? writeGuard.SnapshotCurrent() : null;

        writeGuard.WriteText(text);

        var target = SavedTargetWindow;
        var dispatcher = Dispatcher.CurrentDispatcher;
        _ = Task.Run(() =>
        {
            try
            {
                if (target != nint.Zero)
                    NativeMethods.ForceForeground(target);
                WaitForForeground(target);
                NativeMethods.ReleasePressedModifiers();
                Thread.Sleep(20);
                NativeMethods.SendCtrlV();
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("PasteText", ex);
            }

            if (restore && previous is not null)
            {
                Thread.Sleep(150);
                dispatcher.BeginInvoke(() => writeGuard.Restore(previous));
            }
        });
    }
}
