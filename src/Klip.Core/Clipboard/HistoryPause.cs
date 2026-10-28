namespace Klip.Core.Clipboard;

/// <summary>
/// Pause state of the history: paused for a while or until resumed, and "ignore the
/// next copy". Both deadlines share one one-shot timer that only exists while one of
/// them is pending, so a resting app has no timer at all.
/// </summary>
public sealed class HistoryPause : IDisposable
{
    /// <summary>How long "ignore the next copy" waits for that copy before giving up.</summary>
    public static readonly TimeSpan IgnoreNextWindow = TimeSpan.FromMinutes(5);

    private readonly Lock _sync = new();
    private readonly TimeProvider _time;
    private ITimer? _timer;
    private bool _paused;
    private DateTimeOffset? _pausedUntil;
    private DateTimeOffset? _ignoreUntil;
    private bool _disposed;

    public HistoryPause(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Raised after any change (pause, resume, deadline hit, copy ignored). Any thread.</summary>
    public event Action? Changed;

    public bool IsPaused
    {
        get
        {
            lock (_sync)
                return _paused;
        }
    }

    /// <summary>When a timed pause ends; null while not paused or paused until resumed.</summary>
    public DateTimeOffset? PausedUntil
    {
        get
        {
            lock (_sync)
                return _paused ? _pausedUntil : null;
        }
    }

    public bool IsIgnoringNext
    {
        get
        {
            lock (_sync)
                return _ignoreUntil is not null;
        }
    }

    /// <summary>True while a one-shot timer is armed (tests and the diagnostics card).</summary>
    public bool HasTimer
    {
        get
        {
            lock (_sync)
                return _timer is not null;
        }
    }

    /// <summary>Pauses for <paramref name="duration"/>; null = until <see cref="Resume"/>.</summary>
    public void Pause(TimeSpan? duration)
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _paused = true;
            _pausedUntil = duration is { } d ? _time.GetUtcNow() + d : null;
            RearmLocked();
        }
        Changed?.Invoke();
    }

    public void Resume()
    {
        lock (_sync)
        {
            if (!_paused)
                return;
            _paused = false;
            _pausedUntil = null;
            RearmLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>Flips between paused (until resumed) and running.</summary>
    public void Toggle()
    {
        if (IsPaused)
            Resume();
        else
            Pause(null);
    }

    /// <summary>The next copy is dropped (a password you are about to copy). Expires on its own.</summary>
    public void IgnoreNext()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _ignoreUntil = _time.GetUtcNow() + IgnoreNextWindow;
            RearmLocked();
        }
        Changed?.Invoke();
    }

    public void CancelIgnoreNext()
    {
        lock (_sync)
        {
            if (_ignoreUntil is null)
                return;
            _ignoreUntil = null;
            RearmLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Asked by the clipboard monitor for every new copy: true means "drop this one".
    /// Consumes the ignore flag.
    /// </summary>
    public bool TryConsumeIgnoreNext()
    {
        lock (_sync)
        {
            if (_ignoreUntil is not { } until)
                return false;
            _ignoreUntil = null;
            RearmLocked();
            if (until < _time.GetUtcNow())
                return false; // expired and the timer just did not get to it yet
        }
        Changed?.Invoke();
        return true;
    }

    // earliest pending deadline, or nothing
    private DateTimeOffset? NextDeadlineLocked()
    {
        DateTimeOffset? next = _paused ? _pausedUntil : null;
        if (_ignoreUntil is { } ignore && (next is null || ignore < next))
            next = ignore;
        return next;
    }

    private void RearmLocked()
    {
        _timer?.Dispose();
        _timer = null;
        if (_disposed || NextDeadlineLocked() is not { } deadline)
            return;
        var due = deadline - _time.GetUtcNow();
        if (due < TimeSpan.Zero)
            due = TimeSpan.Zero;
        _timer = _time.CreateTimer(_ => OnDeadline(), null, due, Timeout.InfiniteTimeSpan);
    }

    private void OnDeadline()
    {
        var changed = false;
        lock (_sync)
        {
            if (_disposed)
                return;
            var now = _time.GetUtcNow();
            if (_paused && _pausedUntil is { } until && until <= now)
            {
                _paused = false;
                _pausedUntil = null;
                changed = true;
            }
            if (_ignoreUntil is { } ignore && ignore <= now)
            {
                _ignoreUntil = null;
                changed = true;
            }
            RearmLocked();
        }
        if (changed)
            Changed?.Invoke();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
