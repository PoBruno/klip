using System.Windows;
using System.Windows.Threading;
using Klip.Core.Input;
using Klip.Core.Storage;
using Klip.Interop.Input;

namespace Klip.App.Services;

/// <summary>
/// Sequential paste queue: items picked in the flyout get pasted one per Ctrl+V.
/// A global hook drops the next item on the clipboard right before each Ctrl+V
/// reaches the target app.
/// <para>
/// O hook so existe enquanto a fila esta armada. O escopo e
/// adquirido no <see cref="Arm"/> e devolvido no <see cref="Finish"/>/<see cref="Cancel"/>,
/// o que REMOVE o WH_KEYBOARD_LL do sistema - nao apenas o desativa.
/// </para>
/// </summary>
public sealed class PasteQueueService : IDisposable
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);

    private readonly ClipboardWriteGuard _writeGuard;
    private readonly MediaStore _mediaStore;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _idleTimer;

    private readonly Core.Clipboard.PasteQueue<ClipboardItem> _queue = new();
    private DateTime _lastActivity;

    // what was on the clipboard before the pick started; put back when the queue ends
    private IDataObject? _restore;

    // item 1 already written by an earlier arm of the same pick (re-arm skips the write)
    private long? _writtenFirstId;

    // escopo do hook de teclado. Nulo = nenhum hook instalado por esta fila.
    private IDisposable? _hookScope;

    /// <summary>Queue state for the UI counters (X of N).</summary>
    public event Action<int, int>? QueueProgress; // (current 1-based, total)
    public event Action? QueueFinished;

    public bool IsArmed => _queue.IsArmed;

    /// <summary>1-based item the next Ctrl+V pastes.</summary>
    public int Position => _queue.CursorPosition;

    public int Count => _queue.Count;

    /// <summary>
    /// While the history panel is still open with the pick, the idle timeout waits:
    /// nobody pastes while choosing.
    /// </summary>
    public bool HoldTimeout { get; set; }

    /// <summary>Collect mode can hand over more than a pick holds.</summary>
    public const int MaxItems = Core.Clipboard.PasteJoin.MaxCollected;

    public PasteQueueService(ClipboardWriteGuard writeGuard, MediaStore mediaStore)
    {
        _writeGuard = writeGuard;
        _mediaStore = mediaStore;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _idleTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(10),
        };
        _idleTimer.Tick += (_, _) =>
        {
            if (!_queue.IsArmed)
                _idleTimer.Stop();
            else if (!HoldTimeout && DateTime.UtcNow - _lastActivity > IdleTimeout)
                Cancel();
        };

        // o host entrega os eventos numa thread worker propria; a assinatura
        // dura a vida do servico (singleton) e nao instala hook nenhum sozinha.
        LowLevelHookHost.Shared.Observed += OnHookEvent;
    }

    /// <summary>
    /// Arms the queue with the items in the chosen order and installs the hook. Arming
    /// again replaces the list; when item 1 is the same, the clipboard is not rewritten.
    /// <paramref name="restore"/> is what goes back on the clipboard when it ends.
    /// </summary>
    public void Arm(IReadOnlyList<ClipboardItem> items, IDataObject? restore = null)
    {
        _queue.Reset();
        if (items.Count == 0)
        {
            Disarm();
            return;
        }
        _queue.Begin(items.Count, MaxItems);
        foreach (var item in items)
            _queue.Toggle(item);
        _lastActivity = DateTime.UtcNow;
        if (restore is not null)
            _restore = restore;

        // leave the FIRST item on the clipboard, ready for the first Ctrl+V. synthetic
        // items (joined text, id 0) are always written
        var first = _queue.Current!;
        if (first.Id == 0 || _writtenFirstId != first.Id)
        {
            WriteItem(first);
            _writtenFirstId = first.Id == 0 ? null : first.Id;
        }

        if (_hookScope is null)
        {
            // Start e idempotente, mas precisa ter rodado antes do primeiro Acquire.
            LowLevelHookHost.Shared.Start();
            // ReleaseCtrlV antes de armar: um cancelamento anterior por excecao pode ter
            // deixado o guard de colagem em voo tomado, e a fila nova nasceria bloqueada.
            HookPolicy.ReleaseCtrlV();
            // Arma antes de instalar para nao existir instante com hook vivo e fila
            // desarmada (o Ctrl+V daquele instante seria perdido).
            HookPolicy.CtrlVArmed = true;
            _hookScope = LowLevelHookHost.Shared.Acquire(LowLevelHookKind.Keyboard);
            StartupLog.Write($"Fila de colagem armada: {_queue.Count} itens");
        }
        _idleTimer.Start();
        QueueProgress?.Invoke(_queue.CursorPosition, _queue.Count);
    }

    /// <summary>
    /// Roda na thread WORKER do host, nunca no callback do hook e
    /// nunca na UI thread.
    /// </summary>
    private void OnHookEvent(InputEvent ev)
    {
        if (ev.Message == KlipInputMessages.CtrlV)
            OnCtrlVDetected();
        else if (ev.Message == KlipInputMessages.QueueEscape)
            _dispatcher.BeginInvoke(() => Cancel());
    }

    /// <summary>
    /// Um Ctrl+V com a fila armada. O guard de "colagem em voo" ja foi TOMADO pelo
    /// callback (HookPolicy.TryBeginCtrlV), que so publica o evento para o primeiro
    /// Ctrl+V; os repetidos nem chegam aqui. Todo caminho de saida precisa devolver o
    /// guard, senao a fila trava no proximo item.
    /// </summary>
    private void OnCtrlVDetected()
    {
        if (!_queue.IsArmed)
        {
            HookPolicy.ReleaseCtrlV();
            return;
        }

        // current item is already on the clipboard (put there by Arm or the last Ctrl+V).
        // this Ctrl+V pastes it; meanwhile we get the NEXT one ready in the background.
        _dispatcher.BeginInvoke(() =>
        {
            try
            {
                _lastActivity = DateTime.UtcNow;
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
                        // proximo item pronto: libera o guard para o Ctrl+V seguinte
                        HookPolicy.ReleaseCtrlV();
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
                HookPolicy.ReleaseCtrlV(); // Cancel sai cedo se a fila ja tiver sido zerada
            }
        });
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
            else
                _writeGuard.WriteItem(item); // text keeps html/rtf, files go as files
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("PasteQueueWrite", ex);
        }
    }

    private void Finish()
    {
        if (_hookScope is null)
            return;
        End(restore: true);
        StartupLog.Write("Fila de colagem concluída");
    }

    /// <summary>Stops the queue and puts the original clipboard back.</summary>
    public void Cancel() => Cancel(restore: true);

    public void Cancel(bool restore)
    {
        // the hook scope is what says "armed": the queue list may already be reset
        if (_hookScope is null)
        {
            _restore = null;
            _writtenFirstId = null;
            return;
        }
        End(restore);
    }

    /// <summary>Stops without touching the clipboard (the pick moved to another mode).</summary>
    public void Disarm() => Cancel(restore: false);

    private void End(bool restore)
    {
        ReleaseHook();
        _queue.Reset();
        _idleTimer.Stop();
        _writtenFirstId = null;
        HoldTimeout = false;
        var snapshot = _restore;
        _restore = null;
        if (restore && snapshot is not null)
            _writeGuard.Restore(snapshot);
        QueueFinished?.Invoke();
    }

    /// <summary>
    /// Desarma e REMOVE o hook do sistema. Desarmar tambem devolve o guard de
    /// colagem em voo (ver <see cref="HookPolicy.CtrlVArmed"/>), entao nao ha estado
    /// preso para o proximo arme.
    /// </summary>
    private void ReleaseHook()
    {
        HookPolicy.CtrlVArmed = false;
        _hookScope?.Dispose();
        _hookScope = null;
    }

    public void Dispose()
    {
        LowLevelHookHost.Shared.Observed -= OnHookEvent;
        _idleTimer.Stop();
        ReleaseHook();
    }
}
