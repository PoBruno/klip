using System.Diagnostics;
using System.Windows.Threading;
using Klip.Core.Common;
using Klip.Core.Settings;

namespace Klip.App.Services;

/// <summary>
/// Swaps the process for a fresh one after hours without use or a long sleep, while
/// nothing is open. See <see cref="IdleRecycle"/> for the why. One one-shot timer that
/// every interaction pushes back, so there is no periodic work at rest.
/// </summary>
public sealed class IdleRecycler : IDisposable
{
    // the display/gpu driver is often still settling right at the resume event
    private static readonly TimeSpan ResumeDelay = TimeSpan.FromSeconds(10);

    private readonly SettingsService _settings;
    private readonly Func<bool> _isBusy;
    private readonly Action<string> _recycle;
    private readonly DispatcherTimer _idleTimer;
    private readonly Stopwatch _sinceActivity = Stopwatch.StartNew();
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private DateTime? _suspendedAtUtc;

    public IdleRecycler(Dispatcher dispatcher, SettingsService settings, Func<bool> isBusy, Action<string> recycle)
    {
        _settings = settings;
        _isBusy = isBusy;
        _recycle = recycle;
        _idleTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = IdleRecycle.IdleThreshold };
        _idleTimer.Tick += (_, _) =>
        {
            _idleTimer.Stop();
            TryRecycle(afterResume: false, _sinceActivity.Elapsed);
        };
        _idleTimer.Start();
    }

    /// <summary>The user did something (hotkey, a window opened or closed). Pushes the idle check back.</summary>
    public void NoteActivity()
    {
        _sinceActivity.Restart();
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    public void OnSuspend() => _suspendedAtUtc = DateTime.UtcNow;

    public void OnResume()
    {
        if (_suspendedAtUtc is not { } suspendedAt)
            return;
        _suspendedAtUtc = null;
        var slept = DateTime.UtcNow - suspendedAt;

        var delay = new DispatcherTimer(DispatcherPriority.Background, _idleTimer.Dispatcher) { Interval = ResumeDelay };
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            TryRecycle(afterResume: true, slept);
        };
        delay.Start();
    }

    private void TryRecycle(bool afterResume, TimeSpan away)
    {
        var busy = _isBusy();
        if (IdleRecycle.ShouldRecycle(_settings.Current.IdleRecycle, afterResume,
                DateTime.UtcNow - _startedUtc, away, busy))
        {
            var reason = afterResume
                ? $"sleep de {away.TotalMinutes:0} min"
                : $"{away.TotalMinutes:0} min sem uso";
            _recycle(reason);
            return;
        }

        if (busy)
            StartupLog.WriteVerbose("Reciclagem: adiada, app em uso");
        // still not the moment (busy, too fresh): look again after another full stretch
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    public void Dispose() => _idleTimer.Stop();
}
