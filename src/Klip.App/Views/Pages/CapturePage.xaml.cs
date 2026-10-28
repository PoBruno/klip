using System.IO;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Klip.App.Localization;
using Klip.Core.Capture;
using Klip.Core.Settings;

namespace Klip.App.Views.Pages;

/// <summary>
/// Capture settings: remembered mode, editor after capture, auto-save and folder,
/// scrolling capture cadence. Recording settings live on the recording page.
/// </summary>
/// <remarks>
/// "Ctrl"/"Shift"/"Alt" stay literal on purpose, key names don't get translated;
/// only the "disabled" entry comes from <see cref="Loc"/>.
/// </remarks>
public partial class CapturePage : ISettingsPage
{
    /// <summary>Quiet time before a slider change is saved. See <see cref="_scrollDelayCommit"/>.</summary>
    private const int ScrollDelayCommitMs = 400;

    private readonly SettingsService _settings;

    /// <summary>
    /// Dragging the slider from 100 to 2000 fires ~19 ValueChanged, and each Update
    /// raises the settings Changed event. The label follows the drag live, the save
    /// only happens at the end of the gesture (DragCompleted/LostFocus) or after this
    /// quiet window, which covers keyboard and track clicks (no "drag end" there).
    /// </summary>
    private readonly DispatcherTimer _scrollDelayCommit;

    // reentrancy guard, always reset in a finally
    private bool _loading;

    // path as page state, not read back from the TextBox
    private string _screenshotsFolder = string.Empty;

    public CapturePage(SettingsService settings)
    {
        _settings = settings;
        PageDiagnostics.TrackConstruction(this);
        InitializeComponent();

        _scrollDelayCommit = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(ScrollDelayCommitMs),
        };
        _scrollDelayCommit.Tick += (_, _) => CommitScrollDelay();

        // order matters: combo items, then handlers, then the first refresh.
        // InitializeComponent already coerced the slider (Minimum=100 over the
        // default 0); with the handler wired before that, the page would save
        // the cadence just by being built
        BuildComboItems();
        WireEvents();
        RefreshState();
    }

    /// <summary>Reloads everything (entering the page, language switch).</summary>
    public void RefreshState()
    {
        _loading = true;
        try
        {
            var s = _settings.Current;

            RememberCaptureModeToggle.IsChecked = s.RememberLastCaptureMode;
            AlwaysEditorToggle.IsChecked = s.AlwaysOpenEditorAfterCapture;
            RelabelEditorModifier();
            SelectByTag(EditorModifierCombo, s.EditorModifierKey, CaptureEditorModifier.Control);

            MagnifierToggle.IsChecked = s.CaptureMagnifier;
            SelectByTag(ColorFormatCombo, ColorFormatter.Normalize(s.ColorPickerFormat), ColorFormatter.Hex);

            AutoSaveToggle.IsChecked = s.AutoSaveScreenshots;
            // same default as CaptureController.AutoSave (Pictures\Screenshots)
            _screenshotsFolder = s.ScreenshotsFolder ?? DefaultScreenshotsFolder();
            ScreenshotFolderBox.Text = _screenshotsFolder;

            // a pending commit from the last gesture can't overwrite what was just read
            _scrollDelayCommit.Stop();
            ScrollDelaySlider.Value = s.ScrollCaptureDelayMs;
            // outside ValueChanged on purpose: same value = no event = empty label
            UpdateScrollDelayLabel();
        }
        finally
        {
            _loading = false;
        }
    }

    private void BuildComboItems()
    {
        EditorModifierCombo.Items.Add(new ComboBoxItem { Content = "Ctrl", Tag = CaptureEditorModifier.Control });
        EditorModifierCombo.Items.Add(new ComboBoxItem { Content = "Shift", Tag = CaptureEditorModifier.Shift });
        EditorModifierCombo.Items.Add(new ComboBoxItem { Content = "Alt", Tag = CaptureEditorModifier.Alt });
        EditorModifierCombo.Items.Add(new ComboBoxItem { Tag = CaptureEditorModifier.None });

        // format names are not translated, like the key names above
        ColorFormatCombo.Items.Add(new ComboBoxItem { Content = "HEX", Tag = ColorFormatter.Hex });
        ColorFormatCombo.Items.Add(new ComboBoxItem { Content = "RGB", Tag = ColorFormatter.Rgb });
        ColorFormatCombo.Items.Add(new ComboBoxItem { Content = "HSL", Tag = ColorFormatter.Hsl });
    }

    /// <summary>
    /// Handlers in the constructor, never in Loaded: the page is a singleton and
    /// Loaded fires on every return to the visual tree.
    /// </summary>
    private void WireEvents()
    {
        // off = the overlay always opens on rectangle
        RememberCaptureModeToggle.Click += (_, _) =>
        {
            if (!_loading)
                _settings.Update(s => s.RememberLastCaptureMode = RememberCaptureModeToggle.IsChecked == true);
        };

        AlwaysEditorToggle.Click += (_, _) =>
        {
            if (!_loading)
                _settings.Update(s => s.AlwaysOpenEditorAfterCapture = AlwaysEditorToggle.IsChecked == true);
        };

        EditorModifierCombo.SelectionChanged += (_, _) =>
        {
            if (!_loading && EditorModifierCombo.SelectedItem is ComboBoxItem { Tag: CaptureEditorModifier modifier })
                _settings.Update(s => s.EditorModifierKey = modifier);
        };

        MagnifierToggle.Click += (_, _) =>
        {
            if (!_loading)
                _settings.Update(s => s.CaptureMagnifier = MagnifierToggle.IsChecked == true);
        };

        ColorFormatCombo.SelectionChanged += (_, _) =>
        {
            if (!_loading && ColorFormatCombo.SelectedItem is ComboBoxItem { Tag: string format })
                _settings.Update(s => s.ColorPickerFormat = format);
        };

        AutoSaveToggle.Click += (_, _) =>
        {
            if (!_loading)
                _settings.Update(s => s.AutoSaveScreenshots = AutoSaveToggle.IsChecked == true);
        };

        ChooseFolderButton.Click += (_, _) => ChooseScreenshotFolder();

        // ValueChanged only paints the label; CommitScrollDelay saves
        ScrollDelaySlider.ValueChanged += (_, _) =>
        {
            UpdateScrollDelayLabel();
            if (!_loading)
                _scrollDelayCommit.Start(); // Start on a running timer restarts the count
        };
        // end of drag: the Slider doesn't expose it, it bubbles up from the Thumb
        ScrollDelaySlider.AddHandler(
            Thumb.DragCompletedEvent,
            new DragCompletedEventHandler((_, _) => CommitScrollDelay()));
        // leaving the control (Tab, another card, closing the window) saves now
        ScrollDelaySlider.LostFocus += (_, _) => CommitScrollDelay();
    }

    private void UpdateScrollDelayLabel() =>
        ScrollDelayLabel.Text = string.Format(Loc.CadenceValue, (int)ScrollDelaySlider.Value);

    // idempotent: DragCompleted and the timer can both arrive with the same value
    private void CommitScrollDelay()
    {
        _scrollDelayCommit.Stop();
        if (_loading)
            return;

        var value = (int)ScrollDelaySlider.Value;
        if (_settings.Current.ScrollCaptureDelayMs == value)
            return;

        _settings.Update(s => s.ScrollCaptureDelayMs = value);
    }

    private void ChooseScreenshotFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Loc.ChooseFolder,
            InitialDirectory = _screenshotsFolder,
        };
        if (dialog.ShowDialog() != true)
            return;

        var folder = dialog.FolderName;
        _screenshotsFolder = folder;
        ScreenshotFolderBox.Text = folder;
        _settings.Update(s => s.ScreenshotsFolder = folder);
    }

    // found by Tag, never by index
    private void RelabelEditorModifier()
    {
        foreach (var item in EditorModifierCombo.Items)
        {
            if (item is ComboBoxItem { Tag: CaptureEditorModifier.None } entry)
                entry.Content = Loc.ModifierDisabled;
        }
    }

    /// <summary>Selects the item whose Tag matches; unknown saved value falls back to the default.</summary>
    private static void SelectByTag<T>(ComboBox combo, T value, T fallback)
        where T : notnull =>
        combo.SelectedItem = FindByTag(combo, value) ?? FindByTag(combo, fallback);

    private static ComboBoxItem? FindByTag<T>(ComboBox combo, T tag)
        where T : notnull
    {
        foreach (var item in combo.Items)
        {
            if (item is ComboBoxItem entry && tag.Equals(entry.Tag))
                return entry;
        }
        return null;
    }

    private static string DefaultScreenshotsFolder() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "Screenshots");
}
