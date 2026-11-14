using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Klip.Core.Settings;

namespace Klip.App.Views.Pages;

/// <summary>
/// "Clipboard" page: the Win+V panel, pasting, images as files and automatic cleanup.
/// The app list is an ObservableCollection bound once and changed item by item.
/// </summary>
public partial class ClipboardPage : ISettingsPage
{
    private const double BytesPerGb = 1024d * 1024 * 1024;

    private readonly SettingsService _settings;

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
            var on = AlwaysPlainTextToggle.IsChecked == true;
            RichPasteAppsCard.IsEnabled = on;
            if (on)
                PlainTextExpander.IsExpanded = true; // a lista so faz sentido com a opcao ligada
        };
        ShowEmojiTabToggle.Click += (_, _) => SaveToggle(ShowEmojiTabToggle, (s, v) => s.ShowEmojiTab = v);
        ShowSnippetsTabToggle.Click += (_, _) => SaveToggle(ShowSnippetsTabToggle, (s, v) => s.ShowSnippetsTab = v);
        RestoreClipboardToggle.Click += (_, _) => SaveToggle(RestoreClipboardToggle, (s, v) => s.RestoreClipboardAfterPaste = v);
        FlyoutSizeResetButton.Click += (_, _) =>
        {
            var defaults = new AppSettings();
            FlyoutWidthBox.Value = defaults.FlyoutWidth;
            FlyoutHeightBox.Value = defaults.FlyoutHeight;
        };
        EnterActionCombo.SelectionChanged += (_, _) =>
        {
            if (!_loading && EnterActionCombo.SelectedItem is ComboBoxItem { Tag: string action })
                _settings.Update(s => s.FlyoutEnterAction = action);
        };

        AddRichAppButton.Click += (_, _) => AppListEditor.Add(_settings, RichAppBox, _richApps, s => s.RichPasteApps);
        RichAppBox.KeyDown += (_, e) => AppListEditor.OnEnter(e, () => AppListEditor.Add(_settings, RichAppBox, _richApps, s => s.RichPasteApps));

        // one handler for N rows: the remove button Click bubbles up to the list
        RichAppsList.AddHandler(ButtonBase.ClickEvent,
            new RoutedEventHandler((_, e) => AppListEditor.Remove(_settings, e, _richApps, s => s.RichPasteApps)));

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
            ShowEmojiTabToggle.IsChecked = s.ShowEmojiTab;
            ShowSnippetsTabToggle.IsChecked = s.ShowSnippetsTab;
            RestoreClipboardToggle.IsChecked = s.RestoreClipboardAfterPaste;
            AppListEditor.Sync(_richApps, s.RichPasteApps);
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
}
