using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Klip.Core.Settings;

namespace Klip.App.Views.Pages;

/// <summary>
/// "History" page: retention (items, images, media space, age, excluded apps) and
/// the Win+V panel options. The app lists are ObservableCollections bound once and
/// changed item by item, so adding or removing an app never rebuilds every row.
/// </summary>
public partial class ClipboardPage : ISettingsPage
{
    private const double BytesPerGb = 1024d * 1024 * 1024;

    private readonly SettingsService _settings;

    private readonly ObservableCollection<string> _excludedApps = [];
    private readonly ObservableCollection<string> _richApps = [];

    /// <summary>
    /// Reentrancy guard: setting NumberBox.Value raises ValueChanged, and without it the
    /// refresh would write back what it just read. Always in try/finally (see RefreshState).
    /// </summary>
    private bool _loading;

    public ClipboardPage(SettingsService settings)
    {
        _settings = settings;
        PageDiagnostics.TrackConstruction(this);
        InitializeComponent();

        ExcludedAppsList.ItemsSource = _excludedApps;
        RichAppsList.ItemsSource = _richApps;

        // wired in the CONSTRUCTOR, never in Loaded: the page is a singleton and Loaded
        // fires on every visit, which would stack the handlers
        MaxItemsBox.ValueChanged += (_, _) => Save(MaxItemsBox, (s, v) => s.RetentionMaxItems = (int)v);
        MaxImagesBox.ValueChanged += (_, _) => Save(MaxImagesBox, (s, v) => s.RetentionMaxImages = (int)v);
        MaxMediaBox.ValueChanged += (_, _) => Save(MaxMediaBox, (s, v) => s.RetentionMaxMediaBytes = (long)(v * BytesPerGb));
        MaxAgeBox.ValueChanged += (_, _) => Save(MaxAgeBox, (s, v) => s.RetentionMaxAgeDays = (int)v);
        FlyoutWidthBox.ValueChanged += (_, _) => Save(FlyoutWidthBox, (s, v) => s.FlyoutWidth = Math.Max(360, v));
        FlyoutHeightBox.ValueChanged += (_, _) => Save(FlyoutHeightBox, (s, v) => s.FlyoutHeight = Math.Max(460, v));

        OpenAtCaretToggle.Click += (_, _) => SaveToggle(OpenAtCaretToggle, (s, v) => s.FlyoutOpenAtCaret = v);
        RememberScrollToggle.Click += (_, _) => SaveToggle(RememberScrollToggle, (s, v) => s.FlyoutRememberScroll = v);
        AlwaysPlainTextToggle.Click += (_, _) =>
        {
            SaveToggle(AlwaysPlainTextToggle, (s, v) => s.AlwaysPastePlainText = v);
            RichPasteAppsCard.IsEnabled = AlwaysPlainTextToggle.IsChecked == true;
        };
        EnterActionCombo.SelectionChanged += (_, _) =>
        {
            if (!_loading && EnterActionCombo.SelectedItem is ComboBoxItem { Tag: string action })
                _settings.Update(s => s.FlyoutEnterAction = action);
        };

        AddExcludeButton.Click += (_, _) => AddApp(ExcludeAppBox, _excludedApps, s => s.ExcludedApps);
        ExcludeAppBox.KeyDown += (_, e) => OnEnter(e, () => AddApp(ExcludeAppBox, _excludedApps, s => s.ExcludedApps));
        AddRichAppButton.Click += (_, _) => AddApp(RichAppBox, _richApps, s => s.RichPasteApps);
        RichAppBox.KeyDown += (_, e) => OnEnter(e, () => AddApp(RichAppBox, _richApps, s => s.RichPasteApps));

        // one handler for N rows: the remove button Click bubbles up to the list
        ExcludedAppsList.AddHandler(ButtonBase.ClickEvent,
            new RoutedEventHandler((_, e) => RemoveApp(e, _excludedApps, s => s.ExcludedApps)));
        RichAppsList.AddHandler(ButtonBase.ClickEvent,
            new RoutedEventHandler((_, e) => RemoveApp(e, _richApps, s => s.RichPasteApps)));
        ExcludedAppsList.PreviewMouseWheel += OnListPreviewMouseWheel;

        RefreshState();
    }

    /// <summary>Reloads the saved values. Runs at the end of the constructor and on every visit.</summary>
    public void RefreshState()
    {
        _loading = true;
        try
        {
            var s = _settings.Current;
            MaxItemsBox.Value = s.RetentionMaxItems;
            MaxImagesBox.Value = s.RetentionMaxImages;
            MaxMediaBox.Value = Math.Round(s.RetentionMaxMediaBytes / BytesPerGb, 1);
            MaxAgeBox.Value = s.RetentionMaxAgeDays;
            FlyoutWidthBox.Value = Math.Round(s.FlyoutWidth);
            FlyoutHeightBox.Value = Math.Round(s.FlyoutHeight);
            OpenAtCaretToggle.IsChecked = s.FlyoutOpenAtCaret;
            RememberScrollToggle.IsChecked = s.FlyoutRememberScroll;
            AlwaysPlainTextToggle.IsChecked = s.AlwaysPastePlainText;
            RichPasteAppsCard.IsEnabled = s.AlwaysPastePlainText;
            EnterActionCombo.SelectedIndex =
                string.Equals(s.FlyoutEnterAction, "copy", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            Sync(_excludedApps, s.ExcludedApps);
            Sync(_richApps, s.RichPasteApps);
        }
        finally
        {
            _loading = false;
        }
    }

    private void Save(Wpf.Ui.Controls.NumberBox box, Action<AppSettings, double> apply)
    {
        // Value is null while the box is empty: keep the last valid setting
        if (_loading || box.Value is not { } value)
            return;
        _settings.Update(s => apply(s, value));
    }

    private void SaveToggle(Wpf.Ui.Controls.ToggleSwitch toggle, Action<AppSettings, bool> apply)
    {
        if (_loading)
            return;
        var on = toggle.IsChecked == true;
        _settings.Update(s => apply(s, on));
    }

    // ----- App lists -----

    private static void OnEnter(KeyEventArgs e, Action action)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        action();
    }

    private void AddApp(Wpf.Ui.Controls.TextBox box, ObservableCollection<string> view,
        Func<AppSettings, List<string>> list)
    {
        var name = box.Text.Trim();
        if (name.Length == 0)
            return;

        // process names: no dot gets ".exe", and dedupe ignores case like Windows does
        if (!name.Contains('.'))
            name += ".exe";

        var added = false;
        _settings.Update(s =>
        {
            var apps = list(s);
            if (apps.Contains(name, StringComparer.OrdinalIgnoreCase))
                return;
            apps.Add(name);
            added = true;
        });

        box.Text = "";
        if (added)
            view.Add(name);
    }

    private void RemoveApp(RoutedEventArgs e, ObservableCollection<string> view,
        Func<AppSettings, List<string>> list)
    {
        // the button DataContext already is the process name
        if (e.OriginalSource is not FrameworkElement { DataContext: string app })
            return;

        e.Handled = true;
        _settings.Update(s => list(s).RemoveAll(
            a => string.Equals(a, app, StringComparison.OrdinalIgnoreCase)));

        for (var i = view.Count - 1; i >= 0; i--)
        {
            if (string.Equals(view[i], app, StringComparison.OrdinalIgnoreCase))
                view.RemoveAt(i);
        }
    }

    /// <summary>Only rebuilds the collection when the saved list really changed.</summary>
    private static void Sync(ObservableCollection<string> view, List<string> saved)
    {
        if (view.Count == saved.Count && view.SequenceEqual(saved, StringComparer.Ordinal))
            return;
        view.Clear();
        foreach (var app in saved)
            view.Add(app);
    }

    /// <summary>
    /// The excluded apps list scrolls on its own (virtualization needs a finite viewport),
    /// and a WPF ScrollViewer eats the wheel even when it can't move. At either end the
    /// wheel goes back to the page scroller.
    /// </summary>
    private void OnListPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ItemsControl list ||
            list.Template?.FindName("PART_ListScroll", list) is not ScrollViewer scroll)
            return;

        var atTop = e.Delta > 0 && scroll.VerticalOffset <= 0;
        var atBottom = e.Delta < 0 && scroll.VerticalOffset >= scroll.ScrollableHeight;
        if (!atTop && !atBottom)
            return;

        e.Handled = true;
        // MouseWheelEvent only bubbles: the re-raised one goes up to the page scroller
        list.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = list,
        });
    }
}
