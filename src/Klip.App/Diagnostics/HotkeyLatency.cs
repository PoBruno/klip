using System.Diagnostics;
using Klip.App.Services;
using Klip.Interop;

namespace Klip.App.Diagnostics;

/// <summary>
/// One log line per window opened from a hotkey, always on (not only with --verbose):
/// how long the key sat in the queue, how long the window took, how many page faults
/// it paid and the process priority class at that moment. This is what tells a slow
/// open after hours idle (faults, class) from a busy ui thread (queue).
/// </summary>
public readonly struct HotkeyLatency
{
    private readonly string _what;
    private readonly int _queueMs;
    private readonly long _startTicks;
    private readonly uint _startFaults;

    private HotkeyLatency(string what, int queueMs)
    {
        _what = what;
        _queueMs = queueMs;
        _startTicks = Stopwatch.GetTimestamp();
        _startFaults = NativeMethods.GetPageFaultCount();
    }

    /// <summary>Starts measuring; call first thing in the handler.</summary>
    public static HotkeyLatency Begin(string what) => new(what, HotkeyService.TakeQueueMs());

    /// <summary>Writes the line; call once the window is shown.</summary>
    public void End()
    {
        if (_what is null)
            return;
        var ms = (long)Stopwatch.GetElapsedTime(_startTicks).TotalMilliseconds;
        var faults = NativeMethods.GetPageFaultCount() - _startFaults;
        var queue = _queueMs >= 0 ? $"{_queueMs} ms" : "-";
        StartupLog.Write(
            $"Atalho {_what}: fila {queue}, visivel em {ms} ms, faults {faults}, classe {PowerEfficiency.DescribePriorityClass()}");
    }
}
