using System.Text.Json;
using Klip.Core.Common;
using Klip.Core.Settings;
using Klip.Core.Storage;

namespace Klip.Core.Clipboard;

/// <summary>
/// Pulls clipboard snapshots in: typing, hashing, dedupe, media on disk and
/// persistence. Thread-safe, runs off the UI thread.
/// </summary>
public sealed class ClipboardIngestService(
    ClipboardItemRepository repository,
    MediaStore mediaStore,
    SettingsService settings,
    IThumbnailGenerator? thumbnailGenerator = null,
    IImageTextExtractor? textExtractor = null)
{
    private readonly Lock _writeLock = new(); // writes are serialized

    /// <summary>Fires after an item is persisted, so the UI can refresh.</summary>
    public event Action<ClipboardItem>? ItemIngested;

    /// <summary>Process and persist; returns the item, or null if it was filtered out.</summary>
    public ClipboardItem? Ingest(ClipboardSnapshot snapshot)
    {
        if (snapshot.IsEmpty)
            return null;

        var s = settings.Current;

        // drop it if the source app is on the exclude list
        if (snapshot.SourceApp is not null &&
            s.ExcludedApps.Any(a => string.Equals(a, snapshot.SourceApp, StringComparison.OrdinalIgnoreCase)))
            return null;

        var item = BuildItem(snapshot, s);
        if (item is null)
            return null;

        // per-item size cap
        if (s.RetentionMaxItemBytes > 0 && item.ByteSize > s.RetentionMaxItemBytes)
            return null;

        lock (_writeLock)
        {
            repository.Upsert(item);
        }

        ItemIngested?.Invoke(item);

        // OCR runs in the background so it never blocks the capture. When it finds
        // text we save it, and the FTS index picks it up for search inside images.
        if (item.Type == ClipboardItemType.Image &&
            snapshot.PngBytes is { Length: > 0 } png &&
            textExtractor is { IsAvailable: true })
        {
            RunOcrInBackground(item.Id, png);
        }

        return item;
    }

    private void RunOcrInBackground(long itemId, byte[] png)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var text = await textExtractor!.ReadTextAsync(png).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    lock (_writeLock)
                        repository.UpdateOcrText(itemId, text);
                }
            }
            catch
            {
                // best effort, ocr failing is not fatal
            }
        });
    }

    private ClipboardItem? BuildItem(ClipboardSnapshot snapshot, AppSettings s)
    {
        var ts = snapshot.Timestamp;

        // type priority: image > files > text
        if (snapshot.PngBytes is { Length: > 0 } png && s.CaptureImages)
        {
            var hash = HashUtil.Sha256Hex(png);
            var path = mediaStore.SavePng(png, hash, ts);

            // build the JPEG thumbnail once, here on ingest (the card reuses it)
            string? thumbPath = null;
            var thumb = thumbnailGenerator?.CreateJpegThumbnail(png);
            if (thumb is not null)
                thumbPath = mediaStore.SaveThumb(thumb, hash, ts);

            return new ClipboardItem
            {
                Type = ClipboardItemType.Image,
                CreatedAt = ts,
                LastCopiedAt = ts,
                SourceApp = snapshot.SourceApp,
                SourceTitle = snapshot.SourceTitle,
                Origin = snapshot.Origin,
                ContentHash = hash,
                ByteSize = png.Length,
                FilePath = path,
                ThumbPath = thumbPath,
                Width = snapshot.ImageWidth,
                Height = snapshot.ImageHeight,
            };
        }

        if (snapshot.Files is { Count: > 0 } files && s.CaptureFiles)
        {
            var joined = string.Join("\n", files);
            return new ClipboardItem
            {
                Type = ClipboardItemType.Files,
                CreatedAt = ts,
                LastCopiedAt = ts,
                SourceApp = snapshot.SourceApp,
                SourceTitle = snapshot.SourceTitle,
                ContentHash = HashUtil.Sha256Hex("files:" + joined),
                ByteSize = joined.Length,
                TextContent = joined, // searchable through FTS
                FilesJson = JsonSerializer.Serialize(files),
            };
        }

        if (snapshot.Text is { Length: > 0 } text && s.CaptureText)
        {
            // don't keep stuff that looks like a secret (token/password/JWT)
            if (s.SkipSecrets && SecretDetector.LooksLikeSecret(text))
                return null;

            var html = s.CaptureHtml ? snapshot.HtmlFragment : null;
            var rtf = s.CaptureHtml ? snapshot.Rtf : null; // o RTF segue o mesmo toggle de formatac[ao
            return new ClipboardItem
            {
                // a text item can carry HTML/RTF along with it
                Type = html is null && rtf is null ? ClipboardItemType.Text : ClipboardItemType.Html,
                CreatedAt = ts,
                LastCopiedAt = ts,
                SourceApp = snapshot.SourceApp,
                SourceTitle = snapshot.SourceTitle,
                ContentHash = HashUtil.Sha256Hex("text:" + text),
                ByteSize = System.Text.Encoding.UTF8.GetByteCount(text),
                TextContent = text,
                HtmlContent = html,
                RtfContent = rtf,
            };
        }

        return null;
    }
}
