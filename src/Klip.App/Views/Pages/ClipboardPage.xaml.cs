using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Klip.Core.Settings;

namespace Klip.App.Views.Pages;

/// <summary>
/// RF-S.05 - secao "Historico de clipboard" (absorve <c>SectionClipboard</c>):
/// retencao por itens e por idade, e a lista de apps excluidos.
/// <para>
/// RF-S.09: a lista de apps excluidos e uma <see cref="ObservableCollection{T}"/>
/// ligada uma unica vez ao <c>ItemsSource</c>, com <c>VirtualizingStackPanel</c> em
/// <c>VirtualizationMode=Recycling</c>. Antes era um <c>ItemsControl</c> sem
/// virtualizacao cujo <c>ItemsSource</c> era REATRIBUIDO inteiro
/// (<c>ExcludedAppsList.ItemsSource = ...ToList()</c>) a cada adicao ou remocao, o
/// que jogava fora e regerava todos os containers.
/// </para>
/// </summary>
public partial class ClipboardPage : ISettingsPage
{
    private readonly SettingsService _settings;

    /// <summary>RF-S.09: fonte unica da lista; nunca e reatribuida ao ItemsSource.</summary>
    private readonly ObservableCollection<string> _excludedApps = [];

    /// <summary>
    /// ADR-S.07: guard de reentrancia. Setar <c>NumberBox.Value</c> dispara
    /// <c>ValueChanged</c>, entao sem ele o refresh regravaria o valor que acabou
    /// de ler. Sempre manipulado em <c>try/finally</c> (ver <see cref="RefreshState"/>).
    /// </summary>
    private bool _loading;

    public ClipboardPage(SettingsService settings)
    {
        _settings = settings;
        PageDiagnostics.TrackConstruction(this);
        InitializeComponent();

        // RF-S.09: uma unica atribuicao de ItemsSource na vida da pagina
        ExcludedAppsList.ItemsSource = _excludedApps;

        // RF-S.05: assinatura no CONSTRUTOR, nunca em Loaded - a pagina e singleton
        // (ADR-S.03) e Loaded dispara a cada re-entrada na arvore visual, duplicando
        // as assinaturas.
        MaxItemsBox.ValueChanged += (_, _) => OnMaxItemsChanged();
        MaxAgeBox.ValueChanged += (_, _) => OnMaxAgeChanged();
        AddExcludeButton.Click += (_, _) => AddExcludedApp();
        ExcludeAppBox.KeyDown += OnExcludeAppBoxKeyDown;

        // Um handler para N linhas: o Click do botao de remover borbulha ate o
        // ItemsControl. Substitui o Click="OnRemoveExcludedApp" declarado por string
        // no XAML antigo, que so quebrava em runtime se o metodo fosse renomeado.
        ExcludedAppsList.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnRemoveExcludedApp));
        ExcludedAppsList.PreviewMouseWheel += OnExcludedAppsPreviewMouseWheel;

        RefreshState();
    }

    /// <summary>
    /// RF-S.05: recarrega os valores salvos. Chamado no fim do construtor e a cada
    /// entrada na pagina (<c>SettingsShell.OnNavigated</c>).
    /// </summary>
    public void RefreshState()
    {
        // ADR-S.07: o try/finally e o ponto. O _loading da MainWindow nao estava
        // protegido: uma excecao no meio de RefreshStatus deixava a tela
        // somente-leitura ate reiniciar o app.
        _loading = true;
        try
        {
            var current = _settings.Current;
            MaxItemsBox.Value = current.RetentionMaxItems;
            MaxAgeBox.Value = current.RetentionMaxAgeDays;
            SyncExcludedApps(current.ExcludedApps);
        }
        finally
        {
            _loading = false;
        }
    }

    // ----- Retencao -----

    private void OnMaxItemsChanged()
    {
        // Value e double?: null acontece quando a caixa fica vazia, e ai nao ha o
        // que gravar (o setting mantem o ultimo valor valido).
        if (_loading || MaxItemsBox.Value is not { } value)
            return;
        _settings.Update(s => s.RetentionMaxItems = (int)value);
    }

    private void OnMaxAgeChanged()
    {
        if (_loading || MaxAgeBox.Value is not { } value)
            return;
        _settings.Update(s => s.RetentionMaxAgeDays = (int)value);
    }

    // ----- Apps excluidos -----

    private void OnExcludeAppBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        AddExcludedApp();
    }

    private void AddExcludedApp()
    {
        var name = ExcludeAppBox.Text.Trim();
        if (name.Length == 0)
            return;

        // Normalizacao herdada da MainWindow: nome sem ponto ganha ".exe", e a
        // deduplicacao e OrdinalIgnoreCase (nome de processo no Windows).
        if (!name.Contains('.'))
            name += ".exe";

        var added = false;
        _settings.Update(s =>
        {
            if (s.ExcludedApps.Contains(name, StringComparer.OrdinalIgnoreCase))
                return;
            s.ExcludedApps.Add(name);
            added = true;
        });

        ExcludeAppBox.Text = "";

        // RF-S.09: mutacao incremental - so o container novo e gerado
        if (added)
            _excludedApps.Add(name);
    }

    private void OnRemoveExcludedApp(object sender, RoutedEventArgs e)
    {
        // O DataContext do botao JA e o nome do processo; o Tag="{Binding}" do XAML
        // antigo era so um veiculo de dado redundante (e mal tipado).
        if (e.OriginalSource is not FrameworkElement { DataContext: string app })
            return;

        e.Handled = true;
        _settings.Update(s => s.ExcludedApps.RemoveAll(
            a => string.Equals(a, app, StringComparison.OrdinalIgnoreCase)));

        for (var i = _excludedApps.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_excludedApps[i], app, StringComparison.OrdinalIgnoreCase))
                _excludedApps.RemoveAt(i);
        }
    }

    /// <summary>
    /// RF-S.09: reconcilia a colecao com o que esta salvo. No caminho comum (entrar
    /// na pagina, nada mudou por fora) NAO toca na colecao - nenhum container e
    /// regerado. So reconstroi quando a lista salva realmente divergiu.
    /// </summary>
    private void SyncExcludedApps(List<string> saved)
    {
        if (_excludedApps.Count == saved.Count
            && _excludedApps.SequenceEqual(saved, StringComparer.Ordinal))
        {
            return;
        }

        _excludedApps.Clear();
        foreach (var app in saved)
            _excludedApps.Add(app);
    }

    /// <summary>
    /// RF-S.09: a lista tem scroll proprio (a virtualizacao exige viewport finita),
    /// e um ScrollViewer do WPF marca a roda do mouse como tratada mesmo quando nao
    /// tem para onde rolar. Sem isto o cartao viraria um poco morto no meio da
    /// pagina. Quando a lista chega ao fim (ou nem rola), a roda e devolvida ao
    /// ScrollViewer da pagina.
    /// </summary>
    private void OnExcludedAppsPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // resolvido a cada roda em vez de cacheado: FindName e um lookup de hashtable
        // no namescope do template, e um campo cacheado ficaria pendurado em uma
        // instancia antiga se o template um dia for reaplicado.
        if (ExcludedAppsList.Template?.FindName("PART_ExcludedAppsScroll", ExcludedAppsList)
            is not ScrollViewer scroll)
        {
            return;
        }

        var atTop = e.Delta > 0 && scroll.VerticalOffset <= 0;
        var atBottom = e.Delta < 0 && scroll.VerticalOffset >= scroll.ScrollableHeight;
        if (!atTop && !atBottom)
            return;

        e.Handled = true;

        // MouseWheelEvent so borbulha: o evento re-emitido sobe para o ScrollViewer
        // da pagina e nao volta para este handler (que escuta o tunel Preview).
        ExcludedAppsList.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = ExcludedAppsList,
        });
    }
}
