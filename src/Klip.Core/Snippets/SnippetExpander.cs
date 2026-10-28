using System.Globalization;
using System.Text;

namespace Klip.Core.Snippets;

/// <summary>
/// Resolves the placeholders of a snippet at paste time:
/// {date} {time} {datetime} {clipboard}. "{{" and "}}" give literal braces, and
/// anything unknown stays as typed, so code snippets with braces survive.
/// </summary>
public static class SnippetExpander
{
    public static readonly IReadOnlyList<string> Placeholders = ["{date}", "{time}", "{datetime}", "{clipboard}"];

    /// <summary>True when the text has something to resolve (skips the clipboard read otherwise).</summary>
    public static bool HasPlaceholders(string? content) =>
        !string.IsNullOrEmpty(content) && content.Contains('{');

    /// <summary>True when the expansion needs the current clipboard text.</summary>
    public static bool UsesClipboard(string? content) =>
        content?.Contains("{clipboard}", StringComparison.OrdinalIgnoreCase) == true;

    /// <param name="content">Snippet text.</param>
    /// <param name="now">Local time used for {date}/{time}/{datetime}.</param>
    /// <param name="clipboardText">Called at most once, only if {clipboard} shows up.</param>
    /// <param name="culture">Date/time format; defaults to the current regional format.</param>
    public static string Expand(string? content, DateTime now, Func<string?>? clipboardText = null,
        CultureInfo? culture = null)
    {
        if (string.IsNullOrEmpty(content))
            return "";
        if (!HasPlaceholders(content) && !content.Contains('}'))
            return content;

        culture ??= CultureInfo.CurrentCulture;
        string? clipboard = null;
        var clipboardRead = false;

        var sb = new StringBuilder(content.Length + 16);
        var i = 0;
        while (i < content.Length)
        {
            var c = content[i];
            if (c == '{' && i + 1 < content.Length && content[i + 1] == '{')
            {
                sb.Append('{');
                i += 2;
                continue;
            }
            if (c == '}' && i + 1 < content.Length && content[i + 1] == '}')
            {
                sb.Append('}');
                i += 2;
                continue;
            }
            if (c == '{')
            {
                var close = content.IndexOf('}', i + 1);
                if (close > i)
                {
                    var name = content.AsSpan(i + 1, close - i - 1);
                    string? value = null;
                    if (name.Equals("date", StringComparison.OrdinalIgnoreCase))
                        value = now.ToString("d", culture);
                    else if (name.Equals("time", StringComparison.OrdinalIgnoreCase))
                        value = now.ToString("t", culture);
                    else if (name.Equals("datetime", StringComparison.OrdinalIgnoreCase))
                        value = now.ToString("g", culture);
                    else if (name.Equals("clipboard", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!clipboardRead)
                        {
                            clipboardRead = true;
                            try
                            {
                                clipboard = clipboardText?.Invoke();
                            }
                            catch
                            {
                                clipboard = null; // an unreadable clipboard just expands to nothing
                            }
                        }
                        value = clipboard ?? "";
                    }

                    if (value is not null)
                    {
                        sb.Append(value);
                        i = close + 1;
                        continue;
                    }
                }
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}
