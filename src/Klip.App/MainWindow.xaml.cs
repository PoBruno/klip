using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Klip.App.Localization;
using Klip.App.Services;
using Klip.Core.Settings;
using Klip.Core.Storage;
using Klip.Interop.SystemIntegration;
using Wpf.Ui.Appearance;

namespace Klip.App;

/// <summary>
/// Settings window: cards like the Windows 11 Settings app, plus the
/// takeover of the native hotkeys. Closing minimizes to the tray.
/// Code-behind drives the system actions, persistence goes through SettingsService.
/// </summary>
public partial class MainWindow
{
    private readonly ClipboardItemRepository _repository;
    private readonly SettingsService _settings;
    private readonly SystemHotkeyService _systemHotkeys;
    private readonly AutostartService _autostart;
    private readonly BackupService _backup;
    private bool _loading;

    public MainWindow(
        ClipboardItemRepository repository,
        SettingsService settings,
        SystemHotkeyService systemHotkeys,
        AutostartService autostart,
        BackupService backup)
    {
        _repository = repository;
        _settings = settings;
        _systemHotkeys = systemHotkeys;
        _autostart = autostart;
        _backup = backup;
        InitializeComponent();
        SystemThemeWatcher.Watch(this);

        WireControls();
        Loaded += (_, _) => RefreshStatus();
    }

    private void WireControls()
    {
        // Takeovers
        TakeWinVButton.Click += async (_, _) => await OnTakeWinV();
        TakePrtScButton.Click += (_, _) => OnTakePrintScreen();
        TakeWinShiftSButton.Click += async (_, _) => await OnTakeWinShiftS();
        RevertButton.Click += async (_, _) => await OnRevert();

        // hotkey editor: click the keycap button to start capturing, then the
        // keys you press show up live as keycaps
        HistoryHotkeyButton.Click += (_, _) => BeginCapture(HotkeySlot.History);
        CaptureHotkeyButton.Click += (_, _) => BeginCapture(HotkeySlot.Capture);
        StopRecHotkeyButton.Click += (_, _) => BeginCapture(HotkeySlot.StopRecording);
        HistoryHotkeyButton.PreviewKeyDown += (_, e) => CaptureHotkey(e, HotkeySlot.History);
        CaptureHotkeyButton.PreviewKeyDown += (_, e) => CaptureHotkey(e, HotkeySlot.Capture);
        StopRecHotkeyButton.PreviewKeyDown += (_, e) => CaptureHotkey(e, HotkeySlot.StopRecording);
        HistoryHotkeyButton.LostKeyboardFocus += (_, _) => EndCapture(HotkeySlot.History);
        CaptureHotkeyButton.LostKeyboardFocus += (_, _) => EndCapture(HotkeySlot.Capture);
        StopRecHotkeyButton.LostKeyboardFocus += (_, _) => EndCapture(HotkeySlot.StopRecording);

        // settings applied on the spot
        AutostartToggle.Click += (_, _) =>
        {
            if (_loading)
                return;
            _settings.Update(s => s.StartWithWindows = AutostartToggle.IsChecked == true);
            try
            {
                _autostart.SetEnabled(AutostartToggle.IsChecked == true);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("Autostart", ex);
            }
        };
        AutoSaveToggle.Click += (_, _) =>
        {
            if (!_loading)
                _settings.Update(s => s.AutoSaveScreenshots = AutoSaveToggle.IsChecked == true);
        };
        MaxItemsBox.ValueChanged += (_, _) =>
        {
            if (!_loading && MaxItemsBox.Value is { } value)
                _settings.Update(s => s.RetentionMaxItems = (int)value);
        };
        MaxAgeBox.ValueChanged += (_, _) =>
        {
            if (!_loading && MaxAgeBox.Value is { } value)
                _settings.Update(s => s.RetentionMaxAgeDays = (int)value);
        };
        ChooseFolderButton.Click += (_, _) => ChooseScreenshotFolder();
        ScrollDelaySlider.ValueChanged += (_, _) =>
        {
            ScrollDelayLabel.Text = $"{(int)ScrollDelaySlider.Value} ms";
            if (!_loading)
                _settings.Update(s => s.ScrollCaptureDelayMs = (int)ScrollDelaySlider.Value);
        };

        // RF-F1.02: modificador que abre a captura direto no editor (Ctrl padrão)
        EditorModifierCombo.Items.Add(new ComboBoxItem { Content = "Ctrl", Tag = CaptureEditorModifier.Control });
        EditorModifierCombo.Items.Add(new ComboBoxItem { Content = "Shift", Tag = CaptureEditorModifier.Shift });
        EditorModifierCombo.Items.Add(new ComboBoxItem { Content = "Alt", Tag = CaptureEditorModifier.Alt });
        EditorModifierCombo.Items.Add(new ComboBoxItem { Content = Loc.ModifierDisabled, Tag = CaptureEditorModifier.None });
        EditorModifierCombo.SelectionChanged += (_, _) =>
        {
            if (!_loading && EditorModifierCombo.SelectedItem is ComboBoxItem selected)
                _settings.Update(s => s.EditorModifierKey = (CaptureEditorModifier)selected.Tag);
        };

        // RF-F1.05: toda captura estática abre no editor
        AlwaysEditorToggle.Click += (_, _) =>
        {
            if (!_loading)
                _settings.Update(s => s.AlwaysOpenEditorAfterCapture = AlwaysEditorToggle.IsChecked == true);
        };

        // ----- Gravação de tela (specs F3/F4) -----

        ChooseRecordingsFolderButton.Click += (_, _) => ChooseRecordingsFolder();

        // RF-F5.14: caminho manual do ffmpeg.exe para o editor de midia
        ChooseFfmpegButton.Click += (_, _) => ChooseFfmpegPath();

        // RF-F4.02: FPS do GIF com o valor EFETIVO do formato na lista
        foreach (var fps in new[] { 10, 15, 20 })
            GifFpsCombo.Items.Add(new ComboBoxItem { Content = BuildGifFpsLabel(fps), Tag = fps });
        GifFpsCombo.SelectionChanged += (_, _) =>
        {
            if (!_loading && GifFpsCombo.SelectedItem is ComboBoxItem selected)
                _settings.Update(s => s.GifFps = (int)selected.Tag);
        };

        // RF-F4.03: escala da gravação GIF
        foreach (var scale in new[] { 100, 75, 50 })
            GifScaleCombo.Items.Add(new ComboBoxItem { Content = $"{scale}%", Tag = scale });
        GifScaleCombo.SelectionChanged += (_, _) =>
        {
            if (!_loading && GifScaleCombo.SelectedItem is ComboBoxItem selected)
                _settings.Update(s => s.GifScalePercent = (int)selected.Tag);
        };

        // Q-F3.1 resolvida como presets: Automático / 5 / 8 / 16 Mbps
        Mp4BitrateCombo.Items.Add(new ComboBoxItem { Content = Loc.Mp4BitrateAuto, Tag = 0 });
        foreach (var kbps in new[] { 5000, 8000, 16000 })
            Mp4BitrateCombo.Items.Add(new ComboBoxItem { Content = $"{kbps / 1000} Mbps", Tag = kbps });
        Mp4BitrateCombo.SelectionChanged += (_, _) =>
        {
            if (!_loading && Mp4BitrateCombo.SelectedItem is ComboBoxItem selected)
                _settings.Update(s => s.Mp4BitrateKbps = (int)selected.Tag);
        };

        // RF-F3.04: modo reunião
        HideRecordingBorderToggle.Click += (_, _) =>
        {
            if (!_loading)
                _settings.Update(s => s.HideRecordingBorder = HideRecordingBorderToggle.IsChecked == true);
        };

        // language (defaults to Windows; applies right away via DynamicResource)
        LanguageCombo.Items.Add(new ComboBoxItem { Content = Loc.LanguageSystem, Tag = "system" });
        foreach (var (value, display) in Loc.AvailableLanguages)
            LanguageCombo.Items.Add(new ComboBoxItem { Content = display, Tag = value });
        LanguageCombo.SelectionChanged += (_, _) =>
        {
            if (_loading || LanguageCombo.SelectedItem is not ComboBoxItem selected)
                return;
            var language = (string)selected.Tag;
            _settings.Update(s => s.Language = language);
            Loc.Initialize(language); // reloads resources and fires the event, whole UI swaps
            ((ComboBoxItem)LanguageCombo.Items[0]).Content = Loc.LanguageSystem;
            RefreshStatus();
        };

        // manual theme, applied right away
        ThemeCombo.Items.Add(new ComboBoxItem { Content = Loc.ThemeSystem, Tag = "system" });
        ThemeCombo.Items.Add(new ComboBoxItem { Content = Loc.ThemeLight, Tag = "light" });
        ThemeCombo.Items.Add(new ComboBoxItem { Content = Loc.ThemeDark, Tag = "dark" });
        ThemeCombo.SelectionChanged += (_, _) =>
        {
            if (_loading || ThemeCombo.SelectedItem is not ComboBoxItem selected)
                return;
            var theme = (string)selected.Tag;
            _settings.Update(s => s.Theme = theme);
            ThemeManager.Apply(theme);
        };

        // excluded apps
        AddExcludeButton.Click += (_, _) => AddExcludedApp();
        ExcludeAppBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                AddExcludedApp();
        };

        // Privacy
        SkipSecretsToggle.Click += (_, _) => { if (!_loading) _settings.Update(s => s.SkipSecrets = SkipSecretsToggle.IsChecked == true); };
        RestoreClipboardToggle.Click += (_, _) => { if (!_loading) _settings.Update(s => s.RestoreClipboardAfterPaste = RestoreClipboardToggle.IsChecked == true); };
        ClearOnExitToggle.Click += (_, _) => { if (!_loading) _settings.Update(s => s.ClearHistoryOnExit = ClearOnExitToggle.IsChecked == true); };

        // Flyout: show/hide the emoji tab (takes effect on the next open)
        ShowEmojiTabToggle.Click += (_, _) => { if (!_loading) _settings.Update(s => s.ShowEmojiTab = ShowEmojiTabToggle.IsChecked == true); };

        // Maintenance and backup
        ExportButton.Click += (_, _) => OnExport();
        ImportButton.Click += (_, _) => OnImport();
        CompactButton.Click += (_, _) => OnCompact();
        OpenDataFolderButton.Click += (_, _) => OnOpenDataFolder();
        DiagnosticsButton.Click += (_, _) => OnRunDiagnostics();
    }

    // ----- Backup and maintenance -----

    private void OnExport()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = Loc.BackupFilter,
            FileName = $"Klip {DateTime.Now:yyyy-MM-dd}.zip",
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            var r = _backup.Export(dialog.FileName);
            MaintenanceStatus.Text = string.Format(Loc.ExportDone, r.Items, r.MediaFiles);
        }
        catch (Exception ex)
        {
            MaintenanceStatus.Text = ex.Message;
            StartupLog.WriteException("Export", ex);
        }
    }

    private void OnImport()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = Loc.BackupFilter };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            var r = _backup.Import(dialog.FileName);
            MaintenanceStatus.Text = string.Format(Loc.ImportDone, r.Imported, r.SkippedDuplicates);
            RefreshStatus();
        }
        catch (Exception ex)
        {
            MaintenanceStatus.Text = ex.Message;
            StartupLog.WriteException("Import", ex);
        }
    }

    private void OnCompact()
    {
        try
        {
            _repository.Vacuum();
            MaintenanceStatus.Text = Loc.CompactDone;
        }
        catch (Exception ex)
        {
            MaintenanceStatus.Text = ex.Message;
        }
    }

    private void OnOpenDataFolder()
    {
        try
        {
            Klip.Core.Common.AppPaths.EnsureCreated();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Klip.Core.Common.AppPaths.Root,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("OpenDataFolder", ex);
        }
    }

    private void OnRunDiagnostics()
    {
        try
        {
            var state = _systemHotkeys.GetState();
            var dbFile = Klip.Core.Common.AppPaths.DatabaseFile;
            var dbSize = File.Exists(dbFile) ? new FileInfo(dbFile).Length : 0;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Itens no histórico: {_repository.Count()}");
            sb.AppendLine($"Tamanho do banco: {dbSize / 1024.0 / 1024.0:F1} MB");
            sb.AppendLine($"Atalho histórico: {_settings.Current.HotkeyHistory}");
            sb.AppendLine($"Atalho captura: {_settings.Current.HotkeyCapture}");
            sb.AppendLine($"DisabledHotkeys (registro): {state.DisabledHotkeys ?? "(vazio)"}");
            sb.AppendLine($"Win+V liberado: {state.WinVFreed}   Win+S liberado: {state.WinSFreed}");
            sb.AppendLine($"PrtSc liberado: {state.PrintScreenFreed}");
            sb.AppendLine($"HKLM clipboard desativado: {state.HklmClipboardFeatureOff}");
            sb.AppendLine($"Políticas corporativas: {state.HasManagedPolicies}");
            DiagnosticsText.Text = sb.ToString();
        }
        catch (Exception ex)
        {
            DiagnosticsText.Text = ex.Message;
        }
    }

    private void ChooseScreenshotFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Localization.Loc.ChooseFolder,
            InitialDirectory = ScreenshotFolderBox.Text,
        };
        if (dialog.ShowDialog() != true)
            return;
        ScreenshotFolderBox.Text = dialog.FolderName;
        _settings.Update(s => s.ScreenshotsFolder = dialog.FolderName);
    }

    /// <summary>RF-F3.06: pasta configurável das gravações.</summary>
    private void ChooseRecordingsFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Localization.Loc.ChooseFolder,
            InitialDirectory = RecordingsFolderBox.Text,
        };
        if (dialog.ShowDialog() != true)
            return;
        RecordingsFolderBox.Text = dialog.FolderName;
        _settings.Update(s => s.RecordingsFolder = dialog.FolderName);
    }

    /// <summary>RF-F5.14: escolha manual do ffmpeg.exe (download automático fica para depois).</summary>
    private void ChooseFfmpegPath()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Localization.Loc.FfmpegChoose,
            Filter = "ffmpeg.exe|ffmpeg.exe|*.exe|*.exe",
        };
        if (dialog.ShowDialog() != true)
            return;
        FfmpegPathBox.Text = dialog.FileName;
        _settings.Update(s => s.FfmpegPath = dialog.FileName);
    }

    /// <summary>RF-F4.02: "15 fps (efetivo 14,3)" - honestidade com a grade de centésimos.</summary>
    private static string BuildGifFpsLabel(int fps)
    {
        var effective = Core.Recording.GifRecordingMath.EffectiveFps(fps);
        return Math.Abs(effective - fps) < 0.05
            ? $"{fps} fps"
            : string.Format(Loc.GifFpsEffective, fps, effective.ToString("0.#"));
    }

    private void AddExcludedApp()
    {
        var name = ExcludeAppBox.Text.Trim();
        if (name.Length == 0)
            return;
        if (!name.Contains('.'))
            name += ".exe";
        _settings.Update(s =>
        {
            if (!s.ExcludedApps.Contains(name, StringComparer.OrdinalIgnoreCase))
                s.ExcludedApps.Add(name);
        });
        ExcludeAppBox.Text = "";
        RefreshExcludedApps();
    }

    private void OnRemoveExcludedApp(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string app })
        {
            _settings.Update(s => s.ExcludedApps.RemoveAll(a => string.Equals(a, app, StringComparison.OrdinalIgnoreCase)));
            RefreshExcludedApps();
        }
    }

    private void RefreshExcludedApps() =>
        ExcludedAppsList.ItemsSource = _settings.Current.ExcludedApps.ToList();

    /// <summary>Seleciona o item do combo cujo Tag (int) bate com o valor salvo.</summary>
    private static void SelectComboByTag(ComboBox combo, int value, int fallbackIndex)
    {
        combo.SelectedIndex = fallbackIndex;
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && (int)item.Tag == value)
            {
                combo.SelectedIndex = i;
                break;
            }
        }
    }

    public void RefreshStatus()
    {
        _loading = true;

        var s = _settings.Current;
        HistoryHotkeyChord.Chord = s.HotkeyHistory;
        CaptureHotkeyChord.Chord = s.HotkeyCapture;
        StopRecHotkeyChord.Chord = s.StopRecordingHotkey;
        AutostartToggle.IsChecked = s.StartWithWindows;
        AutoSaveToggle.IsChecked = s.AutoSaveScreenshots;
        SkipSecretsToggle.IsChecked = s.SkipSecrets;
        RestoreClipboardToggle.IsChecked = s.RestoreClipboardAfterPaste;
        ClearOnExitToggle.IsChecked = s.ClearHistoryOnExit;
        ShowEmojiTabToggle.IsChecked = s.ShowEmojiTab;
        MaxItemsBox.Value = s.RetentionMaxItems;
        MaxAgeBox.Value = s.RetentionMaxAgeDays;
        ScreenshotFolderBox.Text = s.ScreenshotsFolder
            ?? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        ScrollDelaySlider.Value = s.ScrollCaptureDelayMs;
        ScrollDelayLabel.Text = $"{s.ScrollCaptureDelayMs} ms";

        // Gravação de tela (specs F3/F4)
        RecordingsFolderBox.Text = Core.Recording.RecordingPaths.Resolve(s.RecordingsFolder);
        FfmpegPathBox.Text = s.FfmpegPath;
        HideRecordingBorderToggle.IsChecked = s.HideRecordingBorder;
        SelectComboByTag(GifFpsCombo, s.GifFps, fallbackIndex: 1);
        SelectComboByTag(GifScaleCombo, s.GifScalePercent, fallbackIndex: 0);
        SelectComboByTag(Mp4BitrateCombo, s.Mp4BitrateKbps, fallbackIndex: 0);
        ((ComboBoxItem)Mp4BitrateCombo.Items[0]).Content = Loc.Mp4BitrateAuto;
        for (var i = 0; i < GifFpsCombo.Items.Count; i++)
        {
            // relabela após troca de idioma (o "efetivo" é localizado)
            if (GifFpsCombo.Items[i] is ComboBoxItem item)
                item.Content = BuildGifFpsLabel((int)item.Tag);
        }

        // F1: seleciona o modificador salvo por Tag e relabela "Desativado" pós-troca de idioma
        AlwaysEditorToggle.IsChecked = s.AlwaysOpenEditorAfterCapture;
        EditorModifierCombo.SelectedIndex = 0;
        for (var i = 0; i < EditorModifierCombo.Items.Count; i++)
        {
            if (EditorModifierCombo.Items[i] is ComboBoxItem item
                && (CaptureEditorModifier)item.Tag == s.EditorModifierKey)
            {
                EditorModifierCombo.SelectedIndex = i;
                break;
            }
        }
        ((ComboBoxItem)EditorModifierCombo.Items[3]).Content = Loc.ModifierDisabled;

        // pick the language by Tag (the list is dynamic)
        LanguageCombo.SelectedIndex = 0;
        for (var i = 0; i < LanguageCombo.Items.Count; i++)
        {
            if (LanguageCombo.Items[i] is ComboBoxItem item && (string)item.Tag == s.Language)
            {
                LanguageCombo.SelectedIndex = i;
                break;
            }
        }

        ThemeCombo.SelectedIndex = s.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        // relabel the combo items after a language switch
        ((ComboBoxItem)ThemeCombo.Items[0]).Content = Loc.ThemeSystem;
        ((ComboBoxItem)ThemeCombo.Items[1]).Content = Loc.ThemeLight;
        ((ComboBoxItem)ThemeCombo.Items[2]).Content = Loc.ThemeDark;
        RefreshExcludedApps();

        RefreshTakeoverState();

        AboutText.Text = Loc.AboutText;
        StatusText.Text = string.Format(Loc.ItemsInHistory, _repository.Count());

        _loading = false;
    }

    /// <summary>Estado real lido do registro, nao chutado.</summary>
    private void RefreshTakeoverState()
    {
        try
        {
            var state = _systemHotkeys.GetState();
            var s = _settings.Current;

            WinVStatus.Text = s.HotkeyHistory == "Win+V"
                ? Loc.WinVActive
                : state.WinVFreed
                    ? Loc.WinVFreedNotBound
                    : Loc.WinVNative;
            TakeWinVButton.IsEnabled = s.HotkeyHistory != "Win+V";

            CaptureKeyStatus.Text = s.HotkeyCapture switch
            {
                "PrintScreen" => Loc.PrtScActive,
                "Win+Shift+S" => Loc.WinShiftSActive,
                _ => state.PrintScreenFreed
                    ? string.Format(Loc.PrtScFreeInfo, s.HotkeyCapture)
                    : string.Format(Loc.PrtScNativeInfo, s.HotkeyCapture),
            };

            if (state.HasManagedPolicies)
                WinVStatus.Text += Loc.ManagedPolicyWarning;
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("RefreshTakeoverState", ex);
        }
    }

    // ----- Takeover flows -----

    private async Task OnTakeWinV()
    {
        var confirm = MessageBox.Show(this, Loc.ConfirmWinV, Loc.ConfirmWinVTitle,
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        SetBusy(true, Loc.BusyApplying);
        var result = await ((App)Application.Current).TakeoverWinVAsync();

        if (result == "precisa-hklm")
        {
            // 24H2 fallback
            var fallback = MessageBox.Show(this, Loc.ConfirmHklm, Loc.ConfirmHklmTitle,
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (fallback == MessageBoxResult.Yes)
                result = await ((App)Application.Current).TakeoverWinVWithHklmFallbackAsync();
        }

        SetBusy(false, result switch
        {
            "ok" => Loc.ResultWinVOk,
            "uac-cancelado" => Loc.ResultUacCancelled,
            _ => Loc.ResultWinVFail,
        });
        RefreshStatus();
    }

    private void OnTakePrintScreen()
    {
        var result = ((App)Application.Current).TakeoverPrintScreen();
        StatusText.Text = result == "ok" ? Loc.ResultPrtScOk : Loc.ResultPrtScConflict;
        RefreshStatus();
    }

    private async Task OnTakeWinShiftS()
    {
        var confirm = MessageBox.Show(this, Loc.ConfirmWinShiftS, Loc.ConfirmWinShiftSTitle,
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        SetBusy(true, Loc.BusyApplying);
        var result = await ((App)Application.Current).TakeoverWinShiftSAsync();
        SetBusy(false, result == "ok" ? Loc.ResultWinShiftSOk : Loc.ResultWinShiftSFail);
        RefreshStatus();
    }

    private async Task OnRevert()
    {
        var confirm = MessageBox.Show(this, Loc.ConfirmRevert, Loc.ConfirmRevertTitle,
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        SetBusy(true, Loc.BusyReverting);
        await ((App)Application.Current).RevertTakeoversAsync();
        SetBusy(false, Loc.ResultReverted);
        RefreshStatus();
    }

    private void SetBusy(bool busy, string message)
    {
        // never leave a half-done state without telling the user
        TakeWinVButton.IsEnabled = !busy;
        TakePrtScButton.IsEnabled = !busy;
        TakeWinShiftSButton.IsEnabled = !busy;
        RevertButton.IsEnabled = !busy;
        StatusText.Text = message;
    }

    // ----- Hotkey editor -----

    /// <summary>Slots do editor de atalhos (o de parar gravação só registra durante a sessão, RF-F3.05).</summary>
    private enum HotkeySlot
    {
        History,
        Capture,
        StopRecording,
    }

    private bool _capturing;

    private Klip.App.Controls.KeyChord ChordOf(HotkeySlot slot) => slot switch
    {
        HotkeySlot.History => HistoryHotkeyChord,
        HotkeySlot.Capture => CaptureHotkeyChord,
        _ => StopRecHotkeyChord,
    };

    private TextBlock PromptOf(HotkeySlot slot) => slot switch
    {
        HotkeySlot.History => HistoryHotkeyPrompt,
        HotkeySlot.Capture => CaptureHotkeyPrompt,
        _ => StopRecHotkeyPrompt,
    };

    private Button ButtonOf(HotkeySlot slot) => slot switch
    {
        HotkeySlot.History => HistoryHotkeyButton,
        HotkeySlot.Capture => CaptureHotkeyButton,
        _ => StopRecHotkeyButton,
    };

    private string SavedGestureOf(HotkeySlot slot) => slot switch
    {
        HotkeySlot.History => _settings.Current.HotkeyHistory,
        HotkeySlot.Capture => _settings.Current.HotkeyCapture,
        _ => _settings.Current.StopRecordingHotkey,
    };

    /// <summary>Enter capture mode for a hotkey button: prompt for keys.</summary>
    private void BeginCapture(HotkeySlot slot)
    {
        _capturing = true;
        ChordOf(slot).Visibility = Visibility.Collapsed;
        PromptOf(slot).Visibility = Visibility.Visible;
        ButtonOf(slot).Focus();
    }

    /// <summary>Leave capture mode, showing the saved chord again.</summary>
    private void EndCapture(HotkeySlot slot)
    {
        _capturing = false;
        var chord = ChordOf(slot);
        chord.Chord = SavedGestureOf(slot);
        chord.Visibility = Visibility.Visible;
        PromptOf(slot).Visibility = Visibility.Collapsed;
    }

    private void CaptureHotkey(KeyEventArgs e, HotkeySlot slot)
    {
        if (!_capturing)
            return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Esc bails out of capture without changing anything
        if (key == Key.Escape)
        {
            EndCapture(slot);
            return;
        }

        var parts = new List<string>();
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

        // just modifiers so far: preview them live as keycaps and keep waiting
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            var live = ChordOf(slot);
            live.Visibility = Visibility.Visible;
            PromptOf(slot).Visibility = Visibility.Collapsed;
            live.Chord = string.Join("+", parts);
            return;
        }

        string keyName;
        if (key is >= Key.A and <= Key.Z)
            keyName = key.ToString();
        else if (key is >= Key.D0 and <= Key.D9)
            keyName = key.ToString()[1..];
        else if (key is >= Key.F1 and <= Key.F24)
            keyName = key.ToString();
        else if (key == Key.Snapshot)
            keyName = "PrintScreen";
        else
            return; // key not supported

        if (parts.Count == 0 && keyName != "PrintScreen")
            return; // needs a modifier (except PrtSc)

        parts.Add(keyName);
        var gesture = string.Join("+", parts);

        _settings.Update(s =>
        {
            switch (slot)
            {
                case HotkeySlot.History:
                    s.HotkeyHistory = gesture;
                    break;
                case HotkeySlot.Capture:
                    s.HotkeyCapture = gesture;
                    break;
                default:
                    s.StopRecordingHotkey = gesture; // RF-F3.05
                    break;
            }
        });

        // RF-F3.05: o atalho de parar só é registrado durante a gravação -
        // conflitos aparecem lá; os demais registram na hora
        var ok = slot == HotkeySlot.StopRecording
            || ((App)Application.Current).ApplyHotkeys(_settings);
        EndCapture(slot); // shows the freshly saved chord as keycaps
        StatusText.Text = ok
            ? string.Format(Loc.HotkeyUpdated, gesture)
            : string.Format(Loc.HotkeyConflict, gesture);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // minimize to the tray instead of quitting; exit is on the tray menu
        if (!((App)System.Windows.Application.Current).IsExiting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
