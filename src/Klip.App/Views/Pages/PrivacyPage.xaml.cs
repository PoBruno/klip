using System.Windows;
using Klip.Core.Settings;

namespace Klip.App.Views.Pages;

/// <summary>
/// RF-S.05 - secao "Privacidade" (absorve <c>SectionPrivacy</c>): pular conteudo de
/// gerenciadores de senha, restaurar o clipboard e limpar o historico ao sair.
/// Migrada de <c>MainWindow.xaml:520-559</c> + <c>MainWindow.xaml.cs:203-205,
/// 422-424</c>.
/// <para>
/// ADR-S.07: a pagina e dona do proprio guard de reentrancia, em <c>try/finally</c>.
/// </para>
/// </summary>
public partial class PrivacyPage : ISettingsPage
{
    private readonly SettingsService _settings;

    /// <summary>
    /// ADR-S.07: guard de reentrancia, sempre em <c>try/finally</c>. O <c>_loading</c>
    /// da MainWindow (<c>MainWindow.xaml.cs:414-486</c>) nao estava: uma excecao no
    /// meio do refresh deixava a tela inteira somente-leitura ate reiniciar o app.
    /// </summary>
    private bool _loading;

    public PrivacyPage(SettingsService settings)
    {
        PageDiagnostics.TrackConstruction(this);
        _settings = settings;
        InitializeComponent();

        // RF-S.05: assinatura NO CONSTRUTOR, nunca em Loaded. A pagina e singleton
        // (ADR-S.03) e o Loaded dispara a cada re-entrada na arvore visual, o que
        // duplicaria os handlers a cada ida e volta na navegacao.
        SkipSecretsToggle.Click += OnSkipSecretsClick;
        RestoreClipboardToggle.Click += OnRestoreClipboardClick;
        ClearOnExitToggle.Click += OnClearOnExitClick;

        RefreshState();
    }

    /// <inheritdoc />
    public void RefreshState()
    {
        _loading = true;
        try
        {
            // MainWindow.xaml.cs:422-424
            var s = _settings.Current;
            SkipSecretsToggle.IsChecked = s.SkipSecrets;
            RestoreClipboardToggle.IsChecked = s.RestoreClipboardAfterPaste;
            ClearOnExitToggle.IsChecked = s.ClearHistoryOnExit;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>MainWindow.xaml.cs:203. Vale para a proxima ingestao.</summary>
    private void OnSkipSecretsClick(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        _settings.Update(s => s.SkipSecrets = SkipSecretsToggle.IsChecked == true);
    }

    /// <summary>MainWindow.xaml.cs:204.</summary>
    private void OnRestoreClipboardClick(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        _settings.Update(s => s.RestoreClipboardAfterPaste = RestoreClipboardToggle.IsChecked == true);
    }

    /// <summary>MainWindow.xaml.cs:205.</summary>
    private void OnClearOnExitClick(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        _settings.Update(s => s.ClearHistoryOnExit = ClearOnExitToggle.IsChecked == true);
    }
}
