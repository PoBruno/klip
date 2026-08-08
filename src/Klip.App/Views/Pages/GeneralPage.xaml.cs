using System.Windows;
using System.Windows.Controls;
using Klip.App.Localization;
using Klip.App.Services;
using Klip.Core.Settings;

namespace Klip.App.Views.Pages;

/// <summary>
/// RF-S.05 - secao "Geral" (absorve <c>SectionGeneral</c>): inicio com o Windows,
/// idioma, tema e aba de emoji. Migrada de <c>MainWindow.xaml:233-279</c> +
/// <c>MainWindow.xaml.cs:69-82, 166-192, 208, 420-478</c>.
/// <para>
/// ADR-S.07: a pagina e dona da propria logica e do proprio guard de reentrancia,
/// em <c>try/finally</c>.
/// </para>
/// </summary>
public partial class GeneralPage : ISettingsPage
{
    /// <summary>
    /// Tag do item "seguir o Windows", igual nos dois combos desta pagina
    /// (<c>AppSettings.Language</c> e <c>AppSettings.Theme</c> nascem com ela).
    /// </summary>
    private const string SystemTag = "system";

    private const string LightTag = "light";
    private const string DarkTag = "dark";

    private readonly SettingsService _settings;
    private readonly AutostartService _autostart;

    /// <summary>
    /// ADR-S.07: guard de reentrancia. <see cref="RefreshState" /> escreve nos
    /// controles e isso pode disparar os proprios handlers; sem o guard, abrir a
    /// pagina gravaria em settings. Sempre em <c>try/finally</c> - o <c>_loading</c>
    /// da MainWindow (<c>MainWindow.xaml.cs:414-486</c>) nao estava, e uma excecao no
    /// meio do refresh deixava a tela somente-leitura ate reiniciar o app.
    /// </summary>
    private bool _loading;

    public GeneralPage(SettingsService settings, AutostartService autostart)
    {
        PageDiagnostics.TrackConstruction(this);
        _settings = settings;
        _autostart = autostart;
        InitializeComponent();

        // RF-S.05: itens montados ANTES de assinar SelectionChanged. Na ordem inversa
        // o primeiro Add ja seleciona implicitamente o item 0 e o handler gravaria
        // esse valor em settings durante a construcao da pagina.
        BuildLanguageItems();
        BuildThemeItems();

        // RF-S.05: assinatura NO CONSTRUTOR, nunca em Loaded. A pagina e singleton
        // (ADR-S.03) e o Loaded dispara a cada re-entrada na arvore visual, o que
        // duplicaria os handlers a cada ida e volta na navegacao.
        AutostartToggle.Click += OnAutostartClick;
        LanguageCombo.SelectionChanged += OnLanguageSelectionChanged;
        ThemeCombo.SelectionChanged += OnThemeSelectionChanged;
        ShowEmojiTabToggle.Click += OnShowEmojiTabClick;

        RefreshState();
    }

    /// <inheritdoc />
    public void RefreshState()
    {
        _loading = true;
        try
        {
            var s = _settings.Current;

            // MainWindow.xaml.cs:420
            // Q-S.2 continua ABERTA: a UI le AppSettings.StartWithWindows, nao
            // AutostartService.IsEnabled(). Se algo externo apagar a chave Run, a UI
            // mente. Nao resolvida aqui de proposito - mudar a fonte e decisao de
            // spec, e IsEnabled() e leitura de registro, que RF-S.08 quer fora da
            // abertura da pagina.
            AutostartToggle.IsChecked = s.StartWithWindows;

            // MainWindow.xaml.cs:425
            ShowEmojiTabToggle.IsChecked = s.ShowEmojiTab;

            // MainWindow.xaml.cs:463-478: o original re-rotulava e selecionava o tema
            // por indice literal (Items[0], Items[1], Items[2]). Aqui os dois combos
            // trabalham por Tag: a lista de idiomas e dinamica e a de temas deixa de
            // depender da ordem em que foi montada.
            RelabelLanguageItems();
            SelectByTag(LanguageCombo, s.Language);
            RelabelThemeItems();
            SelectByTag(ThemeCombo, s.Theme);
        }
        finally
        {
            _loading = false;
        }
    }

    // ----- Handlers -----

    /// <summary>
    /// MainWindow.xaml.cs:69-82. Unico ajuste da pagina com efeito colateral fora das
    /// settings: alem de gravar <c>StartWithWindows</c>, escreve a chave <c>Run</c> do
    /// HKCU. A escrita no registro pode falhar (politica, permissao) e nao pode
    /// derrubar o app - por isso o <c>try/catch</c> com log.
    /// </summary>
    private void OnAutostartClick(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        var enabled = AutostartToggle.IsChecked == true;
        _settings.Update(s => s.StartWithWindows = enabled);
        try
        {
            _autostart.SetEnabled(enabled);
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("Autostart", ex);
        }
    }

    /// <summary>MainWindow.xaml.cs:170-179. A troca vale na hora, sem reiniciar.</summary>
    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageCombo.SelectedItem is not ComboBoxItem { Tag: string language })
            return;

        _settings.Update(s => s.Language = language);

        // recarrega as tabelas e publica os recursos "Loc.*": tudo que esta em
        // DynamicResource troca de idioma sozinho
        Loc.Initialize(language);

        // ...menos o que foi montado em codigo. RefreshState() re-rotula os itens dos
        // dois combos (o "Sistema" do idioma e os tres do tema) no idioma novo.
        RefreshState();
    }

    /// <summary>MainWindow.xaml.cs:185-192. Aplicado na hora, sem reiniciar (CA-S.7).</summary>
    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeCombo.SelectedItem is not ComboBoxItem { Tag: string theme })
            return;

        _settings.Update(s => s.Theme = theme);
        ThemeManager.Apply(theme);
    }

    /// <summary>MainWindow.xaml.cs:208. Vale na proxima abertura do painel.</summary>
    private void OnShowEmojiTabClick(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        _settings.Update(s => s.ShowEmojiTab = ShowEmojiTabToggle.IsChecked == true);
    }

    // ----- Itens dos combos -----

    /// <summary>MainWindow.xaml.cs:167-169: um item fixo "Sistema" + a lista do Loc.</summary>
    private void BuildLanguageItems()
    {
        LanguageCombo.Items.Add(new ComboBoxItem { Content = Loc.LanguageSystem, Tag = SystemTag });
        foreach (var (value, display) in Loc.AvailableLanguages)
            LanguageCombo.Items.Add(new ComboBoxItem { Content = display, Tag = value });
    }

    /// <summary>MainWindow.xaml.cs:182-184.</summary>
    private void BuildThemeItems()
    {
        ThemeCombo.Items.Add(new ComboBoxItem { Content = Loc.ThemeSystem, Tag = SystemTag });
        ThemeCombo.Items.Add(new ComboBoxItem { Content = Loc.ThemeLight, Tag = LightTag });
        ThemeCombo.Items.Add(new ComboBoxItem { Content = Loc.ThemeDark, Tag = DarkTag });
    }

    /// <summary>
    /// So o item "Sistema" e localizado - os nomes dos idiomas sao nativos
    /// (<c>Loc.AvailableLanguages</c>) e nao mudam com a troca de idioma.
    /// </summary>
    private void RelabelLanguageItems() =>
        SetContentByTag(LanguageCombo, SystemTag, Loc.LanguageSystem);

    private void RelabelThemeItems()
    {
        SetContentByTag(ThemeCombo, SystemTag, Loc.ThemeSystem);
        SetContentByTag(ThemeCombo, LightTag, Loc.ThemeLight);
        SetContentByTag(ThemeCombo, DarkTag, Loc.ThemeDark);
    }

    /// <summary>
    /// Seleciona pelo Tag; se o valor salvo nao existir mais na lista (idioma removido,
    /// arquivo de settings editado a mao), cai no item "Sistema".
    /// </summary>
    private static void SelectByTag(ComboBox combo, string tag)
    {
        var item = FindByTag(combo, tag) ?? FindByTag(combo, SystemTag);
        if (item is not null)
            combo.SelectedItem = item;
    }

    private static void SetContentByTag(ComboBox combo, string tag, string content)
    {
        if (FindByTag(combo, tag) is { } item)
            item.Content = content;
    }

    private static ComboBoxItem? FindByTag(ComboBox combo, string tag)
    {
        foreach (var candidate in combo.Items)
        {
            if (candidate is ComboBoxItem { Tag: string itemTag } item && itemTag == tag)
                return item;
        }

        return null;
    }
}
