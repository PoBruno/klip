using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Klip.Core.Storage;

namespace Klip.App.ViewModels;

/// <summary>A single history card.</summary>
public sealed partial class HistoryItemViewModel(
    ClipboardItem item, string? imageAbsolutePath, string? thumbAbsolutePath = null, bool isSecret = false) : ObservableObject
{
    // what the card shows of a text: a few lines is all that fits
    private const int CardPreviewChars = 300;

    /// <summary>The list row (a summary: text/html/rtf not loaded). Use the repository for the full item.</summary>
    public ClipboardItem Item { get; } = item;

    public long Id => Item.Id;
    public bool IsImage => Item.Type == ClipboardItemType.Image && imageAbsolutePath is not null;
    public string? ImagePath => imageAbsolutePath;

    /// <summary>Card thumbnail file: the thumb if we have one, else the full PNG.</summary>
    public string? ThumbSource => thumbAbsolutePath ?? imageAbsolutePath;

    private ImageSource? _thumbnail;
    private bool _thumbnailRequested;

    /// <summary>
    /// Decoded thumbnail. Starts null; the first read (only realized cards bind it)
    /// kicks off the decode on a pool thread and the card updates when it lands.
    /// </summary>
    public ImageSource? Thumbnail
    {
        get
        {
            if (_thumbnail is null && !_thumbnailRequested && IsImage && ThumbSource is { } path)
            {
                _thumbnailRequested = true;
                if (Controls.PathToThumbnailConverter.TryGetCached(path, out var cached))
                    _thumbnail = cached;
                else
                    _ = LoadThumbnailAsync(path);
            }
            return _thumbnail;
        }
    }

    private async Task LoadThumbnailAsync(string path)
    {
        // the await resumes on the UI thread (the getter runs there), so the
        // property change goes out on the right thread
        var bitmap = await Controls.PathToThumbnailConverter.LoadAsync(path);
        if (bitmap is null)
            return;
        _thumbnail = bitmap;
        OnPropertyChanged(nameof(Thumbnail));
    }

    private string? _mediaFilePath;
    private bool _mediaResolved;

    /// <summary>
    /// Path of the media file when the item is a single .gif/.mp4 that still exists
    /// (recordings land in history like this); null for everything else. Resolved on
    /// first use, so only cards that get drawn pay the json parse and the disk check.
    /// </summary>
    public string? MediaFilePath
    {
        get
        {
            if (!_mediaResolved)
            {
                _mediaResolved = true;
                _mediaFilePath = ResolveMediaFilePath(Item);
            }
            return _mediaFilePath;
        }
    }

    /// <summary>Single file GIF: the card shows a thumbnail that animates on hover.</summary>
    public bool IsGifMedia => MediaFilePath?.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Single file MP4: the card shows the video glyph (no decode).</summary>
    public bool IsMp4Media => MediaFilePath?.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) == true;

    public bool IsMediaFile => MediaFilePath is not null;

    /// <summary>The text preview only shows when there is no image and no media.</summary>
    public bool ShowsTextPreview => !IsImage && !IsMediaFile;

    private static string? ResolveMediaFilePath(ClipboardItem item)
    {
        if (item.Type != ClipboardItemType.Files || item.FilesJson is null)
            return null;
        try
        {
            var files = System.Text.Json.JsonSerializer.Deserialize<List<string>>(item.FilesJson);
            if (files is not [{ Length: > 0 } single])
                return null; // only a single file counts as media
            if (!single.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) &&
                !single.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                return null;
            return System.IO.File.Exists(single) ? single : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Segoe Fluent Icons glyph per type.</summary>
    public string TypeGlyph => Item.Type switch
    {
        ClipboardItemType.Image => "", // Photo
        ClipboardItemType.Files when IsMediaFile => "", // Video (gif/mp4 recording)
        ClipboardItemType.Files => "", // Page
        ClipboardItemType.Html => "",  // Font
        _ => "",
    };

    private string? _previewText;

    /// <summary>Short text preview for the card, built once.</summary>
    public string PreviewText => _previewText ??= BuildPreviewText();

    private string BuildPreviewText()
    {
        if (Item.Type == ClipboardItemType.Image)
            return Item.Width is not null
                ? $"{Localization.Loc.ItemImage} {Item.Width}x{Item.Height}"
                : Localization.Loc.ItemImage;

        var text = Item.Preview ?? Item.TextContent ?? "";
        // cut first, normalize after: never walk a huge string just to show 300 chars
        if (text.Length > CardPreviewChars * 2)
            text = text[..(CardPreviewChars * 2)];
        text = text.ReplaceLineEndings("\n").TrimStart('\n');
        return text.Length > CardPreviewChars ? text[..CardPreviewChars] : text;
    }

    /// <summary>Source app plus relative time.</summary>
    public string MetaLine
    {
        get
        {
            var when = FormatRelative(Item.LastCopiedAt.ToLocalTime());
            return Item.SourceApp is not null ? $"{Item.SourceApp} · {when}" : when;
        }
    }

    [ObservableProperty]
    private bool _isPinned = item.Pinned;

    [ObservableProperty]
    private bool _isFavorite = item.Favorite;

    /// <summary>Short lived secret kept on purpose: the preview is masked and it is not draggable.</summary>
    public bool IsSecret { get; } = isSecret;

    /// <summary>1 to 9 while Ctrl or Alt is held and the card is among the first nine visible; 0 otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasShortcut))]
    private int _shortcutNumber;

    public bool HasShortcut => ShortcutNumber > 0;

    /// <summary>Picked for "paste together" (Ctrl+click or Shift+arrows); the number is the pick order.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChecked))]
    private int _checkOrder;

    public bool IsChecked => CheckOrder > 0;

    /// <summary>Position in the paste queue; 0 means not queued.</summary>
    [ObservableProperty]
    private int _queueOrder;

    public bool IsQueued => QueueOrder > 0;

    partial void OnQueueOrderChanged(int value) => OnPropertyChanged(nameof(IsQueued));

    /// <summary>
    /// "Pinned"/"Recent" label drawn above this card when it opens a section. The list is
    /// flat on purpose: real WPF grouping kills virtualization with the WPF-UI ListBox style.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHeader))]
    private string? _headerText;

    public bool HasHeader => HeaderText is not null;

    private static string FormatRelative(DateTimeOffset localTime)
    {
        var delta = DateTimeOffset.Now - localTime;
        return delta.TotalSeconds < 60 ? Localization.Loc.TimeNow
            : delta.TotalMinutes < 60 ? $"{(int)delta.TotalMinutes} min"
            : delta.TotalHours < 24 && localTime.Date == DateTime.Today ? localTime.ToString("HH:mm")
            : localTime.Date == DateTime.Today.AddDays(-1) ? $"{Localization.Loc.TimeYesterday} {localTime:HH:mm}"
            : localTime.ToString("dd/MM HH:mm");
    }
}
