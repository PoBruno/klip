using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Klip.App.Services;
using Klip.Core.Storage;

namespace Klip.App.ViewModels;

/// <summary>Flyout tabs.</summary>
public enum HistoryTab
{
    Recent,
    Favorites,
    Images,
    Text,
    Files,
    Emoji,
}

/// <summary>Date filter presets.</summary>
public enum DateFilterPreset
{
    All,
    Today,
    Yesterday,
    Last7Days,
    Last30Days,
}

/// <summary>Drives the clipboard history flyout.</summary>
public sealed partial class HistoryFlyoutViewModel : ObservableObject
{
    // first page is small so the flyout opens instantly; the scroll pulls more
    private const int FirstPageSize = 30;
    private const int PageSize = 100;

    private readonly ClipboardItemRepository _repository;
    private readonly MediaStore _mediaStore;
    private readonly PasteService _pasteService;
    private CancellationTokenSource? _searchDebounce;

    public HistoryFlyoutViewModel(
        ClipboardItemRepository repository,
        MediaStore mediaStore,
        PasteService pasteService,
        PasteQueueService pasteQueue)
    {
        _repository = repository;
        _mediaStore = mediaStore;
        _pasteService = pasteService;
        _pasteQueue = pasteQueue;
    }

    private readonly PasteQueueService _pasteQueue;
    private readonly Core.Clipboard.PasteQueue<HistoryItemViewModel> _selection = new();
    // match VMs by item Id: different pages build separate instances
    private static readonly IEqualityComparer<HistoryItemViewModel> ByItemId =
        new ItemIdComparer();

    private sealed class ItemIdComparer : IEqualityComparer<HistoryItemViewModel>
    {
        public bool Equals(HistoryItemViewModel? a, HistoryItemViewModel? b) => a?.Id == b?.Id;
        public int GetHashCode(HistoryItemViewModel vm) => vm.Id.GetHashCode();
    }

    public BulkObservableCollection<HistoryItemViewModel> Items { get; } = [];

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private HistoryTab _selectedTab = HistoryTab.Recent;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private DateFilterPreset _dateFilter = DateFilterPreset.All;

    /// <summary>Chip shown while a date filter is active.</summary>
    public bool HasDateFilter => DateFilter != DateFilterPreset.All;

    public string DateFilterLabel => DateFilter switch
    {
        DateFilterPreset.Today => Localization.Loc.DateToday,
        DateFilterPreset.Yesterday => Localization.Loc.DateYesterday,
        DateFilterPreset.Last7Days => Localization.Loc.DateLast7,
        DateFilterPreset.Last30Days => Localization.Loc.DateLast30,
        _ => "",
    };

    /// <summary>Pinned/Recent grouping only makes sense on the Recent tab with no search.</summary>
    public bool IsGroupingEnabled =>
        SelectedTab == HistoryTab.Recent && string.IsNullOrWhiteSpace(SearchText);

    private bool _allLoaded;

    /// <summary>Multi-select mode is on plus how many are picked.</summary>
    [ObservableProperty]
    private bool _isMultiSelectMode;

    [ObservableProperty]
    private int _multiSelectTarget;

    /// <summary>Reads like "1 / 3" while picking items.</summary>
    public string MultiSelectStatus => $"{_selection.Count} / {MultiSelectTarget}";

    partial void OnIsMultiSelectModeChanged(bool value) => OnPropertyChanged(nameof(MultiSelectStatus));
    partial void OnMultiSelectTargetChanged(int value) => OnPropertyChanged(nameof(MultiSelectStatus));

    /// <summary>Raised to close the window after a paste.</summary>
    public event Action? CloseRequested;

    /// <summary>Open an image in the editor (window is created by the App).</summary>
    public event Action<ClipboardItem>? OpenInEditorRequested;

    /// <summary>Tells the view the grouping changed so it can rebuild GroupDescriptions.</summary>
    public event Action? GroupingChanged;

    [RelayCommand]
    private void OpenInEditor(HistoryItemViewModel? item)
    {
        if (item?.IsImage != true)
            return;
        CloseRequested?.Invoke();
        OpenInEditorRequested?.Invoke(item.Item);
    }

    partial void OnSelectedTabChanged(HistoryTab value)
    {
        // emoji tab shows the picker, not a history query, so skip the reload
        // but still tell the view so it can swap the panel and the tab strip
        if (value != HistoryTab.Emoji)
            LoadFirstPage();
        GroupingChanged?.Invoke();
    }

    partial void OnDateFilterChanged(DateFilterPreset value)
    {
        OnPropertyChanged(nameof(HasDateFilter));
        OnPropertyChanged(nameof(DateFilterLabel));
        LoadFirstPage();
    }

    /// <summary>Search as you type, com debounce de 150 ms.</summary>
    partial void OnSearchTextChanged(string value)
    {
        _searchDebounce?.Cancel();
        var cts = new CancellationTokenSource();
        _searchDebounce = cts;
        var context = SynchronizationContext.Current;

        _ = Task.Delay(150, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled)
                return;
            context?.Post(_ =>
            {
                if (!cts.IsCancellationRequested)
                {
                    LoadFirstPage();
                    GroupingChanged?.Invoke();
                }
            }, null);
        }, TaskScheduler.Default);
    }

    /// <summary>Loads the first page; runs on every show or filter change.</summary>
    public void LoadFirstPage()
    {
        // when reopening or filtering, drop any pending selection (badges go stale)
        if (!IsMultiSelectMode)
            _selection.Reset();
        Items.Clear();
        _allLoaded = false;
        AppendPage(beforeMs: null, limit: FirstPageSize);
    }

    /// <summary>Incremental keyset paging while scrolling.</summary>
    [RelayCommand]
    private void LoadMore()
    {
        if (_allLoaded || !string.IsNullOrWhiteSpace(SearchText))
            return;
        var lastUnpinned = Items.LastOrDefault(i => !i.IsPinned);
        if (lastUnpinned is null)
            return;
        AppendPage(lastUnpinned.Item.LastCopiedAt.ToUnixTimeMilliseconds(), limit: PageSize);
    }

    private void AppendPage(long? beforeMs, int limit)
    {
        var (fromMs, toMs) = ComputeDateRange();
        var results = _repository.Query(new HistoryQuery
        {
            Type = SelectedTab switch
            {
                HistoryTab.Images => ClipboardItemType.Image,
                HistoryTab.Text => ClipboardItemType.Text,
                HistoryTab.Files => ClipboardItemType.Files,
                _ => null,
            },
            OnlyFavorites = SelectedTab == HistoryTab.Favorites,
            SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText,
            DateFromMs = fromMs,
            DateToMs = toMs,
            BeforeLastCopiedAtMs = beforeMs,
            Limit = limit,
        });

        if (results.Count < limit)
            _allLoaded = true;

        // build the VMs first, then add them all at once (single notification)
        var page = new List<HistoryItemViewModel>(results.Count);
        foreach (var item in results)
        {
            var absImage = item.FilePath is not null ? _mediaStore.ToAbsolute(item.FilePath) : null;
            var absThumb = item.ThumbPath is not null ? _mediaStore.ToAbsolute(item.ThumbPath) : null;
            var vm = new HistoryItemViewModel(item, absImage, absThumb);
            if (IsMultiSelectMode)
                vm.QueueOrder = _selection.OrderOf(vm, ByItemId);
            page.Add(vm);
        }
        Items.AddRange(page);
        IsEmpty = Items.Count == 0;
    }

    private (long? from, long? to) ComputeDateRange()
    {
        var today = DateTimeOffset.Now.Date;
        return DateFilter switch
        {
            DateFilterPreset.Today => (ToMs(today), null),
            DateFilterPreset.Yesterday => (ToMs(today.AddDays(-1)), ToMs(today)),
            DateFilterPreset.Last7Days => (ToMs(today.AddDays(-7)), null),
            DateFilterPreset.Last30Days => (ToMs(today.AddDays(-30)), null),
            _ => (null, null),
        };

        static long ToMs(DateTime local) => new DateTimeOffset(local).ToUnixTimeMilliseconds();
    }

    // ----- Item commands -----

    /// <summary>Click or Enter pastes the item into the target app.</summary>
    [RelayCommand]
    private void Paste(HistoryItemViewModel? item)
    {
        if (item is null)
            return;

        // in multi-select mode a click queues instead of pasting
        if (IsMultiSelectMode)
        {
            ToggleSelection(item);
            return;
        }

        CloseRequested?.Invoke(); // close first so the target regains focus
        _pasteService.PasteItem(item.Item);
    }

    // ----- Sequential paste queue -----

    /// <summary>Enters multi-select mode for k items (1 to 5).</summary>
    [RelayCommand]
    private void StartMultiSelect(int count)
    {
        ClearSelectionOrder();
        MultiSelectTarget = Math.Clamp(count, 1, 5);
        _selection.Begin(MultiSelectTarget);
        IsMultiSelectMode = true;
    }

    /// <summary>Leaves multi-select without arming the queue.</summary>
    [RelayCommand]
    private void CancelMultiSelect()
    {
        ClearSelectionOrder();
        IsMultiSelectMode = false;
    }

    /// <summary>Adds or removes an item from the queue, keeping order.</summary>
    private void ToggleSelection(HistoryItemViewModel item)
    {
        _selection.Toggle(item, ByItemId);
        // push the order onto every loaded item (badges)
        foreach (var vm in Items)
            vm.QueueOrder = _selection.OrderOf(vm, ByItemId);

        OnPropertyChanged(nameof(MultiSelectStatus));

        // hit the target count, so arm it on its own
        if (_selection.IsFull)
            ConfirmMultiSelect();
    }

    /// <summary>Closes the flyout and arms the paste queue.</summary>
    [RelayCommand]
    private void ConfirmMultiSelect()
    {
        if (_selection.Count == 0)
            return;
        var items = _selection.Snapshot().Select(vm => vm.Item).ToList();
        ClearSelectionOrder();
        IsMultiSelectMode = false;
        CloseRequested?.Invoke();
        _pasteQueue.Arm(items);
    }

    private void ClearSelectionOrder()
    {
        foreach (var vm in Items)
            vm.QueueOrder = 0;
        _selection.Reset();
        OnPropertyChanged(nameof(MultiSelectStatus));
    }

    /// <summary>Shift+Enter: paste as plain text.</summary>
    [RelayCommand]
    private void PastePlain(HistoryItemViewModel? item)
    {
        if (item is null)
            return;
        CloseRequested?.Invoke();
        _pasteService.PasteItem(item.Item, asPlainText: true);
    }

    /// <summary>Ctrl+click / Ctrl+Enter: copy only, don't paste.</summary>
    [RelayCommand]
    private void CopyOnly(HistoryItemViewModel? item)
    {
        if (item is null)
            return;
        _pasteService.CopyItemToClipboard(item.Item);
        // copying keeps the flyout open on purpose, so you can grab more
    }

    /// <summary>Clicking an emoji in the picker: paste it and close.</summary>
    public void PasteEmoji(string emoji)
    {
        CloseRequested?.Invoke();
        _pasteService.PasteText(emoji);
    }

    /// <summary>Brings the app the user was in back to the front (see PasteService).</summary>
    public void RestoreTargetFocus() => _pasteService.RestoreTargetFocus();

    [RelayCommand]
    private void Delete(HistoryItemViewModel? item)
    {
        if (item is null)
            return;
        _repository.Delete(item.Id);
        Items.Remove(item);
        IsEmpty = Items.Count == 0;
    }

    [RelayCommand]
    private void TogglePin(HistoryItemViewModel? item)
    {
        if (item is null)
            return;
        item.IsPinned = !item.IsPinned;
        _repository.SetPinned(item.Id, item.IsPinned);
    }

    [RelayCommand]
    private void ToggleFavorite(HistoryItemViewModel? item)
    {
        if (item is null)
            return;
        item.IsFavorite = !item.IsFavorite;
        _repository.SetFavorite(item.Id, item.IsFavorite);
        if (SelectedTab == HistoryTab.Favorites && !item.IsFavorite)
        {
            Items.Remove(item);
            IsEmpty = Items.Count == 0;
        }
    }

    /// <summary>Save as... for images.</summary>
    [RelayCommand]
    private void SaveAs(HistoryItemViewModel? item)
    {
        if (item?.ImagePath is null)
            return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = Localization.Loc.PngFilter,
            FileName = $"Klip {item.Item.LastCopiedAt.ToLocalTime():yyyy-MM-dd HHmmss}.png",
        };
        if (dialog.ShowDialog() == true)
            File.Copy(item.ImagePath, dialog.FileName, overwrite: true);
    }

    /// <summary>Keeps pinned and favorites.</summary>
    [RelayCommand]
    private void ClearAll()
    {
        _repository.ClearAll();
        LoadFirstPage();
    }

    [RelayCommand]
    private void SetDateFilter(DateFilterPreset preset) => DateFilter = preset;

    [RelayCommand]
    private void ClearDateFilter() => DateFilter = DateFilterPreset.All;

    /// <summary>Ctrl+Tab / Ctrl+Shift+Tab to move between tabs.</summary>
    public void CycleTab(int direction)
    {
        var values = Enum.GetValues<HistoryTab>();
        var index = ((int)SelectedTab + direction + values.Length) % values.Length;
        SelectedTab = values[index];
    }
}
