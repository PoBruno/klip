using System.Windows;
using System.Windows.Controls;
using Klip.App.Localization;
using Klip.App.Services;
using Klip.Core.Recording;
using Klip.Core.Settings;
using Klip.Interop.Recording;

namespace Klip.App.Views.Pages;

/// <summary>
/// Settings page for screen recording: folder, mp4 (fps, quality, output resolution,
/// cursor, audio sources, codec) and gif. Like the other pages it owns its logic and
/// its reentrancy guard, always reset in a finally.
/// </summary>
public partial class RecordingPage : ISettingsPage
{
    private static readonly int[] Mp4FpsPresets = [30, 60];
    private static readonly string[] ResolutionPresets = ["native", "1440", "1080", "720"];
    private static readonly int[] GifFpsPresets = [10, 15, 20];
    private static readonly int[] GifScalePresets = [100, 75, 50];

    // fallbacks = AppSettings defaults
    private const int DefaultGifFps = 15;
    private const int DefaultGifScalePercent = 100;
    private const string AutoCodec = "auto";

    private readonly SettingsService _settings;
    private bool _loading;
    private string _recordingsFolder = string.Empty;

    // filled once from the encoder catalog (off the ui thread, it touches Media Foundation)
    private IReadOnlyList<VideoCodec>? _hardwareCodecs;

    public RecordingPage(SettingsService settings)
    {
        _settings = settings;
        PageDiagnostics.TrackConstruction(this);
        InitializeComponent();

        // items before handlers, handlers before the first refresh: filling a combo must
        // never write a setting back
        BuildComboItems();
        WireEvents();
        RefreshState();

        _ = LoadCodecsAsync();
        _ = LoadMicrophonesAsync();
    }

    public void RefreshState()
    {
        _loading = true;
        try
        {
            var s = _settings.Current;

            // the box shows the RESOLVED path so it is never empty; only a hand picked
            // folder goes to disk
            _recordingsFolder = RecordingPaths.Resolve(s.RecordingsFolder);
            RecordingsFolderBox.Text = _recordingsFolder;

            RelabelMp4();
            SelectByTag(Mp4FpsCombo, VideoEncodingMath.NormalizeFps(s.Mp4Fps), 30);
            SelectByTag(Mp4QualityCombo, VideoEncodingMath.ParseQuality(s.Mp4Quality), VideoQualityPreset.Balanced);
            SelectByTag(Mp4ResolutionCombo, s.Mp4OutputResolution, "native");
            ShowCursorToggle.IsChecked = s.Mp4ShowCursor;
            SystemAudioToggle.IsChecked = s.Mp4CaptureSystemAudio;
            SelectByTag(Mp4CodecCombo, s.Mp4Codec, AutoCodec);

            HideRecordingBorderToggle.IsChecked = s.HideRecordingBorder;
            FfmpegPathBox.Text = s.FfmpegPath;

            RelabelGif();
            SelectByTag(GifFpsCombo, s.GifFps, DefaultGifFps);
            SelectByTag(GifScaleCombo, s.GifScalePercent, DefaultGifScalePercent);
        }
        finally
        {
            _loading = false;
        }
    }

    private void BuildComboItems()
    {
        foreach (var fps in Mp4FpsPresets)
            Mp4FpsCombo.Items.Add(new ComboBoxItem { Tag = fps });

        foreach (var quality in new[] { VideoQualityPreset.Small, VideoQualityPreset.Balanced, VideoQualityPreset.High })
            Mp4QualityCombo.Items.Add(new ComboBoxItem { Tag = quality });

        foreach (var resolution in ResolutionPresets)
            Mp4ResolutionCombo.Items.Add(new ComboBoxItem { Tag = resolution });

        // codecs other than auto arrive when the catalog answers
        Mp4CodecCombo.Items.Add(new ComboBoxItem { Tag = AutoCodec });

        foreach (var fps in GifFpsPresets)
            GifFpsCombo.Items.Add(new ComboBoxItem { Tag = fps });

        foreach (var scale in GifScalePresets)
            GifScaleCombo.Items.Add(new ComboBoxItem { Tag = scale });
    }

    // handlers in the constructor, never in Loaded: the page is a singleton and Loaded
    // fires again on every visit
    private void WireEvents()
    {
        ChooseRecordingsFolderButton.Click += (_, _) => ChooseRecordingsFolder();

        Mp4FpsCombo.SelectionChanged += (_, _) =>
        {
            if (!_loading && Mp4FpsCombo.SelectedItem is ComboBoxItem { Tag: int fps })
                _settings.Update(s => s.Mp4Fps = fps);
        };

        Mp4QualityCombo.SelectionChanged += (_, _) =>
        {
            if (!_loading && Mp4QualityCombo.SelectedItem is ComboBoxItem { Tag: VideoQualityPreset quality })
                _settings.Update(s => s.Mp4Quality = VideoEncodingMath.ToSetting(quality));
        };

        Mp4ResolutionCombo.SelectionChanged += (_, _) =>
        {
            if (!_loading && Mp4ResolutionCombo.SelectedItem is ComboBoxItem { Tag: string resolution })
                _settings.Update(s => s.Mp4OutputResolution = resolution);
        };

        Mp4CodecCombo.SelectionChanged += (_, _) =>
        {
            if (!_loading && Mp4CodecCombo.SelectedItem is ComboBoxItem { Tag: string codec })
                _settings.Update(s => s.Mp4Codec = codec);
        };

        ShowCursorToggle.Click += (_, _) =>
        {
            if (!_loading)
                _settings.Update(s => s.Mp4ShowCursor = ShowCursorToggle.IsChecked == true);
        };

        SystemAudioToggle.Click += (_, _) =>
        {
            if (!_loading)
                _settings.Update(s => s.Mp4CaptureSystemAudio = SystemAudioToggle.IsChecked == true);
        };

        HideRecordingBorderToggle.Click += (_, _) =>
        {
            if (!_loading)
                _settings.Update(s => s.HideRecordingBorder = HideRecordingBorderToggle.IsChecked == true);
        };

        ChooseFfmpegButton.Click += (_, _) => ChooseFfmpegPath();

        GifFpsCombo.SelectionChanged += (_, _) =>
        {
            if (!_loading && GifFpsCombo.SelectedItem is ComboBoxItem { Tag: int fps })
                _settings.Update(s => s.GifFps = fps);
        };

        GifScaleCombo.SelectionChanged += (_, _) =>
        {
            if (!_loading && GifScaleCombo.SelectedItem is ComboBoxItem { Tag: int scale })
                _settings.Update(s => s.GifScalePercent = scale);
        };
    }

    // ===================== async lists =====================

    private async Task LoadCodecsAsync()
    {
        try
        {
            _hardwareCodecs = await Task.Run(VideoEncoderCatalog.GetHardwareCodecs);
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("RecordingPageCodecs", ex);
            _hardwareCodecs = [];
        }

        _loading = true;
        try
        {
            foreach (var codec in _hardwareCodecs)
                Mp4CodecCombo.Items.Add(new ComboBoxItem { Tag = VideoEncodingMath.ToSetting(codec), Content = CodecName(codec) });

            // a codec saved on another pc shows as auto here, without touching the setting
            SelectByTag(Mp4CodecCombo, _settings.Current.Mp4Codec, AutoCodec);
            CodecCard.Status = _hardwareCodecs.Count == 0 ? Loc.Mp4CodecSoftwareOnly : null;
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task LoadMicrophonesAsync()
    {
        MicrophonesHost.Children.Clear();
        MicrophonesHost.Children.Add(new TextBlock { Text = Loc.RecordingLoadingMics, FontSize = 12 });

        IReadOnlyList<AudioSourceInfo> mics;
        try
        {
            mics = await Task.Run(() => RecordingInteropFactory.CreateAudioDeviceEnumerator().GetActiveMicrophones());
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("RecordingPageMics", ex);
            mics = [];
        }

        MicrophonesHost.Children.Clear();
        if (mics.Count == 0)
        {
            MicrophonesHost.Children.Add(new TextBlock { Text = Loc.RecordingNoMicrophones, FontSize = 12 });
            return;
        }

        var saved = _settings.Current.Mp4MicrophoneIds;
        foreach (var mic in mics)
        {
            var id = mic.Id;
            var box = new CheckBox
            {
                Content = mic.Name,
                IsChecked = saved.Contains(id),
                Margin = new Thickness(0, 2, 0, 2),
            };
            box.Checked += (_, _) => _settings.Update(s =>
            {
                if (!s.Mp4MicrophoneIds.Contains(id))
                    s.Mp4MicrophoneIds.Add(id);
            });
            box.Unchecked += (_, _) => _settings.Update(s => s.Mp4MicrophoneIds.Remove(id));
            MicrophonesHost.Children.Add(box);
        }
    }

    // ===================== dialogs =====================

    private void ChooseRecordingsFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Loc.ChooseFolder,
            InitialDirectory = _recordingsFolder,
        };
        if (dialog.ShowDialog() != true)
            return;

        var folder = dialog.FolderName;
        _recordingsFolder = folder;
        RecordingsFolderBox.Text = folder;
        _settings.Update(s => s.RecordingsFolder = folder);
    }

    private void ChooseFfmpegPath()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.FfmpegChoose,
            Filter = Loc.FfmpegFilter,
        };
        if (dialog.ShowDialog() != true)
            return;

        var path = dialog.FileName;
        FfmpegPathBox.Text = path;
        _settings.Update(s => s.FfmpegPath = path);
    }

    // ===================== labels =====================

    // relabeled on every refresh, which is what runs after a language switch
    private void RelabelMp4()
    {
        foreach (var item in Mp4FpsCombo.Items)
        {
            if (item is ComboBoxItem { Tag: int fps } entry)
                entry.Content = string.Format(Loc.Mp4FpsValue, fps);
        }

        foreach (var item in Mp4QualityCombo.Items)
        {
            if (item is ComboBoxItem { Tag: VideoQualityPreset quality } entry)
                entry.Content = QualityName(quality);
        }

        foreach (var item in Mp4ResolutionCombo.Items)
        {
            // "1080p" is a unit, not a word: only "native" is translated
            if (item is ComboBoxItem { Tag: string resolution } entry)
                entry.Content = resolution == "native" ? Loc.Mp4ResolutionNative : resolution + "p";
        }

        foreach (var item in Mp4CodecCombo.Items)
        {
            if (item is ComboBoxItem { Tag: AutoCodec } entry)
                entry.Content = Loc.Mp4CodecAuto;
        }
    }

    private void RelabelGif()
    {
        foreach (var item in GifFpsCombo.Items)
        {
            if (item is ComboBoxItem { Tag: int fps } entry)
            {
                var effective = GifRecordingMath.EffectiveFps(fps);
                entry.Content = Math.Abs(effective - fps) < 0.05
                    ? string.Format(Loc.Mp4FpsValue, fps)
                    : string.Format(Loc.GifFpsEffective, fps, effective.ToString("0.#"));
            }
        }

        foreach (var item in GifScaleCombo.Items)
        {
            if (item is ComboBoxItem { Tag: int scale } entry)
                entry.Content = string.Format(Loc.GifScaleValue, scale);
        }
    }

    internal static string QualityName(VideoQualityPreset quality) => quality switch
    {
        VideoQualityPreset.Small => Loc.Mp4QualitySmall,
        VideoQualityPreset.High => Loc.Mp4QualityHigh,
        _ => Loc.Mp4QualityBalanced,
    };

    // codec names are trade names, same in every language
    internal static string CodecName(VideoCodec codec) => codec switch
    {
        VideoCodec.Hevc => "HEVC (H.265)",
        VideoCodec.Av1 => "AV1",
        _ => "H.264",
    };

    // ===================== helpers =====================

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
}
