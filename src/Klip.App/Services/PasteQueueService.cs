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
/// RF-P1.03 / ADR-P.02: o hook so existe enquanto a fila esta armada. O escopo e
/// adquirido no <see cref="Arm"/> e devolvido no <see cref="Finish"/>/<see cref="Cancel"/>,
/// o que REMOVE o WH_KEYBOARD_LL do sistema - nao apenas o desativa.
/// </para>
/// </summary>
public sealed class PasteQueueService : IDisposable
{
    private readonly ClipboardWriteGuard _writeGuard;
    private readonly MediaStore _mediaStore;
    private readonly Dispatcher _dispatcher;

    private readonly Core.Clipboard.PasteQueue<ClipboardItem> _queue = new();
    private DateTime _armedAt;

    // RF-P1.03: escopo do hook de teclado. Nulo = nenhum hook instalado por esta fila.
    private IDisposable? _hookScope;

    /// <summary>Queue state for the UI counters (X of N).</summary>
    public event Action<int, int>? QueueProgress; // (current 1-based, total)
    public event Action? QueueFinished;

    public bool IsArmed => _queue.IsArmed;

    public PasteQueueService(ClipboardWriteGuard writeGuard, MediaStore mediaStore)
    {
        _writeGuard = writeGuard;
        _mediaStore = mediaStore;
        _dispatcher = Dispatcher.CurrentDispatcher;

        // RF-P1.01: o host entrega os eventos numa thread worker propria; a assinatura
        // dura a vida do servico (singleton) e nao instala hook nenhum sozinha.
        LowLevelHookHost.Shared.Observed += OnHookEvent;
    }

    /// <summary>Arms the queue with the items in the chosen order and installs the hook.</summary>
    public void Arm(IReadOnlyList<ClipboardItem> items)
    {
        // um novo arme substitui o anterior: solta o escopo antigo antes de contar outro
        ReleaseHook();
        _queue.Reset();
        if (items.Count == 0)
            return;
        _queue.Begin(Math.Min(items.Count, 5));
        foreach (var item in items)
            _queue.Toggle(item);
        _armedAt = DateTime.UtcNow;

        // leave the FIRST item on the clipboard, ready for the first Ctrl+V
        WriteItem(_queue.Current!);

        // Start e idempotente, mas precisa ter rodado antes do primeiro Acquire.
        LowLevelHookHost.Shared.Start();
        // ReleaseCtrlV antes de armar: um cancelamento anterior por excecao pode ter
        // deixado o guard de colagem em voo tomado, e a fila nova nasceria bloqueada.
        HookPolicy.ReleaseCtrlV();
        // Arma antes de instalar para nao existir instante com hook vivo e fila
        // desarmada (o Ctrl+V daquele instante seria perdido).
        HookPolicy.CtrlVArmed = true;
        _hookScope = LowLevelHookHost.Shared.Acquire(LowLevelHookKind.Keyboard);

        QueueProgress?.Invoke(_queue.CursorPosition, _queue.Count);
        StartupLog.Write($"Fila de colagem armada: {_queue.Count} itens");
    }

    /// <summary>
    /// RF-P1.04 / ADR-P.01: roda na thread WORKER do host, nunca no callback do hook e
    /// nunca na UI thread.
    /// </summary>
    private void OnHookEvent(InputEvent ev)
    {
        if (ev.Message == KlipInputMessages.CtrlV)
            OnCtrlVDetected();
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

        // timeout de seguranca: se travou no meio, aborta a fila
        if ((DateTime.UtcNow - _armedAt).TotalMinutes > 2)
        {
            // Cancel desarma, e desarmar tambem devolve o guard (ver HookPolicy.CtrlVArmed)
            _dispatcher.BeginInvoke(Cancel);
            return;
        }

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
        ReleaseHook();
        _queue.Reset();
        QueueFinished?.Invoke();
        StartupLog.Write("Fila de colagem concluída");
    }

    public void Cancel()
    {
        if (!_queue.IsArmed)
            return;
        ReleaseHook();
        _queue.Reset();
        QueueFinished?.Invoke();
    }

    /// <summary>
    /// RF-P1.03: desarma e REMOVE o hook do sistema. Desarmar tambem devolve o guard de
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
        ReleaseHook();
    }
}
