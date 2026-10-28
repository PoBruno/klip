using System.Globalization;
using System.Text;

namespace Klip.Core.Common;

/// <summary>
/// Builds file names for images saved from the clipboard ("Klip {date} {time}").
/// Tokens: {date} = yyyy-MM-dd, {time} = HH.mm.ss, {app} = source app without ".exe".
/// The result only uses characters that are valid on NTFS, FAT/exFAT and SMB shares.
/// </summary>
public static class FileNameTemplate
{
    public const string DefaultTemplate = "Klip {date} {time}";

    // long enough for anything sane, short enough to leave room for the folder path
    private const int MaxBaseLength = 120;

    private static readonly char[] InvalidChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Expands the template into a file name base (no extension). Unknown tokens stay as
    /// typed; an empty or all-invalid result falls back to the default template.
    /// </summary>
    public static string Format(string? template, DateTime timestamp, string? app)
    {
        var expanded = Expand(string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template, timestamp, app);
        var clean = Sanitize(expanded);
        if (clean.Length == 0 && !string.IsNullOrWhiteSpace(template))
            clean = Sanitize(Expand(DefaultTemplate, timestamp, app));
        return clean;
    }

    /// <summary>Format + extension, e.g. "Klip 2026-10-26 14.03.12.png".</summary>
    public static string ToFileName(string? template, DateTime timestamp, string? app, string extension = ".png")
        => Format(template, timestamp, app) + NormalizeExtension(extension);

    /// <summary>
    /// First free name in the folder: "name.png", then "name (2).png", "name (3).png"...
    /// <paramref name="exists"/> receives the bare file name (no folder).
    /// </summary>
    public static string MakeUnique(string fileName, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        if (!exists(fileName))
            return fileName;

        var ext = Path.GetExtension(fileName);
        var stem = fileName[..^ext.Length];
        for (var n = 2; n < 10_000; n++)
        {
            var candidate = $"{stem} ({n}){ext}";
            if (!exists(candidate))
                return candidate;
        }
        // 10k files with the same name: give up being pretty
        return $"{stem} ({Guid.NewGuid():N}){ext}";
    }

    /// <summary>"chrome.exe" -> "chrome"; null/blank -> "".</summary>
    public static string AppToken(string? app)
    {
        if (string.IsNullOrWhiteSpace(app))
            return "";
        var name = app.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private static string Expand(string template, DateTime timestamp, string? app)
    {
        var sb = new StringBuilder(template.Length + 24);
        var i = 0;
        while (i < template.Length)
        {
            if (template[i] == '{')
            {
                var close = template.IndexOf('}', i + 1);
                if (close > i)
                {
                    var token = template.AsSpan(i + 1, close - i - 1);
                    string? value = null;
                    if (token.Equals("date", StringComparison.OrdinalIgnoreCase))
                        value = timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    else if (token.Equals("time", StringComparison.OrdinalIgnoreCase))
                        value = timestamp.ToString("HH.mm.ss", CultureInfo.InvariantCulture);
                    else if (token.Equals("app", StringComparison.OrdinalIgnoreCase))
                        value = AppToken(app);

                    if (value is not null)
                    {
                        sb.Append(value);
                        i = close + 1;
                        continue;
                    }
                }
            }
            sb.Append(template[i]);
            i++;
        }
        return sb.ToString();
    }

    private static string Sanitize(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        var lastWasSpace = false;
        foreach (var c in raw)
        {
            var ch = c < 32 || Array.IndexOf(InvalidChars, c) >= 0 ? '_' : c;
            if (char.IsWhiteSpace(ch))
            {
                // an empty {app} leaves a double space behind, squash it
                if (lastWasSpace)
                    continue;
                ch = ' ';
                lastWasSpace = true;
            }
            else
            {
                lastWasSpace = false;
            }
            sb.Append(ch);
        }

        // windows silently drops trailing dots and spaces, so do it here first
        var name = sb.ToString().Trim().TrimEnd('.', ' ');
        if (name.Length > MaxBaseLength)
            name = name[..MaxBaseLength].TrimEnd('.', ' ');

        if (name.Length > 0)
        {
            var dot = name.IndexOf('.');
            var stem = dot >= 0 ? name[..dot] : name;
            if (ReservedNames.Contains(stem.TrimEnd()))
                name = "_" + name;
        }
        return name;
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension))
            return "";
        return extension[0] == '.' ? extension : "." + extension;
    }
}
