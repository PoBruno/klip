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
/// </summary>
public sealed class MemorySettler
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(2);

    private readonly Dispatcher _dispatcher;
    private DispatcherTimer? _pending;

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

        // restart the countdown: closing the editor right after the overlay = one GC
        _pending?.Stop();
        _pending = new DispatcherTimer(DispatcherPriority.ApplicationIdle, _dispatcher) { Interval = Delay };
        _pending.Tick += (_, _) =>
        {
            _pending?.Stop();
            _pending = null;
            Settle();
        };
        _pending.Start();
    }

    private static void Settle()
    {
        try
        {
            var before = GC.GetTotalMemory(false);
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            // second pass picks up what the finalizers just released (InteropBitmap etc)
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

            // trim only with nothing on screen, otherwise the next frame pays soft faults
            var trimmed = !AnyWindowVisible() && NativeMethods.TrimWorkingSet();
            StartupLog.WriteVerbose(
                $"Memoria: settle {before / 1024 / 1024} -> {GC.GetTotalMemory(false) / 1024 / 1024} MB gerenciado, trim={(trimmed ? "sim" : "nao")}");
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
