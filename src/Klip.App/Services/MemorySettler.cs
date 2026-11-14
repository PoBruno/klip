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
/// No working set trim. It used to run 3 min after a heavy session and push every page
/// out, so the first hotkey after a while away paid all of it back in page faults
/// (seconds, not ms). Which pages stay resident is the memory manager's call.
/// </para>
/// </summary>
public sealed class MemorySettler : IDisposable
{
    private static readonly TimeSpan GcDelay = TimeSpan.FromSeconds(2);

    private readonly Dispatcher _dispatcher;
    private DispatcherTimer? _pendingGc;
    private int _postponed;

    // a pinned image is a window that can stay up for hours: stop waiting after a minute
    private const int MaxPostpones = 30;

    public MemorySettler(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

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
        _postponed = 0;
        _pendingGc = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = GcDelay };
        _pendingGc.Tick += (_, _) =>
        {
            // a blocking compacting gc on the ui thread while the user has a window up
            // (the next capture, the panel) is a visible stall: wait for it to close
            if (AnyWindowVisible() && ++_postponed < MaxPostpones)
                return; // ticks again in GcDelay
            _pendingGc?.Stop();
            _pendingGc = null;
            RunGc();
        };
        _pendingGc.Start();
    }

    public void Dispose()
    {
        _pendingGc?.Stop();
    }

    private static void RunGc()
    {
        try
        {
            var before = GC.GetTotalMemory(false);
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            // one blocking pass; the finalizers release the WIC memory on their own thread
            // and the next ordinary gc takes what they free (a second forced pass here
            // doubled the pause for little)
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

            StartupLog.WriteVerbose(
                $"Memoria: settle {before / 1024 / 1024} -> {GC.GetTotalMemory(false) / 1024 / 1024} MB gerenciado");
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
