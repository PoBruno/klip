namespace Klip.Core.Storage;

/// <summary>Clipboard item kind (items.type column).</summary>
public enum ClipboardItemType
{
    Text,
    Html,
    Image,
    Files,
}

/// <summary>Where the item came from (items.origin column).</summary>
public enum ClipboardItemOrigin
{
    Clipboard,
    Capture,
    Editor,

    /// <summary>Gravacao de tela GIF/MP4. Novo valor no FIM: nao muda os ints persistidos.</summary>
    Recording,
}

/// <summary>One row of the history (items table).</summary>
public sealed class ClipboardItem
{
    public long Id { get; set; }
    public ClipboardItemType Type { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastCopiedAt { get; set; }
    public string? SourceApp { get; set; }
    public string? SourceTitle { get; set; }
    public ClipboardItemOrigin Origin { get; set; } = ClipboardItemOrigin.Clipboard;
    public bool Pinned { get; set; }
    public bool Favorite { get; set; }
    public required string ContentHash { get; set; }
    public long ByteSize { get; set; }
    public string? TextContent { get; set; }
    public string? HtmlContent { get; set; }
    public string? RtfContent { get; set; }
    public string? FilePath { get; set; }
    public string? ThumbPath { get; set; }
    public string? FilesJson { get; set; }
    public string? OcrText { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }

    /// <summary>
    /// First few hundred chars of the text, saved on ingest. The list reads only this,
    /// so a 20 MB text never gets loaded just to draw a card.
    /// </summary>
    public string? Preview { get; set; }

    /// <summary>
    /// True when the row came from a list query: text, html, rtf and ocr are NOT loaded.
    /// Load the full item with ClipboardItemRepository.GetById before using the content.
    /// </summary>
    public bool IsSummary { get; set; }

    /// <summary>How many chars of text go into <see cref="Preview"/>.</summary>
    public const int PreviewLength = 600;

    public static string? BuildPreview(string? text)
    {
        if (text is null || text.Length <= PreviewLength)
            return text;
        // don't cut an emoji in half
        var cut = char.IsHighSurrogate(text[PreviewLength - 1]) ? PreviewLength - 1 : PreviewLength;
        return text[..cut];
    }
}
