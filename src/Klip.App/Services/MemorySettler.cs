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
public sealed class MemorySettler
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

    /// <summary>Asks for a settle; several calls in a row collapse into one.</summary>
    public void Schedule()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(Schedule);
            return;
        }

        // restart the countdown: closing the editor right after the overlay = one GC
        _pendingGc?.Stop();
        _pendingGc = new DispatcherTimer(DispatcherPriority.ApplicationIdle, _dispatcher) { Interval = GcDelay };
        _pendingGc.Tick += (_, _) =>
        {
            _pendingGc?.Stop();
            _pendingGc = null;
            RunGc();
        };
        _pendingGc.Start();

        // a new heavy session pushes the trim back out: only truly idle stretches pay it
        _pendingTrim?.Stop();
        _pendingTrim = new DispatcherTimer(DispatcherPriority.ApplicationIdle, _dispatcher) { Interval = TrimDelay };
        _pendingTrim.Tick += (_, _) =>
        {
            _pendingTrim?.Stop();
            _pendingTrim = null;
            RunTrim();
        };
        _pendingTrim.Start();
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

    private static void RunTrim()
    {
        try
        {
            // still nothing on screen 3 min later: this is real idle, not "stepped away a sec"
            var trimmed = !AnyWindowVisible() && NativeMethods.TrimWorkingSet();
            StartupLog.WriteVerbose($"Memoria: trim do working set={(trimmed ? "sim" : "nao")}");
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
