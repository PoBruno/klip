using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using Klip.App.ViewModels;
using Klip.Interop;
using Wpf.Ui.Appearance;

namespace Klip.App.Windows;

/// <summary>
/// Win+V style history flyout.
/// Pre-built and hidden, placed at the bottom right corner of the monitor
/// under the cursor, closes on Esc or when it loses focus.
/// Code-behind is limited to window interop and input routing (MVVM).
/// </summary>
public partial class HistoryFlyoutWindow
{
    private readonly HistoryFlyoutViewModel _viewModel;
    private bool _closing;
    private bool _suppressTabEvents;
    // keyboard hook: the flyout never takes focus (so the app below keeps its
    // caret), so we drive its navigation keys through a global hook instead
    private readonly Klip.Interop.GlobalKeyboardListener _keys = new();
    // mouse hook: same reason. no-activate windows don't get Deactivated, so we
    // watch for a click outside the flyout to close it
    private readonly Klip.Interop.GlobalMouseListener _mouse = new();

    public HistoryFlyoutWindow(HistoryFlyoutViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        Resources["BoolToVisibility"] = new BooleanToVisibilityConverter();
        Resources["InverseBoolToVisibility"] = new InverseBooleanToVisibilityConverter();
        Resources["PathToThumbnail"] = new Controls.PathToThumbnailConverter();
        InitializeComponent();
        SystemThemeWatcher.Watch(this);

        ItemsList.SetBinding(ItemsControl.ItemsSourceProperty,
            new Binding(nameof(HistoryFlyoutViewModel.Items)));
        ClearAllButton.Command = viewModel.ClearAllCommand;

        WireTabs();
        BuildDateFilterMenu();
        BuildMultiSelectMenu();

        viewModel.CloseRequested += HideFlyout;
        viewModel.GroupingChanged += ApplyGrouping;
        ItemsList.PreviewMouseLeftButtonUp += OnListClick;

        // route hooked keys onto the UI thread; returns true when we consumed it
        _keys.OnKeyDown = vk => Dispatcher.Invoke(() => HandleHookedKey(vk));
        _keys.Install();

        // click outside the flyout closes it (screen point in physical px)
        // click outside the flyout closes it. BeginInvoke (async) so the mouse
        // hook returns instantly and never stalls the whole system's input.
        _mouse.OnButtonDown = (x, y) => Dispatcher.BeginInvoke(() => OnGlobalClick(x, y));
        _mouse.Install();

        // switching language rebuilds the date menu right away
        Localization.Loc.LanguageChanged += () =>
        {
            DateFilterMenu.Items.Clear();
            BuildDateFilterMenu();
            MultiSelectMenu.Items.Clear();
            RebuildMultiSelectItems();
        };

        // any reload (search/tab/filter) clears the selection, so keep the first
        // item picked and Enter pastes it right away
        viewModel.Items.CollectionChanged += (_, _) =>
        {
            if (ItemsList.SelectedIndex < 0 && _viewModel.Items.Count > 0)
                ItemsList.SelectedIndex = 0;
        };

        // build the HWND up front so the first Show is instant
        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();
        // let a click on the flyout activate it (so the search box can type),
        // even though it opens as a no-activate window
        HwndSource.FromHwnd(helper.Handle)?.AddHook(WndProc);
        ApplyGrouping();
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        // a click inside the flyout: drop NOACTIVATE for this moment and let it
        // take focus, so the search box works. we saved the target app already,
        // and the paste flow restores its focus afterwards.
        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            var exStyle = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            if ((exStyle & NativeMethods.WS_EX_NOACTIVATE) != 0)
            {
                exStyle &= ~NativeMethods.WS_EX_NOACTIVATE;
                NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, (nint)exStyle);
            }
            handled = true;
            return NativeMethods.MA_ACTIVATE;
        }
        return nint.Zero;
    }

    // ----- Tabs -----

    private void WireTabs()
    {
        Wire(TabRecent, HistoryTab.Recent);
        Wire(TabFavorites, HistoryTab.Favorites);
        Wire(TabImages, HistoryTab.Images);
        Wire(TabText, HistoryTab.Text);
        Wire(TabFiles, HistoryTab.Files);
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

    private void ShowEmojiPanel(bool show)
    {
        EmojiPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ItemsList.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        SearchBox.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (show && !_emojiBuilt)
            BuildEmojiPanel();
        if (show)
            EmojiSearchBox.Focus();
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
        EmojiWrap.Items.Clear();
        var style = (Style)Resources["EmojiButton"];
        foreach (var emoji in emojis)
        {
            var button = new Button
            {
                Style = style,
                Content = new System.Windows.Controls.Image
                {
                    Source = LoadEmojiImage(emoji.Code),
                    Width = 20,
                    Height = 20,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                },
                ToolTip = emoji.Name,
            };
            var glyph = emoji.Char;
            button.Click += (_, _) => _viewModel.PasteEmoji(glyph);
            EmojiWrap.Items.Add(button);
        }
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

    // ----- Grouping: Pinned/Recent -----

    private void ApplyGrouping()
    {
        var view = CollectionViewSource.GetDefaultView(_viewModel.Items);
        view.GroupDescriptions.Clear();
        if (_viewModel.IsGroupingEnabled)
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(HistoryItemViewModel.GroupName)));
        SyncTabButtons();
        // keep the emoji panel visibility tied to the active tab (covers Ctrl+Tab too)
        ShowEmojiPanel(_viewModel.SelectedTab == HistoryTab.Emoji);
    }

    // ----- Show -----

    /// <summary>Shows the flyout on the monitor under the cursor (physical px).</summary>
    public void ShowFlyout()
    {
        // reopen on the last tab used (the view model is a singleton, so it sticks)
        var onEmoji = _viewModel.SelectedTab == HistoryTab.Emoji;
        if (!onEmoji)
            _viewModel.LoadFirstPage();
        ShowEmojiPanel(onEmoji);
        SyncTabButtons(); // keep the tab strip in sync with the active tab

        var hwnd = new WindowInteropHelper(this).Handle;

        NativeMethods.GetCursorPos(out var cursor);
        var monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        NativeMethods.GetMonitorInfo(monitor, ref info);

        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        if (dpi == 0)
            dpi = 96;
        var widthPx = (int)(Width * dpi / 96.0);
        var heightPx = (int)(Height * dpi / 96.0);

        // bottom right of the work area, 16px physical margin to match the native panel
        var x = info.rcWork.right - widthPx - 16;
        var y = info.rcWork.bottom - heightPx - 16;

        // no-activate: the window shows WITHOUT stealing foreground, so the app
        // you were typing in keeps its caret. keyboard comes via the global hook.
        var exStyle = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        exStyle |= NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, (nint)exStyle);

        NativeMethods.SetWindowPos(hwnd, nint.Zero, x, y, widthPx, heightPx,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        Show();
        if (ItemsList.Items.Count > 0)
            ItemsList.SelectedIndex = 0;
        _keys.Active = true;  // start routing keys to the flyout
        _mouse.Active = true; // start watching for outside clicks
    }

    public void HideFlyout()
    {
        if (_closing)
            return;
        _closing = true;
        _keys.Active = false;  // stop routing keys once hidden
        _mouse.Active = false; // stop watching clicks

        // if we grabbed focus (search box click), hand it back to the app the
        // user came from, so their caret returns where it was
        var ourHwnd = new WindowInteropHelper(this).Handle;
        var weHadFocus = NativeMethods.GetForegroundWindow() == ourHwnd;

        Hide();
        SearchBox.Text = "";
        _closing = false;

        if (weHadFocus)
            _viewModel.RestoreTargetFocus();
    }

    public new bool IsVisible => base.IsVisible;

    /// <summary>A click outside the flyout closes it (screen point, physical px).</summary>
    private void OnGlobalClick(int screenX, int screenY)
    {
        if (!IsVisible || _closing)
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

    /// <summary>
    /// Handles a key routed from the global hook while the flyout is visible.
    /// Returns true when we consumed it (so it does NOT reach the app underneath).
    /// This mirrors OnPreviewKeyDown, but the flyout has no focus so we read the
    /// modifier state from the OS instead of WPF's Keyboard.Modifiers.
    /// </summary>
    private bool HandleHookedKey(int vk)
    {
        if (!IsVisible)
            return false;

        // if the flyout itself is the foreground window (user clicked into it),
        // let WPF's normal keyboard handling do the work, don't double-handle
        var ourHwnd = new WindowInteropHelper(this).Handle;
        if (NativeMethods.GetForegroundWindow() == ourHwnd)
            return false;

        var ctrl = NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL);
        var shift = NativeMethods.IsKeyDown(NativeMethods.VK_SHIFT);

        // if the search box has focus (user clicked it), let typing flow normally
        // and only steal the navigation keys
        var typing = SearchBox.IsKeyboardFocusWithin || EmojiSearchBox.IsKeyboardFocusWithin;

        // emoji panel open: only Esc matters, back to history
        if (EmojiPanel.Visibility == Visibility.Visible)
        {
            if (vk == VK_ESCAPE)
            {
                TabRecent.IsChecked = true;
                return true;
            }
            return typing ? false : vk is VK_UP or VK_DOWN; // swallow arrows, pass the rest to the search box
        }

        switch (vk)
        {
            case VK_ESCAPE:
                if (_viewModel.IsMultiSelectMode)
                    _viewModel.CancelMultiSelectCommand.Execute(null);
                else if (SearchBox.Text.Length > 0)
                    SearchBox.Text = "";
                else
                    HideFlyout();
                return true;

            case VK_RETURN:
                if (ctrl)
                    _viewModel.CopyOnlyCommand.Execute(ItemsList.SelectedItem);
                else if (shift)
                    _viewModel.PastePlainCommand.Execute(ItemsList.SelectedItem);
                else
                    _viewModel.PasteCommand.Execute(ItemsList.SelectedItem);
                return true;

            case VK_DOWN:
                MoveSelection(+1);
                return true;
            case VK_UP:
                MoveSelection(-1);
                return true;

            case VK_TAB when ctrl:
                _viewModel.CycleTab(shift ? -1 : +1);
                return true;

            case VK_DELETE when !typing:
                _viewModel.DeleteCommand.Execute(ItemsList.SelectedItem);
                return true;
            case VK_P when ctrl:
                _viewModel.TogglePinCommand.Execute(ItemsList.SelectedItem);
                return true;
            case VK_D when ctrl:
                _viewModel.ToggleFavoriteCommand.Execute(ItemsList.SelectedItem);
                return true;
            case VK_E when ctrl:
                _viewModel.OpenInEditorCommand.Execute(ItemsList.SelectedItem);
                return true;
            case VK_F when ctrl:
                SearchBox.Focus();
                return true;
        }

        // everything else passes through (so a click-focused search box can type)
        return false;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // emoji panel is open: only Esc matters, let the rest through
        if (EmojiPanel.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape)
            {
                TabRecent.IsChecked = true; // back to history
                e.Handled = true;
            }
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                // Esc: sai do modo de seleção, se não limpa a busca, se não fecha
                if (_viewModel.IsMultiSelectMode)
                    _viewModel.CancelMultiSelectCommand.Execute(null);
                else if (SearchBox.Text.Length > 0)
                    SearchBox.Text = "";
                else
                    HideFlyout();
                e.Handled = true;
                return;
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.Control:
                _viewModel.CopyOnlyCommand.Execute(ItemsList.SelectedItem);
                e.Handled = true;
                return;
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.Shift:
                _viewModel.PastePlainCommand.Execute(ItemsList.SelectedItem);
                e.Handled = true;
                return;
            case Key.Enter:
                _viewModel.PasteCommand.Execute(ItemsList.SelectedItem);
                e.Handled = true;
                return;
            case Key.Delete when !SearchBox.IsKeyboardFocusWithin:
                _viewModel.DeleteCommand.Execute(ItemsList.SelectedItem);
                e.Handled = true;
                return;
            case Key.P when Keyboard.Modifiers == ModifierKeys.Control:
                _viewModel.TogglePinCommand.Execute(ItemsList.SelectedItem);
                e.Handled = true;
                return;
            case Key.D when Keyboard.Modifiers == ModifierKeys.Control:
                _viewModel.ToggleFavoriteCommand.Execute(ItemsList.SelectedItem);
                e.Handled = true;
                return;
            case Key.E when Keyboard.Modifiers == ModifierKeys.Control:
                _viewModel.OpenInEditorCommand.Execute(ItemsList.SelectedItem);
                e.Handled = true;
                return;
            case Key.F when Keyboard.Modifiers == ModifierKeys.Control:
                SearchBox.Focus();
                e.Handled = true;
                return;
            case Key.Tab when Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                _viewModel.CycleTab(-1);
                e.Handled = true;
                return;
            case Key.Tab when Keyboard.Modifiers == ModifierKeys.Control:
                _viewModel.CycleTab(+1);
                e.Handled = true;
                return;
            // window level navigation, doesn't matter which control has focus
            case Key.Down when !SearchBox.IsKeyboardFocusWithin || ItemsList.Items.Count > 0:
                MoveSelection(+1);
                e.Handled = true;
                return;
            case Key.Up when !SearchBox.IsKeyboardFocusWithin:
                MoveSelection(-1);
                e.Handled = true;
                return;
        }
        base.OnPreviewKeyDown(e);
    }

    /// <summary>Typing with the list focused throws the text into the search box.</summary>
    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        if (EmojiPanel.Visibility == Visibility.Visible)
            return; // no search box while the emoji picker is up
        if (!SearchBox.IsKeyboardFocusWithin && e.Text.Length > 0 && !char.IsControl(e.Text[0]))
        {
            SearchBox.Focus();
            SearchBox.Text += e.Text;
            SearchBox.CaretIndex = SearchBox.Text.Length;
            e.Handled = true;
        }
        base.OnPreviewTextInput(e);
    }

    private void MoveSelection(int delta)
    {
        if (ItemsList.Items.Count == 0)
            return;
        var index = Math.Clamp(ItemsList.SelectedIndex + delta, 0, ItemsList.Items.Count - 1);
        ItemsList.SelectedIndex = index;
        ItemsList.ScrollIntoView(ItemsList.SelectedItem);
    }

    // ----- Infinite scroll -----

    private void OnListScroll(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange <= 0 || e.ExtentHeight <= 0)
            return;
        if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight * 0.8)
            _viewModel.LoadMoreCommand.Execute(null);
    }

    // ----- Mouse -----

    private void OnListClick(object sender, MouseButtonEventArgs e)
    {
        // plain click pastes, Ctrl+click only copies.
        // in multi-select any click just queues it up (PasteCommand handles that).
        if (e.OriginalSource is DependencyObject source &&
            FindItem(source) is { } item &&
            e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && !_viewModel.IsMultiSelectMode)
                _viewModel.CopyOnlyCommand.Execute(item);
            else
                _viewModel.PasteCommand.Execute(item);
        }
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

        var menu = new ContextMenu { PlacementTarget = element };
        AddMenuItem(menu, Localization.Loc.MenuPaste, "\uE77F", () => _viewModel.PasteCommand.Execute(item));
        AddMenuItem(menu, Localization.Loc.MenuPastePlain, "\uE8E9", () => _viewModel.PastePlainCommand.Execute(item));
        AddMenuItem(menu, Localization.Loc.MenuCopy, "\uE8C8", () => _viewModel.CopyOnlyCommand.Execute(item));
        menu.Items.Add(new Separator());
        if (item.IsImage)
        {
            AddMenuItem(menu, Localization.Loc.MenuSaveAs, "\uE792", () => _viewModel.SaveAsCommand.Execute(item));
            AddMenuItem(menu, Localization.Loc.MenuOpenInEditor, "\uE70F", () => _viewModel.OpenInEditorCommand.Execute(item));
            menu.Items.Add(new Separator());
        }
        AddMenuItem(menu, item.IsPinned ? Localization.Loc.MenuUnpin : Localization.Loc.MenuPin, item.IsPinned ? "\uE77A" : "\uE718",
            () => _viewModel.TogglePinCommand.Execute(item));
        AddMenuItem(menu, item.IsFavorite ? Localization.Loc.MenuUnfavorite : Localization.Loc.MenuFavorite, item.IsFavorite ? "\uE8D9" : "\uE734",
            () => _viewModel.ToggleFavoriteCommand.Execute(item));
        menu.Items.Add(new Separator());
        AddMenuItem(menu, Localization.Loc.MenuDelete, "\uE74D", () => _viewModel.DeleteCommand.Execute(item));

        menu.IsOpen = true;
        e.Handled = true;

        static void AddMenuItem(ContextMenu menu, string header, string glyph, Action action)
        {
            var menuItem = new MenuItem { Header = header, Icon = MenuGlyph(glyph) };
            menuItem.Click += (_, _) => action();
            menu.Items.Add(menuItem);
        }

        static TextBlock MenuGlyph(string glyph) => new()
        {
            Text = glyph,
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons"),
            FontSize = 14,
        };
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
