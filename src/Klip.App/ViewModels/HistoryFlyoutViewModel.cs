using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Klip.App.Services;
using Klip.Core.Clipboard;
using Klip.Core.Settings;
using Klip.Core.Snippets;
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
    Snippets,
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
    public const int FirstPageSize = 30;
    private const int PageSize = 100;
    private const int SearchLimit = 100;

    // pinned items are loaded all at once, apart from the recents; this is only a sanity cap
    private const int MaxPinned = 1_000;

    // relative times on the cards ("5 min") go stale, so even with no data change a
    // reopen after this long rebuilds the first page
    private static readonly TimeSpan MaxListAge = TimeSpan.FromMinutes(1);

    private readonly ClipboardItemRepository _repository;
    private readonly MediaStore _mediaStore;
    private readonly PasteService _pasteService;
    private readonly SettingsService _settings;
    private readonly PasteQueueService _pasteQueue;
    private readonly SnippetRepository _snippets;
    private readonly HistoryPause _pause;
    private readonly ClipboardIngestService _ingest;
    private readonly OcrService _ocr;
    private readonly PinnedImageService _pins;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private CancellationTokenSource? _searchDebounce;
    private CancellationTokenSource? _reloadCts;

    // bumps on every reload; an async result from an older generation is dropped
    private int _generation;

    // keyset cursor: last unpinned row loaded
    private long? _cursorMs;
    private long? _cursorId;
    private int _pinnedCount;
    private bool _allLoaded;
    private bool _loadingMore;

    // what the current list was built from
    private long _loadedVersion = -1;
    private DateTime _loadedAtUtc;
    private bool _listDirty = true;
    private bool _suppressSearchReload;

    // db writes from the flyout (pin, favorite, delete) run in order, off the UI thread
    private Task _writeChain = Task.CompletedTask;

    public HistoryFlyoutViewModel(
        ClipboardItemRepository repository,
        MediaStore mediaStore,
        PasteService pasteService,
        PasteQueueService pasteQueue,
        SettingsService settings,
        SnippetRepository snippets,
        HistoryPause pause,
        ClipboardIngestService ingest,
        OcrService ocr,
        PinnedImageService pins)
    {
        _repository = repository;
        _mediaStore = mediaStore;
        _pasteService = pasteService;
        _pasteQueue = pasteQueue;
        _settings = settings;
        _snippets = snippets;
        _pause = pause;
        _ingest = ingest;
        _ocr = ocr;
        _pins = pins;
        _dispatcher = System.Windows.Application.Current.Dispatcher;
        InitFeatures();
    }

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

    private bool HasSearch => !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>Pinned/Recent headers only make sense on the Recent tab with no search.</summary>
    private bool ShowSectionHeaders => SelectedTab == HistoryTab.Recent && !HasSearch;

    /// <summary>Multi-select mode is on plus how many are picked.</summary>
    [ObservableProperty]
    private bool _isMultiSelectMode;

    [ObservableProperty]
    private int _multiSelectTarget;

    /// <summary>Reads like "1 / 3" while picking items.</summary>
    public string MultiSelectStatus => $"{_selection.Count} / {MultiSelectTarget}";

    partial void OnIsMultiSelectModeChanged(bool value) => OnPropertyChanged(nameof(MultiSelectStatus));
    partial void OnMultiSelectTargetChanged(int value) => OnPropertyChanged(nameof(MultiSelectStatus));

    /// <summary>
    /// Raised to close the window. The bool says whether HideFlyout should restore
    /// focus to the target app: false when we're about to paste (the paste flow
    /// handles focus + Ctrl+V itself, so a second restore would race it).
    /// </summary>
    public event Action<bool>? CloseRequested;

    /// <summary>Open an image in the editor (window is created by the App).</summary>
    public event Action<ClipboardItem>? OpenInEditorRequested;

    /// <summary>Tells the view the tab changed so it can sync the tab strip and the emoji panel.</summary>
    public event Action? GroupingChanged;

    /// <summary>
    /// The list was rebuilt from scratch (tab, filter, search, reopen). The view puts the
    /// scroll back at the top and selects the first item, so Enter never pastes something
    /// that is off screen.
    /// </summary>
    public event Action? ListReloaded;

    /// <summary>
    /// True when the list on screen no longer matches the db (or was trimmed for a search,
    /// or is old enough that the relative times are wrong). Opening the flyout with a fresh
    /// list skips the query entirely.
    /// </summary>
    public bool IsStale =>
        _listDirty ||
        _loadedVersion != _repository.DataVersion ||
        DateTime.UtcNow - _loadedAtUtc > MaxListAge;

    [RelayCommand]
    private void OpenInEditor(HistoryItemViewModel? item)
    {
        if (item?.IsImage != true)
            return;
        CloseRequested?.Invoke(true);
        OpenInEditorRequested?.Invoke(_repository.LoadFull(item.Item));
    }

    partial void OnSelectedTabChanged(HistoryTab value)
    {
        // emoji tab shows the picker, not a history query, so skip the reload
        // but still tell the view so it can swap the panel and the tab strip
        ClosePreview();
        OnPropertyChanged(nameof(ShowEmptyHistory));
        SetAliasHit(null);
        if (value != HistoryTab.Emoji)
            _ = ReloadAsync();
        GroupingChanged?.Invoke();
    }

    partial void OnDateFilterChanged(DateFilterPreset value)
    {
        OnPropertyChanged(nameof(HasDateFilter));
        OnPropertyChanged(nameof(DateFilterLabel));
        _ = ReloadAsync();
    }

    /// <summary>Search as you type, debounced; the query itself runs off the UI thread.</summary>
    partial void OnSearchTextChanged(string value)
    {
        _searchDebounce?.Cancel();
        if (_suppressSearchReload)
            return;
        var cts = new CancellationTokenSource();
        _searchDebounce = cts;
        _ = DebouncedReloadAsync(cts.Token);
    }

    private async Task DebouncedReloadAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(150, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (!token.IsCancellationRequested)
            await ReloadAsync();
    }

    /// <summary>
    /// Clears the search box text without reloading (used when the flyout hides). The
    /// list is marked dirty so the next open rebuilds it.
    /// </summary>
    public void ClearSearchSilently()
    {
        _searchDebounce?.Cancel();
        if (SearchText.Length == 0)
            return;
        _suppressSearchReload = true;
        try
        {
            SearchText = "";
        }
        finally
        {
            _suppressSearchReload = false;
        }
        _listDirty = true;
        MarkSnippetsDirty();
    }

    // ----- Loading -----

    private HistoryQuery BaseQuery(bool? pinned, int limit, bool withSearch)
    {
        var (fromMs, toMs) = ComputeDateRange();
        return new HistoryQuery
        {
            Type = SelectedTab switch
            {
                HistoryTab.Images => ClipboardItemType.Image,
                HistoryTab.Text => ClipboardItemType.Text,
                HistoryTab.Files => ClipboardItemType.Files,
                _ => null,
            },
            OnlyFavorites = SelectedTab == HistoryTab.Favorites,
            SearchText = withSearch ? SearchText : null,
            DateFromMs = fromMs,
            DateToMs = toMs,
            Pinned = pinned,
            Limit = limit,
        };
    }

    private sealed record FirstPage(IReadOnlyList<ClipboardItem> Pinned, IReadOnlyList<ClipboardItem> Recent,
        bool Search, bool Headers, long Version, Snippet? AliasHit = null);

    // pure db work, safe on any thread
    private FirstPage QueryFirstPage(HistoryQuery? search, HistoryQuery pinned, HistoryQuery recent,
        bool headers, CancellationToken token)
    {
        // read the version BEFORE querying: a write landing in between makes the list
        // look stale (one extra reload), never the other way around
        var version = _repository.DataVersion;
        if (search is not null)
            return new FirstPage([], _repository.QuerySummaries(search, token), true, false, version,
                FindAliasHit(search.SearchText));
        var p = _repository.QuerySummaries(pinned, token);
        var r = _repository.QuerySummaries(recent, token);
        return new FirstPage(p, r, false, headers, version);
    }

    private (HistoryQuery? search, HistoryQuery pinned, HistoryQuery recent) BuildFirstPageQueries() =>
        HasSearch
            ? (BaseQuery(null, SearchLimit, withSearch: true), null!, null!)
            : (null, BaseQuery(true, MaxPinned, false), BaseQuery(false, FirstPageSize, false));

    /// <summary>
    /// Loads the first page right here (pinned + first recents, ~1 ms with slim rows).
    /// Used when the flyout opens, where the first frame has to show real content.
    /// </summary>
    public void LoadFirstPage()
    {
        CancelPendingReload();
        var generation = ++_generation;
        var (search, pinned, recent) = BuildFirstPageQueries();
        var page = QueryFirstPage(search, pinned, recent, ShowSectionHeaders, CancellationToken.None);
        ApplyFirstPage(page, generation);
    }

    /// <summary>Same as <see cref="LoadFirstPage"/> but the query runs on a pool thread (tab, filter, search).</summary>
    public async Task ReloadAsync()
    {
        if (SelectedTab == HistoryTab.Snippets)
        {
            await ReloadSnippetsAsync();
            return;
        }
        CancelPendingReload();
        var cts = new CancellationTokenSource();
        _reloadCts = cts;
        var generation = ++_generation;
        var (search, pinned, recent) = BuildFirstPageQueries();
        var headers = ShowSectionHeaders;
        try
        {
            var page = await Task.Run(() => QueryFirstPage(search, pinned, recent, headers, cts.Token), cts.Token);
            ApplyFirstPage(page, generation);
        }
        catch (OperationCanceledException)
        {
            // a newer reload took over
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("History reload", ex);
        }
    }

    private void CancelPendingReload()
    {
        _reloadCts?.Cancel();
        _reloadCts = null;
    }

    private void ApplyFirstPage(FirstPage page, int generation)
    {
        if (generation != _generation)
            return; // something newer already replaced the list

        // when reopening or filtering, drop any pending selection (badges go stale)
        if (!IsMultiSelectMode)
            _selection.Reset();
        ClearChecks();
        SetAliasHit(page.AliasHit);

        var vms = new List<HistoryItemViewModel>(page.Pinned.Count + page.Recent.Count);
        AddViewModels(vms, page.Pinned, page.Headers ? Localization.Loc.GroupPinned : null);
        AddViewModels(vms, page.Recent, page.Headers ? Localization.Loc.GroupRecent : null);

        _pinnedCount = page.Pinned.Count;
        _allLoaded = page.Search || page.Recent.Count < FirstPageSize;
        SetCursor(page.Recent.Count > 0 ? page.Recent[^1] : null);
        _loadedVersion = page.Version;
        _loadedAtUtc = DateTime.UtcNow;
        _listDirty = false;

        Items.Reset(vms);
        IsEmpty = Items.Count == 0;
        ListReloaded?.Invoke();
    }

    private void SetCursor(ClipboardItem? last)
    {
        _cursorMs = last?.LastCopiedAt.ToUnixTimeMilliseconds();
        _cursorId = last?.Id;
    }

    private void AddViewModels(List<HistoryItemViewModel> target, IReadOnlyList<ClipboardItem> rows, string? header)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            var vm = CreateViewModel(rows[i]);
            if (i == 0 && header is not null)
                vm.HeaderText = header;
            target.Add(vm);
        }
    }

    private HistoryItemViewModel CreateViewModel(ClipboardItem item)
    {
        var absImage = item.FilePath is not null ? _mediaStore.ToAbsolute(item.FilePath) : null;
        var absThumb = item.ThumbPath is not null ? _mediaStore.ToAbsolute(item.ThumbPath) : null;
        var vm = new HistoryItemViewModel(item, absImage, absThumb, _ingest.IsSecretItem(item.Id));
        if (IsMultiSelectMode)
            vm.QueueOrder = _selection.OrderOf(vm, ByItemId);
        return vm;
    }

    /// <summary>Incremental keyset paging while scrolling; the query runs on a pool thread.</summary>
    [RelayCommand]
    private async Task LoadMore()
    {
        if (_allLoaded || _loadingMore || HasSearch || _cursorMs is null)
            return;
        _loadingMore = true;
        var generation = _generation;
        var query = NextPageQuery();
        try
        {
            var rows = await Task.Run(() => _repository.QuerySummaries(query));
            if (generation == _generation)
                AppendRecent(rows);
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("History page", ex);
        }
        finally
        {
            _loadingMore = false;
        }
    }

    private HistoryQuery NextPageQuery()
    {
        var q = BaseQuery(false, PageSize, false);
        return new HistoryQuery
        {
            Type = q.Type,
            OnlyFavorites = q.OnlyFavorites,
            DateFromMs = q.DateFromMs,
            DateToMs = q.DateToMs,
            Pinned = false,
            Limit = PageSize,
            BeforeLastCopiedAtMs = _cursorMs,
            BeforeId = _cursorId,
        };
    }

    private void AppendRecent(IReadOnlyList<ClipboardItem> rows)
    {
        if (rows.Count < PageSize)
            _allLoaded = true;
        if (rows.Count == 0)
            return;
        SetCursor(rows[^1]);
        var page = new List<HistoryItemViewModel>(rows.Count);
        foreach (var row in rows)
            page.Add(CreateViewModel(row));
        Items.AppendRange(page);
        IsEmpty = false;
    }

    /// <summary>
    /// Keeps loading pages (synchronously, slim rows) until the item with this id is in
    /// the list or <paramref name="maxItems"/> are loaded. Used to restore the scroll anchor.
    /// </summary>
    public HistoryItemViewModel? LoadUntil(long id, int maxItems)
    {
        while (true)
        {
            foreach (var vm in Items)
            {
                if (vm.Id == id)
                    return vm;
            }
            if (_allLoaded || HasSearch || _cursorMs is null || Items.Count >= maxItems)
                return null;
            AppendRecent(_repository.QuerySummaries(NextPageQuery()));
        }
    }

    /// <summary>
    /// Drops everything past the first page (flyout hidden): with 1k+ items scrolled the
    /// VMs and their thumbnails are memory nobody looks at.
    /// </summary>
    public void TrimToFirstPage()
    {
        var keep = _pinnedCount + FirstPageSize;
        if (HasSearch || Items.Count <= keep)
            return;
        Items.TrimTo(keep);
        // more than a page of recents was loaded, so the last kept row is a recent one
        SetCursor(Items[^1].Item);
        _allLoaded = false;
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

    /// <summary>True when Enter / click should only copy (setting), not paste.</summary>
    public bool EnterCopiesOnly =>
        string.Equals(_settings.Current.FlyoutEnterAction, "copy", StringComparison.OrdinalIgnoreCase);

    /// <summary>Enter or a plain click: paste, or just copy, depending on the setting.</summary>
    [RelayCommand]
    private void Choose(HistoryItemViewModel? item)
    {
        if (item is null)
            return;
        if (IsMultiSelectMode || !EnterCopiesOnly)
        {
            Paste(item);
            return;
        }
        _pasteService.CopyItemToClipboard(item.Item);
        CloseRequested?.Invoke(true);
    }

    /// <summary>Pastes the item into the target app.</summary>
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

        CloseRequested?.Invoke(false); // paste flow handles focus + Ctrl+V itself
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
        // only queued cards carry a badge, so only they (and the clicked one) need refreshing
        foreach (var vm in Items)
        {
            if (vm.QueueOrder > 0 || ReferenceEquals(vm, item))
                vm.QueueOrder = _selection.OrderOf(vm, ByItemId);
        }

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
        // the queue writes the content itself, so it needs the full rows (at most 5, ~0.1 ms each)
        var items = _selection.Snapshot().Select(vm => _repository.LoadFull(vm.Item)).ToList();
        ClearSelectionOrder();
        IsMultiSelectMode = false;
        CloseRequested?.Invoke(true);
        _pasteQueue.Arm(items);
    }

    private void ClearSelectionOrder()
    {
        foreach (var vm in Items)
        {
            if (vm.QueueOrder != 0)
                vm.QueueOrder = 0;
        }
        _selection.Reset();
        OnPropertyChanged(nameof(MultiSelectStatus));
    }

    /// <summary>Shift+Enter: paste as plain text.</summary>
    [RelayCommand]
    private void PastePlain(HistoryItemViewModel? item)
    {
        if (item is null)
            return;
        CloseRequested?.Invoke(false); // paste flow handles focus + Ctrl+V itself
        _pasteService.PasteItem(item.Item, asPlainText: true);
    }

    /// <summary>Alt+Enter: type as keystrokes, for fields that block paste.</summary>
    [RelayCommand]
    private void TypeText(HistoryItemViewModel? item)
    {
        if (item is null || item.IsImage)
            return;
        CloseRequested?.Invoke(false); // the type flow handles focus itself
        _pasteService.TypeItem(item.Item);
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
        CloseRequested?.Invoke(false); // paste flow handles focus + Ctrl+V itself
        _pasteService.PasteText(emoji);
    }

    /// <summary>Brings the app the user was in back to the front (see PasteService).</summary>
    public void RestoreTargetFocus() => _pasteService.RestoreTargetFocus();

    private void RunWrite(Action write)
    {
        _writeChain = _writeChain.ContinueWith(_ =>
        {
            try
            {
                write();
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("History write", ex);
            }
        }, TaskScheduler.Default);
    }

    [RelayCommand]
    private void Delete(HistoryItemViewModel? item)
    {
        if (item is null)
            return;
        var id = item.Id;
        RunWrite(() => _repository.Delete(id));

        RemoveFromList(item);
    }

    [RelayCommand]
    private void TogglePin(HistoryItemViewModel? item)
    {
        if (item is null)
            return;
        item.IsPinned = !item.IsPinned;
        var (id, value) = (item.Id, item.IsPinned);
        RunWrite(() => _repository.SetPinned(id, value));
        // the card stays where it is until the next reload, like before
    }

    [RelayCommand]
    private void ToggleFavorite(HistoryItemViewModel? item)
    {
        if (item is null)
            return;
        item.IsFavorite = !item.IsFavorite;
        var (id, value) = (item.Id, item.IsFavorite);
        RunWrite(() => _repository.SetFavorite(id, value));
        if (SelectedTab == HistoryTab.Favorites && !item.IsFavorite)
        {
            Items.Remove(item);
            IsEmpty = Items.Count == 0;
        }
    }

    /// <summary>Save as... for images (PNG) and text (.txt).</summary>
    [RelayCommand]
    private void SaveAs(HistoryItemViewModel? item)
    {
        if (item is null)
            return;
        var stamp = item.Item.LastCopiedAt.ToLocalTime();
        if (item.ImagePath is not null)
        {
            // the dialog would open behind the topmost flyout, so it goes first
            CloseRequested?.Invoke(true);
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = Localization.Loc.PngFilter,
                FileName = $"Klip {stamp:yyyy-MM-dd HHmmss}.png",
            };
            if (dialog.ShowDialog() == true)
                File.Copy(item.ImagePath, dialog.FileName, overwrite: true);
            return;
        }
        if (item.IsSecret || item.Item.Type is not (ClipboardItemType.Text or ClipboardItemType.Html))
            return;
        var id = item.Id;
        CloseRequested?.Invoke(true);
        var text = _repository.GetById(id)?.TextContent;
        if (string.IsNullOrEmpty(text))
            return;
        var textDialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = Localization.Loc.TextFilter,
            FileName = $"Klip {stamp:yyyy-MM-dd HHmmss}.txt",
        };
        if (textDialog.ShowDialog() == true)
            File.WriteAllText(textDialog.FileName, text, new System.Text.UTF8Encoding(false));
    }

    /// <summary>Keeps pinned and favorites.</summary>
    [RelayCommand]
    private async Task ClearAll()
    {
        RunWrite(() => _repository.ClearAll());
        await _writeChain;
        await ReloadAsync();
    }

    [RelayCommand]
    private void SetDateFilter(DateFilterPreset preset) => DateFilter = preset;

    [RelayCommand]
    private void ClearDateFilter() => DateFilter = DateFilterPreset.All;

    /// <summary>Ctrl+Tab / Ctrl+Shift+Tab to move between tabs.</summary>
    public void CycleTab(int direction)
    {
        var values = Enum.GetValues<HistoryTab>();
        var index = (int)SelectedTab;
        // hidden tabs (emoji, snippets turned off in settings) are skipped
        for (var step = 0; step < values.Length; step++)
        {
            index = (index + direction + values.Length) % values.Length;
            if (IsTabEnabled(values[index]))
            {
                SelectedTab = values[index];
                return;
            }
        }
    }

    /// <summary>False for the tabs the user turned off in settings.</summary>
    public bool IsTabEnabled(HistoryTab tab) => tab switch
    {
        HistoryTab.Emoji => _settings.Current.ShowEmojiTab,
        HistoryTab.Snippets => _settings.Current.ShowSnippetsTab,
        _ => true,
    };
}
