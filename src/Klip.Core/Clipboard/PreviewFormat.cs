using System.Globalization;

namespace Klip.Core.Clipboard;

/// <summary>Small formatting helpers for the flyout preview panel.</summary>
public static class PreviewFormat
{
    /// <summary>Longest text the preview panel loads into its box; a TextBox chokes far before a few MB.</summary>
    public const int MaxPreviewChars = 200_000;

    /// <summary>Cuts the text to <paramref name="max"/> chars without splitting a surrogate pair.</summary>
    public static string Clip(string? text, int max, out bool truncated)
    {
        truncated = false;
        if (string.IsNullOrEmpty(text))
            return "";
        if (text.Length <= max)
            return text;
        truncated = true;
        var cut = max;
        if (char.IsHighSurrogate(text[cut - 1]))
            cut--;
        return text[..cut];
    }

    /// <summary>"512 B", "1,5 KB", "3,2 MB" with the given culture.</summary>
    public static string HumanSize(long bytes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (bytes < 1024)
            return string.Create(culture, $"{bytes} B");
        var kb = bytes / 1024.0;
        if (kb < 1024)
            return string.Create(culture, $"{kb:0.#} KB");
        var mb = kb / 1024.0;
        if (mb < 1024)
            return string.Create(culture, $"{mb:0.#} MB");
        return string.Create(culture, $"{mb / 1024.0:0.##} GB");
    }

    /// <summary>
    /// Maps a digit key to the 1 to 9 shortcut number (Ctrl+N copies, Alt+N pastes).
    /// Both the top row and the numpad count; anything else is 0.
    /// </summary>
    public static int ShortcutNumber(int virtualKey) => virtualKey switch
    {
        >= 0x31 and <= 0x39 => virtualKey - 0x30,
        >= 0x61 and <= 0x69 => virtualKey - 0x60,
        _ => 0,
    };
}
