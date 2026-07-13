using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Klip.App.Windows;

/// <summary>
/// Janelinha de progresso indeterminado pos-gravacao: "Convertendo..." (GIF)
/// ou "Finalizando gravação..." (MP4, RF-F3.16). Some sozinha quando o
/// controller conclui.
/// </summary>
public sealed class RecordingProgressWindow : Window
{
    public RecordingProgressWindow(string message)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SizeToContent = SizeToContent.WidthAndHeight;
        AllowsTransparency = true;
        Background = Brushes.Transparent;

        var panel = new StackPanel { MinWidth = 220 };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = Brushes.White,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new ProgressBar
        {
            IsIndeterminate = true,
            Height = 4,
            Margin = new Thickness(0, 10, 0, 0),
        });

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x2C, 0x2C, 0x2C)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(20, 14, 20, 16),
            Child = panel,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 16,
                ShadowDepth = 3,
                Opacity = 0.5,
            },
        };
    }
}
