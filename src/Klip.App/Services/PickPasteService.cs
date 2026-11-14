using System.Windows;
using System.Windows.Threading;
using Klip.Core.Clipboard;
using Klip.Core.Storage;

namespace Klip.App.Services;

/// <summary>
/// Keeps the clipboard ready while cards are picked in the history panel, so there is
/// nothing to confirm: a plain Ctrl+V already pastes the pick.
/// <list type="bullet">
/// <item>Join, text only: the joined text is on the clipboard.</item>
/// <item>Sequence: the paste queue is armed with item 1 on the clipboard.</item>
/// <item>Join with images or files: nothing fits one clipboard, the panel swallows the
/// Ctrl+V and <see cref="PasteNow"/> pastes the steps back to back.</item>
/// </list>
/// The clipboard from before the first pick is kept and goes back on Esc.
/// <para>
/// Every step runs on a pool thread, one at a time (latest pick wins), so database
/// reads and clipboard writes never sit on a click.
/// </para>
/// </summary>
public sealed class PickPasteService(
    ClipboardWriteGuard writeGuard,
    PasteQueueService queue,
    PasteService paste,
    ClipboardItemRepository repository)
{
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // bumped on every change from the UI; a step that sees a newer number gives up
    private int _version;

    // clipboard from before the first pick (only touched under the gate)
    private IDataObject? _original;
    private bool _hasOriginal;

    /// <summary>The pick changed: get the clipboard ready for it.</summary>
    public void Update(PickMode mode, IReadOnlyList<ClipboardItem> items)
    {
        if (mode == PickMode.None || items.Count == 0)
        {
            Clear(restore: true);
            return;
        }

        var version = Interlocked.Increment(ref _version);
        var joinsAsText = PasteJoin.CanJoinAsText(items);
        Run(() =>
        {
            if (version != Volatile.Read(ref _version))
                return;
            KeepOriginal();

            if (mode == PickMode.Join)
            {
                OnUi(version, queue.Disarm);
                if (!joinsAsText)
                    return; // pasted step by step on Ctrl+V
                var text = paste.JoinText(items);
                if (version == Volatile.Read(ref _version))
                    writeGuard.WriteText(text);
                return;
            }

            var full = items.Select(repository.LoadFull).ToList();
            OnUi(version, () =>
            {
                queue.HoldTimeout = true;
                queue.Arm(full, _original);
            });
        });
    }

    /// <summary>Esc or the last card unpicked: drop everything, optionally put the old clipboard back.</summary>
    public void Clear(bool restore)
    {
        Interlocked.Increment(ref _version);
        queue.Disarm();
        Run(() =>
        {
            if (restore && _hasOriginal)
                writeGuard.Restore(_original);
            ForgetOriginal();
        });
    }

    /// <summary>
    /// The panel closed with a pick (click outside, or the user's own Ctrl+V already went
    /// through). The clipboard stays as it is; a sequence keeps running on its own.
    /// </summary>
    public void Commit(PickMode mode, IReadOnlyList<ClipboardItem> items)
    {
        Interlocked.Increment(ref _version);
        var joinsAsText = PasteJoin.CanJoinAsText(items);
        Run(() =>
        {
            KeepOriginal();
            if (mode == PickMode.Join && joinsAsText)
            {
                // the joined text is the new clipboard; nothing to give back later
                ForgetOriginal();
                return;
            }

            var original = _original;
            if (mode == PickMode.Join)
            {
                // a mixed join can't be one clipboard: it becomes a sequence of steps
                var steps = paste.PlanSteps(items);
                _ui.Invoke(() => queue.Arm(steps, original));
            }
            else
            {
                // the sequence is already armed by the last update, and the user's own
                // Ctrl+V may have moved it on: arming again would start over at item 1
                var full = items.Select(repository.LoadFull).ToList();
                _ui.Invoke(() =>
                {
                    if (!queue.IsArmed)
                        queue.Arm(full, original);
                });
            }
            _ui.Invoke(() => queue.HoldTimeout = false);
            ForgetOriginal(); // the queue owns it now
        });
    }

    /// <summary>Paste the pick right now (Enter, or a Ctrl+V the panel had to catch).</summary>
    public void PasteNow(PickMode mode, IReadOnlyList<ClipboardItem> items)
    {
        if (mode == PickMode.Sequence)
        {
            Commit(mode, items);
            // after the commit step, so the queue is armed when the Ctrl+V lands
            Run(() => paste.PasteNextInQueue());
            return;
        }

        Interlocked.Increment(ref _version);
        queue.Disarm();
        Run(() =>
        {
            KeepOriginal();
            var original = _original;
            ForgetOriginal();
            // text joins into one paste; with images the steps go back to back and the
            // old clipboard returns at the end
            _ui.Invoke(() => paste.PasteTogether(items, PasteJoin.CanJoinAsText(items) ? null : original));
        });
    }

    private void KeepOriginal()
    {
        if (_hasOriginal)
            return;
        _original = writeGuard.SnapshotCurrent();
        _hasOriginal = true;
    }

    private void ForgetOriginal()
    {
        _original = null;
        _hasOriginal = false;
    }

    private void OnUi(int version, Action action) => _ui.Invoke(() =>
    {
        if (version == Volatile.Read(ref _version))
            action();
    });

    private void Run(Action step) => _ = Task.Run(async () =>
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            step();
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("PickPaste", ex);
        }
        finally
        {
            _gate.Release();
        }
    });
}
