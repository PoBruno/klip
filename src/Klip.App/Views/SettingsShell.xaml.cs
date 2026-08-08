using System.ComponentModel;
using System.Windows;
using Klip.App.Services;
using Klip.App.Views.Pages;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Klip.App.Views;

/// <summary>
/// RF-S.03 / ADR-S.02: o shell da tela de Configuracoes. So a moldura -
/// <c>ui:TitleBar</c> + <c>ui:NavigationView</c> com as 7 secoes. Nenhum ajuste mora
/// aqui: cada pagina e dona da propria logica (ADR-S.07).
/// <para>
/// ADR-S.03 / RF-S.04: as paginas sao resolvidas pelo container via
/// <see cref="INavigationViewPageProvider"/> e registradas como <c>AddSingleton</c>.
/// Com um provider ligado o <c>NavigationCacheMode</c> vira codigo morto - quem
/// decide o ciclo de vida e o DI: construida na primeira navegacao e reusada.
/// </para>
/// <para>
/// Fechar minimiza para a bandeja; sair e so pelo menu do tray.
/// </para>
/// </summary>
public partial class SettingsShell
{
#if DEBUG
    // instrumentacao: mede de "new" ate ContentRendered, para comparar com os 793 ms
    // que o UiThreadWatchdog atribui a MainWindow monolitica.
    private readonly System.Diagnostics.Stopwatch _openWatch = System.Diagnostics.Stopwatch.StartNew();
#endif

    // pagina ativa, capturada no evento Navigated: a NavigationView expoe o item de
    // menu selecionado, nao a instancia da pagina.
    private object? _currentPage;

    public SettingsShell(INavigationViewPageProvider pageProvider)
    {
        InitializeComponent();
        SystemThemeWatcher.Watch(this);

        // ADR-S.06: na 4.3.0 a interface e INavigationViewPageProvider (Wpf.Ui.Abstractions)
        // e o gancho e SetPageProviderService. IPageService nao existe.
        RootNavigation.SetPageProviderService(pageProvider);
        RootNavigation.Navigated += OnNavigated;

        // a primeira pagina e navegada no Loaded, nao no construtor: assim o custo de
        // construi-la sai do caminho critico de abertura da janela.
        Loaded += OnShellLoaded;
#if DEBUG
        ContentRendered += OnFirstContentRendered;
#endif
    }

    /// <summary>
    /// RF-S.05: repassa a atualizacao de estado para a pagina ativa. Paginas ainda
    /// nao construidas nao precisam de nada - elas leem o estado atual no proprio
    /// construtor.
    /// </summary>
    public void RefreshStatus()
    {
        if (_currentPage is ISettingsPage page)
            page.RefreshState();
    }

    private void OnShellLoaded(object sender, RoutedEventArgs e)
    {
        // uma vez so: Hide()/Show() nao reconstroem a arvore, mas nao ha garantia de
        // que Loaded nao dispare de novo em uma re-entrada na arvore visual.
        Loaded -= OnShellLoaded;
        RootNavigation.Navigate(typeof(GeneralPage));
    }

    private void OnNavigated(NavigationView sender, NavigatedEventArgs args)
    {
        _currentPage = args.Page;
        if (args.Page is ISettingsPage page)
            page.RefreshState();
    }

#if DEBUG
    private void OnFirstContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnFirstContentRendered;
        _openWatch.Stop();
        StartupLog.Write($"SettingsShell: new -> ContentRendered em {_openWatch.ElapsedMilliseconds} ms");
    }
#endif

    protected override void OnClosing(CancelEventArgs e)
    {
        // minimiza para a bandeja em vez de encerrar; sair e pelo menu do tray.
        // Sem isso, fechar a janela mata o app.
        if (!((App)Application.Current).IsExiting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
