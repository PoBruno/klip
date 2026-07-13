using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Klip.Core.Recording;
using Klip.Interop;

namespace Klip.App.Windows;

/// <summary>
/// Painel pre-gravacao do modo MP4 (RF-F3.02), ancorado a regiao no estilo da
/// capture bar do Snipping Tool: toggle de som do sistema, lista de microfones
/// ativos (multi-selecao), indicador da resolucao e botao Iniciar.
/// FORA DO FLUXO PADRAO (UX submenu de gravacao): as opcoes de audio/bitrate
/// agora sao escolhidas no submenu inline da toolbar do overlay
/// (RecordingOptionsPanel) e persistidas em AppSettings; a selecao de area vai
/// direto para countdown + gravacao. A classe fica no repo como fallback -
/// para reativar, veja o comentario do fluxo antigo no RecordingController.
/// </summary>
public sealed class RecordingSetupWindow : Window
{
    private readonly CheckBox _systemAudio;
    private readonly List<(CheckBox Box, string Id)> _micBoxes = [];

    /// <summary>(som do sistema, ids dos microfones marcados)</summary>
    public event Action<bool, IReadOnlyList<string>>? StartRequested;

    public event Action? CancelRequested;

    public RecordingSetupWindow(RecordingRegion region, IReadOnlyList<AudioSourceInfo> microphones)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.WidthAndHeight;
        AllowsTransparency = true;
        Background = Brushes.Transparent;

        var panel = new StackPanel { MinWidth = 240, MaxWidth = 320 };

        // titulo + resolucao da regiao + fechar
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock
        {
            Text = Localization.Loc.RecordingSetupTitle,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Foreground = Brushes.White,
        });
        titles.Children.Add(new TextBlock
        {
            Text = $"{region.Width} x {region.Height} px · 30 fps",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, 2, 0, 0),
        });
        header.Children.Add(titles);
        var close = GlyphButton("\uE711", Localization.Loc.CloseEsc);
        close.Click += (_, _) => CancelRequested?.Invoke();
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        panel.Children.Add(header);

        // som do sistema (default ligado - caso reuniao)
        _systemAudio = MakeCheck(Localization.Loc.RecordingSystemAudio, isChecked: true);
        _systemAudio.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(_systemAudio);

        // microfones ativos (RF-F3.02: multi-selecao; default desmarcados,
        // paridade com o Snipping Tool - o usuario opta pelo mic)
        panel.Children.Add(new TextBlock
        {
            Text = Localization.Loc.RecordingMicrophones,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, 10, 0, 2),
        });
        if (microphones.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Localization.Loc.RecordingNoMicrophones,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
            });
        }
        else
        {
            foreach (var mic in microphones)
            {
                var box = MakeCheck(mic.Name, isChecked: false);
                box.Margin = new Thickness(0, 4, 0, 0);
                _micBoxes.Add((box, mic.Id));
                panel.Children.Add(box);
            }
        }

        var start = new Button
        {
            Content = new TextBlock
            {
                Text = Localization.Loc.RecordingStart,
                Foreground = Brushes.White,
                FontSize = 13,
            },
            Margin = new Thickness(0, 14, 0, 0),
            Padding = new Thickness(0, 8, 0, 8),
            Background = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        ApplyRoundedTemplate(start, 4);
        start.Click += (_, _) => StartRequested?.Invoke(
            _systemAudio.IsChecked == true,
            _micBoxes.Where(m => m.Box.IsChecked == true).Select(m => m.Id).ToList());
        panel.Children.Add(start);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x2C, 0x2C, 0x2C)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12, 16, 14),
            Child = panel,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 16,
                ShadowDepth = 3,
                Opacity = 0.5,
            },
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                CancelRequested?.Invoke();
        };
    }

    /// <summary>
    /// Ancorado a regiao (px fisicos): abaixo dela; sem espaco, acima;
    /// clampado ao monitor. Excluido de capturas por garantia.
    /// </summary>
    public void ShowNear(RecordingRegion region, NativeMethods.RECT monitor)
    {
        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();
        Klip.Interop.Recording.WindowCaptureExclusion.Exclude(helper.Handle); // RF-F2.10
        Show();
        UpdateLayout();

        var dpi = NativeMethods.GetDpiForWindow(helper.Handle);
        if (dpi == 0)
            dpi = 96;
        var w = (int)(ActualWidth * dpi / 96.0);
        var h = (int)(ActualHeight * dpi / 96.0);

        var x = Math.Clamp(region.Left + (region.Width - w) / 2,
            monitor.left + 8, Math.Max(monitor.left + 8, monitor.right - w - 8));
        int y = region.Top + region.Height + 12;
        if (y + h > monitor.bottom - 8)
            y = Math.Max(monitor.top + 8, region.Top - h - 12);

        NativeMethods.SetWindowPos(helper.Handle, nint.Zero, x, y, 0, 0,
            NativeMethods.SWP_NOZORDER | 0x0001 /*SWP_NOSIZE*/);
        Activate();
    }

    private static CheckBox MakeCheck(string text, bool isChecked) => new()
    {
        Content = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis },
        IsChecked = isChecked,
        Cursor = Cursors.Hand,
    };

    private static Button GlyphButton(string glyph, string tooltip)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 12,
                Foreground = Brushes.White,
            },
            ToolTip = tooltip,
            Padding = new Thickness(8, 6, 8, 6),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Top,
        };
        ApplyRoundedTemplate(button, 12);
        return button;
    }

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
