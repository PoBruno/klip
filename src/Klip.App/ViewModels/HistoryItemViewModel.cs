using CommunityToolkit.Mvvm.ComponentModel;
using Klip.Core.Storage;

namespace Klip.App.ViewModels;

/// <summary>A single history card.</summary>
public sealed partial class HistoryItemViewModel(
    ClipboardItem item, string? imageAbsolutePath, string? thumbAbsolutePath = null) : ObservableObject
{
    public ClipboardItem Item { get; } = item;

    public long Id => Item.Id;
    public bool IsImage => Item.Type == ClipboardItemType.Image && imageAbsolutePath is not null;
    public string? ImagePath => imageAbsolutePath;

    /// <summary>Card thumbnail source: use the thumb if we have one, else the full PNG.</summary>
    public string? ThumbSource => thumbAbsolutePath ?? imageAbsolutePath;

    /// <summary>Segoe Fluent Icons glyph per type.</summary>
    public string TypeGlyph => Item.Type switch
    {
        ClipboardItemType.Image => "\uE91B", // Photo
        ClipboardItemType.Files => "\uE7C3", // Page
        ClipboardItemType.Html => "\uE8D2",  // Font
        _ => "\uE8D2",
    };

    /// <summary>Short text preview for the card.</summary>
    public string PreviewText
    {
        get
        {
            if (Item.Type == ClipboardItemType.Image)
                return Item.Width is not null
                    ? $"{Localization.Loc.ItemImage} {Item.Width}x{Item.Height}"
                    : Localization.Loc.ItemImage;
            var text = Item.TextContent ?? "";
            text = text.ReplaceLineEndings("\n").TrimStart('\n');
            return text.Length > 300 ? text[..300] : text;
        }
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

    /// <summary>Position in the paste queue; 0 means not queued.</summary>
    [ObservableProperty]
    private int _queueOrder;

    public bool IsQueued => QueueOrder > 0;

    partial void OnQueueOrderChanged(int value) => OnPropertyChanged(nameof(IsQueued));

    /// <summary>Group name on the Recent tab; pinned items sit up top.</summary>
    public string GroupName => IsPinned ? Localization.Loc.GroupPinned : Localization.Loc.GroupRecent;

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
