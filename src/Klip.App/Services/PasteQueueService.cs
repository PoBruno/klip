using System.Windows;
using System.Windows.Threading;
using Klip.Core.Storage;
using Klip.Interop;

namespace Klip.App.Services;

/// <summary>
/// Sequential paste queue: items picked in the flyout get pasted one per Ctrl+V.
/// A global hook drops the next item on the clipboard right before each Ctrl+V
/// reaches the target app.
/// </summary>
public sealed class PasteQueueService : IDisposable
{
    private readonly ClipboardWriteGuard _writeGuard;
    private readonly MediaStore _mediaStore;
    private readonly LowLevelKeyboardHook _hook = new();
    private readonly Dispatcher _dispatcher;

    private readonly Core.Clipboard.PasteQueue<ClipboardItem> _queue = new();
    private DateTime _armedAt;
    private bool _pasteInFlight;

    /// <summary>Queue state for the UI counters (X of N).</summary>
    public event Action<int, int>? QueueProgress; // (current 1-based, total)
    public event Action? QueueFinished;

    public bool IsArmed => _queue.IsArmed;

    public PasteQueueService(ClipboardWriteGuard writeGuard, MediaStore mediaStore)
    {
        _writeGuard = writeGuard;
        _mediaStore = mediaStore;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _hook.OnCtrlV = OnCtrlVDetected;
    }

    /// <summary>Arms the queue with the items in the chosen order and installs the hook.</summary>
    public void Arm(IReadOnlyList<ClipboardItem> items)
    {
        _queue.Reset();
        if (items.Count == 0)
            return;
        _queue.Begin(Math.Min(items.Count, 5));
        foreach (var item in items)
            _queue.Toggle(item);
        _armedAt = DateTime.UtcNow;

        // leave the FIRST item on the clipboard, ready for the first Ctrl+V
        WriteItem(_queue.Current!);
        _hook.Install();
        QueueProgress?.Invoke(_queue.CursorPosition, _queue.Count);
        StartupLog.Write($"Fila de colagem armada: {_queue.Count} itens");
    }

    /// <summary>Runs on the hook thread on every Ctrl+V.</summary>
    private bool OnCtrlVDetected()
    {
        if (!_queue.IsArmed || _pasteInFlight)
            return true;

        // timeout de segurança: se travou no meio, aborta a fila
        if ((DateTime.UtcNow - _armedAt).TotalMinutes > 2)
        {
            _dispatcher.BeginInvoke(Cancel);
            return true;
        }

        _pasteInFlight = true;

        // current item is already on the clipboard (put there by Arm or the last Ctrl+V).
        // this Ctrl+V pastes it; meanwhile we get the NEXT one ready in the background.
        _dispatcher.BeginInvoke(() =>
        {
            try
            {
                var hasNext = _queue.Advance();
                if (hasNext)
                {
                    // small delay so the current Ctrl+V finished pasting the previous
                    // item before we swap what's on the clipboard
                    ScheduleAfter(120, () =>
                    {
                        if (_queue.HasCurrent)
                        {
                            WriteItem(_queue.Current!);
                            QueueProgress?.Invoke(_queue.CursorPosition, _queue.Count);
                        }
                        _pasteInFlight = false;
                    });
                }
                else
                {
                    ScheduleAfter(150, Finish);
                }
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("PasteQueue", ex);
                Cancel();
            }
        });

        return true; // let the Ctrl+V keep going to the target app
    }

    private void ScheduleAfter(int ms, Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }

    private void WriteItem(ClipboardItem item)
    {
        try
        {
            if (item.Type == ClipboardItemType.Image && item.FilePath is not null)
                _writeGuard.WriteImageFromPngFile(_mediaStore.ToAbsolute(item.FilePath));
            else if (item.TextContent is not null)
                _writeGuard.WriteText(item.TextContent);
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("PasteQueueWrite", ex);
        }
    }

    private void Finish()
    {
        _hook.Uninstall();
        _queue.Reset();
        _pasteInFlight = false;
        QueueFinished?.Invoke();
        StartupLog.Write("Fila de colagem concluída");
    }

    public void Cancel()
    {
        if (!_queue.IsArmed)
            return;
        _hook.Uninstall();
        _queue.Reset();
        _pasteInFlight = false;
        QueueFinished?.Invoke();
    }

    public void Dispose() => _hook.Dispose();
}
