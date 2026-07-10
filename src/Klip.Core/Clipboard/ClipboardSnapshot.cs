namespace Klip.Core.Clipboard;

/// <summary>
/// Snapshot of a single copy event: one event makes ONE item with several
/// representations. Built by the monitor (App), consumed by the ingest.
/// </summary>
public sealed class ClipboardSnapshot
{
    public string? Text { get; init; }
    public string? HtmlFragment { get; init; }
    public string? Rtf { get; init; }
    public byte[]? PngBytes { get; init; }
    public int? ImageWidth { get; init; }
    public int? ImageHeight { get; init; }
    public IReadOnlyList<string>? Files { get; init; }
    public string? SourceApp { get; init; }
    public string? SourceTitle { get; init; }
    public Storage.ClipboardItemOrigin Origin { get; init; } = Storage.ClipboardItemOrigin.Clipboard;
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public bool IsEmpty => Text is null && PngBytes is null && (Files is null || Files.Count == 0);
}
