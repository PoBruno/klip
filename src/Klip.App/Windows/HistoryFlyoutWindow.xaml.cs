using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using Klip.App.ViewModels;
using Klip.Core.Clipboard;
using Klip.Interop;
using Klip.Interop.Input;
using Wpf.Ui.Appearance;

namespace Klip.App.Windows;

/// <summary>
/// Win+V style history flyout.
/// Pre-built and hidden; opens next to the text caret (or the mouse), or at the
/// bottom right corner of the monitor, and closes on Esc or when it loses focus.
/// Code-behind is limited to window interop and input routing (MVVM).
/// </summary>
public partial class HistoryFlyoutWindow
{
    private readonly HistoryFlyoutViewModel _viewModel;
    private readonly Klip.Core.Settings.SettingsService _settings;
    private bool _closing;
    private bool _suppressTabEvents;

    // escopos dos hooks de baixo nivel. Ficam nulos enquanto o
    // flyout esta fechado - o hook nao e "desativado", ele e REMOVIDO do sistema. Um
    // WH_KEYBOARD_LL/WH_MOUSE_LL instalado continua sendo chamado pela Raw Input Thread
    // a cada evento do sistema inteiro, mesmo que o callback so retorne.
    // O flyout nunca recebe foco (para o app de baixo manter o caret), entao a navegacao
    // por teclado e o clique fora so chegam por esses hooks.
    private IDisposable? _keyboardScope;
    private IDisposable? _mouseScope;

    // true while we're warming the window up off-screen at startup, so the
    // size-changed handler ignores those layout passes
    private bool _warmingUp;

    // set once the warm-up has run, so we don't pay it twice
    private bool _warmedUp;

    public HistoryFlyoutWindow(HistoryFlyoutViewModel viewModel, Klip.Core.Settings.SettingsService settings)
    {
        _viewModel = viewModel;
        _settings = settings;
        DataContext = viewModel;
        Resources["BoolToVisibility"] = new BooleanToVisibilityConverter();
        Resources["InverseBoolToVisibility"] = new InverseBooleanToVisibilityConverter();
        InitializeComponent();
        SystemThemeWatcher.Watch(this);

        ItemsList.SetBinding(ItemsControl.ItemsSourceProperty,
            new Binding(nameof(HistoryFlyoutViewModel.Items)));
        ClearAllButton.Command = viewModel.ClearAllCommand;

        WireTabs();
        BuildDateFilterMenu();
        BuildMultiSelectMenu();
        WireFeatures();

        viewModel.CloseRequested += HideFlyout;
        viewModel.GroupingChanged += SyncTabState;
        viewModel.ListReloaded += OnListReloaded;
        ItemsList.PreviewMouseLeftButtonUp += OnListClick;

        // os hooks vivem no host, numa thread dedicada com message
        // loop proprio. Start e idempotente e precisa ter rodado antes do primeiro
        // Acquire. A assinatura e unica e dura a vida do processo: esta janela e
        // singleton e nunca fecha de verdade (OnClosing cancela e apenas esconde).
        LowLevelHookHost.Shared.Start();
        LowLevelHookHost.Shared.Observed += OnHookEvent;

        // switching language rebuilds the date menu right away
        Localization.Loc.LanguageChanged += () =>
        {
            DateFilterMenu.Items.Clear();
            BuildDateFilterMenu();
            MultiSelectMenu.Items.Clear();
            RebuildMultiSelectItems();
        };

        // a page appended by the scroll can't leave the list with nothing selected
        viewModel.Items.CollectionChanged += (_, _) =>
        {
            if (ItemsList.SelectedIndex < 0 && _viewModel.Items.Count > 0)
                SelectFirstVisible();
        };

        // build the HWND up front so the first Show is instant
        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();
        // let a click on the flyout activate it (so the search box can type),
        // even though it opens as a no-activate window
        HwndSource.FromHwnd(helper.Handle)?.AddHook(WndProc);
        SyncTabState();

        // start at the saved size (the user can resize, and it sticks)
        ApplySavedSize();
        // hide the emoji and snippets tabs if the user turned them off in settings
        ApplyTabVisibility();
        // react live when the setting is toggled in the settings window
        _settings.Changed += _ => Dispatcher.BeginInvoke(() =>
        {
            ApplyTabVisibility();
            // size typed in the settings page (while hidden; a drag on the border wins)
            if (!IsVisible)
                ApplySavedSize();
        });
        // remember the new size when the user drags the borders (debounced)
        _saveSizeDebounce = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _saveSizeDebounce.Tick += (_, _) => { _saveSizeDebounce.Stop(); SaveSize(); };
        SizeChanged += OnFlyoutSizeChanged;
    }

    private readonly System.Windows.Threading.DispatcherTimer _saveSizeDebounce;

    private void ApplySavedSize()
    {
        var s = _settings.Current;
        // clamp to the minimums declared in xaml so a bad saved value can't shrink it
        Width = Math.Max(360, s.FlyoutWidth);
        Height = Math.Max(460, s.FlyoutHeight);
    }

    /// <summary>Shows or hides the emoji and snippets tabs based on their settings.</summary>
    private void ApplyTabVisibility()
    {
        var emoji = _settings.Current.ShowEmojiTab;
        var snippets = _settings.Current.ShowSnippetsTab;
        TabEmoji.Visibility = emoji ? Visibility.Visible : Visibility.Collapsed;
        TabSnippets.Visibility = snippets ? Visibility.Visible : Visibility.Collapsed;
        // if the active tab got turned off, fall back to history
        if ((!emoji && _viewModel.SelectedTab == HistoryTab.Emoji) ||
            (!snippets && _viewModel.SelectedTab == HistoryTab.Snippets))
        {
            _viewModel.SelectedTab = HistoryTab.Recent;
            // swap the panel and tab strip right away so it doesn't sit on the
            // now-hidden view
            UpdatePanels();
            SyncTabButtons();
        }
    }

    private void OnFlyoutSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // ignore the layout passes that happen while we warm the window up
        // off-screen; those aren't real user resizes
        if (_warmingUp)
            return;
        // while shown, keep the flyout on its anchor (corner or caret) as it
        // grows, and debounce saving the new size
        if (IsVisible && !_closing)
            Reposition();
        _saveSizeDebounce.Stop();
        _saveSizeDebounce.Start();
    }

    private void SaveSize()
    {
        var w = Width;
        var h = Height;
        if (w < 360 || h < 460)
            return;
        _settings.Update(x => { x.FlyoutWidth = w; x.FlyoutHeight = h; });
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        // a click inside the flyout: drop NOACTIVATE for this moment and let it
        // take focus, so the search box works. we saved the target app already,
        // and the paste flow restores its focus afterwards.
        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            // a plain left click on a card (or an emoji) pastes right away, so don't take
            // the foreground for it. Stealing it makes the target app restore focus to
            // its field before our Ctrl+V lands, and apps like TeamViewer or a browser
            // login form are often not ready in time, so the paste just vanishes.
            // The keyboard keeps coming through the hook.
            const int WM_LBUTTONDOWN = 0x0201;
            var mouseMessage = (int)((lParam >> 16) & 0xFFFF);
            if (mouseMessage == WM_LBUTTONDOWN
                && !System.Threading.Volatile.Read(ref _hasFocus)
                && Keyboard.Modifiers == ModifierKeys.None
                && IsPasteClickTarget())
            {
                handled = true;
                return NativeMethods.MA_NOACTIVATE;
            }

            var exStyle = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            if ((exStyle & NativeMethods.WS_EX_NOACTIVATE) != 0)
            {
                exStyle &= ~NativeMethods.WS_EX_NOACTIVATE;
                NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, (nint)exStyle);
            }
            // we own the keyboard from here: the WPF side handles typing now.
            // tracking this explicitly (instead of polling GetForegroundWindow)
            // avoids the race where the hook and WPF fight over the first keys.
            System.Threading.Volatile.Write(ref _hasFocus, true);
            // desarma o hook de teclado NO MESMO instante da ativacao, senao
            // o callback e o WPF disputam as teclas ate a proxima troca de estado.
            UpdateSwallowMasks();
            handled = true;
            return NativeMethods.MA_ACTIVATE;
        }
        // lost activation (clicked elsewhere): hand the keyboard back to the hook
        if (msg == NativeMethods.WM_ACTIVATE)
        {
            const int WA_INACTIVE = 0;
            if ((wParam & 0xFFFF) == WA_INACTIVE)
            {
                System.Threading.Volatile.Write(ref _hasFocus, false);
                UpdateSwallowMasks(); // devolve as teclas de navegacao ao hook
            }
        }
        return nint.Zero;
    }

    /// <summary>
    /// True when the mouse is over a history card or an emoji, and not over one of the
    /// card's buttons. Those clicks paste and close, so they don't need keyboard focus;
    /// everything else (search, menus, buttons) still activates the panel.
    /// </summary>
    private bool IsPasteClickTarget()
    {
        try
        {
            NativeMethods.GetCursorPos(out var cursor);
            var point = PointFromScreen(new Point(cursor.x, cursor.y));
            if (InputHitTest(point) is not DependencyObject hit)
                return false;

            var overControl = false;
            for (var node = hit; node is not null; node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                     ? System.Windows.Media.VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
                     : LogicalTreeHelper.GetParent(node))
            {
                if (ReferenceEquals(node, EmojiWrap))
                    return true; // emoji buttons paste too
                if (node is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.ScrollBar
                    or System.Windows.Controls.Primitives.TextBoxBase)
                    overControl = true;
                if (node is ListBoxItem)
                    return !overControl; // pin, favorite and more on the card still activate
                if (ReferenceEquals(node, this))
                    break;
            }
        }
        catch (Exception)
        {
            // hit test is best effort; fall back to the usual activate behavior
        }
        return false;
    }

    // true once a click activated the flyout, so typing goes through WPF. set in
    // WndProc synchronously with the activation, cleared when we lose it or hide.
    private bool _hasFocus;

    /// <summary>This window's native handle (for the paste target guard).</summary>
    public nint Hwnd => new WindowInteropHelper(this).Handle;

    // ----- Tabs -----

    private void WireTabs()
    {
        Wire(TabRecent, HistoryTab.Recent);
        Wire(TabFavorites, HistoryTab.Favorites);
        Wire(TabImages, HistoryTab.Images);
        Wire(TabText, HistoryTab.Text);
        Wire(TabFiles, HistoryTab.Files);
        Wire(TabSnippets, HistoryTab.Snippets);
        Wire(TabEmoji, HistoryTab.Emoji);

        void Wire(RadioButton button, HistoryTab tab) =>
            button.Checked += (_, _) =>
            {
                if (_suppressTabEvents)
                    return;
                // setting the tab drives everything: query reload, tab strip sync
                // and emoji panel visibility all happen via ApplyGrouping
                _viewModel.SelectedTab = tab;
            };
    }

    // ----- Emoji panel -----

    private bool _emojiBuilt;

    /// <summary>
    /// One of four things fills the body: the history list, the emoji picker, the snippets
    /// tab or the preview. The tab (and the preview flag) decide which.
    /// </summary>
    private void UpdatePanels()
    {
        var tab = _viewModel.SelectedTab;
        var emoji = tab == HistoryTab.Emoji;
        var snippets = tab == HistoryTab.Snippets;
        EmojiPanel.Visibility = emoji ? Visibility.Visible : Visibility.Collapsed;
        SnippetsPanel.Visibility = snippets ? Visibility.Visible : Visibility.Collapsed;
        ItemsList.Visibility = emoji || snippets || _viewModel.IsPreviewOpen ? Visibility.Collapsed : Visibility.Visible;
        SearchBox.Visibility = emoji ? Visibility.Collapsed : Visibility.Visible;
        if (emoji && !_emojiBuilt)
            BuildEmojiPanel();
        if (emoji)
            EmojiSearchBox.Focus();
        // o conjunto de teclas engolidas depende do painel aberto, entao
        // toda abertura/fechamento republica a mascara.
        UpdateSwallowMasks();
    }

    private void BuildEmojiPanel()
    {
        _emojiBuilt = true;
        var repo = Controls.EmojiRepository.Instance;

        foreach (var category in repo.Categories)
        {
            var catButton = new Button
            {
                Content = new TextBlock
                {
                    Text = category.Glyph,
                    FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons"),
                    FontSize = 16,
                },
                ToolTip = category.Name,
                Style = (Style)Resources["CardActionButton"],
                Padding = new Thickness(8, 6, 8, 6),
            };
            var cat = category;
            catButton.Click += (_, _) => { EmojiSearchBox.Text = ""; FillEmojis(cat.Emojis); };
            EmojiCategories.Children.Add(catButton);
        }

        EmojiSearchBox.TextChanged += (_, _) =>
            FillEmojis(repo.Search(EmojiSearchBox.Text));

        // open on the first category
        if (repo.Categories.Count > 0)
            FillEmojis(repo.Categories[0].Emojis);
    }

    private void FillEmojis(IReadOnlyList<Controls.EmojiRepository.Emoji> emojis)
    {
        // bump the token so any pending lazy-decode from a previous fill stops
        // touching images that no longer belong to the current view
        var token = ++_emojiFillToken;

        EmojiWrap.Items.Clear();
        var style = (Style)Resources["EmojiButton"];
        var pending = new List<(string Code, System.Windows.Controls.Image Image)>(emojis.Count);
        foreach (var emoji in emojis)
        {
            var image = new System.Windows.Controls.Image
            {
                Width = 20,
                Height = 20,
                Stretch = System.Windows.Media.Stretch.Uniform,
            };
            // if it's already decoded, use it now; otherwise decode later off the
            // critical path so opening the tab stays instant
            if (_emojiCache.TryGetValue(emoji.Code, out var cached))
                image.Source = cached;
            else
                pending.Add((emoji.Code, image));

            var button = new Button
            {
                Style = style,
                Content = image,
                ToolTip = emoji.Name,
            };
            var glyph = emoji.Char;
            button.Click += (_, _) => _viewModel.PasteEmoji(glyph);
            EmojiWrap.Items.Add(button);
        }

        if (pending.Count > 0)
            DecodeEmojisLazily(pending, token);
    }

    // token so a rapid category/search switch cancels stale lazy decodes
    private int _emojiFillToken;

    /// <summary>
    /// Decodes the emoji PNGs one at a time at background priority and drops each
    /// into its image as it becomes ready, so the panel shows up instantly and the
    /// glyphs fill in over the next few frames instead of blocking the open.
    /// </summary>
    private void DecodeEmojisLazily(List<(string Code, System.Windows.Controls.Image Image)> pending, int token)
    {
        var index = 0;

        void Step()
        {
            // a newer fill happened, so this batch is stale; stop
            if (token != _emojiFillToken)
                return;
            // decode a small chunk per tick to keep the ui smooth
            var end = Math.Min(index + 12, pending.Count);
            for (; index < end; index++)
            {
                var (code, image) = pending[index];
                image.Source = LoadEmojiImage(code);
            }
            if (index < pending.Count)
                Dispatcher.BeginInvoke(Step, System.Windows.Threading.DispatcherPriority.Background);
        }

        Dispatcher.BeginInvoke(Step, System.Windows.Threading.DispatcherPriority.Background);
    }

    // small cache so re-opening the emoji panel or searching doesn't re-decode
    private static readonly Dictionary<string, System.Windows.Media.Imaging.BitmapImage> _emojiCache = new();

    private static System.Windows.Media.Imaging.BitmapImage LoadEmojiImage(string code)
    {
        if (_emojiCache.TryGetValue(code, out var cached))
            return cached;
        var img = new System.Windows.Media.Imaging.BitmapImage();
        img.BeginInit();
        img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        img.DecodePixelWidth = 24; // shown at 20px, decode small instead of full 72px
        img.UriSource = new Uri(Controls.EmojiRepository.ImageUri(code));
        img.EndInit();
        img.Freeze();
        _emojiCache[code] = img;
        return img;
    }

    private void SyncTabButtons()
    {
        _suppressTabEvents = true;
        TabRecent.IsChecked = _viewModel.SelectedTab == HistoryTab.Recent;
        TabFavorites.IsChecked = _viewModel.SelectedTab == HistoryTab.Favorites;
        TabImages.IsChecked = _viewModel.SelectedTab == HistoryTab.Images;
        TabText.IsChecked = _viewModel.SelectedTab == HistoryTab.Text;
        TabFiles.IsChecked = _viewModel.SelectedTab == HistoryTab.Files;
        TabSnippets.IsChecked = _viewModel.SelectedTab == HistoryTab.Snippets;
        TabEmoji.IsChecked = _viewModel.SelectedTab == HistoryTab.Emoji;
        _suppressTabEvents = false;
    }

    // ----- Date filter -----

    private void BuildDateFilterMenu()
    {
        Add(Localization.Loc.DateToday, DateFilterPreset.Today);
        Add(Localization.Loc.DateYesterday, DateFilterPreset.Yesterday);
        Add(Localization.Loc.DateLast7, DateFilterPreset.Last7Days);
        Add(Localization.Loc.DateLast30, DateFilterPreset.Last30Days);
        DateFilterMenu.Items.Add(new Separator());
        Add(Localization.Loc.DateAll, DateFilterPreset.All);

        // wire the button only once, the menu itself gets rebuilt on every language change
        if (!_dateButtonWired)
        {
            _dateButtonWired = true;
            DateFilterButton.Click += (_, _) =>
            {
                DateFilterMenu.PlacementTarget = DateFilterButton;
                DateFilterMenu.IsOpen = true;
            };
        }

        void Add(string header, DateFilterPreset preset)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => _viewModel.SetDateFilterCommand.Execute(preset);
            DateFilterMenu.Items.Add(item);
        }
    }

    private bool _dateButtonWired;

    /// <summary>1 to 5 menu on the multi-select button.</summary>
    private void BuildMultiSelectMenu()
    {
        RebuildMultiSelectItems();
        MultiSelectButton.Click += (_, _) =>
        {
            MultiSelectMenu.PlacementTarget = MultiSelectButton;
            MultiSelectMenu.IsOpen = true;
        };
    }

    private void RebuildMultiSelectItems()
    {
        for (var i = 1; i <= 5; i++)
        {
            var count = i;
            var item = new MenuItem
            {
                Header = count == 1
                    ? Localization.Loc.MultiPasteItem
                    : string.Format(Localization.Loc.MultiPasteItems, count),
            };
            item.Click += (_, _) => _viewModel.StartMultiSelectCommand.Execute(count);
            MultiSelectMenu.Items.Add(item);
        }
    }

    // ----- Tab state -----

    private void SyncTabState()
    {
        SyncTabButtons();
        // keep the panel visibility tied to the active tab (covers Ctrl+Tab too)
        UpdatePanels();
    }

    // ----- Show -----

    /// <summary>
    /// Shows the flyout next to the caret / mouse, or at the bottom right corner
    /// (physical px). By default it always opens at the top with the first item
    /// selected and visible; with "remember scroll" it comes back where it was.
    /// </summary>
    public void ShowFlyout()
    {
        // if the idle warm-up didn't get to run yet (user hit Win+V right after
        // launch), pay it now so the very first open still comes up prepared
        // instead of flashing an empty dark window
        if (!_warmedUp)
            WarmUp();

        var s = _settings.Current;
        var remember = s.FlyoutRememberScroll;

        // reopen on the last tab used (the view model is a singleton, so it sticks).
        // nothing changed since the last open = no query and no rebuild at all
        var onEmoji = _viewModel.SelectedTab == HistoryTab.Emoji;
        var onSnippets = _viewModel.SelectedTab == HistoryTab.Snippets;
        if (onSnippets && _viewModel.SnippetsStale)
            _ = _viewModel.ReloadSnippetsAsync();
        if (!onEmoji && !onSnippets && _viewModel.IsStale)
        {
            var anchor = remember ? _hiddenAnchor : null;
            _suppressReloadReset = remember;
            try
            {
                _viewModel.LoadFirstPage();
            }
            finally
            {
                _suppressReloadReset = false;
            }
            _pendingAnchor = anchor;
        }
        UpdatePanels();
        SyncTabButtons(); // keep the tab strip in sync with the active tab
        FooterHintsText.SetResourceReference(TextBlock.TextProperty,
            _viewModel.EnterCopiesOnly ? "Loc.FooterHintsCopy" : "Loc.FooterHints");

        // top first, so the very first frame already comes out at the top
        if (!remember && !onSnippets)
            ResetListToTop();

        var hwnd = new WindowInteropHelper(this).Handle;

        // no-activate: the window shows WITHOUT stealing foreground, so the app
        // you were typing in keeps its caret. keyboard comes via the global hook.
        var exStyle = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        exStyle |= NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, (nint)exStyle);

        PlaceOnShow(s.FlyoutOpenAtCaret);

        System.Threading.Volatile.Write(ref _hasFocus, false); // opens no-activate; a click will set this
        Show();

        // the layout of a rebuilt list only settles after Show; finish the job once it has
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (!IsVisible)
                return;
            if (_pendingAnchor is { } a)
            {
                _pendingAnchor = null;
                RestoreScrollAnchor(a);
            }
            else if (!remember)
            {
                ResetListToTop();
            }
            else
            {
                SelectFirstVisibleIfSelectionOffscreen();
            }
        });

        // the hooks go in AFTER Show() on purpose: Acquire waits for the pump to confirm
        // the install, and a hook armed before the window exists would swallow Esc/Enter
        // from the app below with nothing to handle them. ??= so a double Show can't leak
        // an orphan scope.
        _keyboardScope ??= LowLevelHookHost.Shared.Acquire(LowLevelHookKind.Keyboard);
        _mouseScope ??= LowLevelHookHost.Shared.Acquire(LowLevelHookKind.Mouse);
        HookPolicy.MouseActive = true; // passa a observar clique fora
        // KeyboardActive e as mascaras sao definidos aqui dentro: ligar antes deixaria
        // o callback enxergar a mascara do estado anterior.
        UpdateSwallowMasks();
        Diagnostics.StartupLogHookTrace.Trace("flyout aberto");
    }

    // ----- Scroll position -----

    private ScrollViewer? _listScroll;
    private ScrollAnchor? _pendingAnchor;

    // taken right before hiding: a hidden window has no visible containers to measure
    private ScrollAnchor? _hiddenAnchor;
    private bool _suppressReloadReset;

    /// <summary>First visible card and how far from the viewport top it was.</summary>
    private sealed record ScrollAnchor(long ItemId, double OffsetFromTop);

    private ScrollViewer? ListScroll => _listScroll ??= FindDescendant<ScrollViewer>(ItemsList);

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;
            if (FindDescendant<T>(child) is { } deeper)
                return deeper;
        }
        return null;
    }

    private void OnListReloaded()
    {
        // tab, filter and search go back to the top too; a reload made to restore an
        // anchor handles the scroll itself
        if (!_suppressReloadReset)
            ResetListToTop();
    }

    /// <summary>Scroll to the top and select the first item (Enter pastes what you see).</summary>
    private void ResetListToTop()
    {
        ListScroll?.ScrollToVerticalOffset(0);
        if (ItemsList.Items.Count > 0)
        {
            ItemsList.SelectedIndex = 0;
            ItemsList.ScrollIntoView(ItemsList.Items[0]);
        }
        else
        {
            ItemsList.SelectedIndex = -1;
        }
    }

    /// <summary>Realized containers at least partly inside the viewport, top to bottom.</summary>
    private List<(ListBoxItem Container, double Top)> VisibleContainers()
    {
        var visible = new List<(ListBoxItem, double)>();
        if (ListScroll is not { } scroll || scroll.ViewportHeight <= 0)
            return visible;
        // only realized containers exist (a few dozen), so this never walks the whole list
        var panel = FindDescendant<VirtualizingStackPanel>(ItemsList);
        if (panel is null)
            return visible;
        foreach (var child in panel.Children)
        {
            if (child is not ListBoxItem container || !container.IsVisible)
                continue;
            var top = container.TranslatePoint(new Point(0, 0), scroll).Y;
            if (top + container.ActualHeight <= 0 || top >= scroll.ViewportHeight)
                continue;
            visible.Add((container, top));
        }
        visible.Sort((a, b) => a.Item2.CompareTo(b.Item2));
        return visible;
    }

    private ScrollAnchor? CaptureScrollAnchor()
    {
        if (!_warmedUp || ItemsList.Items.Count == 0)
            return null;
        foreach (var (container, top) in VisibleContainers())
        {
            if (container.DataContext is HistoryItemViewModel vm)
                return new ScrollAnchor(vm.Id, top);
        }
        return null;
    }

    private void RestoreScrollAnchor(ScrollAnchor anchor)
    {
        // new items may have pushed the anchor past the first page: pull pages until it
        // is back (capped; a deleted anchor falls back to the top)
        var vm = _viewModel.LoadUntil(anchor.ItemId, maxItems: 1_000);
        if (vm is null)
        {
            ResetListToTop();
            return;
        }
        ItemsList.ScrollIntoView(vm);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (ListScroll is { } scroll &&
                ItemsList.ItemContainerGenerator.ContainerFromItem(vm) is ListBoxItem container)
            {
                var top = container.TranslatePoint(new Point(0, 0), scroll).Y;
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset + top - anchor.OffsetFromTop);
            }
            ItemsList.SelectedItem = vm;
        });
    }

    private void SelectFirstVisible()
    {
        var visible = VisibleContainers();
        if (visible.Count > 0)
            ItemsList.SelectedItem = visible[0].Container.DataContext;
        else if (ItemsList.Items.Count > 0)
            ItemsList.SelectedIndex = 0;
    }

    /// <summary>With "remember", keep the selection, but never on a card the user can't see.</summary>
    private void SelectFirstVisibleIfSelectionOffscreen()
    {
        var selected = ItemsList.SelectedItem;
        if (selected is not null && VisibleContainers().Any(v => ReferenceEquals(v.Container.DataContext, selected)))
            return;
        SelectFirstVisible();
    }

    // ----- Placement -----

    // where this open was anchored (physical px); null = bottom right corner
    private NativeMethods.POINT? _anchorPoint;
    private NativeMethods.RECT _anchorExclude;

    /// <summary>Picks the anchor for this open: caret, then mouse; the corner when the setting is off.</summary>
    private void PlaceOnShow(bool atCaret)
    {
        _anchorPoint = null;
        if (atCaret)
        {
            if (NativeMethods.TryGetCaretRect(out var caret))
            {
                _anchorPoint = new NativeMethods.POINT { x = caret.left, y = caret.bottom };
                _anchorExclude = caret;
            }
            else if (NativeMethods.GetCursorPos(out var cursor))
            {
                _anchorPoint = cursor;
                _anchorExclude = new NativeMethods.RECT { left = cursor.x, top = cursor.y, right = cursor.x, bottom = cursor.y };
            }
        }
        Reposition();
    }

    private void Reposition()
    {
        if (_anchorPoint is { } anchor)
            RepositionAt(anchor, _anchorExclude);
        else
            RepositionBottomRight();
    }

    /// <summary>
    /// Below the anchor (caret line or mouse) with a small gap, above it when there is no
    /// room below. Always fully inside the work area of the anchor's monitor.
    /// </summary>
    private void RepositionAt(NativeMethods.POINT anchor, NativeMethods.RECT exclude)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero)
            return;

        var monitor = NativeMethods.MonitorFromPoint(anchor, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        NativeMethods.GetMonitorInfo(monitor, ref info);
        var work = info.rcWork;

        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        if (dpi == 0)
            dpi = 96;
        var widthPx = Math.Min((int)(Width * dpi / 96.0), work.right - work.left);
        var heightPx = Math.Min((int)(Height * dpi / 96.0), work.bottom - work.top);
        var gap = (int)(4 * dpi / 96.0);

        var y = exclude.bottom + gap;
        if (y + heightPx > work.bottom)
            y = exclude.top - gap - heightPx; // no room below: flip above the caret
        var x = Math.Clamp(anchor.x, work.left, work.right - widthPx);
        y = Math.Clamp(y, work.top, work.bottom - heightPx);

        NativeMethods.SetWindowPos(hwnd, nint.Zero, x, y, widthPx, heightPx,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>
    /// Pins the flyout to the bottom-right of the work area (16px margin, like the
    /// native panel). Resizing grows it up and to the left, since this corner
    /// stays put. Uses the monitor under the cursor.
    /// </summary>
    private void RepositionBottomRight()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero)
            return;

        NativeMethods.GetCursorPos(out var cursor);
        var monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        NativeMethods.GetMonitorInfo(monitor, ref info);

        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        if (dpi == 0)
            dpi = 96;
        var widthPx = (int)(Width * dpi / 96.0);
        var heightPx = (int)(Height * dpi / 96.0);

        var x = info.rcWork.right - widthPx - 16;
        var y = info.rcWork.bottom - heightPx - 16;

        NativeMethods.SetWindowPos(hwnd, nint.Zero, x, y, widthPx, heightPx,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    public void HideFlyout(bool restoreFocus = true)
    {
        if (_closing)
            return;
        _closing = true;

        // para de observar E remove os hooks do sistema. So baixar
        // os flags nao resolveria: um hook instalado continua sendo chamado a cada
        // tecla e a cada clique do sistema inteiro, mesmo com o callback so retornando.
        HookPolicy.KeyboardActive = false;
        HookPolicy.MouseActive = false;
        HookPolicy.ClearSwallowKeys();
        _keyboardScope?.Dispose();
        _keyboardScope = null;
        _mouseScope?.Dispose();
        _mouseScope = null;
        // aqui os dois precisam voltar a "nao" - se aparecer "sim", o escopo vazou
        Diagnostics.StartupLogHookTrace.Trace("flyout fechado");

        // if we're still in multi-select here, the flyout is closing with a
        // pending selection (click outside, alt-tab, etc). that arms the queue,
        // same as pressing Enter. only Esc cancels (it clears the mode first).
        if (_viewModel.IsMultiSelectMode)
            _viewModel.ConfirmMultiSelectCommand.Execute(null);

        // transient views do not survive a hide: preview, snippet editor, badges, picked cards
        ShowShortcutBadges(false);
        _viewModel.ClosePreview();
        _viewModel.CancelSnippetEditCommand.Execute(null);
        _viewModel.ClearChecks();

        // if we grabbed focus (search box click), hand it back to the app the
        // user came from, so their caret returns where it was. skip this when a
        // paste is about to run: that flow restores focus and fires Ctrl+V on its
        // own, and a second restore here would race it and paste in the wrong spot.
        var weHadFocus = System.Threading.Volatile.Read(ref _hasFocus);
        System.Threading.Volatile.Write(ref _hasFocus, false);

        _hiddenAnchor = _settings.Current.FlyoutRememberScroll && _viewModel.SearchText.Length == 0
            ? CaptureScrollAnchor()
            : null;
        Hide();
        // clear the box without the debounced reload a TextChanged would schedule
        // for a hidden window; the next open rebuilds if the search changed the list
        _viewModel.ClearSearchSilently();
        if (SearchBox.Text.Length > 0)
            SearchBox.Text = "";
        if (!_settings.Current.FlyoutRememberScroll)
        {
            // back to the first page and the top: frees what the scroll pulled in, and
            // the next open is already in place
            _viewModel.TrimToFirstPage();
            ResetListToTop();
        }
        Controls.PathToThumbnailConverter.TrimCache(Controls.PathToThumbnailConverter.HiddenCacheSize);
        _closing = false;

        if (restoreFocus && weHadFocus)
            _viewModel.RestoreTargetFocus();
    }

    public new bool IsVisible => base.IsVisible;

    /// <summary>
    /// Pre-pays the first layout and the Mica composition by showing the window
    /// off-screen once and hiding it, so the real first open is instant. Call at
    /// idle after startup. The hooks and query are NOT touched here.
    /// </summary>
    public void WarmUp()
    {
        // only pays off once; a second call (idle timer after a manual open) is a no-op
        if (_warmedUp)
            return;
        try
        {
            _warmingUp = true;
            var hwnd = new WindowInteropHelper(this).Handle;
            var exStyle = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            exStyle |= NativeMethods.WS_EX_NOACTIVATE;
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, (nint)exStyle);
            // park it far off-screen so nothing flashes
            NativeMethods.SetWindowPos(hwnd, nint.Zero, -32000, -32000, 1, 1,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            Show();
            // warm the sqlite side too: first query pays connection + plan + page
            // cache, so we take that hit here instead of on the first real Win+V
            _viewModel.LoadFirstPage();
            UpdateLayout();
            Hide();
        }
        catch
        {
            // warm-up is best effort, never let it break startup
        }
        finally
        {
            _warmingUp = false;
            _warmedUp = true;
        }
    }

    /// <summary>A click outside the flyout closes it (screen point, physical px).</summary>
    private void OnGlobalClick(int screenX, int screenY)
    {
        if (!IsVisible || _closing || _menuOpen)
            return;

        // real window rect in physical pixels
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
            return;

        var inside = screenX >= rect.left && screenX < rect.right &&
                     screenY >= rect.top && screenY < rect.bottom;
        if (!inside)
            HideFlyout();
    }

    // ----- Keyboard -----

    // virtual key codes we care about (the hook gives us raw VKs)
    private const int VK_ESCAPE = 0x1B;
    private const int VK_RETURN = 0x0D;
    private const int VK_DELETE = 0x2E;
    private const int VK_UP = 0x26;
    private const int VK_DOWN = 0x28;
    private const int VK_TAB = 0x09;
    private const int VK_P = 0x50;
    private const int VK_D = 0x44;
    private const int VK_E = 0x45;
    private const int VK_F = 0x46;
    private const int VK_SPACE = 0x20;
    private const int VK_PRIOR = 0x21;
    private const int VK_NEXT = 0x22;
    // Ctrl and Alt themselves (left/right): they only drive the number badges
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RMENU = 0xA5;

    // conjuntos de descarte pre-calculados. Estaticos para que
    // UpdateSwallowMasks nao aloque nada a cada troca de estado.
    private static readonly int[] SwallowAlways = [VK_ESCAPE, VK_RETURN, VK_UP, VK_DOWN, VK_DELETE, VK_SPACE];
    private static readonly int[] SwallowWithCtrl = [VK_TAB, VK_P, VK_D, VK_E, VK_F, .. DigitKeys];
    private static readonly int[] SwallowWithAlt = [.. DigitKeys];
    private static readonly int[] SwallowAlwaysOnEmoji = [VK_ESCAPE, VK_UP, VK_DOWN];
    private static readonly int[] SwallowOnSnippets = [VK_ESCAPE, VK_RETURN, VK_UP, VK_DOWN];
    private static readonly int[] SwallowOnPreview = [VK_ESCAPE, VK_SPACE, VK_UP, VK_DOWN, VK_PRIOR, VK_NEXT];
    private static readonly int[] SwallowOnSnippetEditor = [VK_ESCAPE];

    // 1 to 9 on the top row (numpad digits too)
    private static int[] DigitKeys => [0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39,
        0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69];

    /// <summary>Superconjunto das mascaras: teclas que <see cref="ExecuteHookedKey"/> trata em algum estado.</summary>
    private static bool IsHookedKey(int vk) => vk is VK_ESCAPE or VK_RETURN or VK_UP or VK_DOWN
        or VK_DELETE or VK_TAB or VK_P or VK_D or VK_E or VK_F or VK_SPACE or VK_PRIOR or VK_NEXT
        || vk is >= VK_LCONTROL and <= VK_RMENU
        || PreviewFormat.ShortcutNumber(vk) > 0;

    /// <summary>
    /// roda na thread WORKER do host, NUNCA na UI thread. Aqui so
    /// pode existir despacho assincrono - o Dispatcher.Invoke sincrono que existia antes
    /// segurava a Raw Input Thread do Windows a cada tecla do sistema inteiro enquanto
    /// SQLite e clipboard rodavam na UI thread.
    /// </summary>
    private void OnHookEvent(Klip.Core.Input.InputEvent ev)
    {
        switch (ev.Message)
        {
            case NativeMethods.WM_KEYUP:
            case NativeMethods.WM_SYSKEYUP:
                // a Ctrl or Alt let go: the number badges follow the real key state
                if (ev.A is >= VK_LCONTROL and <= VK_RMENU)
                    Dispatcher.BeginInvoke(RefreshShortcutBadges);
                return;

            case NativeMethods.WM_KEYDOWN:
            case NativeMethods.WM_SYSKEYDOWN:
                int vk = ev.A;
                // o host publica TODA tecla enquanto KeyboardActive; filtrar aqui evita
                // um hop de Dispatcher por caractere digitado no app de baixo. E um
                // superconjunto das mascaras, entao nenhuma tecla engolida fica sem acao.
                if (!IsHookedKey(vk))
                    return;
                Dispatcher.BeginInvoke(() => ExecuteHookedKey(vk));
                return;

            case NativeMethods.WM_LBUTTONDOWN:
            case NativeMethods.WM_RBUTTONDOWN:
            case NativeMethods.WM_MBUTTONDOWN:
                // ponto de tela em pixels fisicos, como o MSLLHOOKSTRUCT entrega
                int x = ev.A;
                int y = ev.B;
                Dispatcher.BeginInvoke(() => OnGlobalClick(x, y));
                return;
        }
    }

    /// <summary>
    /// publica no <see cref="HookPolicy"/> a decisao "engolir ou nao" que o
    /// callback do hook precisa tomar em O(1). Roda na UI thread e e chamada sempre que
    /// muda algo de que a decisao depende: abrir/fechar o flyout, ganhar/perder foco
    /// (WndProc) e abrir/fechar o painel de emoji.
    /// <para>
    /// Espelha exatamente as guardas do antigo HandleHookedKey, so que antecipadas: o
    /// callback nao pode mais perguntar nada a UI para decidir.
    /// </para>
    /// </summary>
    private void UpdateSwallowMasks()
    {
        // Sem escopo nao ha hook instalado (inclusive durante o WarmUp, que mostra e
        // esconde a janela fora da tela). Com foco, o WPF trata a digitacao normalmente:
        // e o mesmo curto-circuito por _hasFocus que existia no inicio do callback.
        if (_keyboardScope is null || !IsVisible || System.Threading.Volatile.Read(ref _hasFocus))
        {
            HookPolicy.KeyboardActive = false;
            HookPolicy.ClearSwallowKeys();
            return;
        }

        HookPolicy.SetSwallowKeysWithAlt(ReadOnlySpan<int>.Empty);
        if (EmojiPanel.Visibility == Visibility.Visible)
        {
            // Painel de emoji: Esc volta para Recentes e as setas somem; o resto passa
            // adiante (nenhuma combinacao com Ctrl e tratada neste estado).
            HookPolicy.SetSwallowKeys(SwallowAlwaysOnEmoji);
            HookPolicy.SetSwallowKeysWithCtrl(ReadOnlySpan<int>.Empty);
        }
        else if (_viewModel.IsSnippetEditing)
        {
            // typing in the editor goes through WPF; only Esc needs the hook
            HookPolicy.SetSwallowKeys(SwallowOnSnippetEditor);
            HookPolicy.SetSwallowKeysWithCtrl(ReadOnlySpan<int>.Empty);
        }
        else if (_viewModel.IsPreviewOpen)
        {
            HookPolicy.SetSwallowKeys(SwallowOnPreview);
            HookPolicy.SetSwallowKeysWithCtrl(ReadOnlySpan<int>.Empty);
        }
        else if (_viewModel.SelectedTab == HistoryTab.Snippets)
        {
            HookPolicy.SetSwallowKeys(SwallowOnSnippets);
            HookPolicy.SetSwallowKeysWithCtrl(SwallowWithCtrl.Where(k => k is VK_TAB or VK_F).ToArray());
        }
        else
        {
            HookPolicy.SetSwallowKeys(SwallowAlways);
            HookPolicy.SetSwallowKeysWithCtrl(SwallowWithCtrl);
            HookPolicy.SetSwallowKeysWithAlt(SwallowWithAlt);
        }

        // Ativa por ultimo: assim o callback nunca enxerga a mascara do estado anterior.
        HookPolicy.KeyboardActive = true;
    }

    /// <summary>
    /// o EFEITO da tecla, ja na UI thread via BeginInvoke. Nao retorna nada -
    /// quem decidiu o descarte foi a mascara publicada por <see cref="UpdateSwallowMasks"/>.
    /// Mantem as mesmas acoes do antigo HandleHookedKey; os modificadores sao relidos do
    /// SO porque o flyout abre sem foco e Keyboard.Modifiers do WPF nao vale aqui.
    /// </summary>
    private void ExecuteHookedKey(int vk)
    {
        // O evento agora e assincrono: entre o callback do hook e esta execucao o
        // flyout pode ter fechado ou recebido foco (clique no campo de busca).
        if (!IsVisible)
            return;
        if (System.Threading.Volatile.Read(ref _hasFocus))
            return;

        // emoji panel open: only Esc matters, back to history. as setas sao engolidas
        // pela mascara e continuam sem acao, igual ao comportamento anterior.
        if (EmojiPanel.Visibility == Visibility.Visible)
        {
            if (vk == VK_ESCAPE)
                TabRecent.IsChecked = true;
            return;
        }

        var ctrl = NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL);
        var alt = NativeMethods.IsKeyDown(NativeMethods.VK_MENU);
        var shift = NativeMethods.IsKeyDown(NativeMethods.VK_SHIFT);

        // Ctrl or Alt pressed: show the numbers on the cards
        if (vk is >= VK_LCONTROL and <= VK_RMENU)
        {
            EnsureShortcutBadges();
            return;
        }

        // Ctrl+1..9 copies, Alt+1..9 pastes
        var number = PreviewFormat.ShortcutNumber(vk);
        if (number > 0)
        {
            if (ctrl || alt)
                RunNumberShortcut(number, paste: !ctrl && alt);
            return;
        }

        if (_viewModel.IsSnippetEditing)
        {
            if (vk == VK_ESCAPE)
                _viewModel.CancelSnippetEditCommand.Execute(null);
            return;
        }

        if (_viewModel.IsPreviewOpen)
        {
            HandlePreviewKey(vk);
            return;
        }

        var onSnippets = _viewModel.SelectedTab == HistoryTab.Snippets;

        switch (vk)
        {
            case VK_ESCAPE:
                HandleEscape();
                return;

            case VK_RETURN:
                HandleEnter(ctrl, shift);
                return;

            case VK_DOWN:
                HandleArrow(+1, shift);
                return;
            case VK_UP:
                HandleArrow(-1, shift);
                return;

            case VK_SPACE:
                if (!onSnippets)
                    _viewModel.TogglePreview(ItemsList.SelectedItem as HistoryItemViewModel);
                return;

            case VK_TAB when ctrl:
                _viewModel.CycleTab(shift ? -1 : +1);
                return;

            // A guarda "!typing" do codigo antigo era sempre verdadeira neste caminho: o
            // callback so chega aqui com _hasFocus == false, ou seja com o flyout sem
            // ativacao, quando nenhum TextBox do WPF detem o teclado. Mante-la agora
            // faria a tecla SUMIR nos casos de borda - a mascara ja engoliu o Delete e
            // a acao o ignoraria. Mascara e acao precisam concordar.
            case VK_DELETE when !onSnippets:
                _viewModel.DeleteCommand.Execute(ItemsList.SelectedItem);
                return;
            case VK_P when ctrl && !onSnippets:
                _viewModel.TogglePinCommand.Execute(ItemsList.SelectedItem);
                return;
            case VK_D when ctrl && !onSnippets:
                _viewModel.ToggleFavoriteCommand.Execute(ItemsList.SelectedItem);
                return;
            case VK_E when ctrl && !onSnippets:
                _viewModel.OpenInEditorCommand.Execute(ItemsList.SelectedItem);
                return;
            case VK_F when ctrl:
                // dar foco ao campo de busca NAO muda a mascara. Enquanto o
                // flyout nao for ativado (_hasFocus), o teclado continua chegando pelo
                // hook e a navegacao segue funcionando - igual ao comportamento antigo.
                SearchBox.Focus();
                return;
        }

        // everything else passes through (so a click-focused search box can type)
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Alt combos arrive as Key.System
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        // emoji panel is open: only Esc matters, let the rest through
        if (EmojiPanel.Visibility == Visibility.Visible)
        {
            if (key == Key.Escape)
            {
                TabRecent.IsChecked = true; // back to history
                e.Handled = true;
            }
            return;
        }

        // Ctrl or Alt held: numbers on the cards
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt)
            EnsureShortcutBadges();

        // Ctrl+1..9 copies, Alt+1..9 pastes (the history list only)
        if (!_viewModel.IsSnippetEditing && !_viewModel.IsPreviewOpen &&
            _viewModel.SelectedTab != HistoryTab.Snippets &&
            (mods == ModifierKeys.Control || mods == ModifierKeys.Alt) &&
            PreviewFormat.ShortcutNumber(KeyInterop.VirtualKeyFromKey(key)) is > 0 and var number)
        {
            RunNumberShortcut(number, paste: mods == ModifierKeys.Alt);
            e.Handled = true;
            return;
        }

        // the snippet editor owns the keyboard: only Esc is ours
        if (_viewModel.IsSnippetEditing)
        {
            if (key == Key.Escape)
            {
                _viewModel.CancelSnippetEditCommand.Execute(null);
                e.Handled = true;
            }
            return;
        }

        if (_viewModel.IsPreviewOpen)
        {
            var handledKey = key switch
            {
                Key.Escape or Key.Space => ClosePreviewKey(),
                Key.Up => ScrollPreview(-1),
                Key.Down => ScrollPreview(+1),
                Key.PageUp => ScrollPreview(-8),
                Key.PageDown => ScrollPreview(+8),
                _ => false,
            };
            if (handledKey)
                e.Handled = true;
            return;
        }

        var onSnippets = _viewModel.SelectedTab == HistoryTab.Snippets;

        switch (key)
        {
            case Key.Escape:
                HandleEscape();
                e.Handled = true;
                return;
            case Key.Enter when !_viewModel.IsMultiSelectMode && mods == ModifierKeys.Control && !onSnippets:
                _viewModel.CopyOnlyCommand.Execute(ItemsList.SelectedItem);
                e.Handled = true;
                return;
            case Key.Enter when !_viewModel.IsMultiSelectMode && mods == ModifierKeys.Alt && !onSnippets:
                _viewModel.TypeTextCommand.Execute(ItemsList.SelectedItem);
                e.Handled = true;
                return;
            case Key.Enter:
                HandleEnter(mods == ModifierKeys.Control, mods == ModifierKeys.Shift);
                e.Handled = true;
                return;
            case Key.Space when !SearchBox.IsKeyboardFocusWithin && !onSnippets:
                _viewModel.TogglePreview(ItemsList.SelectedItem as HistoryItemViewModel);
                e.Handled = true;
                return;
            case Key.Delete when !SearchBox.IsKeyboardFocusWithin && !onSnippets:
                _viewModel.DeleteCommand.Execute(ItemsList.SelectedItem);
                e.Handled = true;
                return;
            case Key.P when mods == ModifierKeys.Control && !onSnippets:
                _viewModel.TogglePinCommand.Execute(ItemsList.SelectedItem);
                e.Handled = true;
                return;
            case Key.D when mods == ModifierKeys.Control && !onSnippets:
                _viewModel.ToggleFavoriteCommand.Execute(ItemsList.SelectedItem);
                e.Handled = true;
                return;
            case Key.E when mods == ModifierKeys.Control && !onSnippets:
                _viewModel.OpenInEditorCommand.Execute(ItemsList.SelectedItem);
                e.Handled = true;
                return;
            case Key.F when mods == ModifierKeys.Control:
                SearchBox.Focus();
                e.Handled = true;
                return;
            case Key.Tab when mods == (ModifierKeys.Control | ModifierKeys.Shift):
                _viewModel.CycleTab(-1);
                e.Handled = true;
                return;
            case Key.Tab when mods == ModifierKeys.Control:
                _viewModel.CycleTab(+1);
                e.Handled = true;
                return;
            // window level navigation, doesn't matter which control has focus
            case Key.Down when !SearchBox.IsKeyboardFocusWithin || ActiveListCount > 0:
                HandleArrow(+1, mods == ModifierKeys.Shift);
                e.Handled = true;
                return;
            case Key.Up when !SearchBox.IsKeyboardFocusWithin:
                HandleArrow(-1, mods == ModifierKeys.Shift);
                e.Handled = true;
                return;
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl || (e.Key == Key.System && e.SystemKey is Key.LeftAlt or Key.RightAlt))
            RefreshShortcutBadges();
        base.OnPreviewKeyUp(e);
    }

    // Esc peels one layer at a time: picked cards, multi-select, search, then the flyout
    private void HandleEscape()
    {
        if (_viewModel.HasChecks)
            _viewModel.ClearChecks();
        else if (_viewModel.IsMultiSelectMode)
            _viewModel.CancelMultiSelectCommand.Execute(null);
        else if (SearchBox.Text.Length > 0)
            SearchBox.Text = "";
        else
            HideFlyout();
    }

    private void HandleEnter(bool ctrl, bool shift)
    {
        if (_viewModel.SelectedTab == HistoryTab.Snippets)
        {
            _viewModel.PasteSnippetCommand.Execute(SnippetsList.SelectedItem);
            return;
        }
        var selected = ItemsList.SelectedItem as HistoryItemViewModel;
        if (_viewModel.IsMultiSelectMode)
            _viewModel.ConfirmMultiSelectCommand.Execute(null); // arm the built series
        else if (ctrl)
            _viewModel.CopyOnlyCommand.Execute(selected);
        else if (shift)
            _viewModel.PastePlainCommand.Execute(selected);
        else
            _viewModel.ChooseCurrent(selected);
    }

    // Shift+arrow also picks the cards it walks over, for "paste together"
    private void HandleArrow(int delta, bool shift)
    {
        if (shift && _viewModel.SelectedTab != HistoryTab.Snippets && ItemsList.SelectedItem is HistoryItemViewModel current)
        {
            _viewModel.SetCheck(current, true);
            MoveSelection(delta);
            if (ItemsList.SelectedItem is HistoryItemViewModel next)
                _viewModel.SetCheck(next, true);
            return;
        }
        MoveSelection(delta);
    }

    /// <summary>Typing with the list focused throws the text into the search box.</summary>
    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        if (EmojiPanel.Visibility == Visibility.Visible)
            return; // no search box while the emoji picker is up

        // only redirect when the search box is neither focused nor about to be.
        // during the click that focuses it, the focused element is already the
        // text box, so appending here would double the first character.
        if (SearchBox.IsKeyboardFocusWithin || Keyboard.FocusedElement == SearchBox)
        {
            base.OnPreviewTextInput(e);
            return;
        }

        if (e.Text.Length > 0 && !char.IsControl(e.Text[0]))
        {
            SearchBox.Focus();
            SearchBox.Text += e.Text;
            SearchBox.CaretIndex = SearchBox.Text.Length;
            e.Handled = true;
        }
        base.OnPreviewTextInput(e);
    }

    private ListBox ActiveList => _viewModel.SelectedTab == HistoryTab.Snippets ? SnippetsList : ItemsList;

    private int ActiveListCount => ActiveList.Items.Count;

    private void MoveSelection(int delta)
    {
        var list = ActiveList;
        if (list.Items.Count == 0)
            return;
        var index = Math.Clamp(list.SelectedIndex + delta, 0, list.Items.Count - 1);
        list.SelectedIndex = index;
        list.ScrollIntoView(list.SelectedItem);
    }

    // ----- Infinite scroll -----

    private void OnListScroll(object sender, ScrollChangedEventArgs e)
    {
        if (_badgesOn)
            ShowShortcutBadges(true); // renumber for what is on screen now
        if (e.VerticalChange <= 0 || e.ExtentHeight <= 0)
            return;
        if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight * 0.8)
            _viewModel.LoadMoreCommand.Execute(null);
    }

    // ----- Mouse -----

    private void OnListClick(object sender, MouseButtonEventArgs e)
    {
        // the mouse-up that ends a drag is not a click
        if (_suppressClick)
        {
            _suppressClick = false;
            return;
        }

        // clicks on the card's action buttons (pin/fav/more) are handled there
        if (e.OriginalSource is not DependencyObject source ||
            FindItem(source) is not { } item ||
            IsInsideButton(source))
            return;

        var mods = Keyboard.Modifiers;

        // Alt+click on an image jumps straight into the editor
        if (mods == ModifierKeys.Alt && item.IsImage)
        {
            _viewModel.OpenInEditorCommand.Execute(item);
            return;
        }

        // Shift+click builds the sequential paste queue: the first one arms
        // multi-select on its own, and each click adds in the order clicked
        if (mods == ModifierKeys.Shift)
        {
            if (!_viewModel.IsMultiSelectMode)
                _viewModel.StartMultiSelectCommand.Execute(5);
            _viewModel.PasteCommand.Execute(item); // routes to queue while in multi-select
            return;
        }

        // Ctrl+click picks the card for "paste together" (Ctrl+Enter still copies)
        if (mods == ModifierKeys.Control && !_viewModel.IsMultiSelectMode)
        {
            _viewModel.ToggleCheck(item);
            return;
        }

        // plain click pastes or copies (setting), or queues if multi-select is active
        _viewModel.ChooseCommand.Execute(item);
    }

    /// <summary>
    /// Re-arms WS_EX_NOACTIVATE and hands the foreground back to the app the user
    /// came from, without hiding the flyout. Used after Ctrl+click (copy) so the
    /// panel stays open but doesn't hold the user's focus.
    /// </summary>
    private void ReleaseFocusButStayOpen()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        exStyle |= NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, (nint)exStyle);
        _viewModel.RestoreTargetFocus();
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        _viewModel.TogglePinCommand.Execute((sender as FrameworkElement)?.DataContext);
        e.Handled = true;
    }

    private void OnFavoriteClick(object sender, RoutedEventArgs e)
    {
        _viewModel.ToggleFavoriteCommand.Execute((sender as FrameworkElement)?.DataContext);
        e.Handled = true;
    }

    /// <summary>The card's "..." menu.</summary>
    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: HistoryItemViewModel item } element)
            return;
        ShowItemMenu(item, element);
        e.Handled = true;
    }

    private static HistoryItemViewModel? FindItem(DependencyObject source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: HistoryItemViewModel vm })
                return vm;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // a janela j[a existe e é reaproveitada, nunca destruir enquanto o app vive
        e.Cancel = true;
        HideFlyout();
    }
}

/// <summary>Simple inverse converter, saves pulling in an extra dependency.</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
