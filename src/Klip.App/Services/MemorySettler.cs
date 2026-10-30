using System.Runtime;
using System.Windows;
using System.Windows.Threading;
using Klip.Interop;

namespace Klip.App.Services;

/// <summary>
/// One compacting GC after a heavy session (overlay, editor, media editor, recording).
/// The frozen frames live in native WIC memory that only comes back when a finalizer
/// runs, and an idle tray app never allocates, so without this the memory stays
/// stuck until the next activity. Never periodic: one shot per heavy session.
/// <para>
/// The working set trim runs on its own, much longer clock than the GC. Trimming right
/// after the 2s GC debounce used to mean the very next Win+V or Win+Shift+S (often
/// seconds later) paid the pages back one fault at a time - the 2-3s freeze people
/// noticed was this, not the hotkey or the window itself.
/// </para>
/// </summary>
public sealed class MemorySettler : IDisposable
{
    private static readonly TimeSpan GcDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TrimDelay = TimeSpan.FromMinutes(3);

    private readonly Dispatcher _dispatcher;
    private DispatcherTimer? _pendingGc;
    private DispatcherTimer? _pendingTrim;

    public MemorySettler(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Raised right after a trim actually ran. A trim quietly undoes whatever the
    /// startup warm-ups paid for (capture overlay JIT/brushes, flyout layout+query),
    /// and nothing else repays that - without a listener the next real hotkey eats
    /// the cost live. Fired on the dispatcher thread.
    /// </summary>
    public event Action? Trimmed;

    /// <summary>Asks for a settle; several calls in a row collapse into one.</summary>
    public void Schedule()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(Schedule);
            return;
        }

        // Background, not ApplicationIdle: the latter only fires once the dispatcher
        // queue is truly empty, which a fullscreen game (or just normal clipboard/hook
        // traffic) can keep from happening for hours. Background still runs on the UI
        // thread, same as before - a blocking compacting GC needs to run there, not on
        // a raw pool thread, or its finalizer pass can step on native WIC/GDI cleanup
        // in ways that are not safe off the thread that owns those objects.
        _pendingGc?.Stop();
        _pendingGc = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = GcDelay };
        _pendingGc.Tick += (_, _) =>
        {
            _pendingGc?.Stop();
            _pendingGc = null;
            RunGc();
        };
        _pendingGc.Start();

        _pendingTrim?.Stop();
        _pendingTrim = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TrimDelay };
        _pendingTrim.Tick += (_, _) =>
        {
            _pendingTrim?.Stop();
            _pendingTrim = null;
            RunTrim();
        };
        _pendingTrim.Start();
    }

    public void Dispose()
    {
        _pendingGc?.Stop();
        _pendingTrim?.Stop();
    }

    private static void RunGc()
    {
        try
        {
            var before = GC.GetTotalMemory(false);
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            // second pass picks up what the finalizers just released (InteropBitmap etc)
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

            StartupLog.WriteVerbose(
                $"Memoria: settle {before / 1024 / 1024} -> {GC.GetTotalMemory(false) / 1024 / 1024} MB gerenciado");
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("MemorySettler", ex);
        }
    }

    private void RunTrim()
    {
        try
        {
            // still nothing on screen 3 min later: this is real idle, not "stepped away a sec"
            var trimmed = !AnyWindowVisible() && NativeMethods.TrimWorkingSet();
            StartupLog.WriteVerbose($"Memoria: trim do working set={(trimmed ? "sim" : "nao")}");
            if (trimmed)
                Trimmed?.Invoke();
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("MemorySettler", ex);
        }
    }

    private static bool AnyWindowVisible()
    {
        if (Application.Current is null)
            return false;
        foreach (Window window in Application.Current.Windows)
        {
            if (window.IsVisible)
                return true;
        }
        return false;
    }
}
