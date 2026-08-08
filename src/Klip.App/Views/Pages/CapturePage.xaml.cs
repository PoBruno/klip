using System.IO;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Klip.App.Localization;
using Klip.Core.Recording;
using Klip.Core.Settings;

namespace Klip.App.Views.Pages;

/// <summary>
/// RF-S.05 - secao "Captura de tela" (absorve <c>SectionCapture</c> e
/// <c>SectionRecording</c>): 11 ajustes de captura, cadencia da rolagem, editor,
/// pastas, GIF/MP4 e o caminho do ffmpeg.
/// <para>
/// ADR-S.07: a pagina e dona da propria logica e do proprio guard de reentrancia,
/// sempre em <c>try/finally</c> - uma excecao no meio do refresh nao pode deixar a
/// tela somente-leitura ate reiniciar o app, que e o que a MainWindow fazia.
/// </para>
/// <para>
/// RF-S.07: nada de texto montado em codigo. "{0} ms" da cadencia, "{0}%" da escala
/// do GIF, "{0} Mbps" do bitrate e o filtro do dialogo do ffmpeg saem do <see cref="Loc"/>.
/// Os rotulos "Ctrl"/"Shift"/"Alt" continuam literais de proposito: nome de tecla nao
/// se traduz; so o item "desativado" e localizado.
/// </para>
/// </summary>
public partial class CapturePage : ISettingsPage
{
    /// <summary>Presets de FPS do GIF (RF-F4.02); o rotulo mostra o valor efetivo.</summary>
    private static readonly int[] GifFpsPresets = [10, 15, 20];

    /// <summary>Presets de escala da gravacao GIF (RF-F4.03).</summary>
    private static readonly int[] GifScalePresets = [100, 75, 50];

    /// <summary>Presets de bitrate do MP4 em kbps (Q-F3.1 resolvida como presets).</summary>
    private static readonly int[] Mp4BitratePresets = [5000, 8000, 16000];

    /// <summary>Tag do item "Automatico": 0 = escolhe pela resolucao da regiao.</summary>
    private const int Mp4BitrateAutoKbps = 0;

    /// <summary>Fallbacks = defaults de <see cref="AppSettings"/>, nao indices literais.</summary>
    private const int DefaultGifFps = 15;
    private const int DefaultGifScalePercent = 100;

    /// <summary>Janela de coalescencia do slider de cadencia. Ver <see cref="_scrollDelayCommit"/>.</summary>
    private const int ScrollDelayCommitMs = 400;

    private readonly SettingsService _settings;

    /// <summary>
    /// RF-P3.06: arrastar o slider de 100 a 2000 dispara ~19 ValueChanged, e cada
    /// Update do SettingsService dispara o Changed sincrono (que agenda um
    /// BeginInvoke no flyout). O rotulo acompanha o arrasto ao vivo, mas a gravacao
    /// so acontece no fim do gesto (DragCompleted/LostFocus) ou depois desta janela
    /// de silencio - que cobre teclado e clique na trilha, que nao tem "fim de arrasto".
    /// </summary>
    private readonly DispatcherTimer _scrollDelayCommit;

    /// <summary>ADR-S.07: guard de reentrancia; some sempre, via <c>finally</c>.</summary>
    private bool _loading;

    /// <summary>
    /// Caminhos como estado da pagina, nao como Text de controle: ler
    /// <c>ScreenshotFolderBox.Text</c> para decidir o InitialDirectory acoplava a
    /// logica ao controle (e quebraria se a caixa virasse binding ou placeholder).
    /// </summary>
    private string _screenshotsFolder = string.Empty;
    private string _recordingsFolder = string.Empty;

    public CapturePage(SettingsService settings)
    {
        _settings = settings;
        PageDiagnostics.TrackConstruction(this);
        InitializeComponent();

        _scrollDelayCommit = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(ScrollDelayCommitMs),
        };
        _scrollDelayCommit.Tick += (_, _) => CommitScrollDelay();

        // Ordem obrigatoria: itens dos combos ANTES das assinaturas, e as assinaturas
        // ANTES do primeiro RefreshState. O InitializeComponent ja coage o Value do
        // slider (Minimum=100 sobre o default 0) - com o handler ligado antes disso a
        // pagina gravaria a cadencia so por ter sido construida.
        BuildComboItems();
        WireEvents();

        // RF-S.05: a pagina nasce com o estado atual; nada depende de Loaded.
        RefreshState();
    }

    /// <summary>
    /// RF-S.05: recarrega tudo. Chamado pelo shell ao entrar na pagina e quando o
    /// estado externo muda (troca de idioma), entao tambem relabela o que e localizado.
    /// </summary>
    public void RefreshState()
    {
        // ADR-S.07: try/finally. Se qualquer leitura abaixo lancar, o guard volta a
        // false e a tela continua editavel.
        _loading = true;
        try
        {
            var s = _settings.Current;

            // ----- Captura -----
            AutoSaveToggle.IsChecked = s.AutoSaveScreenshots;

            // mesmo default do CaptureController.AutoSave (Imagens\Screenshots)
            _screenshotsFolder = s.ScreenshotsFolder ?? DefaultScreenshotsFolder();
            ScreenshotFolderBox.Text = _screenshotsFolder;

            // o commit pendente do gesto anterior nao pode sobrescrever o que
            // acabou de ser lido do disco
            _scrollDelayCommit.Stop();
            ScrollDelaySlider.Value = s.ScrollCaptureDelayMs;
            // fora do ValueChanged de proposito: se o valor salvo for igual ao que o
            // slider ja tem, o evento nao dispara e o rotulo ficaria vazio.
            UpdateScrollDelayLabel();

            RelabelEditorModifier();
            SelectByTag(EditorModifierCombo, s.EditorModifierKey, CaptureEditorModifier.Control);
            AlwaysEditorToggle.IsChecked = s.AlwaysOpenEditorAfterCapture;

            // ----- Gravacao de tela (specs F3/F4) -----

            // Intencional (RF-F3.06): a caixa mostra o caminho RESOLVIDO, entao nunca
            // aparece vazia mesmo com a setting em null - o usuario ve onde as
            // gravacoes vao cair (Videos\Gravacoes de Tela) antes da primeira gravacao.
            // O que vai para o disco continua sendo so o caminho escolhido a mao.
            _recordingsFolder = RecordingPaths.Resolve(s.RecordingsFolder);
            RecordingsFolderBox.Text = _recordingsFolder;

            RelabelGifFps();
            SelectByTag(GifFpsCombo, s.GifFps, DefaultGifFps);
            RelabelGifScale();
            SelectByTag(GifScaleCombo, s.GifScalePercent, DefaultGifScalePercent);
            RelabelMp4Bitrate();
            SelectByTag(Mp4BitrateCombo, s.Mp4BitrateKbps, Mp4BitrateAutoKbps);

            HideRecordingBorderToggle.IsChecked = s.HideRecordingBorder;
            FfmpegPathBox.Text = s.FfmpegPath;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// Popula os combos. Roda uma vez, antes das assinaturas de SelectionChanged:
    /// adicionar item a um combo vazio nao seleciona nada, mas depender disso e
    /// depender de acidente.
    /// </summary>
    private void BuildComboItems()
    {
        // RF-F1.02: modificador segurado ao soltar a selecao abre direto no editor.
        // RF-S.07: nome de tecla nao se traduz - so "Desativado" sai do Loc.
        EditorModifierCombo.Items.Add(new ComboBoxItem { Content = "Ctrl", Tag = CaptureEditorModifier.Control });
        EditorModifierCombo.Items.Add(new ComboBoxItem { Content = "Shift", Tag = CaptureEditorModifier.Shift });
        EditorModifierCombo.Items.Add(new ComboBoxItem { Content = "Alt", Tag = CaptureEditorModifier.Alt });
        EditorModifierCombo.Items.Add(new ComboBoxItem { Tag = CaptureEditorModifier.None });

        foreach (var fps in GifFpsPresets)
            GifFpsCombo.Items.Add(new ComboBoxItem { Tag = fps });

        foreach (var scale in GifScalePresets)
            GifScaleCombo.Items.Add(new ComboBoxItem { Tag = scale });

        Mp4BitrateCombo.Items.Add(new ComboBoxItem { Tag = Mp4BitrateAutoKbps });
        foreach (var kbps in Mp4BitratePresets)
            Mp4BitrateCombo.Items.Add(new ComboBoxItem { Tag = kbps });

        // o Content de todos eles e localizado: quem escreve e o Relabel* do RefreshState
    }

    /// <summary>
    /// RF-S.05: assinaturas no construtor, nunca em Loaded - a pagina e singleton
    /// (ADR-S.03) e Loaded dispara a cada re-entrada na arvore visual, duplicando tudo.
    /// </summary>
    private void WireEvents()
    {
        AutoSaveToggle.Click += (_, _) =>
        {
            if (!_loading)
                _settings.Update(s => s.AutoSaveScreenshots = AutoSaveToggle.IsChecked == true);
        };

        ChooseFolderButton.Click += (_, _) => ChooseScreenshotFolder();

        // RF-P3.06: o ValueChanged so pinta o rotulo; quem grava e o CommitScrollDelay
        ScrollDelaySlider.ValueChanged += (_, _) =>
        {
            UpdateScrollDelayLabel();
            if (!_loading)
                _scrollDelayCommit.Start(); // Start em timer ligado reinicia a contagem
        };
        // fim do arrasto: o Slider nao expoe o evento, ele borbulha do Thumb do template
        ScrollDelaySlider.AddHandler(
            Thumb.DragCompletedEvent,
            new DragCompletedEventHandler((_, _) => CommitScrollDelay()));
        // sair do controle (Tab, clique em outro cartao, fechar a janela) grava na hora
        ScrollDelaySlider.LostFocus += (_, _) => CommitScrollDelay();

        EditorModifierCombo.SelectionChanged += (_, _) =>
        {
            if (!_loading && EditorModifierCombo.SelectedItem is ComboBoxItem { Tag: CaptureEditorModifier modifier })
                _settings.Update(s => s.EditorModifierKey = modifier);
        };

        // RF-F1.05: toda captura estatica abre no editor
        AlwaysEditorToggle.Click += (_, _) =>
        {
            if (!_loading)
                _settings.Update(s => s.AlwaysOpenEditorAfterCapture = AlwaysEditorToggle.IsChecked == true);
        };

        // ----- Gravacao de tela (specs F3/F4) -----

        ChooseRecordingsFolderButton.Click += (_, _) => ChooseRecordingsFolder();

        GifFpsCombo.SelectionChanged += (_, _) =>
        {
            if (!_loading && GifFpsCombo.SelectedItem is ComboBoxItem { Tag: int fps })
                _settings.Update(s => s.GifFps = fps);
        };

        GifScaleCombo.SelectionChanged += (_, _) =>
        {
            if (!_loading && GifScaleCombo.SelectedItem is ComboBoxItem { Tag: int scale })
                _settings.Update(s => s.GifScalePercent = scale);
        };

        Mp4BitrateCombo.SelectionChanged += (_, _) =>
        {
            if (!_loading && Mp4BitrateCombo.SelectedItem is ComboBoxItem { Tag: int kbps })
                _settings.Update(s => s.Mp4BitrateKbps = kbps);
        };

        // RF-F3.04: modo reuniao
        HideRecordingBorderToggle.Click += (_, _) =>
        {
            if (!_loading)
                _settings.Update(s => s.HideRecordingBorder = HideRecordingBorderToggle.IsChecked == true);
        };

        // RF-F5.14: caminho manual do ffmpeg.exe para o editor de midia
        ChooseFfmpegButton.Click += (_, _) => ChooseFfmpegPath();
    }

    // ===================== Cadencia da captura com rolagem =====================

    /// <summary>RF-S.07: unico lugar que monta o "{0} ms" (eram dois).</summary>
    private void UpdateScrollDelayLabel() =>
        ScrollDelayLabel.Text = string.Format(Loc.CadenceValue, (int)ScrollDelaySlider.Value);

    /// <summary>
    /// RF-P3.06: grava a cadencia uma vez por gesto. Idempotente de proposito - o
    /// DragCompleted e o timer podem chegar os dois com o mesmo valor.
    /// </summary>
    private void CommitScrollDelay()
    {
        _scrollDelayCommit.Stop();
        if (_loading)
            return;

        var value = (int)ScrollDelaySlider.Value;
        if (_settings.Current.ScrollCaptureDelayMs == value)
            return;

        _settings.Update(s => s.ScrollCaptureDelayMs = value);
    }

    // ===================== Dialogos (modais e bloqueantes) =====================

    private void ChooseScreenshotFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Loc.ChooseFolder,
            InitialDirectory = _screenshotsFolder,
        };
        if (dialog.ShowDialog() != true)
            return;

        var folder = dialog.FolderName;
        _screenshotsFolder = folder;
        ScreenshotFolderBox.Text = folder;
        _settings.Update(s => s.ScreenshotsFolder = folder);
    }

    /// <summary>RF-F3.06: pasta configuravel das gravacoes.</summary>
    private void ChooseRecordingsFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Loc.ChooseFolder,
            InitialDirectory = _recordingsFolder,
        };
        if (dialog.ShowDialog() != true)
            return;

        var folder = dialog.FolderName;
        // o campo guarda o resolvido e o disco guarda o escolhido; aqui os dois
        // coincidem, porque escolher a mao ja e um caminho absoluto
        _recordingsFolder = folder;
        RecordingsFolderBox.Text = folder;
        _settings.Update(s => s.RecordingsFolder = folder);
    }

    /// <summary>RF-F5.14: escolha manual do ffmpeg.exe (download automatico fica para depois).</summary>
    private void ChooseFfmpegPath()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.FfmpegChoose,
            Filter = Loc.FfmpegFilter, // RF-S.07: era fixo em codigo
        };
        if (dialog.ShowDialog() != true)
            return;

        var path = dialog.FileName;
        FfmpegPathBox.Text = path;
        _settings.Update(s => s.FfmpegPath = path);
    }

    // ===================== Rotulos localizados dos combos =====================

    /// <summary>
    /// RF-F4.02: "15 fps (efetivo 14,3)" - honestidade com a grade de centesimos do
    /// formato GIF. O "efetivo" e localizado, entao a lista inteira e reescrita a cada
    /// refresh (que e o que roda depois de uma troca de idioma).
    /// </summary>
    private void RelabelGifFps()
    {
        foreach (var item in GifFpsCombo.Items)
        {
            if (item is ComboBoxItem { Tag: int fps } entry)
                entry.Content = BuildGifFpsLabel(fps);
        }
    }

    /// <summary>RF-S.07: "{0}%" saiu do codigo para o Loc.</summary>
    private void RelabelGifScale()
    {
        foreach (var item in GifScaleCombo.Items)
        {
            if (item is ComboBoxItem { Tag: int scale } entry)
                entry.Content = string.Format(Loc.GifScaleValue, scale);
        }
    }

    /// <summary>
    /// RF-S.07: "{0} Mbps" saiu do codigo para o Loc. O item "Automatico" e achado
    /// pela Tag 0 - o original reescrevia <c>Items[0]</c>, que estoura ou troca o
    /// rotulo errado assim que a ordem da lista mudar.
    /// </summary>
    private void RelabelMp4Bitrate()
    {
        foreach (var item in Mp4BitrateCombo.Items)
        {
            if (item is not ComboBoxItem { Tag: int kbps } entry)
                continue;
            entry.Content = kbps == Mp4BitrateAutoKbps
                ? Loc.Mp4BitrateAuto
                : string.Format(Loc.Mp4BitrateValue, kbps / 1000);
        }
    }

    /// <summary>
    /// So o item "Desativado" e localizado (os outros sao nomes de tecla). Achado pela
    /// Tag <c>None</c>, nao por <c>Items[3]</c>.
    /// </summary>
    private void RelabelEditorModifier()
    {
        foreach (var item in EditorModifierCombo.Items)
        {
            if (item is ComboBoxItem { Tag: CaptureEditorModifier.None } entry)
                entry.Content = Loc.ModifierDisabled;
        }
    }

    /// <summary>RF-F4.02: "15 fps (efetivo 14,3)"; 10 e 20 fps caem exatos na grade.</summary>
    private static string BuildGifFpsLabel(int fps)
    {
        var effective = GifRecordingMath.EffectiveFps(fps);
        return Math.Abs(effective - fps) < 0.05
            ? $"{fps} fps" // sem chave propria no Loc: "fps" e simbolo de unidade
            : string.Format(Loc.GifFpsEffective, fps, effective.ToString("0.#"));
    }

    // ===================== Helpers =====================

    /// <summary>
    /// Seleciona o item cujo Tag bate com o valor salvo; se nenhum bater (settings.json
    /// editado a mao, preset removido), cai no default da <see cref="AppSettings"/>.
    /// Nunca por indice: um literal quebra em silencio se a ordem da lista mudar.
    /// </summary>
    private static void SelectByTag<T>(ComboBox combo, T value, T fallback)
        where T : notnull =>
        combo.SelectedItem = FindByTag(combo, value) ?? FindByTag(combo, fallback);

    private static ComboBoxItem? FindByTag<T>(ComboBox combo, T tag)
        where T : notnull
    {
        foreach (var item in combo.Items)
        {
            if (item is ComboBoxItem entry && tag.Equals(entry.Tag))
                return entry;
        }
        return null;
    }

    /// <summary>
    /// Mesmo default do <c>CaptureController.AutoSave</c>: Imagens\Screenshots, o
    /// caminho que a ferramenta nativa usa.
    /// </summary>
    private static string DefaultScreenshotsFolder() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "Screenshots");
}
