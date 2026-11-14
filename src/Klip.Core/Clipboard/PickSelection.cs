namespace Klip.Core.Clipboard;

/// <summary>How the picked cards get pasted.</summary>
public enum PickMode
{
    None,

    /// <summary>One Ctrl+V pastes everything, one per line.</summary>
    Join,

    /// <summary>Each Ctrl+V pastes the next one.</summary>
    Sequence,
}

/// <summary>What a toggle did to the pick.</summary>
public enum PickChange
{
    Added,
    Removed,

    /// <summary>Already at the limit, nothing changed.</summary>
    Rejected,
}

/// <summary>
/// The cards picked in the history panel: one list in click order and a single mode.
/// Picking with the other mode's gesture switches the whole list to that mode, so the
/// two modes never coexist. Keys are item ids, so the pick survives list reloads.
/// </summary>
public sealed class PickSelection<TKey> where TKey : notnull
{
    /// <summary>Cards one pick can hold.</summary>
    public const int DefaultMax = 20;

    private readonly List<TKey> _keys = [];

    public PickSelection(int max = DefaultMax)
    {
        Max = Math.Max(1, max);
    }

    public int Max { get; }

    public PickMode Mode { get; private set; } = PickMode.None;

    public int Count => _keys.Count;

    public bool IsEmpty => _keys.Count == 0;

    public IReadOnlyList<TKey> Keys => _keys;

    /// <summary>
    /// Adds or removes <paramref name="key"/> under <paramref name="mode"/>. A different
    /// mode than the current one converts the existing pick first.
    /// </summary>
    public PickChange Toggle(TKey key, PickMode mode)
    {
        if (mode == PickMode.None)
            throw new ArgumentException("A pick needs a mode.", nameof(mode));

        Mode = mode;
        var index = _keys.IndexOf(key);
        if (index >= 0)
        {
            _keys.RemoveAt(index);
            if (_keys.Count == 0)
                Mode = PickMode.None;
            return PickChange.Removed;
        }
        if (_keys.Count >= Max)
            return PickChange.Rejected;
        _keys.Add(key);
        return PickChange.Added;
    }

    /// <summary>Makes sure the key is picked (Shift+arrows walk over cards).</summary>
    public PickChange Include(TKey key, PickMode mode)
    {
        if (_keys.Contains(key))
        {
            Mode = mode;
            return PickChange.Added;
        }
        return Toggle(key, mode);
    }

    /// <summary>1-based place of the key (0 = not picked).</summary>
    public int OrderOf(TKey key)
    {
        var index = _keys.IndexOf(key);
        return index < 0 ? 0 : index + 1;
    }

    public void Clear()
    {
        _keys.Clear();
        Mode = PickMode.None;
    }
}
