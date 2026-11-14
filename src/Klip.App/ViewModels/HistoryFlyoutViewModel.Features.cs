using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Klip.App.Services;
using Klip.Core.Clipboard;
using Klip.Core.Snippets;
using Klip.Core.Storage;

namespace Klip.App.ViewModels;

// preview, snippets, collect mode, paste together, pause banner and the item extras
public sealed partial class HistoryFlyoutViewModel
{
    private const int SnippetSearchLimit = 100;
    private const int SnippetNameChars = 40;

    private void InitFeatures()
    {
        _pause.Changed += () => _dispatcher.BeginInvoke(RefreshPauseState);
        // a secret that ran out of time is gone from the db; take its card off an open list too
        _ingest.Secrets.Expired += id => _dispatcher.BeginInvoke(() => OnSecretExpired(id));
        RefreshPauseState();
    }

    /// <summary>The "nothing here" text only belongs to the history tabs, and not under the preview.</summary>
    public bool ShowEmptyHistory => IsEmpty && SelectedTab is not (HistoryTab.Snippets or HistoryTab.Emoji) && !IsPreviewOpen;

    partial void OnIsEmptyChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyHistory));

    partial void OnIsPreviewOpenChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyHistory));

    /// <summary>Settings window requested from the header menu.</summary>
    public event Action? OpenSettingsRequested;

    public void OpenSettings()
    {
        CloseRequested?.Invoke(true);
        OpenSettingsRequested?.Invoke();
    }

    // ----- Status line -----

    [ObservableProperty]
    private string _statusText = "";

    private int _statusGeneration;

    /// <summary>A short message in the footer that clears itself.</summary>
    public async Task ShowStatusAsync(string text)
    {
        var generation = ++_statusGeneration;
        StatusText = text;
        await Task.Delay(2500);
        if (generation == _statusGeneration)
            StatusText = "";
    }

    // ----- Pause banner -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPauseBanner))]
    private bool _isHistoryPaused;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPauseBanner))]
    private bool _isIgnoringNext;

    [ObservableProperty]
    private string _pauseBannerText = "";

    public bool HasPauseBanner => IsHistoryPaused || IsIgnoringNext;

    public bool IsPausedNow => _pause.IsPaused;
    public bool IsIgnoringNextNow => _pause.IsIgnoringNext;

    private void RefreshPauseState()
    {
        IsHistoryPaused = _pause.IsPaused;
        IsIgnoringNext = _pause.IsIgnoringNext;
        PauseBannerText = _pause.IsPaused
            ? _pause.PausedUntil is { } until
                ? string.Format(Localization.Loc.PausedUntil, until.ToLocalTime().ToString("t"))
                : Localization.Loc.PausedIndefinitely
            : _pause.IsIgnoringNext ? Localization.Loc.IgnoringNext : "";
    }

    public void PauseHistory(TimeSpan? duration) => _pause.Pause(duration);

    [RelayCommand]
    private void ResumeHistory()
    {
        if (_pause.IsPaused)
            _pause.Resume();
        else
            _pause.CancelIgnoreNext();
    }

    public void IgnoreNextCopy()
    {
        if (_pause.IsIgnoringNext)
            _pause.CancelIgnoreNext();
        else
            _pause.IgnoreNext();
    }

    // ----- Secrets that expired -----

    private void OnSecretExpired(long id)
    {
        lock (_collectLock)
            _collected.RemoveAll(c => c.Id == id);
        CollectedCount = CurrentCollectedCount();
        if (_previewItem?.Id == id)
            ClosePreview();
        foreach (var vm in Items)
        {
            if (vm.Id == id)
            {
                RemoveFromList(vm);
                break;
            }
        }
    }

    // ----- Collect mode -----

    private readonly List<ClipboardItem> _collected = [];
    private readonly Lock _collectLock = new();

    [ObservableProperty]
    private bool _isCollectMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CollectBannerText))]
    private int _collectedCount;

    public string CollectBannerText => string.Format(Localization.Loc.CollectBanner, CollectedCount);

    private int CurrentCollectedCount()
    {
        lock (_collectLock)
            return _collected.Count;
    }

    public void ToggleCollectMode()
    {
        if (IsCollectMode)
            ExitCollect();
        else
            EnterCollect();
    }

    private void EnterCollect()
    {
        lock (_collectLock)
            _collected.Clear();
        CollectedCount = 0;
        IsCollectMode = true;
        // the subscription only lives while the mode does
        _ingest.ItemIngested += OnCollectIngested;
    }

    [RelayCommand]
    private void ExitCollect()
    {
        _ingest.ItemIngested -= OnCollectIngested;
        lock (_collectLock)
            _collected.Clear();
        CollectedCount = 0;
        IsCollectMode = false;
    }

    // pool thread, straight from the ingest
    private void OnCollectIngested(ClipboardItem item)
    {
        bool added;
        int count;
        lock (_collectLock)
        {
            added = PasteJoin.AddCollected(_collected, item, static (a, b) => a.ContentHash == b.ContentHash);
            count = _collected.Count;
        }
        if (added)
            _dispatcher.BeginInvoke(() => CollectedCount = count);
    }

    [RelayCommand]
    private void CollectClear()
    {
        lock (_collectLock)
            _collected.Clear();
        CollectedCount = 0;
    }

    [RelayCommand]
    private void CollectPasteAll()
    {
        List<ClipboardItem> snapshot;
        lock (_collectLock)
            snapshot = [.. _collected];
        if (snapshot.Count == 0)
            return;
        ExitCollect();
        PasteMany(snapshot);
    }

    // text joins into one paste (the flow handles focus itself), anything else goes through
    // the sequential queue, which wants the target focused first
    private void PasteMany(IReadOnlyList<ClipboardItem> items)
    {
        CloseRequested?.Invoke(!PasteJoin.CanJoinAsText(items));
        _pasteService.PasteTogether(items);
    }

    // ----- Pick: paste together (Shift+click) or in sequence (Ctrl+click) -----

    private readonly PickSelection<long> _pick = new();

    // the summary row of every picked card, so the pick outlives a list reload
    private readonly Dictionary<long, ClipboardItem> _pickRows = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPick))]
    [NotifyPropertyChangedFor(nameof(IsJoinPick))]
    [NotifyPropertyChangedFor(nameof(PickTitle))]
    [NotifyPropertyChangedFor(nameof(PickCountText))]
    [NotifyPropertyChangedFor(nameof(PickHint))]
    private PickMode _pickMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPick))]
    [NotifyPropertyChangedFor(nameof(PickCountText))]
    private int _pickCount;

    public bool HasPick => PickCount > 0;

    /// <summary>Join shows the link glyph in the footer, sequence the ticket.</summary>
    public bool IsJoinPick => PickMode == PickMode.Join;

    public string PickTitle => IsJoinPick ? Localization.Loc.PickJoinTitle : Localization.Loc.PickSequenceTitle;

    public string PickCountText => IsJoinPick
        ? (PickCount == 1 ? Localization.Loc.PickCountOne : string.Format(Localization.Loc.PickCountMany, PickCount))
        : string.Format(Localization.Loc.PickSequenceCount, PickCount);

    public string PickHint => IsJoinPick ? Localization.Loc.PickJoinHint : Localization.Loc.PickSequenceHint;

    /// <summary>
    /// A join with images or files can't sit on the clipboard as one thing: the panel has
    /// to catch the Ctrl+V and paste the steps itself.
    /// </summary>
    public bool PickCatchesCtrlV => IsJoinPick && !PasteJoin.CanJoinAsText(PickedRows());

    /// <summary>The pick changed (the window re-reads which keys its hook swallows).</summary>
    public event Action? PickChanged;

    /// <summary>Shift+click (join) or Ctrl+click (sequence). The other gesture converts the pick.</summary>
    public void Pick(HistoryItemViewModel item, PickMode mode)
    {
        switch (_pick.Toggle(item.Id, mode))
        {
            case PickChange.Added:
                _pickRows[item.Id] = item.Item;
                break;
            case PickChange.Removed:
                _pickRows.Remove(item.Id);
                break;
            case PickChange.Rejected:
                _ = ShowStatusAsync(string.Format(Localization.Loc.PickLimit, _pick.Max));
                break;
        }
        ApplyPick();
    }

    /// <summary>A plain click while something is picked adds to (or drops from) the pick.</summary>
    public void PickInCurrentMode(HistoryItemViewModel item) => Pick(item, _pick.Mode);

    /// <summary>Shift+arrows walk over cards and join them.</summary>
    public void IncludeInJoin(HistoryItemViewModel item)
    {
        if (_pick.Include(item.Id, PickMode.Join) == PickChange.Rejected)
        {
            _ = ShowStatusAsync(string.Format(Localization.Loc.PickLimit, _pick.Max));
            return;
        }
        _pickRows[item.Id] = item.Item;
        ApplyPick();
    }

    /// <summary>Esc: drop the pick and put the clipboard from before it back.</summary>
    public void ClearPick()
    {
        if (_pick.IsEmpty)
            return;
        ResetPick();
        _pickPaste.Clear(restore: true);
    }

    /// <summary>
    /// The panel is closing with a pick that was not cancelled (click outside, or the
    /// user's Ctrl+V already went through): the clipboard stays ready.
    /// </summary>
    public void CommitPick()
    {
        if (_pick.IsEmpty)
            return;
        var (mode, rows) = (_pick.Mode, PickedRows());
        ResetPick();
        _pickPaste.Commit(mode, rows);
    }

    /// <summary>Enter, or a Ctrl+V the panel caught: paste the pick now and close.</summary>
    public void PastePickNow()
    {
        if (_pick.IsEmpty)
            return;
        var (mode, rows) = (_pick.Mode, PickedRows());
        ResetPick();
        CloseRequested?.Invoke(false); // the paste flow handles focus and Ctrl+V
        _pickPaste.PasteNow(mode, rows);
    }

    private List<ClipboardItem> PickedRows() => _pick.Keys.Select(id => _pickRows[id]).ToList();

    private void ApplyPick()
    {
        RefreshPickBadges();
        _pickPaste.Update(_pick.Mode, PickedRows());
        PickChanged?.Invoke();
    }

    private void ResetPick()
    {
        _pick.Clear();
        _pickRows.Clear();
        RefreshPickBadges();
        PickChanged?.Invoke();
    }

    private void RefreshPickBadges()
    {
        foreach (var vm in Items)
            vm.SetPick(_pick.Mode, _pick.OrderOf(vm.Id));
        PickMode = _pick.Mode;
        PickCount = _pick.Count;
        OnPropertyChanged(nameof(PickCatchesCtrlV));
    }

    // ----- Preview -----

    [ObservableProperty]
    private bool _isPreviewOpen;

    [ObservableProperty]
    private bool _previewLoading;

    [ObservableProperty]
    private string _previewTitle = "";

    [ObservableProperty]
    private string _previewBody = "";

    [ObservableProperty]
    private string _previewMeta = "";

    [ObservableProperty]
    private string _previewNote = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewImage))]
    private ImageSource? _previewImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewOcr))]
    private string _previewOcr = "";

    public bool HasPreviewImage => PreviewImage is not null;
    public bool HasPreviewOcr => PreviewOcr.Length > 0;
    public bool HasPreviewNote => PreviewNote.Length > 0;

    partial void OnPreviewNoteChanged(string value) => OnPropertyChanged(nameof(HasPreviewNote));

    private HistoryItemViewModel? _previewItem;
    private int _previewGeneration;

    private sealed record PreviewData(string Body, bool Truncated, ImageSource? Image, string Ocr, ClipboardItem Full);

    public void TogglePreview(HistoryItemViewModel? item)
    {
        if (IsPreviewOpen)
            ClosePreview();
        else
            _ = OpenPreviewAsync(item);
    }

    /// <summary>Opens the panel right away from the slim row, then fills it with the full content.</summary>
    public async Task OpenPreviewAsync(HistoryItemViewModel? item)
    {
        if (item is null)
            return;
        var generation = ++_previewGeneration;
        _previewItem = item;
        PreviewTitle = item.Item.Type switch
        {
            ClipboardItemType.Image => Localization.Loc.ItemImage,
            ClipboardItemType.Files => Localization.Loc.TabFiles,
            _ => Localization.Loc.TabText,
        };
        PreviewBody = "";
        PreviewImage = null;
        PreviewOcr = "";
        PreviewNote = "";
        PreviewMeta = BuildMeta(item.Item, null);
        PreviewLoading = true;
        IsPreviewOpen = true;

        try
        {
            var data = await Task.Run(() => LoadPreviewData(item));
            if (generation != _previewGeneration)
                return; // closed or replaced meanwhile
            PreviewBody = data.Body;
            PreviewImage = data.Image;
            PreviewOcr = data.Ocr;
            PreviewNote = data.Truncated ? string.Format(Localization.Loc.PreviewTruncated, PreviewFormat.MaxPreviewChars) : "";
            PreviewMeta = BuildMeta(data.Full, data.Full.TextContent?.Length);
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("Preview", ex);
        }
        finally
        {
            if (generation == _previewGeneration)
                PreviewLoading = false;
        }
    }

    public void ClosePreview()
    {
        _previewGeneration++;
        _previewItem = null;
        if (!IsPreviewOpen)
            return;
        IsPreviewOpen = false;
        PreviewLoading = false;
        PreviewImage = null;
        PreviewBody = "";
        PreviewOcr = "";
        PreviewNote = "";
    }

    // pool thread: the full row (text, ocr) and the full size image come from here, never from the list
    private PreviewData LoadPreviewData(HistoryItemViewModel vm)
    {
        var full = _repository.GetById(vm.Id) ?? vm.Item;
        var body = "";
        var truncated = false;
        ImageSource? image = null;

        switch (full.Type)
        {
            case ClipboardItemType.Image:
                if (vm.ImagePath is { } path && File.Exists(path))
                    image = DecodeFullImage(path);
                break;
            case ClipboardItemType.Files:
                body = DescribeFiles(full.FilesJson);
                break;
            default:
                // a masked secret stays masked here too
                body = vm.IsSecret
                    ? vm.Item.Preview ?? ""
                    : PreviewFormat.Clip(full.TextContent, PreviewFormat.MaxPreviewChars, out truncated);
                break;
        }
        return new PreviewData(body, truncated, image, full.OcrText?.Trim() ?? "", full);
    }

    private static BitmapImage DecodeFullImage(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad; // releases the file right away
        bitmap.UriSource = new Uri(path);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static string DescribeFiles(string? json)
    {
        if (json is null)
            return "";
        try
        {
            var paths = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? [];
            var lines = new List<string>(paths.Count);
            foreach (var p in paths)
            {
                lines.Add(File.Exists(p)
                    ? $"{p}  ({PreviewFormat.HumanSize(new FileInfo(p).Length)})"
                    : p);
            }
            return string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            return "";
        }
    }

    private static string BuildMeta(ClipboardItem item, int? chars)
    {
        var parts = new List<string>(5);
        if (!string.IsNullOrEmpty(item.SourceApp))
            parts.Add(item.SourceApp);
        parts.Add(item.LastCopiedAt.ToLocalTime().ToString("g"));
        if (item.ByteSize > 0)
            parts.Add(PreviewFormat.HumanSize(item.ByteSize));
        if (item.Width is { } w && item.Height is { } h)
            parts.Add($"{w} x {h}");
        else if (chars is { } n && n > 0 && item.Type is ClipboardItemType.Text or ClipboardItemType.Html)
            parts.Add(string.Format(Localization.Loc.PreviewChars, n));
        return string.Join("  ·  ", parts);
    }

    [RelayCommand]
    private void CopyPreviewOcr()
    {
        if (!HasPreviewOcr)
            return;
        _pasteService.CopyText(PreviewOcr);
        _ = ShowStatusAsync(Localization.Loc.FlyoutOcrCopied);
    }

    // ----- Item extras -----

    /// <summary>"Copy text from image" in the card menu: stored OCR text, else a read on demand.</summary>
    public async Task CopyImageTextAsync(HistoryItemViewModel? item)
    {
        if (item?.IsImage != true || item.ImagePath is not { } path)
            return;
        var id = item.Id;
        _ = ShowStatusAsync(Localization.Loc.FlyoutOcrWorking);
        try
        {
            var text = await Task.Run(async () =>
            {
                var stored = _repository.GetById(id)?.OcrText;
                if (!string.IsNullOrWhiteSpace(stored))
                    return stored;
                if (!_ocr.IsAvailable)
                    return "";
                var read = await _ocr.ReadTextAsync(await File.ReadAllBytesAsync(path)).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(read))
                {
                    try
                    {
                        _repository.UpdateOcrText(id, read);
                    }
                    catch (Exception ex)
                    {
                        StartupLog.WriteException("OcrSave", ex); // the copy still goes ahead
                    }
                }
                return read;
            });
            if (string.IsNullOrWhiteSpace(text))
            {
                _ = ShowStatusAsync(Localization.Loc.FlyoutOcrNothing);
                return;
            }
            _pasteService.CopyText(text.Trim());
            _ = ShowStatusAsync(Localization.Loc.FlyoutOcrCopied);
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("OcrCopy", ex);
            _ = ShowStatusAsync(Localization.Loc.FlyoutOcrNothing);
        }
    }

    /// <summary>Image on top of every window (the pin service owns the window).</summary>
    public async Task PinOnScreenAsync(HistoryItemViewModel? item)
    {
        if (item?.IsImage != true || item.ImagePath is not { } path)
            return;
        CloseRequested?.Invoke(true);
        try
        {
            var bitmap = await Task.Run(() => ClipboardWriteGuard.DecodeImageFile(path).bitmap);
            _pins.Pin(bitmap);
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("PinFromHistory", ex);
        }
    }

    /// <summary>Complete row for a drag out of the flyout (the list only has slim rows).</summary>
    public ClipboardItem LoadFullItem(HistoryItemViewModel item) => _repository.LoadFull(item.Item);

    // the card goes away from the list (no db write; callers do that when needed)
    private void RemoveFromList(HistoryItemViewModel item)
    {
        var index = Items.IndexOf(item);
        if (index < 0)
            return;
        // a deleted card leaves the pick too
        if (_pick.OrderOf(item.Id) > 0)
            Pick(item, _pick.Mode);
        // the section label moves down to the next card of the same section
        if (item.HeaderText is { } header && index + 1 < Items.Count &&
            Items[index + 1].HeaderText is null && Items[index + 1].Item.Pinned == item.Item.Pinned)
        {
            Items[index + 1].HeaderText = header;
        }
        if (item.Item.Pinned && _pinnedCount > 0 && index < _pinnedCount)
            _pinnedCount--;
        Items.RemoveAt(index);
        IsEmpty = Items.Count == 0;
    }

    // ----- Alias hit (search matches a snippet alias) -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAliasHit))]
    private SnippetViewModel? _aliasHit;

    public bool HasAliasHit => AliasHit is not null;

    // pool thread friendly: alias must match the typed text exactly (case-insensitive)
    private Snippet? FindAliasHit(string? text)
    {
        if (!_settings.Current.ShowSnippetsTab || SelectedTab == HistoryTab.Snippets)
            return null;
        var typed = text?.Trim();
        if (typed is null || typed.Length < 2)
            return null;
        return _snippets.FindByAliasPrefix(typed, 5)
            .FirstOrDefault(s => string.Equals(s.Alias, typed, StringComparison.OrdinalIgnoreCase));
    }

    private void SetAliasHit(Snippet? snippet) =>
        AliasHit = snippet is null ? null : new SnippetViewModel(snippet);

    // ----- Snippets -----

    public BulkObservableCollection<SnippetViewModel> Snippets { get; } = [];

    [ObservableProperty]
    private bool _isSnippetsEmpty = true;

    /// <summary>The view selects the first snippet after every reload.</summary>
    public event Action? SnippetsReloaded;

    private long _snippetsLoadedVersion = -1;
    private bool _snippetsDirty = true;
    private CancellationTokenSource? _snippetsCts;
    private int _snippetsGeneration;

    /// <summary>True when the snippets on screen no longer match the db (or the search text changed).</summary>
    public bool SnippetsStale => _snippetsDirty || _snippetsLoadedVersion != _snippets.DataVersion;

    public async Task ReloadSnippetsAsync()
    {
        _snippetsCts?.Cancel();
        var cts = new CancellationTokenSource();
        _snippetsCts = cts;
        var generation = ++_snippetsGeneration;
        var query = SearchText;
        var version = _snippets.DataVersion;
        try
        {
            var rows = await Task.Run(() => _snippets.Search(query, SnippetSearchLimit, cts.Token), cts.Token);
            if (generation != _snippetsGeneration)
                return;
            Snippets.Reset(rows.Select(s => new SnippetViewModel(s)));
            IsSnippetsEmpty = Snippets.Count == 0;
            _snippetsLoadedVersion = version;
            _snippetsDirty = false;
            SnippetsReloaded?.Invoke();
        }
        catch (OperationCanceledException)
        {
            // a newer search took over
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("Snippets reload", ex);
        }
    }

    /// <summary>Marks the snippet list as stale (the search box was cleared while hidden).</summary>
    public void MarkSnippetsDirty() => _snippetsDirty = true;

    // editor state (inline panel on the snippets tab)

    [ObservableProperty]
    private bool _isSnippetEditing;

    [ObservableProperty]
    private string _snippetName = "";

    [ObservableProperty]
    private string _snippetAlias = "";

    [ObservableProperty]
    private string _snippetContent = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSnippetError))]
    private string _snippetError = "";

    public bool HasSnippetError => SnippetError.Length > 0;

    [ObservableProperty]
    private bool _canDeleteSnippet;

    private long _snippetEditId;

    [RelayCommand]
    private void NewSnippet() => OpenSnippetEditor(0, "", "", "");

    [RelayCommand]
    private void EditSnippet(SnippetViewModel? snippet)
    {
        if (snippet is null)
            return;
        OpenSnippetEditor(snippet.Id, snippet.Name, snippet.Alias ?? "", snippet.Snippet.Content);
    }

    private void OpenSnippetEditor(long id, string name, string alias, string content)
    {
        _snippetEditId = id;
        SnippetName = name;
        SnippetAlias = alias;
        SnippetContent = content;
        SnippetError = "";
        CanDeleteSnippet = id != 0;
        IsSnippetEditing = true;
    }

    /// <summary>"Save as snippet" from a text card: jumps to the snippets tab with the text in the editor.</summary>
    public async Task SaveAsSnippetAsync(HistoryItemViewModel? item)
    {
        if (item is null || item.IsSecret || !_settings.Current.ShowSnippetsTab ||
            item.Item.Type is not (ClipboardItemType.Text or ClipboardItemType.Html))
            return;
        var id = item.Id;
        var text = await Task.Run(() => _repository.GetById(id)?.TextContent);
        if (string.IsNullOrWhiteSpace(text))
            return;
        SelectedTab = HistoryTab.Snippets;
        OpenSnippetEditor(0, NameFromContent(text), "", text);
    }

    private static string NameFromContent(string content)
    {
        var line = content.ReplaceLineEndings("\n").Split('\n')
            .Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return line.Length > SnippetNameChars ? line[..SnippetNameChars].TrimEnd() : line;
    }

    [RelayCommand]
    private void CancelSnippetEdit()
    {
        IsSnippetEditing = false;
        SnippetError = "";
    }

    [RelayCommand]
    private async Task SaveSnippet()
    {
        var content = SnippetContent;
        if (string.IsNullOrWhiteSpace(content))
        {
            SnippetError = Localization.Loc.SnippetNeedsContent;
            return;
        }
        var name = SnippetName.Trim();
        if (name.Length == 0)
            name = NameFromContent(content);
        var alias = SnippetRepository.NormalizeAlias(SnippetAlias);
        var id = _snippetEditId;

        try
        {
            var saved = await Task.Run(() =>
            {
                if (alias is not null)
                {
                    var clash = _snippets.FindByAliasPrefix(alias, 20)
                        .Any(s => s.Id != id && string.Equals(s.Alias, alias, StringComparison.OrdinalIgnoreCase));
                    if (clash)
                        return false;
                }
                _snippets.Save(new Snippet { Id = id, Name = name, Alias = alias, Content = content });
                return true;
            });
            if (!saved)
            {
                SnippetError = Localization.Loc.SnippetAliasTaken;
                return;
            }
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("Snippet save", ex);
            SnippetError = Localization.Loc.SnippetSaveFailed;
            return;
        }
        IsSnippetEditing = false;
        SnippetError = "";
        await ReloadSnippetsAsync();
    }

    [RelayCommand]
    private async Task DeleteSnippet(SnippetViewModel? snippet)
    {
        // from the editor the id is the one being edited
        var id = snippet?.Id ?? _snippetEditId;
        if (id == 0)
            return;
        IsSnippetEditing = false;
        try
        {
            await Task.Run(() => _snippets.Delete(id));
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("Snippet delete", ex);
        }
        await ReloadSnippetsAsync();
    }

    [RelayCommand]
    private void PasteSnippet(SnippetViewModel? snippet)
    {
        if (snippet is null)
            return;
        CloseRequested?.Invoke(false); // paste flow handles focus + Ctrl+V itself
        _pasteService.PasteSnippet(snippet.Snippet.Content);
    }

    [RelayCommand]
    private void CopySnippet(SnippetViewModel? snippet)
    {
        if (snippet is null)
            return;
        _pasteService.CopySnippet(snippet.Snippet.Content);
        _ = ShowStatusAsync(Localization.Loc.SnippetCopied);
    }

    /// <summary>Enter: the pick, else the snippet the search typed (alias), else the selected card.</summary>
    public void ChooseCurrent(HistoryItemViewModel? selected)
    {
        if (HasPick)
            PastePickNow();
        else if (AliasHit is { } hit)
            PasteSnippetCommand.Execute(hit);
        else
            ChooseCommand.Execute(selected);
    }
}
