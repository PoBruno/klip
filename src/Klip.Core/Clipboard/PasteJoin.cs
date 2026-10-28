using Klip.Core.Storage;

namespace Klip.Core.Clipboard;

/// <summary>
/// Rules for pasting several items at once (collect mode, "paste together"): plain
/// text gets joined into one paste, anything else goes through the sequential queue.
/// </summary>
public static class PasteJoin
{
    /// <summary>Items kept by collect mode before the oldest start to drop.</summary>
    public const int MaxCollected = 100;

    /// <summary>True when every item is text, so a single joined paste works.</summary>
    public static bool CanJoinAsText(IEnumerable<ClipboardItem> items)
    {
        var any = false;
        foreach (var item in items)
        {
            any = true;
            if (item.Type is not (ClipboardItemType.Text or ClipboardItemType.Html))
                return false;
        }
        return any;
    }

    /// <summary>
    /// One line break between parts. A part that already ends with a line break does not
    /// get a second one, so copied lines don't turn into double spacing. Empty parts skip.
    /// </summary>
    public static string Join(IEnumerable<string?> parts, string newLine = "\r\n")
    {
        var sb = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part))
                continue;
            if (sb.Length > 0 && sb[^1] != '\n')
                sb.Append(newLine);
            sb.Append(part);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Adds a newly copied item to the collected list: the same item copied twice in a
    /// row counts once, and past <paramref name="max"/> the oldest drop off.
    /// </summary>
    public static bool AddCollected<T>(List<T> collected, T item, Func<T, T, bool> same, int max = MaxCollected)
    {
        if (collected.Count > 0 && same(collected[^1], item))
            return false;
        collected.Add(item);
        while (collected.Count > max)
            collected.RemoveAt(0);
        return true;
    }
}
