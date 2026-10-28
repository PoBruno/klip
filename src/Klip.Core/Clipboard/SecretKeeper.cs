namespace Klip.Core.Clipboard;

/// <summary>
/// Deadlines of history items that look like secrets. Pure bookkeeping, no timers,
/// so it can be tested with a fake clock.
/// </summary>
public sealed class SecretSchedule
{
    private readonly Dictionary<long, DateTimeOffset> _deadlines = [];

    public int Count => _deadlines.Count;

    public IReadOnlyCollection<long> Ids => _deadlines.Keys;

    public bool Contains(long id) => _deadlines.ContainsKey(id);

    /// <summary>Adds or pushes the deadline (copying the same secret again restarts the count).</summary>
    public void Set(long id, DateTimeOffset deadline) => _deadlines[id] = deadline;

    public bool Remove(long id) => _deadlines.Remove(id);

    /// <summary>Earliest deadline, or null when nothing is pending.</summary>
    public DateTimeOffset? NextDeadline()
    {
        DateTimeOffset? next = null;
        foreach (var d in _deadlines.Values)
        {
            if (next is null || d < next)
                next = d;
        }
        return next;
    }

    /// <summary>Removes and returns every id whose deadline is at or before <paramref name="now"/>.</summary>
    public List<long> TakeDue(DateTimeOffset now)
    {
        var due = new List<long>();
        foreach (var (id, deadline) in _deadlines)
        {
            if (deadline <= now)
                due.Add(id);
        }
        foreach (var id in due)
            _deadlines.Remove(id);
        return due;
    }

    public List<long> TakeAll()
    {
        var all = _deadlines.Keys.ToList();
        _deadlines.Clear();
        return all;
    }
}

/// <summary>
/// Keeps secret items for a short while and then wipes them. One single-shot timer
/// armed for the nearest deadline; with nothing pending there is no timer at all.
/// The pending ids also go to a small file, so a crash doesn't leave a password
/// sitting in the history forever (the next start wipes the leftovers).
/// </summary>
public sealed class SecretKeeper : IDisposable
{
    private readonly Lock _sync = new();
    private readonly SecretSchedule _schedule = new();
    private readonly Action<long> _delete;
    private readonly string? _pendingFile;
    private readonly TimeProvider _time;
    private ITimer? _timer;
    private bool _disposed;

    /// <param name="delete">Removes the item from the history. Runs on a pool thread.</param>
    /// <param name="pendingFile">Where pending ids survive a crash; null = memory only.</param>
    public SecretKeeper(Action<long> delete, string? pendingFile = null, TimeProvider? time = null)
    {
        _delete = delete;
        _pendingFile = pendingFile;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Fires after an item was wiped, so the UI can drop it.</summary>
    public event Action<long>? Expired;

    /// <summary>Preview shown instead of the secret: the first 4 chars (ghp_, sk-a...) and dots.</summary>
    public static string Mask(string? text)
    {
        const string Dots = "••••••••";
        if (string.IsNullOrEmpty(text))
            return Dots;
        var head = text.Trim();
        return (head.Length > 12 ? head[..4] : "") + Dots;
    }

    /// <summary>True while the item is kept as a secret (the preview should be masked).</summary>
    public bool IsSecret(long id)
    {
        lock (_sync)
            return _schedule.Contains(id);
    }

    public void Track(long id, TimeSpan keepFor)
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _schedule.Set(id, _time.GetUtcNow() + keepFor);
            SavePending();
            Rearm();
        }
    }

    /// <summary>The user pinned/kept the item on purpose: stop the countdown.</summary>
    public void Forget(long id)
    {
        lock (_sync)
        {
            if (_schedule.Remove(id))
            {
                SavePending();
                Rearm();
            }
        }
    }

    /// <summary>Wipes everything pending right now (app exit). Returns how many.</summary>
    public int PurgeAll()
    {
        List<long> ids;
        lock (_sync)
        {
            ids = _schedule.TakeAll();
            _timer?.Dispose();
            _timer = null;
        }
        DeleteAll(ids);
        lock (_sync)
            SavePending();
        return ids.Count;
    }

    /// <summary>Ids left behind by a previous run that didn't exit cleanly.</summary>
    public static IReadOnlyList<long> ReadLeftovers(string pendingFile)
    {
        try
        {
            if (!File.Exists(pendingFile))
                return [];
            var ids = new List<long>();
            foreach (var line in File.ReadAllLines(pendingFile))
            {
                if (long.TryParse(line, out var id))
                    ids.Add(id);
            }
            return ids;
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
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

    // always under _sync
    private void Rearm()
    {
        _timer?.Dispose();
        _timer = null;
        var next = _schedule.NextDeadline();
        if (next is null || _disposed)
            return;
        var due = next.Value - _time.GetUtcNow();
        if (due < TimeSpan.Zero)
            due = TimeSpan.Zero;
        // one shot, Infinite period: never a periodic wakeup
        _timer = _time.CreateTimer(_ => OnDue(), null, due, Timeout.InfiniteTimeSpan);
    }

    private void OnDue()
    {
        List<long> due;
        lock (_sync)
        {
            if (_disposed)
                return;
            due = _schedule.TakeDue(_time.GetUtcNow());
        }

        DeleteAll(due);

        lock (_sync)
        {
            SavePending();
            Rearm();
        }
    }

    private void DeleteAll(List<long> ids)
    {
        foreach (var id in ids)
        {
            try
            {
                _delete(id);
                Expired?.Invoke(id);
            }
            catch
            {
                // timer callback: nothing can escape or the process goes down
            }
        }
    }

    private void SavePending()
    {
        if (_pendingFile is null)
            return;
        try
        {
            if (_schedule.Count == 0)
            {
                if (File.Exists(_pendingFile))
                    File.Delete(_pendingFile);
                return;
            }
            File.WriteAllLines(_pendingFile, _schedule.Ids.Select(i => i.ToString()));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
