using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Klip.App.Localization;
using Klip.App.Services;

namespace Klip.App.Views.Pages;

/// <summary>
/// "Sobre": identidade, versao e links. A contagem do historico e o tamanho do banco
/// moraram aqui; foram para "Backup e manutencao", em Armazenamento.
/// </summary>
public partial class AboutPage : ISettingsPage
{
    public AboutPage()
    {
        PageDiagnostics.TrackConstruction(this);
        InitializeComponent();

        // assinatura no construtor, nunca em Loaded (pagina singleton)
        CopyVersionButton.Click += OnCopyVersionClick;
        // um handler para os 5 links: o Click de cada botao sobe ate a pagina com a URL no Tag
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnLinkClick));

        RefreshState();
    }

    /// <inheritdoc />
    public void RefreshState()
    {
        // Loc.AppVersion muda com o idioma, entao o texto e remontado a cada refresh
        VersionText.Text = FormatVersion();
    }

    private void OnLinkClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not Button { Tag: string url } || !url.StartsWith("https://", StringComparison.Ordinal))
            return;
        e.Handled = true;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("AboutPage.OpenLink", ex);
        }
    }

    private void OnCopyVersionClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // nao e link: nao deixa o handler de links ver este clique
        try
        {
            var version = typeof(AboutPage).Assembly.GetName().Version?.ToString(3) ?? "?";
            Clipboard.SetText($"Klip {version} (x64, Windows {Environment.OSVersion.Version})");
        }
        catch (Exception ex)
        {
            // clipboard ocupado por outro app: nao vale derrubar a pagina por isso
            StartupLog.WriteException("AboutPage.CopyVersion", ex);
        }
    }

    /// <summary>
    /// Versao lida dos metadados do proprio assembly. <c>Assembly.Location</c> nao serve:
    /// em publicacao single-file ele volta string vazia. Tres campos (o quarto e sempre 0).
    /// </summary>
    private static string FormatVersion() =>
        typeof(AboutPage).Assembly.GetName().Version is { } version
            ? string.Format(CultureInfo.CurrentCulture, Loc.AppVersion, version.ToString(3))
            : string.Empty;
}
