using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Klip.Interop;

namespace Klip.App.Windows;

/// <summary>
/// Panel for the panoramic capture: the hint, live height, a preview that
/// grows and the Done/Cancel buttons. Sits OUTSIDE the region and stays out
/// of the capture.
/// </summary>
public sealed class ScrollCapturePanel : Window
{
    private readonly TextBlock _heightLabel;
    private readonly TextBlock _hintLabel;
    private readonly Image _preview;
    private DateTime _lastWarning = DateTime.MinValue;

    public event Action? DoneRequested;
    public event Action? CancelRequested;

    public ScrollCapturePanel()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.WidthAndHeight;
        AllowsTransparency = true;
        Background = Brushes.Transparent;

        var title = new TextBlock
        {
            Text = Localization.Loc.PanoramicTitle,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Foreground = Brushes.White,
        };
        var instruction = new TextBlock
        {
            Text = Localization.Loc.PanoramicInstruction,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, 4, 0, 8),
        };
        _heightLabel = new TextBlock
        {
            FontSize = 12,
            Foreground = Brushes.White,
            Text = string.Format(Localization.Loc.PanoramicCaptured, 0),
        };
        _hintLabel = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB9, 0x00)),
            Text = "",
            Margin = new Thickness(0, 2, 0, 0),
        };

        _preview = new Image
        {
            Width = 168,
            MaxHeight = 240,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var previewHost = new Border
        {
            Margin = new Thickness(0, 8, 0, 8),
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x00, 0x00)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4),
            MinHeight = 60,
            Child = _preview,
        };

        var done = MakeButton(Localization.Loc.PanoramicDone, accent: true);
        done.Click += (_, _) => DoneRequested?.Invoke();
        var cancel = MakeButton(Localization.Loc.PanoramicCancel, accent: false);
        cancel.Click += (_, _) => CancelRequested?.Invoke();

        var buttons = new UniformGrid { Rows = 1, Columns = 2 };
        buttons.Children.Add(done);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Width = 196 };
        panel.Children.Add(title);
        panel.Children.Add(instruction);
        panel.Children.Add(_heightLabel);
        panel.Children.Add(_hintLabel);
        panel.Children.Add(previewHost);
        panel.Children.Add(buttons);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x2C, 0x2C, 0x2C)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12, 16, 12),
            Child = panel,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 16,
                ShadowDepth = 3,
                Opacity = 0.5,
            },
        };
    }

    public void UpdateProgress(int heightPx, bool lastDiscarded)
    {
        _heightLabel.Text = string.Format(Localization.Loc.PanoramicCaptured, heightPx.ToString("N0"));
        if (lastDiscarded)
        {
            _hintLabel.Text = Localization.Loc.PanoramicSlowDown;
            _lastWarning = DateTime.UtcNow;
        }
        else if ((DateTime.UtcNow - _lastWarning).TotalSeconds > 2)
        {
            _hintLabel.Text = "";
        }
    }

    public void UpdatePreview(BitmapSource image) => _preview.Source = image;

    /// <summary>Places it OUTSIDE the region: to the right, else left, else the corner.</summary>
    public void ShowBeside(NativeMethods.RECT monitor, int regionX, int regionY, int regionW, int regionH)
    {
        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();
        NativeMethods.SetWindowDisplayAffinity(helper.Handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        Show();
        UpdateLayout();

        var dpi = NativeMethods.GetDpiForWindow(helper.Handle);
        if (dpi == 0)
            dpi = 96;
        var panelW = (int)(ActualWidth * dpi / 96.0);
        var panelH = (int)(ActualHeight * dpi / 96.0);

        int x;
        var y = Math.Clamp(regionY, monitor.top + 8, Math.Max(monitor.top + 8, monitor.bottom - panelH - 8));
        if (regionX + regionW + panelW + 24 <= monitor.right)
            x = regionX + regionW + 16;                    // right
        else if (regionX - panelW - 24 >= monitor.left)
            x = regionX - panelW - 16;                     // left
        else
            x = monitor.right - panelW - 16;               // overlaps the corner (excluded from capture)

        NativeMethods.SetWindowPos(helper.Handle, nint.Zero, x, y, 0, 0,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | 0x0001 /*SWP_NOSIZE*/);
    }

    private static Button MakeButton(string text, bool accent)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 12 },
            Margin = new Thickness(2, 0, 2, 0),
            Padding = new Thickness(0, 7, 0, 7),
            Background = accent
                ? new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4))
                : new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent),
        });
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.PaddingProperty, new System.Windows.Data.Binding("Padding")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent),
        });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        button.Template = template;
        return button;
    }
}
