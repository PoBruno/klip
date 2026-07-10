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
    /// Writes the item to the clipboard and pastes it into the target. Call on the UI
    /// thread; the delays run in the background so the dispatcher stays free.
    /// </summary>
    public void PasteItem(ClipboardItem item, bool asPlainText = false)
    {
        // stash the current clipboard so we can restore it after ("paste without clobbering")
        var restore = settings.Current.RestoreClipboardAfterPaste;
        var previous = restore ? writeGuard.SnapshotCurrent() : null;

        WriteToClipboard(item, asPlainText);

        var target = SavedTargetWindow;
        var dispatcher = Dispatcher.CurrentDispatcher;
        _ = Task.Run(() =>
        {
            var ok = true;
            try
            {
                if (target != nint.Zero && !NativeMethods.ForceForeground(target))
                    ok = false; // couldn't bring the target back up

                Thread.Sleep(80); // give the target window time to reactivate
                NativeMethods.ReleasePressedModifiers();
                Thread.Sleep(30);
                NativeMethods.SendCtrlV();
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("Paste", ex);
                ok = false;
            }

            // put the old clipboard back once the paste already ate the item
            if (restore && previous is not null)
            {
                Thread.Sleep(180);
                dispatcher.BeginInvoke(() => writeGuard.Restore(previous));
            }

            if (!ok)
                dispatcher.BeginInvoke(() => PasteFailed?.Invoke());
        });
    }

    /// <summary>Just copy, no paste (Ctrl+click).</summary>
    public void CopyItemToClipboard(ClipboardItem item, bool asPlainText = false) =>
        WriteToClipboard(item, asPlainText);

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
                Thread.Sleep(80);
                NativeMethods.ReleasePressedModifiers();
                Thread.Sleep(30);
                NativeMethods.SendCtrlV();
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("PasteText", ex);
            }

            if (restore && previous is not null)
            {
                Thread.Sleep(180);
                dispatcher.BeginInvoke(() => writeGuard.Restore(previous));
            }
        });
    }

    private void WriteToClipboard(ClipboardItem item, bool asPlainText)
    {
        // imagem: resolve o caminho absoluto antes de passar pro guard
        if (item.Type == ClipboardItemType.Image && item.FilePath is not null)
        {
            writeGuard.WriteImageFromPngFile(mediaStore.ToAbsolute(item.FilePath));
            return;
        }
        // text + HTML + RTF, or plain text only when asPlainText
        writeGuard.WriteItem(item, plainTextOnly: asPlainText);
    }
}
