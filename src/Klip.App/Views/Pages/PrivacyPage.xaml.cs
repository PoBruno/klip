using System.Windows;
using System.Windows.Controls;
using Klip.App.Localization;
using Klip.Core.Settings;

namespace Klip.App.Views.Pages;

/// <summary>
/// Privacy: skip (or briefly keep) things that look like secrets, clear the history
/// on exit and restore the clipboard after pasting.
/// </summary>
public partial class PrivacyPage : ISettingsPage
{
    // 0 = don't keep (skipped like before), then 30 s, 60 s, 2 min
    private static readonly int[] SecretKeepPresets = [0, 30, 60, 120];

    private readonly SettingsService _settings;

    // reentrancy guard, always reset in a finally
    private bool _loading;

    public PrivacyPage(SettingsService settings)
    {
        PageDiagnostics.TrackConstruction(this);
        _settings = settings;
        InitializeComponent();

        foreach (var seconds in SecretKeepPresets)
            SecretKeepCombo.Items.Add(new ComboBoxItem { Tag = seconds });

        // handlers in the constructor, never in Loaded (singleton page)
        SkipSecretsToggle.Click += OnSkipSecretsClick;
        SecretKeepCombo.SelectionChanged += OnSecretKeepChanged;
        RestoreClipboardToggle.Click += OnRestoreClipboardClick;
        ClearOnExitToggle.Click += OnClearOnExitClick;

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
            RestoreClipboardToggle.IsChecked = s.RestoreClipboardAfterPaste;
            ClearOnExitToggle.IsChecked = s.ClearHistoryOnExit;
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

    private void OnRestoreClipboardClick(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        _settings.Update(s => s.RestoreClipboardAfterPaste = RestoreClipboardToggle.IsChecked == true);
    }

    private void OnClearOnExitClick(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        _settings.Update(s => s.ClearHistoryOnExit = ClearOnExitToggle.IsChecked == true);
    }
}
