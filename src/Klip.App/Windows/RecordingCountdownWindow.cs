using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Klip.Core.Recording;
using Klip.Interop;

namespace Klip.App.Windows;

/// <summary>
/// Contagem regressiva de 3 s sobre a regiao antes da gravacao comecar
/// (RF-F3.01/RF-F4.01). Mesmo visual do delay de captura (RF-05.05): numero
/// grande branco com sombra. Click-through, sem foco e fora de capturas.
/// </summary>
public sealed class RecordingCountdownWindow : Window
{
    private readonly TextBlock _label;

    private RecordingCountdownWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;

        // mesmo estilo do countdown do delay de captura no overlay
        _label = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 96,
            FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Segoe UI Variable, Segoe UI"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 0 },
        };
        Content = new Grid { Children = { _label } };
    }

    /// <summary>
    /// Mostra a contagem sobre a regiao (px fisicos) e completa ao chegar em
    /// zero. Cancelavel: o token fecha a janela e a task completa cancelada
    /// (RequestStop durante a contagem / saida do app).
    /// </summary>
    public static Task RunAsync(RecordingRegion region, CancellationToken cancellationToken = default, int seconds = 3)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new RecordingCountdownWindow();

        var helper = new WindowInteropHelper(window);
        helper.EnsureHandle();

        // click-through + sem ativacao + fora de Alt-Tab e de qualquer captura
        var exStyle = (long)NativeMethods.GetWindowLongPtr(helper.Handle, NativeMethods.GWL_EXSTYLE);
        exStyle |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongPtr(helper.Handle, NativeMethods.GWL_EXSTYLE, (nint)exStyle);
        Klip.Interop.Recording.WindowCaptureExclusion.Exclude(helper.Handle); // RF-F2.10

        NativeMethods.SetWindowPos(helper.Handle, nint.Zero,
            region.Left, region.Top, region.Width, region.Height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        var remaining = Math.Max(1, seconds);
        window._label.Text = remaining.ToString();
        window.Show();

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        timer.Tick += (_, _) =>
        {
            remaining--;
            if (remaining <= 0)
            {
                timer.Stop();
                window.Close();
                tcs.TrySetResult();
            }
            else
            {
                window._label.Text = remaining.ToString();
            }
        };
        timer.Start();

        // cancelamento pode vir de qualquer thread: remarshala para fechar a
        // janela; o registro e liberado quando a task completa por qualquer via
        var registration = cancellationToken.Register(() => window.Dispatcher.BeginInvoke(() =>
        {
            timer.Stop();
            window.Close();
            tcs.TrySetCanceled(cancellationToken);
        }));
        _ = tcs.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);

        return tcs.Task;
    }
}
