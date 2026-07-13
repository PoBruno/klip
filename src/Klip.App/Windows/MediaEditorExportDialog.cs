using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Klip.App.Localization;

namespace Klip.App.Windows;

/// <summary>
/// Janela modal de progresso da exportacao do editor de midia (RF-F5.12/13)
/// com cancelamento (CA-F5.7: quem cancela nao deixa arquivo parcial - o
/// servico de exportacao usa temp + move atomico).
/// </summary>
public sealed class MediaEditorExportDialog : Window
{
    private readonly ProgressBar _bar;
    private readonly CancellationTokenSource _cts = new();
    private bool _completed;

    /// <summary>Token cancelado quando o usuario clica em Cancelar ou fecha.</summary>
    public CancellationToken CancellationToken => _cts.Token;

    public MediaEditorExportDialog(Window owner)
    {
        Owner = owner;
        Title = Loc.MediaExporting;
        WindowStyle = WindowStyle.ToolWindow;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush?)TryFindResource("ApplicationBackgroundBrush") ?? Brushes.White;

        _bar = new ProgressBar
        {
            Width = 280,
            Height = 6,
            Minimum = 0,
            Maximum = 1,
            IsIndeterminate = true,
        };

        var cancel = new Button
        {
            Content = Loc.PanoramicCancel,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
            Padding = new Thickness(16, 6, 16, 6),
        };
        cancel.Click += (_, _) => _cts.Cancel();

        var text = new TextBlock
        {
            Text = Loc.MediaExporting,
            Margin = new Thickness(0, 0, 0, 10),
            Foreground = (Brush?)TryFindResource("TextFillColorPrimaryBrush") ?? Brushes.Black,
        };

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(text);
        panel.Children.Add(_bar);
        panel.Children.Add(cancel);
        Content = panel;

        Closing += (_, _) =>
        {
            if (!_completed)
                _cts.Cancel();
        };
        // bug do CTS nunca disposed: libera no fechamento. O token ja
        // capturado pelos loaders continua legivel apos o Dispose.
        Closed += (_, _) => _cts.Dispose();
    }

    /// <summary>Progresso 0..1; chame da UI thread (via IProgress).</summary>
    public void ReportProgress(double fraction)
    {
        _bar.IsIndeterminate = false;
        _bar.Value = fraction;
    }

    /// <summary>Fecha sem cancelar (exportacao concluida ou falha ja tratada).</summary>
    public void CloseCompleted()
    {
        _completed = true;
        Close();
    }
}
