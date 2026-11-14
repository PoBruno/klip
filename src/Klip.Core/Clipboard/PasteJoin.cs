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

    /// <summary>True for the types that can go into a joined text paste.</summary>
    public static bool IsTextual(ClipboardItem item) =>
        item.Type is ClipboardItemType.Text or ClipboardItemType.Html;

    /// <summary>
    /// Splits a mixed pick into paste steps: runs of text become one step (pasted as one
    /// joined block), every image or file list is a step of its own. Order is kept.
    /// </summary>
    public static List<List<T>> PlanSeries<T>(IReadOnlyList<T> items, Func<T, bool> isText)
    {
        var steps = new List<List<T>>();
        List<T>? textRun = null;
        foreach (var item in items)
        {
            if (isText(item))
            {
                if (textRun is null)
                {
                    textRun = [];
                    steps.Add(textRun);
                }
                textRun.Add(item);
            }
            else
            {
                textRun = null;
                steps.Add([item]);
            }
        }
        return steps;
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
