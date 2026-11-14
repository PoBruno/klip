using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Klip.App.Localization;
using Klip.Core.Settings;

namespace Klip.App.Views.Pages;

/// <summary>
/// Privacy: skip (or briefly keep) things that look like secrets, apps whose copies are
/// never saved, and clearing the history on exit.
/// </summary>
public partial class PrivacyPage : ISettingsPage
{
    // 0 = don't keep (skipped like before), then 30 s, 60 s, 2 min
    private static readonly int[] SecretKeepPresets = [0, 30, 60, 120];

    private readonly SettingsService _settings;
    private readonly ObservableCollection<string> _excludedApps = [];

    // reentrancy guard, always reset in a finally
    private bool _loading;

    public PrivacyPage(SettingsService settings)
    {
        PageDiagnostics.TrackConstruction(this);
        _settings = settings;
        InitializeComponent();

        foreach (var seconds in SecretKeepPresets)
            SecretKeepCombo.Items.Add(new ComboBoxItem { Tag = seconds });

        ExcludedAppsList.ItemsSource = _excludedApps;

        // handlers in the constructor, never in Loaded (singleton page)
        SkipSecretsToggle.Click += OnSkipSecretsClick;
        SecretKeepCombo.SelectionChanged += OnSecretKeepChanged;
        ClearOnExitToggle.Click += OnClearOnExitClick;

        AddExcludeButton.Click += (_, _) => AppListEditor.Add(_settings, ExcludeAppBox, _excludedApps, s => s.ExcludedApps);
        ExcludeAppBox.KeyDown += (_, e) => AppListEditor.OnEnter(e,
            () => AppListEditor.Add(_settings, ExcludeAppBox, _excludedApps, s => s.ExcludedApps));
        // one handler for N rows: the remove button Click bubbles up to the list
        ExcludedAppsList.AddHandler(ButtonBase.ClickEvent,
            new RoutedEventHandler((_, e) => AppListEditor.Remove(_settings, e, _excludedApps, s => s.ExcludedApps)));
        ExcludedAppsList.PreviewMouseWheel += OnListPreviewMouseWheel;

        RefreshState();
    }

    /// <inheritdoc />
    public void RefreshState()
    {
        _loading = true;
        try
        {
            var s = _settings.Current;
            SkipSecretsToggle.IsChecked = s.SkipSecrets;
            SecretKeepCard.IsEnabled = s.SkipSecrets;
            RelabelSecretKeep();
            SecretKeepCombo.SelectedItem = FindPreset(s.SecretKeepSeconds) ?? FindPreset(0);
            ClearOnExitToggle.IsChecked = s.ClearHistoryOnExit;
            AppListEditor.Sync(_excludedApps, s.ExcludedApps);
            IgnoredAppsExpander.Status = s.ExcludedApps.Count > 0
                ? string.Format(Loc.IgnoredAppsCount, s.ExcludedApps.Count)
                : null;
        }
        finally
        {
            _loading = false;
        }
    }

    private void RelabelSecretKeep()
    {
        foreach (var item in SecretKeepCombo.Items)
        {
            if (item is not ComboBoxItem { Tag: int seconds } entry)
                continue;
            entry.Content = seconds switch
            {
                0 => Loc.SecretKeepNever,
                < 120 => string.Format(Loc.SecretKeepSeconds, seconds),
                _ => string.Format(Loc.SecretKeepMinutes, seconds / 60),
            };
        }
    }

    private ComboBoxItem? FindPreset(int seconds)
    {
        foreach (var item in SecretKeepCombo.Items)
        {
            if (item is ComboBoxItem { Tag: int tag } entry && tag == seconds)
                return entry;
        }
        return null;
    }

    // applies to the next ingest
    private void OnSkipSecretsClick(object sender, RoutedEventArgs e)
    {
        var on = SkipSecretsToggle.IsChecked == true;
        SecretKeepCard.IsEnabled = on;
        if (on)
            SecretsExpander.IsExpanded = true;
        if (_loading)
            return;

        _settings.Update(s => s.SkipSecrets = on);
    }

    private void OnSecretKeepChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SecretKeepCombo.SelectedItem is not ComboBoxItem { Tag: int seconds })
            return;

        _settings.Update(s => s.SecretKeepSeconds = seconds);
    }

    private void OnClearOnExitClick(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        _settings.Update(s => s.ClearHistoryOnExit = ClearOnExitToggle.IsChecked == true);
    }

    /// <summary>
    /// The ignored apps list scrolls on its own (virtualization needs a finite viewport),
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
