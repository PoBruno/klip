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

    /// <summary>Gravacao de tela GIF/MP4 (RF-F3.07). Novo valor no FIM: nao muda os ints persistidos.</summary>
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
}
