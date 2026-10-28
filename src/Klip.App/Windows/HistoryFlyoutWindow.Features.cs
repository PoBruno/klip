using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Klip.App.Services;
using Klip.App.ViewModels;
using Klip.Core.Clipboard;
using Klip.Core.Storage;
using Klip.Interop;
using Loc = Klip.App.Localization.Loc;

namespace Klip.App.Windows;

// number shortcuts, preview, drag out, menus and the snippets tab
public partial class HistoryFlyoutWindow
{
    // one of our context menus is open: a click on it lands outside the flyout rect
    private bool _menuOpen;

    // number badges on the cards, only while Ctrl or Alt is held
    private bool _badgesOn;
    private readonly List<HistoryItemViewModel> _badged = [];

    // drag out of the list
    private Point _dragStart;
    private HistoryItemViewModel? _dragItem;
    private bool _suppressClick;

    private void WireFeatures()
    {
        SnippetsList.SetBinding(ItemsControl.ItemsSourceProperty,
            new System.Windows.Data.Binding(nameof(HistoryFlyoutViewModel.Snippets)));

        HeaderMenuButton.Click += (_, _) => ShowHeaderMenu();

        ItemsList.PreviewMouseLeftButtonDown += OnListMouseDown;
        ItemsList.PreviewMouseMove += OnListMouseMove;
        ItemsList.PreviewMouseRightButtonUp += OnListRightClick;
        SnippetsList.PreviewMouseLeftButtonUp += OnSnippetsClick;

        PreviewCloseButton.Click += (_, _) => _viewModel.ClosePreview();
        PreviewFitButton.Click += (_, _) => FitPreviewImage();
        PreviewActualButton.Click += (_, _) => SetPreviewZoom(ActualPixelsZoom());
        PreviewZoomInButton.Click += (_, _) => SetPreviewZoom(_previewZoom * 1.25);
        PreviewZoomOutButton.Click += (_, _) => SetPreviewZoom(_previewZoom / 1.25);
        PreviewImageScroll.PreviewMouseWheel += OnPreviewWheel;

        _viewModel.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(HistoryFlyoutViewModel.IsPreviewOpen):
                    UpdatePanels();
                    if (!_viewModel.IsPreviewOpen)
                        ShowShortcutBadges(false);
                    break;
                case nameof(HistoryFlyoutViewModel.PreviewImage):
                    if (_viewModel.PreviewImage is not null)
                    {
                        SetPreviewZoom(ActualPixelsZoom());
                        PreviewImageScroll.ScrollToHome();
                    }
                    break;
                case nameof(HistoryFlyoutViewModel.IsSnippetEditing):
                    UpdateSwallowMasks();
                    if (_viewModel.IsSnippetEditing)
                        Dispatcher.BeginInvoke(() => SnippetNameBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
                    break;
            }
        };
        _viewModel.SnippetsReloaded += () =>
        {
            if (SnippetsList.Items.Count > 0)
                SnippetsList.SelectedIndex = 0;
        };

        Deactivated += (_, _) => ShowShortcutBadges(false);
    }

    // ----- Menus -----

    private static TextBlock MenuGlyph(string glyph) => new()
    {
        Text = glyph,
        FontFamily = new FontFamily("Segoe Fluent Icons"),
        FontSize = 14,
    };

    private static MenuItem AddMenuItem(ContextMenu menu, string header, string glyph, Action action)
    {
        var item = new MenuItem { Header = header, Icon = MenuGlyph(glyph) };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
        return item;
    }

    private void TrackMenu(ContextMenu menu)
    {
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => _menuOpen = false;
    }

    /// <summary>The "..." menu in the header: pause, ignore next copy, collect mode, settings.</summary>
    private void ShowHeaderMenu()
    {
        var menu = new ContextMenu { PlacementTarget = HeaderMenuButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        if (_viewModel.IsPausedNow)
        {
            AddMenuItem(menu, Loc.MenuResume, "", () => _viewModel.ResumeHistoryCommand.Execute(null));
        }
        else
        {
            AddMenuItem(menu, Loc.MenuPause15, "", () => _viewModel.PauseHistory(TimeSpan.FromMinutes(15)));
            AddMenuItem(menu, Loc.MenuPause60, "", () => _viewModel.PauseHistory(TimeSpan.FromHours(1)));
            AddMenuItem(menu, Loc.MenuPauseUntil, "", () => _viewModel.PauseHistory(null));
        }
        AddMenuItem(menu, Loc.MenuIgnoreNext, _viewModel.IsIgnoringNextNow ? "" : "",
            _viewModel.IgnoreNextCopy);
        menu.Items.Add(new Separator());
        AddMenuItem(menu, Loc.MenuCollectMode, _viewModel.IsCollectMode ? "" : "",
            _viewModel.ToggleCollectMode);
        menu.Items.Add(new Separator());
        AddMenuItem(menu, Loc.TraySettings, "", _viewModel.OpenSettings);
        TrackMenu(menu);
        menu.IsOpen = true;
    }

    /// <summary>Full item menu, from the card's "..." button or a right click.</summary>
    private void ShowItemMenu(HistoryItemViewModel item, FrameworkElement placementTarget, bool atMouse = false)
    {
        var menu = new ContextMenu { PlacementTarget = placementTarget };
        if (atMouse)
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;

        AddMenuItem(menu, Loc.MenuPaste, "", () => _viewModel.PasteCommand.Execute(item));
        AddMenuItem(menu, Loc.MenuPastePlain, "", () => _viewModel.PastePlainCommand.Execute(item));
        if (item.Item.Type is ClipboardItemType.Text or ClipboardItemType.Html)
            AddMenuItem(menu, Loc.MenuTypeText, "", () => _viewModel.TypeTextCommand.Execute(item));
        AddMenuItem(menu, Loc.MenuCopy, "", () => _viewModel.CopyOnlyCommand.Execute(item));
        AddMenuItem(menu, Loc.MenuPreview, "", () => _ = _viewModel.OpenPreviewAsync(item));
        AddMenuItem(menu, item.IsChecked ? Loc.MenuUnselect : Loc.MenuSelect, "", () => _viewModel.ToggleCheck(item));
        menu.Items.Add(new Separator());

        var isText = item.Item.Type is ClipboardItemType.Text or ClipboardItemType.Html;
        if (item.IsImage)
        {
            AddMenuItem(menu, Loc.MenuSaveAs, "", () => _viewModel.SaveAsCommand.Execute(item));
            AddMenuItem(menu, Loc.MenuOpenInEditor, "", () => _viewModel.OpenInEditorCommand.Execute(item));
            AddMenuItem(menu, Loc.MenuPinOnScreen, "", () => _ = _viewModel.PinOnScreenAsync(item));
            AddMenuItem(menu, Loc.MenuCopyImageText, "", () => _ = _viewModel.CopyImageTextAsync(item));
            menu.Items.Add(new Separator());
        }
        else if (isText && !item.IsSecret)
        {
            AddMenuItem(menu, Loc.MenuSaveAs, "", () => _viewModel.SaveAsCommand.Execute(item));
            if (_settings.Current.ShowSnippetsTab)
                AddMenuItem(menu, Loc.MenuSaveAsSnippet, "", () => _ = _viewModel.SaveAsSnippetAsync(item));
            menu.Items.Add(new Separator());
        }

        // gif/mp4 de arquivo unico abre no editor de midia, se ele
        // existir nesta build e o arquivo ainda estiver no disco
        if (item.MediaFilePath is { } mediaPath &&
            Services.MediaEditorGateway.IsAvailable &&
            System.IO.File.Exists(mediaPath))
        {
            AddMenuItem(menu, Loc.MenuOpenInMediaEditor, "", () =>
            {
                HideFlyout();
                Services.MediaEditorGateway.Open(mediaPath);
            });
            menu.Items.Add(new Separator());
        }

        AddMenuItem(menu, item.IsPinned ? Loc.MenuUnpin : Loc.MenuPin, item.IsPinned ? "" : "",
            () => _viewModel.TogglePinCommand.Execute(item));
        AddMenuItem(menu, item.IsFavorite ? Loc.MenuUnfavorite : Loc.MenuFavorite, item.IsFavorite ? "" : "",
            () => _viewModel.ToggleFavoriteCommand.Execute(item));
        menu.Items.Add(new Separator());
        AddMenuItem(menu, Loc.MenuDelete, "", () => _viewModel.DeleteCommand.Execute(item));

        TrackMenu(menu);
        menu.IsOpen = true;
    }

    private void OnListRightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || FindItem(source) is not { } item)
            return;
        ItemsList.SelectedItem = item;
        ShowItemMenu(item, ItemsList, atMouse: true);
        e.Handled = true;
    }

    // a click on the glyph inside a card button has the glyph as OriginalSource, so walk up
    private static bool IsInsideButton(DependencyObject source)
    {
        var current = source;
        while (current is not null and not ListBoxItem)
        {
            if (current is System.Windows.Controls.Primitives.ButtonBase)
                return true;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    // ----- Number shortcuts -----

    private List<HistoryItemViewModel> VisibleItems()
    {
        var list = new List<HistoryItemViewModel>(9);
        foreach (var (container, _) in VisibleContainers())
        {
            if (container.DataContext is HistoryItemViewModel vm)
                list.Add(vm);
            if (list.Count == 9)
                break;
        }
        return list;
    }

    private void EnsureShortcutBadges()
    {
        if (!_badgesOn)
            ShowShortcutBadges(true);
    }

    /// <summary>Numbers 1 to 9 on the cards that are on screen, top to bottom. Off clears them.</summary>
    private void ShowShortcutBadges(bool on)
    {
        foreach (var vm in _badged)
            vm.ShortcutNumber = 0;
        _badged.Clear();

        if (!on)
        {
            _badgesOn = false;
            return;
        }
        // only the history list has numbered cards
        if (!IsVisible || _viewModel.IsPreviewOpen || _viewModel.IsSnippetEditing ||
            ItemsList.Visibility != Visibility.Visible)
        {
            _badgesOn = false;
            return;
        }
        _badgesOn = true;
        var visible = VisibleItems();
        for (var i = 0; i < visible.Count; i++)
        {
            visible[i].ShortcutNumber = i + 1;
            _badged.Add(visible[i]);
        }
    }

    // the badges follow the real key state, so letting go of one of two held keys keeps them
    private void RefreshShortcutBadges() =>
        ShowShortcutBadges(NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL) || NativeMethods.IsKeyDown(NativeMethods.VK_MENU));

    /// <summary>Ctrl+N copies the N-th visible card, Alt+N pastes it.</summary>
    private void RunNumberShortcut(int number, bool paste)
    {
        if (ItemsList.Visibility != Visibility.Visible)
            return;
        var visible = VisibleItems();
        if (number > visible.Count)
            return;
        var item = visible[number - 1];
        ItemsList.SelectedItem = item;

        if (paste)
        {
            // Alt is still down and its digit never reaches the app: tap a dummy key so the
            // app does not read a lone Alt as "open the menu bar"
            if (NativeMethods.IsKeyDown(NativeMethods.VK_MENU))
                NativeMethods.SendMaskKey();
            ShowShortcutBadges(false);
            _viewModel.PasteCommand.Execute(item);
            return;
        }
        _viewModel.CopyOnlyCommand.Execute(item);
        _ = _viewModel.ShowStatusAsync(string.Format(Loc.CopiedItemN, number));
    }

    // ----- Preview -----

    private double _previewZoom = 1;

    private bool ClosePreviewKey()
    {
        _viewModel.ClosePreview();
        return true;
    }

    // keys while the preview is open, from the hook (no focus)
    private void HandlePreviewKey(int vk)
    {
        switch (vk)
        {
            case VK_ESCAPE or VK_SPACE:
                _viewModel.ClosePreview();
                break;
            case VK_UP:
                ScrollPreview(-1);
                break;
            case VK_DOWN:
                ScrollPreview(+1);
                break;
            case VK_PRIOR:
                ScrollPreview(-8);
                break;
            case VK_NEXT:
                ScrollPreview(+8);
                break;
        }
    }

    // positive goes down; 8 or more is a page
    private bool ScrollPreview(int lines)
    {
        var page = Math.Abs(lines) >= 8;
        if (_viewModel.HasPreviewImage)
        {
            if (lines < 0)
            {
                if (page) PreviewImageScroll.PageUp(); else PreviewImageScroll.LineUp();
            }
            else
            {
                if (page) PreviewImageScroll.PageDown(); else PreviewImageScroll.LineDown();
            }
        }
        else
        {
            if (lines < 0)
            {
                if (page) PreviewTextBox.PageUp(); else PreviewTextBox.LineUp();
            }
            else
            {
                if (page) PreviewTextBox.PageDown(); else PreviewTextBox.LineDown();
            }
        }
        return true;
    }

    // zoom that shows one image pixel per screen pixel (the element is laid out in dips at the image dpi)
    private double ActualPixelsZoom()
    {
        if (_viewModel.PreviewImage is not System.Windows.Media.Imaging.BitmapSource bitmap)
            return 1;
        var screenScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var imageDpi = bitmap.DpiX > 0 ? bitmap.DpiX : 96;
        return imageDpi / (96 * screenScale);
    }

    private void SetPreviewZoom(double zoom)
    {
        _previewZoom = Math.Clamp(zoom, 0.05, 16);
        PreviewImageElement.LayoutTransform = new ScaleTransform(_previewZoom, _previewZoom);
    }

    private void FitPreviewImage()
    {
        if (_viewModel.PreviewImage is not System.Windows.Media.Imaging.BitmapSource bitmap)
            return;
        var viewW = PreviewImageScroll.ViewportWidth > 0 ? PreviewImageScroll.ViewportWidth : PreviewImageScroll.ActualWidth;
        var viewH = PreviewImageScroll.ViewportHeight > 0 ? PreviewImageScroll.ViewportHeight : PreviewImageScroll.ActualHeight;
        if (viewW <= 0 || viewH <= 0 || bitmap.Width <= 0 || bitmap.Height <= 0)
            return;
        // never blow a small image up to fill the panel
        SetPreviewZoom(Math.Min(1.0, Math.Min(viewW / bitmap.Width, viewH / bitmap.Height)));
    }

    private void OnPreviewWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
            return;
        SetPreviewZoom(_previewZoom * (e.Delta > 0 ? 1.15 : 1 / 1.15));
        e.Handled = true;
    }

    // ----- Drag out -----

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragItem = null;
        _suppressClick = false;
        if (e.OriginalSource is not DependencyObject source || IsInsideButton(source))
            return;
        _dragItem = FindItem(source);
        _dragStart = e.GetPosition(ItemsList);
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem is null)
            return;
        var pos = e.GetPosition(ItemsList);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var item = _dragItem;
        _dragItem = null;
        StartDrag(item);
    }

    private void StartDrag(HistoryItemViewModel item)
    {
        IDataObject? data;
        try
        {
            // the list only has slim rows; text and the rest come from the db right now
            var full = _viewModel.LoadFullItem(item);
            data = FlyoutDragData.Create(full, item.ImagePath, item.IsSecret, _settings.Current);
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("FlyoutDrag", ex);
            return;
        }
        if (data is null)
            return;

        // the mouse-up that ends the drag must not paste the card
        _suppressClick = true;
        var effect = DragDrop.DoDragDrop(ItemsList, data, DragDropEffects.Copy);
        if (effect == DragDropEffects.None)
            ReleaseFocusButStayOpen(); // dropped nowhere: the flyout stays
        else
            HideFlyout(restoreFocus: false); // the drop target already has the foreground
    }

    // ----- Snippets tab -----

    private void OnSnippetsClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || IsInsideButton(source) ||
            FindSnippet(source) is not { } snippet)
            return;
        if (Keyboard.Modifiers == ModifierKeys.Control)
            _viewModel.CopySnippetCommand.Execute(snippet);
        else
            _viewModel.PasteSnippetCommand.Execute(snippet);
    }

    private void OnSnippetCopyClick(object sender, RoutedEventArgs e)
    {
        _viewModel.CopySnippetCommand.Execute((sender as FrameworkElement)?.DataContext);
        e.Handled = true;
    }

    private void OnSnippetEditClick(object sender, RoutedEventArgs e)
    {
        _viewModel.EditSnippetCommand.Execute((sender as FrameworkElement)?.DataContext);
        e.Handled = true;
    }

    private void OnSnippetDeleteClick(object sender, RoutedEventArgs e)
    {
        _viewModel.DeleteSnippetCommand.Execute((sender as FrameworkElement)?.DataContext);
        e.Handled = true;
    }

    private static SnippetViewModel? FindSnippet(DependencyObject source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: SnippetViewModel vm })
                return vm;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
