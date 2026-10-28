using System.Windows;
using System.Windows.Controls;
using Klip.App.Localization;
using Klip.Core.Common;
using Klip.Core.Settings;

namespace Klip.App.Views.Controls;

/// <summary>
/// "Paste image as file" block of the history settings page: attach a virtual file to
/// our own images, Ctrl+V in Explorer, and the file name template with a live preview.
/// Self contained so the host page only needs one line of XAML.
/// </summary>
public partial class PasteAsFileSettings : UserControl
{
    /// <summary>Set once at startup; the control is created by XAML and has no DI of its own.</summary>
    internal static SettingsService? SettingsSource { get; set; }

    private bool _loading;

    public PasteAsFileSettings()
    {
        InitializeComponent();

        AttachToggle.Checked += (_, _) => Save(s => s.AttachFileToCopiedImages = true);
        AttachToggle.Unchecked += (_, _) => Save(s => s.AttachFileToCopiedImages = false);
        ExplorerToggle.Checked += (_, _) => Save(s => s.PasteImageAsFileInExplorer = true);
        ExplorerToggle.Unchecked += (_, _) => Save(s => s.PasteImageAsFileInExplorer = false);
        TemplateBox.TextChanged += (_, _) => OnTemplateChanged();

        // the page is a singleton and comes back into the tree on every visit
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        if (SettingsSource is not { } settings)
            return;
        _loading = true;
        try
        {
            var s = settings.Current;
            AttachToggle.IsChecked = s.AttachFileToCopiedImages;
            ExplorerToggle.IsChecked = s.PasteImageAsFileInExplorer;
            if (TemplateBox.Text != s.ImageFileNameTemplate)
                TemplateBox.Text = s.ImageFileNameTemplate;
            UpdatePreview();
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnTemplateChanged()
    {
        UpdatePreview();
        var text = TemplateBox.Text ?? "";
        Save(s => s.ImageFileNameTemplate = text);
    }

    private void UpdatePreview()
    {
        var name = FileNameTemplate.ToFileName(TemplateBox.Text, DateTime.Now, "chrome.exe");
        PreviewText.Text = string.Format(Loc.PasteAsFileNamePreview, name);
    }

    private void Save(Action<AppSettings> mutate)
    {
        if (_loading || SettingsSource is not { } settings)
            return;
        settings.Update(mutate);
    }
}
