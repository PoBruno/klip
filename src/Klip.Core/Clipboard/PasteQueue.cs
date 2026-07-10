namespace Klip.Core.Clipboard;

/// <summary>
/// Pure logic for the sequential paste queue: keeps the pick order, badges and
/// steps through one item at a time. Testable without any UI/hook.
/// </summary>
/// <typeparam name="T">Item type (ClipboardItem in the App, int in tests).</typeparam>
public sealed class PasteQueue<T>
{
    private readonly List<T> _items = [];
    private int _cursor;

    public int Target { get; private set; }
    public int Count => _items.Count;
    public bool IsFull => _items.Count >= Target;
    public bool IsArmed => Target > 0 && _items.Count > 0;

    /// <summary>Starts picking for k items (1 to 5), wiping any state.</summary>
    public void Begin(int target)
    {
        _items.Clear();
        _cursor = 0;
        Target = Math.Clamp(target, 1, 5);
    }

    /// <summary>
    /// Adds it (if new and not full) or removes it (if already there).
    /// Returns the item's new 1-based spot, or 0 if it was removed/rejected.
    /// </summary>
    public int Toggle(T item, IEqualityComparer<T>? comparer = null)
    {
        comparer ??= EqualityComparer<T>.Default;
        var index = _items.FindIndex(x => comparer.Equals(x, item));
        if (index >= 0)
        {
            _items.RemoveAt(index);
            return 0;
        }
        if (_items.Count >= Target)
            return 0;
        _items.Add(item);
        return _items.Count;
    }

    /// <summary>1-based spot of an item in the queue (0 = not queued).</summary>
    public int OrderOf(T item, IEqualityComparer<T>? comparer = null)
    {
        comparer ??= EqualityComparer<T>.Default;
        var index = _items.FindIndex(x => comparer.Equals(x, item));
        return index < 0 ? 0 : index + 1;
    }

    /// <summary>Items in the chosen order (snapshot to arm the paste).</summary>
    public IReadOnlyList<T> Snapshot() => _items.ToList();

    // ----- Consumed while pasting -----

    /// <summary>Item to paste right now (cursor); default(T) once we run out.</summary>
    public T? Current => _cursor < _items.Count ? _items[_cursor] : default;

    public bool HasCurrent => _cursor < _items.Count;

    /// <summary>1-based cursor position.</summary>
    public int CursorPosition => _cursor + 1;

    /// <summary>Moves to the next item; returns false when the queue is done.</summary>
    public bool Advance()
    {
        _cursor++;
        return _cursor < _items.Count;
    }

    public void Reset()
    {
        _items.Clear();
        _cursor = 0;
        Target = 0;
    }
}
