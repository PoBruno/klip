using System.Globalization;
using System.IO;
using System.Windows;
using Klip.App.Localization;
using Klip.App.Services;
using Klip.Core.Common;
using Klip.Core.Storage;

namespace Klip.App.Views.Pages;

/// <summary>
/// RF-S.05 - secao "Sobre" (absorve <c>SectionAbout</c>): identidade e versao do app,
/// contagem de itens do historico, tamanho do banco e a pasta de dados.
/// Migrada de <c>MainWindow.xaml:588-594</c> + <c>MainWindow.xaml.cs:483-484</c>.
/// <para>
/// RF-S.06: a contagem de itens passa a morar aqui. Na tela antiga ela dividia o
/// <c>StatusText</c> com outros 3 papeis e, pior, era escrita logo DEPOIS das
/// mensagens de resultado dos takeovers - que por isso nunca chegavam a ser lidas.
/// </para>
/// <para>
/// RF-S.08: <c>Count()</c> e um <c>SELECT COUNT(*)</c> e <c>FileInfo.Length</c> toca o
/// disco. Os dois saem da UI thread (<c>Task.Run</c> + <c>Dispatcher</c>), com
/// placeholder enquanto carregam.
/// </para>
/// </summary>
public partial class AboutPage : ISettingsPage
{
    /// <summary>Mostrado ate a leitura de SQLite/disco voltar (RF-S.08).</summary>
    private const string LoadingPlaceholder = "...";

    private readonly ClipboardItemRepository _repository;

    /// <summary>
    /// Ha uma leitura de estatisticas em voo. Lido e escrito so na UI thread.
    /// </summary>
    private bool _statisticsLoading;

    public AboutPage(ClipboardItemRepository repository)
    {
        PageDiagnostics.TrackConstruction(this);
        _repository = repository;
        InitializeComponent();

        // RF-S.05: assinatura NO CONSTRUTOR, nunca em Loaded. A pagina e singleton
        // (ADR-S.03) e o Loaded dispara a cada re-entrada na arvore visual, o que
        // duplicaria o handler a cada ida e volta na navegacao.
        OpenDataFolderButton.Click += OnOpenDataFolderClick;

        RefreshState();
    }

    /// <inheritdoc />
    public void RefreshState()
    {
        // Sem guard de reentrancia: esta pagina nao tem nenhum controle que grave em
        // settings, entao nao ha handler para o refresh acordar (ADR-S.07 nao se
        // aplica). Um flag aqui seria codigo morto.

        // Loc.AppVersion muda com o idioma, entao o texto e remontado a cada refresh.
        VersionText.Text = FormatVersion();

        // leitura de string em memoria, sem IO
        DatabaseCard.Description = AppPaths.Root;

        LoadStatistics();
    }

    /// <summary>
    /// RF-S.08: SQLite e disco fora da UI thread. A tela antiga fazia as duas leituras
    /// de forma sincrona dentro do <c>RefreshStatus()</c> chamado no <c>Loaded</c> -
    /// ou seja, antes de a janela aparecer.
    /// </summary>
    private void LoadStatistics()
    {
        // O construtor e o OnNavigated do shell chamam RefreshState em sequencia na
        // primeira navegacao; sem isto a mesma leitura sairia duas vezes. A carga em
        // voo publica dados igualmente frescos.
        if (_statisticsLoading)
            return;

        _statisticsLoading = true;
        HistoryCard.Description = LoadingPlaceholder;
        DatabaseSizeText.Text = LoadingPlaceholder;

        _ = Task.Run(() =>
        {
            long? count = null;
            long? bytes = null;
            try
            {
                count = _repository.Count();
                var databaseFile = AppPaths.DatabaseFile;
                bytes = File.Exists(databaseFile) ? new FileInfo(databaseFile).Length : 0L;
            }
            catch (Exception ex)
            {
                // banco em uso exclusivo, arquivo removido no meio do caminho: a
                // pagina Sobre nao pode derrubar o app por causa de uma estatistica
                StartupLog.WriteException("AboutPage.LoadStatistics", ex);
            }

            Publish(count, bytes);
        });
    }

    private void Publish(long? count, long? bytes) =>
        _ = Dispatcher.InvokeAsync(() =>
        {
            _statisticsLoading = false;

            HistoryCard.Description = count is null
                ? string.Empty
                : string.Format(
                    CultureInfo.CurrentCulture,
                    Loc.ItemsInHistory,
                    count.Value.ToString("N0", CultureInfo.CurrentCulture));

            DatabaseSizeText.Text = bytes is null ? string.Empty : FormatMegabytes(bytes.Value);
        });

    /// <summary>
    /// MainWindow.xaml:571 tem o mesmo botao na secao de manutencao; este e um segundo
    /// ponto de entrada, e nao uma mudanca de lugar.
    /// </summary>
    private void OnOpenDataFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            AppPaths.EnsureCreated();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppPaths.Root,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("OpenDataFolder", ex);
        }
    }

    /// <summary>
    /// Versao lida dos metadados do proprio assembly. <c>Assembly.Location</c> nao
    /// serve: em publicacao single-file ele volta string vazia. E
    /// <c>typeof(AboutPage).Assembly</c> em vez de <c>GetEntryAssembly()</c> porque
    /// nao depende de quem hospeda o runtime e nunca e nulo. Formatada com 3 campos:
    /// o csproj declara <c>1.3.0</c> e o quarto campo do <see cref="Version" /> e
    /// sempre 0.
    /// </summary>
    private static string FormatVersion() =>
        typeof(AboutPage).Assembly.GetName().Version is { } version
            ? string.Format(CultureInfo.CurrentCulture, Loc.AppVersion, version.ToString(3))
            : string.Empty;

    /// <summary>
    /// "12,3 MB". "MB" e simbolo de unidade, nao string de UI: nao existe chave no
    /// Loc para ele e as 15 tabelas de idioma escreveriam o mesmo. So a parte numerica
    /// e localizada (virgula decimal em pt-BR).
    /// </summary>
    private static string FormatMegabytes(long bytes) =>
        string.Format(CultureInfo.CurrentCulture, "{0:F1} MB", bytes / 1024.0 / 1024.0);
}
