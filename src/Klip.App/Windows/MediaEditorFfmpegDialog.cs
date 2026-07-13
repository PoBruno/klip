using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Klip.App.Localization;

namespace Klip.App.Windows;

/// <summary>
/// RF-F5.14 (parcial): sem ffmpeg.exe o export MP4/MP4-para-GIF abre este
/// dialogo com a explicacao, um botao para escolher o executavel manualmente
/// e o link da build recomendada (BtbN). O download automatico com hash e
/// tela de progresso fica como pendencia da proxima entrega.
/// </summary>
public sealed class MediaEditorFfmpegDialog : Window
{
    private const string DownloadUrl = "https://github.com/BtbN/FFmpeg-Builds/releases";

    /// <summary>Caminho escolhido pelo usuario, ou null se cancelou.</summary>
    public string? SelectedPath { get; private set; }

    public MediaEditorFfmpegDialog(Window owner)
    {
        Owner = owner;
        Title = Loc.FfmpegMissingTitle;
        WindowStyle = WindowStyle.ToolWindow;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush?)TryFindResource("ApplicationBackgroundBrush") ?? Brushes.White;

        var text = new TextBlock
        {
            Text = Loc.FfmpegMissingText,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
            Foreground = (Brush?)TryFindResource("TextFillColorPrimaryBrush") ?? Brushes.Black,
        };

        var link = new Hyperlink(new Run(Loc.FfmpegDownload))
        {
            NavigateUri = new Uri(DownloadUrl),
        };
        link.RequestNavigate += (_, e) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = e.Uri.AbsoluteUri, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Services.StartupLog.WriteException("FfmpegDownloadLink", ex);
            }
        };
        var linkBlock = new TextBlock(link) { Margin = new Thickness(0, 10, 0, 0) };

        var choose = new Button
        {
            Content = Loc.FfmpegChoose,
            Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(0, 0, 8, 0),
        };
        choose.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = Loc.FfmpegChoose,
                Filter = "ffmpeg.exe|ffmpeg.exe|*.exe|*.exe",
            };
            if (dialog.ShowDialog(this) == true)
            {
                SelectedPath = dialog.FileName;
                DialogResult = true;
            }
        };

        var cancel = new Button
        {
            Content = Loc.PanoramicCancel,
            Padding = new Thickness(16, 6, 16, 6),
        };
        cancel.Click += (_, _) => DialogResult = false;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        buttons.Children.Add(choose);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(text);
        panel.Children.Add(linkBlock);
        panel.Children.Add(buttons);
        Content = panel;
    }
}
