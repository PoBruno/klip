using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Klip.App.Services;
using Klip.Core.Recording;
using Klip.Core.Settings;

namespace Klip.App.Windows;

/// <summary>
/// UX submenu de gravacao: painel compacto ancorado ABAIXO do botao GIF/MP4
/// na toolbar do overlay de captura. As opcoes (FPS/escala do GIF; som do
/// sistema, microfones e bitrate do MP4) sao persistidas IMEDIATAMENTE em
/// AppSettings via SettingsService e aplicadas na proxima gravacao - a
/// selecao de area inicia a gravacao direto, sem painel pre-gravacao.
/// Todos os controles sao Focusable=false: o overlay mantem o foco do
/// teclado (Esc continua fechando; hint do modificador continua vivo).
/// </summary>
public sealed class RecordingOptionsPanel : Border
{
    private static readonly Brush LabelBrush = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF));
    private static readonly Brush FaintBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
    private static readonly Brush SelectedFill = new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF));

    private readonly SettingsService _settings;

    public RecordingKind Kind { get; }

    public RecordingOptionsPanel(
        RecordingKind kind,
        SettingsService settings,
        Func<IAudioDeviceEnumerator> audioEnumeratorFactory)
    {
        Kind = kind;
        _settings = settings;

        // mesmo material da toolbar pill (acrilico escuro composto no canvas)
        Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x2C, 0x2C, 0x2C));
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(14, 10, 14, 12);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        MinWidth = 220;
        MaxWidth = 320;
        Cursor = Cursors.Arrow;
        Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 12,
            ShadowDepth = 2,
            Opacity = 0.5,
        };

        Child = kind == RecordingKind.Gif ? BuildGifContent() : BuildMp4Content(audioEnumeratorFactory);
    }

    // ----- GIF: FPS (efetivo, como na pagina de settings) + escala -----

    private StackPanel BuildGifContent()
    {
        var panel = new StackPanel();

        panel.Children.Add(SectionLabel(Localization.Loc.RecordingFpsLabel, topMargin: 0));
        panel.Children.Add(Segmented(
            [10, 15, 20],
            // Q-F4.2: o GIF trabalha em centesimos; o segmento mostra o FPS
            // efetivo (10 / 14,3 / 20), igual a pagina de settings
            fps => GifRecordingMath.EffectiveFps(fps).ToString("0.#"),
            fps => string.Format(Localization.Loc.GifFpsEffective, fps,
                GifRecordingMath.EffectiveFps(fps).ToString("0.#")),
            selected: _settings.Current.GifFps,
            onSelect: fps => _settings.Update(s => s.GifFps = fps)));

        panel.Children.Add(SectionLabel(Localization.Loc.RecordingScaleLabel));
        panel.Children.Add(Segmented(
            [100, 75, 50],
            scale => $"{scale}%",
            scale => $"{scale}%",
            selected: _settings.Current.GifScalePercent,
            onSelect: scale => _settings.Update(s => s.GifScalePercent = scale)));

        return panel;
    }

    // ----- MP4: som do sistema + microfones (async) + qualidade -----

    private StackPanel BuildMp4Content(Func<IAudioDeviceEnumerator> audioEnumeratorFactory)
    {
        var panel = new StackPanel();

        // som do sistema (default ligado; persistido na hora)
        var systemAudio = MakeCheck(Localization.Loc.RecordingSystemAudio,
            _settings.Current.Mp4CaptureSystemAudio);
        systemAudio.Checked += (_, _) => _settings.Update(s => s.Mp4CaptureSystemAudio = true);
        systemAudio.Unchecked += (_, _) => _settings.Update(s => s.Mp4CaptureSystemAudio = false);
        panel.Children.Add(systemAudio);

        // microfones: enumeracao WASAPI pode bloquear - fora da UI thread,
        // com placeholder "carregando" (mesmo padrao do antigo ShowSetupAsync)
        panel.Children.Add(SectionLabel(Localization.Loc.RecordingMicrophones));
        var micHost = new StackPanel();
        micHost.Children.Add(new TextBlock
        {
            Text = Localization.Loc.RecordingLoadingMics,
            FontSize = 12,
            Foreground = FaintBrush,
        });
        panel.Children.Add(micHost);
        _ = LoadMicrophonesAsync(audioEnumeratorFactory, micHost);

        panel.Children.Add(SectionLabel(Localization.Loc.RecordingQualityLabel));
        panel.Children.Add(Segmented(
            [0, 5000, 8000, 16000],
            kbps => kbps == 0 ? Localization.Loc.Mp4BitrateAuto : $"{kbps / 1000}",
            kbps => kbps == 0 ? Localization.Loc.Mp4BitrateAuto : $"{kbps / 1000} Mbps",
            selected: _settings.Current.Mp4BitrateKbps,
            onSelect: kbps => _settings.Update(s => s.Mp4BitrateKbps = kbps)));

        return panel;
    }

    private async Task LoadMicrophonesAsync(
        Func<IAudioDeviceEnumerator> audioEnumeratorFactory, StackPanel micHost)
    {
        IReadOnlyList<AudioSourceInfo> mics;
        try
        {
            mics = await Task.Run(() => audioEnumeratorFactory().GetActiveMicrophones());
        }
        catch (Exception ex)
        {
            // enumeracao indisponivel: trata como lista vazia
            StartupLog.WriteException("RecordingPanelAudioEnum", ex);
            mics = [];
        }

        micHost.Children.Clear();
        if (mics.Count == 0)
        {
            micHost.Children.Add(new TextBlock
            {
                Text = Localization.Loc.RecordingNoMicrophones,
                FontSize = 12,
                Foreground = FaintBrush,
            });
            return;
        }

        var saved = _settings.Current.Mp4MicrophoneIds;
        foreach (var mic in mics)
        {
            var id = mic.Id;
            var box = MakeCheck(mic.Name, saved.Contains(id));
            box.Margin = new Thickness(0, 4, 0, 0);
            box.Checked += (_, _) => _settings.Update(s =>
            {
                if (!s.Mp4MicrophoneIds.Contains(id))
                    s.Mp4MicrophoneIds.Add(id);
            });
            box.Unchecked += (_, _) => _settings.Update(s => s.Mp4MicrophoneIds.Remove(id));
            micHost.Children.Add(box);
        }
    }

    // ----- blocos visuais -----

    private static TextBlock SectionLabel(string text, double topMargin = 10) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = LabelBrush,
        Margin = new Thickness(0, topMargin, 0, 4),
    };

    /// <summary>
    /// Controle segmentado compacto: um botao por opcao, o ativo ganha o
    /// mesmo realce dos botoes de modo da toolbar. onSelect persiste na hora.
    /// </summary>
    private static StackPanel Segmented(
        int[] values,
        Func<int, string> label,
        Func<int, string> tooltip,
        int selected,
        Action<int> onSelect)
    {
        var host = new StackPanel { Orientation = Orientation.Horizontal };
        var buttons = new List<(Button Button, int Value)>();

        foreach (var value in values)
        {
            var button = new Button
            {
                Content = new TextBlock
                {
                    Text = label(value),
                    FontSize = 12,
                    Foreground = Brushes.White,
                },
                ToolTip = tooltip(value),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 4, 0),
                Background = value == selected ? SelectedFill : Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Focusable = false, // o overlay fica com o teclado (Esc fecha)
            };
            ApplyRoundedTemplate(button, 4);
            buttons.Add((button, value));
            host.Children.Add(button);
        }

        foreach (var (button, value) in buttons)
        {
            button.Click += (_, _) =>
            {
                onSelect(value);
                foreach (var (other, otherValue) in buttons)
                    other.Background = otherValue == value ? SelectedFill : Brushes.Transparent;
            };
        }

        return host;
    }

    private static CheckBox MakeCheck(string text, bool isChecked) => new()
    {
        Content = new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        },
        IsChecked = isChecked,
        Cursor = Cursors.Hand,
        Focusable = false, // idem: nao rouba Esc/modificador do overlay
    };

    /// <summary>Template plano arredondado (mesmo padrao dos botoes do overlay).</summary>
    private static void ApplyRoundedTemplate(Button button, double radius)
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent),
        });
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        border.SetValue(Border.PaddingProperty, new System.Windows.Data.Binding("Padding")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent),
        });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        button.Template = template;
    }
}
