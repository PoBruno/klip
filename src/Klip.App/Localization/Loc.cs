using System.Globalization;

namespace Klip.App.Localization;

/// <summary>
/// App localization: pt-BR and en-US, defaults to the Windows language.
/// Strings resolve at startup; switching language needs a restart.
/// Used from XAML via x:Static (e.g. {x:Static loc:Loc.SettingsTitle}).
/// </summary>
public static class Loc
{
    // null until Initialize; Get falls back to Pt (static initializers run in text order)
    private static Dictionary<string, string>? _table;

    /// <summary>Languages shown in the picker (saved value, native name).</summary>
    public static readonly IReadOnlyList<(string Value, string Display)> AvailableLanguages =
    [
        ("pt-BR", "Português (Brasil)"),
        ("en-US", "English (US)"),
        ("es", "Español"),
        ("fr", "Français"),
        ("de", "Deutsch"),
        ("it", "Italiano"),
        ("nl", "Nederlands"),
        ("pl", "Polski"),
        ("tr", "Türkçe"),
        ("ru", "Русский"),
        ("ar", "العربية"),
        ("hi", "हिन्दी"),
        ("zh-CN", "中文（简体）"),
        ("ja", "日本語"),
        ("ko", "한국어"),
    ];

    /// <summary>Fires after a language switch so code-built parts rebuild.</summary>
    public static event Action? LanguageChanged;

    /// <summary>"system" or one of AvailableLanguages (AppSettings.Language).</summary>
    public static void Initialize(string language)
    {
        var code = language == "system" || string.IsNullOrWhiteSpace(language)
            ? CultureInfo.CurrentUICulture.Name // default: Windows language
            : language;
        _table = ResolveTable(code);
        PushToApplicationResources();
        LanguageChanged?.Invoke();
    }

    /// <summary>
    /// Publishes every string as "Loc.*" resources on the Application: XAML binds
    /// with DynamicResource, so a language switch updates all windows on the spot.
    /// </summary>
    private static void PushToApplicationResources()
    {
        var app = System.Windows.Application.Current;
        if (app is null)
            return;
        foreach (var key in En.Keys)
            app.Resources["Loc." + key] = Get(key);
    }

    private static Dictionary<string, string> ResolveTable(string code)
    {
        // normalize "pt-BR" -> "pt", "zh-CN"/"zh-Hans..." -> "zh" and so on
        var two = code.Length >= 2 ? code[..2].ToLowerInvariant() : "en";
        return two switch
        {
            "pt" => Pt,
            "es" => Tables.Es.Table,
            "fr" => Tables.Fr.Table,
            "de" => Tables.De.Table,
            "it" => Tables.It.Table,
            "nl" => Tables.Nl.Table,
            "pl" => Tables.Pl.Table,
            "tr" => Tables.Tr.Table,
            "ru" => Tables.Ru.Table,
            "ar" => Tables.Ar.Table,
            "hi" => Tables.Hi.Table,
            "zh" => Tables.Zh.Table,
            "ja" => Tables.Ja.Table,
            "ko" => Tables.Ko.Table,
            _ => En,
        };
    }

    private static string Get(string key) =>
        (_table ?? Pt).TryGetValue(key, out var value) ? value
        : En.TryGetValue(key, out var fallback) ? fallback // new key not translated yet
        : key;

    // ----- Tray -----
    public static string TrayOpenHistory => Get(nameof(TrayOpenHistory));
    public static string TrayCapture => Get(nameof(TrayCapture));
    public static string TraySettings => Get(nameof(TraySettings));
    public static string TrayExit => Get(nameof(TrayExit));
    public static string TrayPauseHistory => Get(nameof(TrayPauseHistory));
    public static string TrayResumeHistory => Get(nameof(TrayResumeHistory));
    public static string TrayPauseTooltip => Get(nameof(TrayPauseTooltip));

    // ----- Notifications -----
    public static string NotifyCaptureCopied => Get(nameof(NotifyCaptureCopied));
    public static string NotifyClickToEdit => Get(nameof(NotifyClickToEdit));
    public static string NotifyHotkeyConflict => Get(nameof(NotifyHotkeyConflict));
    public static string NotifyMemoryLimit => Get(nameof(NotifyMemoryLimit));
    public static string NotifyFramesDiscarded => Get(nameof(NotifyFramesDiscarded));
    public static string PasteFailedToast => Get(nameof(PasteFailedToast));

    // ----- Flyout -----
    public static string TabRecent => Get(nameof(TabRecent));
    public static string TabFavorites => Get(nameof(TabFavorites));
    public static string TabImages => Get(nameof(TabImages));
    public static string TabText => Get(nameof(TabText));
    public static string TabFiles => Get(nameof(TabFiles));
    public static string TabEmoji => Get(nameof(TabEmoji));
    public static string EmojiSearchPlaceholder => Get(nameof(EmojiSearchPlaceholder));
    public static string FilterByDate => Get(nameof(FilterByDate));
    public static string SearchPlaceholder => Get(nameof(SearchPlaceholder));
    public static string EmptyHistory => Get(nameof(EmptyHistory));
    public static string FooterHints => Get(nameof(FooterHints));
    public static string ClearAll => Get(nameof(ClearAll));
    public static string ClearAllTooltip => Get(nameof(ClearAllTooltip));
    public static string PinTooltip => Get(nameof(PinTooltip));
    public static string FavoriteTooltip => Get(nameof(FavoriteTooltip));
    public static string MoreOptions => Get(nameof(MoreOptions));
    public static string PinnedBadge => Get(nameof(PinnedBadge));
    public static string FavoriteBadge => Get(nameof(FavoriteBadge));
    public static string DateToday => Get(nameof(DateToday));
    public static string DateYesterday => Get(nameof(DateYesterday));
    public static string DateLast7 => Get(nameof(DateLast7));
    public static string DateLast30 => Get(nameof(DateLast30));
    public static string DateAll => Get(nameof(DateAll));
    public static string MenuPaste => Get(nameof(MenuPaste));
    public static string MenuPastePlain => Get(nameof(MenuPastePlain));
    public static string MenuCopy => Get(nameof(MenuCopy));
    public static string MenuSaveAs => Get(nameof(MenuSaveAs));
    public static string MenuOpenInEditor => Get(nameof(MenuOpenInEditor));
    public static string MenuPin => Get(nameof(MenuPin));
    public static string MenuUnpin => Get(nameof(MenuUnpin));
    public static string MenuFavorite => Get(nameof(MenuFavorite));
    public static string MenuUnfavorite => Get(nameof(MenuUnfavorite));
    public static string MenuDelete => Get(nameof(MenuDelete));
    public static string GroupPinned => Get(nameof(GroupPinned));
    public static string GroupRecent => Get(nameof(GroupRecent));
    public static string MultiPasteTooltip => Get(nameof(MultiPasteTooltip));
    public static string MultiPasteOrder => Get(nameof(MultiPasteOrder));
    public static string MultiPasteConfirm => Get(nameof(MultiPasteConfirm));
    public static string MultiPasteItem => Get(nameof(MultiPasteItem));
    public static string MultiPasteItems => Get(nameof(MultiPasteItems));
    public static string ItemImage => Get(nameof(ItemImage));
    public static string TimeNow => Get(nameof(TimeNow));
    public static string TimeYesterday => Get(nameof(TimeYesterday));
    public static string PngFilter => Get(nameof(PngFilter));

    // ----- Capture overlay -----
    public static string ModeRectangle => Get(nameof(ModeRectangle));
    public static string ModeWindow => Get(nameof(ModeWindow));
    public static string ModeFullscreen => Get(nameof(ModeFullscreen));
    public static string ModeFreeform => Get(nameof(ModeFreeform));
    public static string ModeScrolling => Get(nameof(ModeScrolling));
    public static string CloseEsc => Get(nameof(CloseEsc));
    public static string CaptureDelayTooltip => Get(nameof(CaptureDelayTooltip));

    // ----- Scrolling capture panel -----
    public static string PanoramicTitle => Get(nameof(PanoramicTitle));
    public static string PanoramicInstruction => Get(nameof(PanoramicInstruction));
    public static string PanoramicCaptured => Get(nameof(PanoramicCaptured));
    public static string PanoramicSlowDown => Get(nameof(PanoramicSlowDown));
    public static string PanoramicDone => Get(nameof(PanoramicDone));
    public static string PanoramicCancel => Get(nameof(PanoramicCancel));
    public static string CaptureTitleScreen => Get(nameof(CaptureTitleScreen));
    public static string CaptureTitleScrolling => Get(nameof(CaptureTitleScrolling));

    // ----- Editor -----
    public static string EditorTitle => Get(nameof(EditorTitle));
    public static string ToolSelect => Get(nameof(ToolSelect));
    public static string ToolPen => Get(nameof(ToolPen));
    public static string ToolHighlighter => Get(nameof(ToolHighlighter));
    public static string ToolEraser => Get(nameof(ToolEraser));
    public static string ToolRect => Get(nameof(ToolRect));
    public static string ToolEllipse => Get(nameof(ToolEllipse));
    public static string ToolLine => Get(nameof(ToolLine));
    public static string ToolArrow => Get(nameof(ToolArrow));
    public static string ToolText => Get(nameof(ToolText));
    public static string ToolCrop => Get(nameof(ToolCrop));
    public static string ToolBlur => Get(nameof(ToolBlur));
    public static string ToolEmoji => Get(nameof(ToolEmoji));
    public static string ToolRotateLeft => Get(nameof(ToolRotateLeft));
    public static string ToolRotateRight => Get(nameof(ToolRotateRight));
    public static string ToolRemoveBg => Get(nameof(ToolRemoveBg));
    public static string ToolUndo => Get(nameof(ToolUndo));
    public static string ToolRedo => Get(nameof(ToolRedo));
    public static string ToolAutoCopy => Get(nameof(ToolAutoCopy));
    public static string ToolSaveAs => Get(nameof(ToolSaveAs));
    public static string Thickness => Get(nameof(Thickness));
    public static string Zoom => Get(nameof(Zoom));
    public static string StatusCopied => Get(nameof(StatusCopied));
    public static string BgRemoved => Get(nameof(BgRemoved));
    public static string BgNotFound => Get(nameof(BgNotFound));
    public static string ToolTextActions => Get(nameof(ToolTextActions));
    public static string OcrUnavailable => Get(nameof(OcrUnavailable));
    public static string OcrWorking => Get(nameof(OcrWorking));
    public static string OcrNoText => Get(nameof(OcrNoText));
    public static string OcrCopied => Get(nameof(OcrCopied));
    public static string ToolQuickRedact => Get(nameof(ToolQuickRedact));
    public static string RedactWorking => Get(nameof(RedactWorking));
    public static string RedactNothing => Get(nameof(RedactNothing));
    public static string RedactDone => Get(nameof(RedactDone));
    public static string SaveImageFilter => Get(nameof(SaveImageFilter));

    // ----- Settings -----
    public static string SettingsTitle => Get(nameof(SettingsTitle));
    public static string SectionNativeHotkeys => Get(nameof(SectionNativeHotkeys));
    public static string CardWinVTitle => Get(nameof(CardWinVTitle));
    public static string CardWinVCaption => Get(nameof(CardWinVCaption));
    public static string TakeWinV => Get(nameof(TakeWinV));
    public static string CardCaptureKeyTitle => Get(nameof(CardCaptureKeyTitle));
    public static string CardCaptureKeyCaption => Get(nameof(CardCaptureKeyCaption));
    public static string UsePrintScreen => Get(nameof(UsePrintScreen));
    public static string UseWinShiftS => Get(nameof(UseWinShiftS));
    public static string CardRevertTitle => Get(nameof(CardRevertTitle));
    public static string CardRevertCaption => Get(nameof(CardRevertCaption));
    public static string Revert => Get(nameof(Revert));
    public static string SectionAppHotkeys => Get(nameof(SectionAppHotkeys));
    public static string OpenHistoryHotkey => Get(nameof(OpenHistoryHotkey));
    public static string NewCaptureHotkey => Get(nameof(NewCaptureHotkey));
    public static string HotkeyHint => Get(nameof(HotkeyHint));
    public static string SectionGeneral => Get(nameof(SectionGeneral));
    public static string StartWithWindows => Get(nameof(StartWithWindows));
    public static string LanguageTitle => Get(nameof(LanguageTitle));
    public static string LanguageCaption => Get(nameof(LanguageCaption));
    public static string LanguageSystem => Get(nameof(LanguageSystem));
    public static string LanguageRestart => Get(nameof(LanguageRestart));
    public static string ThemeTitle => Get(nameof(ThemeTitle));
    public static string ThemeSystem => Get(nameof(ThemeSystem));
    public static string ThemeLight => Get(nameof(ThemeLight));
    public static string ThemeDark => Get(nameof(ThemeDark));
    public static string ExcludedAppsTitle => Get(nameof(ExcludedAppsTitle));
    public static string ExcludedAppsCaption => Get(nameof(ExcludedAppsCaption));
    public static string AddApp => Get(nameof(AddApp));
    public static string RemoveApp => Get(nameof(RemoveApp));
    public static string SectionClipboard => Get(nameof(SectionClipboard));
    public static string MaxItemsTitle => Get(nameof(MaxItemsTitle));
    public static string MaxItemsCaption => Get(nameof(MaxItemsCaption));
    public static string MaxAgeTitle => Get(nameof(MaxAgeTitle));
    public static string MaxAgeCaption => Get(nameof(MaxAgeCaption));
    public static string ScreenshotFolderTitle => Get(nameof(ScreenshotFolderTitle));
    public static string ScreenshotFolderCaption => Get(nameof(ScreenshotFolderCaption));
    public static string ChooseFolder => Get(nameof(ChooseFolder));
    public static string SectionCapture => Get(nameof(SectionCapture));
    public static string AutoSaveTitle => Get(nameof(AutoSaveTitle));
    public static string AutoSaveCaption => Get(nameof(AutoSaveCaption));
    public static string CadenceTitle => Get(nameof(CadenceTitle));
    public static string CadenceCaption => Get(nameof(CadenceCaption));
    public static string SectionAbout => Get(nameof(SectionAbout));
    public static string SectionPrivacy => Get(nameof(SectionPrivacy));
    public static string SectionMaintenance => Get(nameof(SectionMaintenance));
    public static string SectionDiagnostics => Get(nameof(SectionDiagnostics));
    public static string SkipSecretsTitle => Get(nameof(SkipSecretsTitle));
    public static string SkipSecretsCaption => Get(nameof(SkipSecretsCaption));
    public static string RestoreClipboardTitle => Get(nameof(RestoreClipboardTitle));
    public static string RestoreClipboardCaption => Get(nameof(RestoreClipboardCaption));
    public static string ClearOnExitTitle => Get(nameof(ClearOnExitTitle));
    public static string ClearOnExitCaption => Get(nameof(ClearOnExitCaption));
    public static string BackupTitle => Get(nameof(BackupTitle));
    public static string BackupCaption => Get(nameof(BackupCaption));
    public static string ExportHistory => Get(nameof(ExportHistory));
    public static string ImportHistory => Get(nameof(ImportHistory));
    public static string CompactDb => Get(nameof(CompactDb));
    public static string OpenDataFolder => Get(nameof(OpenDataFolder));
    public static string DiagnosticsCaption => Get(nameof(DiagnosticsCaption));
    public static string RunDiagnostics => Get(nameof(RunDiagnostics));
    public static string ExportDone => Get(nameof(ExportDone));
    public static string ImportDone => Get(nameof(ImportDone));
    public static string CompactDone => Get(nameof(CompactDone));
    public static string BackupFilter => Get(nameof(BackupFilter));
    public static string OnboardWelcomeTitle => Get(nameof(OnboardWelcomeTitle));
    public static string OnboardWelcomeSubtitle => Get(nameof(OnboardWelcomeSubtitle));
    public static string OnboardShortcutsTitle => Get(nameof(OnboardShortcutsTitle));
    public static string OnboardHistoryDesc => Get(nameof(OnboardHistoryDesc));
    public static string OnboardCaptureDesc => Get(nameof(OnboardCaptureDesc));
    public static string OnboardWinVTitle => Get(nameof(OnboardWinVTitle));
    public static string OnboardWinVDesc => Get(nameof(OnboardWinVDesc));
    public static string OnboardFinish => Get(nameof(OnboardFinish));
    public static string AboutText => Get(nameof(AboutText));
    public static string ItemsInHistory => Get(nameof(ItemsInHistory));

    // takeover states and flows
    public static string WinVActive => Get(nameof(WinVActive));
    public static string WinVFreedNotBound => Get(nameof(WinVFreedNotBound));
    public static string WinVNative => Get(nameof(WinVNative));
    public static string ManagedPolicyWarning => Get(nameof(ManagedPolicyWarning));
    public static string PrtScActive => Get(nameof(PrtScActive));
    public static string WinShiftSActive => Get(nameof(WinShiftSActive));
    public static string PrtScFreeInfo => Get(nameof(PrtScFreeInfo));
    public static string PrtScNativeInfo => Get(nameof(PrtScNativeInfo));
    public static string ConfirmWinV => Get(nameof(ConfirmWinV));
    public static string ConfirmWinVTitle => Get(nameof(ConfirmWinVTitle));
    public static string ConfirmHklm => Get(nameof(ConfirmHklm));
    public static string ConfirmHklmTitle => Get(nameof(ConfirmHklmTitle));
    public static string ConfirmWinShiftS => Get(nameof(ConfirmWinShiftS));
    public static string ConfirmWinShiftSTitle => Get(nameof(ConfirmWinShiftSTitle));
    public static string ConfirmRevert => Get(nameof(ConfirmRevert));
    public static string ConfirmRevertTitle => Get(nameof(ConfirmRevertTitle));
    public static string BusyApplying => Get(nameof(BusyApplying));
    public static string BusyReverting => Get(nameof(BusyReverting));
    public static string ResultWinVOk => Get(nameof(ResultWinVOk));
    public static string ResultUacCancelled => Get(nameof(ResultUacCancelled));
    public static string ResultWinVFail => Get(nameof(ResultWinVFail));
    public static string ResultPrtScOk => Get(nameof(ResultPrtScOk));
    public static string ResultPrtScConflict => Get(nameof(ResultPrtScConflict));
    public static string ResultWinShiftSOk => Get(nameof(ResultWinShiftSOk));
    public static string ResultWinShiftSFail => Get(nameof(ResultWinShiftSFail));
    public static string ResultReverted => Get(nameof(ResultReverted));
    public static string HotkeyUpdated => Get(nameof(HotkeyUpdated));
    public static string HotkeyConflict => Get(nameof(HotkeyConflict));

    // ----- Tables -----

    private static readonly Dictionary<string, string> Pt = new()
    {
        [nameof(TrayOpenHistory)] = "Abrir histórico",
        [nameof(TrayCapture)] = "Capturar tela",
        [nameof(TraySettings)] = "Configurações",
        [nameof(TrayExit)] = "Sair",
        [nameof(TrayPauseHistory)] = "Pausar histórico de clipboard",
        [nameof(TrayResumeHistory)] = "Retomar histórico de clipboard",
        [nameof(TrayPauseTooltip)] = "Enquanto pausado, nada que você copiar é salvo no histórico",

        [nameof(NotifyCaptureCopied)] = "Captura copiada ({0}x{1})",
        [nameof(NotifyClickToEdit)] = "Clique para editar",
        [nameof(NotifyHotkeyConflict)] = "O atalho {0} já está em uso por outro aplicativo",
        [nameof(NotifyMemoryLimit)] = "Limite de memória atingido. A captura foi concluída automaticamente",
        [nameof(NotifyFramesDiscarded)] = "Alguns trechos foram descartados. Role mais devagar na próxima",
        [nameof(PasteFailedToast)] = "Item copiado. Cole com Ctrl+V (não foi possível colar automaticamente).",

        [nameof(TabRecent)] = "Recentes",
        [nameof(TabFavorites)] = "Favoritos",
        [nameof(TabImages)] = "Imagens e prints",
        [nameof(TabText)] = "Texto",
        [nameof(TabFiles)] = "Arquivos",
        [nameof(TabEmoji)] = "Emoji e simbolos",
        [nameof(EmojiSearchPlaceholder)] = "Buscar emoji...",
        [nameof(FilterByDate)] = "Filtrar por data",
        [nameof(SearchPlaceholder)] = "Pesquisar no histórico",
        [nameof(EmptyHistory)] = "Nada por aqui. Copie algo!",
        [nameof(FooterHints)] = "Enter cola · Shift+Enter texto puro · Del exclui",
        [nameof(ClearAll)] = "Limpar tudo",
        [nameof(ClearAllTooltip)] = "Remove itens não fixados e não favoritos",
        [nameof(PinTooltip)] = "Fixar no topo (Ctrl+P)",
        [nameof(FavoriteTooltip)] = "Favoritar (Ctrl+D)",
        [nameof(MoreOptions)] = "Mais opções",
        [nameof(PinnedBadge)] = "Fixado",
        [nameof(FavoriteBadge)] = "Favorito",
        [nameof(DateToday)] = "Hoje",
        [nameof(DateYesterday)] = "Ontem",
        [nameof(DateLast7)] = "Últimos 7 dias",
        [nameof(DateLast30)] = "Últimos 30 dias",
        [nameof(DateAll)] = "Todo o período",
        [nameof(MenuPaste)] = "Colar",
        [nameof(MenuPastePlain)] = "Colar como texto puro",
        [nameof(MenuCopy)] = "Copiar",
        [nameof(MenuSaveAs)] = "Salvar como...",
        [nameof(MenuOpenInEditor)] = "Abrir no editor",
        [nameof(MenuPin)] = "Fixar no topo",
        [nameof(MenuUnpin)] = "Desafixar",
        [nameof(MenuFavorite)] = "Favoritar",
        [nameof(MenuUnfavorite)] = "Remover dos favoritos",
        [nameof(MenuDelete)] = "Excluir",
        [nameof(GroupPinned)] = "Fixados",
        [nameof(GroupRecent)] = "Recentes",
        [nameof(MultiPasteTooltip)] = "Colar vários em sequência",
        [nameof(MultiPasteOrder)] = "Selecione a ordem de colagem",
        [nameof(MultiPasteConfirm)] = "Selecionar",
        [nameof(MultiPasteItem)] = "1 item",
        [nameof(MultiPasteItems)] = "{0} itens",
        [nameof(ItemImage)] = "Imagem",
        [nameof(TimeNow)] = "agora",
        [nameof(TimeYesterday)] = "ontem",
        [nameof(PngFilter)] = "Imagem PNG|*.png",

        [nameof(ModeRectangle)] = "Retângulo",
        [nameof(ModeWindow)] = "Janela",
        [nameof(ModeFullscreen)] = "Tela cheia",
        [nameof(ModeFreeform)] = "Forma livre (desenhe o contorno da area)",
        [nameof(ModeScrolling)] = "Captura com rolagem (arraste sobre a área que rola)",
        [nameof(CloseEsc)] = "Fechar (Esc)",
        [nameof(CaptureDelayTooltip)] = "Atraso antes de capturar (clique para alternar 3/5/10s)",

        [nameof(PanoramicTitle)] = "Captura com rolagem",
        [nameof(PanoramicInstruction)] = "Role a área destacada no seu ritmo,\nsuave e numa direção só.",
        [nameof(PanoramicCaptured)] = "{0} px capturados",
        [nameof(PanoramicSlowDown)] = "Role mais devagar",
        [nameof(PanoramicDone)] = "Concluir",
        [nameof(PanoramicCancel)] = "Cancelar",
        [nameof(CaptureTitleScreen)] = "Captura de tela",
        [nameof(CaptureTitleScrolling)] = "Captura com rolagem",

        [nameof(EditorTitle)] = "Editor do Klip",
        [nameof(ToolSelect)] = "Selecionar e mover (V)",
        [nameof(ToolPen)] = "Caneta (P)",
        [nameof(ToolHighlighter)] = "Marca-texto (H)",
        [nameof(ToolEraser)] = "Borracha (E)",
        [nameof(ToolRect)] = "Retângulo (R)",
        [nameof(ToolEllipse)] = "Elipse (O)",
        [nameof(ToolLine)] = "Linha (L)",
        [nameof(ToolArrow)] = "Seta (A)",
        [nameof(ToolText)] = "Texto (T)",
        [nameof(ToolCrop)] = "Recortar (C): Enter aplica, Esc cancela",
        [nameof(ToolBlur)] = "Desfocar (B): arraste sobre a area pra borrar",
        [nameof(ToolEmoji)] = "Emoji e stickers: escolha e clique pra carimbar",
        [nameof(ToolRotateLeft)] = "Girar a esquerda",
        [nameof(ToolRotateRight)] = "Girar a direita",
        [nameof(ToolRemoveBg)] = "Remover fundo (fundos sólidos conectados às bordas viram transparência)",
        [nameof(ToolUndo)] = "Desfazer (Ctrl+Z)",
        [nameof(ToolRedo)] = "Refazer (Ctrl+Y)",
        [nameof(ToolAutoCopy)] = "Copiar automaticamente a cada edição",
        [nameof(ToolSaveAs)] = "Salvar como... (Ctrl+S)",
        [nameof(Thickness)] = "Espessura",
        [nameof(Zoom)] = "Zoom",
        [nameof(StatusCopied)] = "copiado",
        [nameof(BgRemoved)] = "Fundo removido ({0} px transparentes)",
        [nameof(BgNotFound)] = "Nenhum fundo uniforme detectado nas bordas",
        [nameof(ToolTextActions)] = "Extrair texto (OCR)",
        [nameof(OcrUnavailable)] = "OCR indisponivel: falta um pacote de idioma no Windows",
        [nameof(OcrWorking)] = "Lendo o texto da imagem...",
        [nameof(OcrNoText)] = "Nenhum texto encontrado na imagem",
        [nameof(OcrCopied)] = "Texto extraido e copiado",
        [nameof(ToolQuickRedact)] = "Tapar dados sensiveis (email, telefone, cartao) automaticamente",
        [nameof(RedactWorking)] = "Procurando dados sensiveis...",
        [nameof(RedactNothing)] = "Nenhum dado sensivel encontrado",
        [nameof(RedactDone)] = "{0} trecho(s) tapado(s)",
        [nameof(SaveImageFilter)] = "Imagem PNG|*.png|Imagem JPEG|*.jpg",

        [nameof(SettingsTitle)] = "Configurações do Klip",
        [nameof(SectionNativeHotkeys)] = "Atalhos nativos do Windows",
        [nameof(CardWinVTitle)] = "Usar Win+V para o Klip",
        [nameof(CardWinVCaption)] = "Desativa o painel nativo de histórico (registro DisabledHotkeys) e registra o Win+V para o Klip. Reinicia a Área de Trabalho no processo. Reversível a qualquer momento.",
        [nameof(TakeWinV)] = "Assumir Win+V",
        [nameof(CardCaptureKeyTitle)] = "Atalho nativo de captura",
        [nameof(CardCaptureKeyCaption)] = "Print Screen (recomendado): libera a tecla PrtSc do Snipping Tool nativo, sem efeitos colaterais. Win+Shift+S: também desativa o Win+S (Pesquisa do Windows), limitação do Windows.",
        [nameof(UsePrintScreen)] = "Usar Print Screen",
        [nameof(UseWinShiftS)] = "Usar Win+Shift+S",
        [nameof(CardRevertTitle)] = "Reverter tudo",
        [nameof(CardRevertCaption)] = "Restaura os atalhos nativos do Windows exatamente como estavam (backup automático) e volta o Klip para Ctrl+Shift+V / Ctrl+Shift+S.",
        [nameof(Revert)] = "Reverter",
        [nameof(SectionAppHotkeys)] = "Atalhos do Klip",
        [nameof(OpenHistoryHotkey)] = "Abrir histórico",
        [nameof(NewCaptureHotkey)] = "Nova captura",
        [nameof(HotkeyHint)] = "Clique no campo e pressione a combinação",
        [nameof(SectionGeneral)] = "Geral",
        [nameof(StartWithWindows)] = "Iniciar com o Windows",
        [nameof(LanguageTitle)] = "Idioma",
        [nameof(LanguageCaption)] = "Padrão: idioma do Windows. A troca é aplicada na hora.",
        [nameof(LanguageSystem)] = "Sistema",
        [nameof(LanguageRestart)] = "Idioma salvo. Reinicie o Klip para aplicar.",
        [nameof(ThemeTitle)] = "Tema",
        [nameof(ThemeSystem)] = "Sistema",
        [nameof(ThemeLight)] = "Claro",
        [nameof(ThemeDark)] = "Escuro",
        [nameof(ExcludedAppsTitle)] = "Aplicativos excluídos",
        [nameof(ExcludedAppsCaption)] = "Nada copiado a partir destes aplicativos é salvo no histórico. Informe o nome do processo (ex.: keepass.exe).",
        [nameof(AddApp)] = "Adicionar",
        [nameof(RemoveApp)] = "Remover",
        [nameof(SectionClipboard)] = "Histórico de clipboard",
        [nameof(MaxItemsTitle)] = "Máximo de itens",
        [nameof(MaxItemsCaption)] = "Fixados e favoritos nunca são removidos. 0 = ilimitado.",
        [nameof(MaxAgeTitle)] = "Apagar apos (dias)",
        [nameof(MaxAgeCaption)] = "Remove itens mais velhos que isso. 0 = sem limite. Fixados e favoritos ficam.",
        [nameof(ScreenshotFolderTitle)] = "Pasta das capturas",
        [nameof(ScreenshotFolderCaption)] = "Onde as capturas salvam quando o auto-salvar esta ligado.",
        [nameof(ChooseFolder)] = "Escolher",
        [nameof(SectionCapture)] = "Captura de tela",
        [nameof(AutoSaveTitle)] = "Salvar capturas automaticamente",
        [nameof(AutoSaveCaption)] = "Pasta Imagens\\Screenshots, padrão do Windows",
        [nameof(CadenceTitle)] = "Cadência da captura com rolagem",
        [nameof(CadenceCaption)] = "Intervalo entre quadros enquanto você rola (padrão 150 ms; aumente se a máquina engasgar)",
        [nameof(SectionAbout)] = "Sobre",
        [nameof(SectionPrivacy)] = "Privacidade",
        [nameof(SectionMaintenance)] = "Manutenção e backup",
        [nameof(SectionDiagnostics)] = "Diagnóstico",
        [nameof(SkipSecretsTitle)] = "Não guardar senhas e tokens",
        [nameof(SkipSecretsCaption)] = "Ignora itens que parecem segredos (chaves de API, JWT, senhas) e não os salva no histórico.",
        [nameof(RestoreClipboardTitle)] = "Restaurar a área de transferência após colar",
        [nameof(RestoreClipboardCaption)] = "Depois de colar um item, devolve o conteúdo que estava antes (colar sem poluir).",
        [nameof(ClearOnExitTitle)] = "Limpar histórico ao sair",
        [nameof(ClearOnExitCaption)] = "Ao fechar o Klip, apaga o histórico (mantém fixados e favoritos).",
        [nameof(BackupTitle)] = "Backup do histórico",
        [nameof(BackupCaption)] = "Exporte todo o histórico (itens e imagens) para um arquivo, ou importe de volta em outra máquina.",
        [nameof(ExportHistory)] = "Exportar",
        [nameof(ImportHistory)] = "Importar",
        [nameof(CompactDb)] = "Compactar banco",
        [nameof(OpenDataFolder)] = "Abrir pasta de dados",
        [nameof(DiagnosticsCaption)] = "Estado do sistema: atalhos registrados, chaves de registro e tamanho do banco.",
        [nameof(RunDiagnostics)] = "Verificar estado do sistema",
        [nameof(ExportDone)] = "Exportado: {0} itens, {1} arquivos.",
        [nameof(ImportDone)] = "Importado: {0} novos, {1} já existiam.",
        [nameof(CompactDone)] = "Banco compactado.",
        [nameof(BackupFilter)] = "Backup do Klip|*.zip",
        [nameof(OnboardWelcomeTitle)] = "Bem-vindo ao Klip",
        [nameof(OnboardWelcomeSubtitle)] = "Seu histórico de área de transferência e captura de tela, com a cara do Windows 11. Tudo fica no seu computador.",
        [nameof(OnboardShortcutsTitle)] = "Atalhos",
        [nameof(OnboardHistoryDesc)] = "Abre o histórico da área de transferência",
        [nameof(OnboardCaptureDesc)] = "Captura de tela",
        [nameof(OnboardWinVTitle)] = "Usar o atalho nativo Win+V (opcional)",
        [nameof(OnboardWinVDesc)] = "O Klip pode assumir o Win+V no lugar do painel nativo do Windows. Reinicia a Área de Trabalho e é reversível a qualquer momento nas Configurações.",
        [nameof(OnboardFinish)] = "Começar a usar",
        [nameof(AboutText)] = "Klip: histórico de clipboard e captura de tela nativos do Windows 11. Local-first: nada sai da sua máquina.",
        [nameof(ItemsInHistory)] = "{0} item(ns) no histórico.",

        [nameof(WinVActive)] = "Ativo: Win+V abre o Klip.",
        [nameof(WinVFreedNotBound)] = "Win+V está liberado no registro, mas não atribuído ao Klip.",
        [nameof(WinVNative)] = "Win+V está com o Windows (painel nativo).",
        [nameof(ManagedPolicyWarning)] = " (Máquina com políticas corporativas: alterações podem ser revertidas pelo domínio.)",
        [nameof(PrtScActive)] = "Ativo: Print Screen abre a captura do Klip.",
        [nameof(WinShiftSActive)] = "Ativo: Win+Shift+S abre a captura do Klip (Win+S desativado).",
        [nameof(PrtScFreeInfo)] = "PrtSc livre no sistema; captura do Klip em {0}.",
        [nameof(PrtScNativeInfo)] = "PrtSc abre o Snipping Tool nativo; captura do Klip em {0}.",
        [nameof(ConfirmWinV)] = "O Klip vai:\n\n1. Adicionar 'V' ao valor DisabledHotkeys do registro (HKCU), com backup automático.\n2. Reiniciar a Área de Trabalho (janelas do Explorer fecham; a barra pisca).\n3. Registrar o Win+V para o Klip.\n\nO painel nativo de histórico deixa de abrir. Reversível pelo botão Reverter.\n\nContinuar?",
        [nameof(ConfirmWinVTitle)] = "Assumir Win+V",
        [nameof(ConfirmHklm)] = "O Windows ainda segura o Win+V (comum no 24H2 ou mais novo).\n\nO plano B desativa o recurso nativo de histórico via HKLM (pede administrador). A página 'Área de transferência' some das Configurações do Windows até reverter.\n\nAplicar plano B?",
        [nameof(ConfirmHklmTitle)] = "Plano B (requer administrador)",
        [nameof(ConfirmWinShiftS)] = "ATENÇÃO: por limitação do Windows, desativar o Win+Shift+S também desativa o Win+S (Pesquisa do Windows).\n\nA rota recomendada é a tecla Print Screen.\n\nContinuar mesmo assim? (Reinicia a Área de Trabalho)",
        [nameof(ConfirmWinShiftSTitle)] = "Assumir Win+Shift+S",
        [nameof(ConfirmRevert)] = "Restaura os atalhos nativos do Windows a partir do backup e volta o Klip para Ctrl+Shift+V / Ctrl+Shift+S. Reinicia a Área de Trabalho.\n\nContinuar?",
        [nameof(ConfirmRevertTitle)] = "Reverter",
        [nameof(BusyApplying)] = "Aplicando registro e reiniciando a Área de Trabalho...",
        [nameof(BusyReverting)] = "Revertendo registro e reiniciando a Área de Trabalho...",
        [nameof(ResultWinVOk)] = "Pronto! Win+V agora abre o Klip.",
        [nameof(ResultUacCancelled)] = "Cancelado no UAC. Nada foi alterado no HKLM.",
        [nameof(ResultWinVFail)] = "Não foi possível registrar o Win+V. Veja o log em %LocalAppData%\\Klip.",
        [nameof(ResultPrtScOk)] = "Pronto! Print Screen agora abre a captura do Klip.",
        [nameof(ResultPrtScConflict)] = "PrtSc liberado, mas outro app segura a tecla.",
        [nameof(ResultWinShiftSOk)] = "Pronto! Win+Shift+S agora abre a captura do Klip.",
        [nameof(ResultWinShiftSFail)] = "Não foi possível registrar o Win+Shift+S.",
        [nameof(ResultReverted)] = "Atalhos nativos do Windows restaurados.",
        [nameof(HotkeyUpdated)] = "Atalho atualizado: {0}",
        [nameof(HotkeyConflict)] = "{0} está em conflito com outro aplicativo.",
    };

    private static readonly Dictionary<string, string> En = new()
    {
        [nameof(TrayOpenHistory)] = "Open history",
        [nameof(TrayCapture)] = "Capture screen",
        [nameof(TraySettings)] = "Settings",
        [nameof(TrayExit)] = "Exit",
        [nameof(TrayPauseHistory)] = "Pause clipboard history",
        [nameof(TrayResumeHistory)] = "Resume clipboard history",
        [nameof(TrayPauseTooltip)] = "While paused, nothing you copy is saved to history",

        [nameof(NotifyCaptureCopied)] = "Capture copied ({0}x{1})",
        [nameof(NotifyClickToEdit)] = "Click to edit",
        [nameof(NotifyHotkeyConflict)] = "The shortcut {0} is already in use by another app",
        [nameof(NotifyMemoryLimit)] = "Memory limit reached. The capture was finished automatically",
        [nameof(NotifyFramesDiscarded)] = "Some sections were discarded. Scroll slower next time",
        [nameof(PasteFailedToast)] = "Item copied. Paste with Ctrl+V (could not paste automatically).",

        [nameof(TabRecent)] = "Recent",
        [nameof(TabFavorites)] = "Favorites",
        [nameof(TabImages)] = "Images and screenshots",
        [nameof(TabText)] = "Text",
        [nameof(TabFiles)] = "Files",
        [nameof(TabEmoji)] = "Emoji and symbols",
        [nameof(EmojiSearchPlaceholder)] = "Search emoji...",
        [nameof(FilterByDate)] = "Filter by date",
        [nameof(SearchPlaceholder)] = "Search history",
        [nameof(EmptyHistory)] = "Nothing here yet. Copy something!",
        [nameof(FooterHints)] = "Enter pastes · Shift+Enter plain text · Del deletes",
        [nameof(ClearAll)] = "Clear all",
        [nameof(ClearAllTooltip)] = "Removes items that are not pinned or favorited",
        [nameof(PinTooltip)] = "Pin to top (Ctrl+P)",
        [nameof(FavoriteTooltip)] = "Favorite (Ctrl+D)",
        [nameof(MoreOptions)] = "More options",
        [nameof(PinnedBadge)] = "Pinned",
        [nameof(FavoriteBadge)] = "Favorite",
        [nameof(DateToday)] = "Today",
        [nameof(DateYesterday)] = "Yesterday",
        [nameof(DateLast7)] = "Last 7 days",
        [nameof(DateLast30)] = "Last 30 days",
        [nameof(DateAll)] = "All time",
        [nameof(MenuPaste)] = "Paste",
        [nameof(MenuPastePlain)] = "Paste as plain text",
        [nameof(MenuCopy)] = "Copy",
        [nameof(MenuSaveAs)] = "Save as...",
        [nameof(MenuOpenInEditor)] = "Open in editor",
        [nameof(MenuPin)] = "Pin to top",
        [nameof(MenuUnpin)] = "Unpin",
        [nameof(MenuFavorite)] = "Favorite",
        [nameof(MenuUnfavorite)] = "Remove from favorites",
        [nameof(MenuDelete)] = "Delete",
        [nameof(GroupPinned)] = "Pinned",
        [nameof(GroupRecent)] = "Recent",
        [nameof(MultiPasteTooltip)] = "Paste several in sequence",
        [nameof(MultiPasteOrder)] = "Choose the paste order",
        [nameof(MultiPasteConfirm)] = "Select",
        [nameof(MultiPasteItem)] = "1 item",
        [nameof(MultiPasteItems)] = "{0} items",
        [nameof(ItemImage)] = "Image",
        [nameof(TimeNow)] = "now",
        [nameof(TimeYesterday)] = "yesterday",
        [nameof(PngFilter)] = "PNG image|*.png",

        [nameof(ModeRectangle)] = "Rectangle",
        [nameof(ModeWindow)] = "Window",
        [nameof(ModeFullscreen)] = "Full screen",
        [nameof(ModeFreeform)] = "Free form (draw the outline of the area)",
        [nameof(ModeScrolling)] = "Scrolling capture (drag over the area that scrolls)",
        [nameof(CloseEsc)] = "Close (Esc)",
        [nameof(CaptureDelayTooltip)] = "Delay before capturing (click to cycle 3/5/10s)",

        [nameof(PanoramicTitle)] = "Scrolling capture",
        [nameof(PanoramicInstruction)] = "Scroll the highlighted area at your own pace,\nsmoothly and in one direction.",
        [nameof(PanoramicCaptured)] = "{0} px captured",
        [nameof(PanoramicSlowDown)] = "Scroll slower",
        [nameof(PanoramicDone)] = "Done",
        [nameof(PanoramicCancel)] = "Cancel",
        [nameof(CaptureTitleScreen)] = "Screen capture",
        [nameof(CaptureTitleScrolling)] = "Scrolling capture",

        [nameof(EditorTitle)] = "Klip Editor",
        [nameof(ToolSelect)] = "Select and move (V)",
        [nameof(ToolPen)] = "Pen (P)",
        [nameof(ToolHighlighter)] = "Highlighter (H)",
        [nameof(ToolEraser)] = "Eraser (E)",
        [nameof(ToolRect)] = "Rectangle (R)",
        [nameof(ToolEllipse)] = "Ellipse (O)",
        [nameof(ToolLine)] = "Line (L)",
        [nameof(ToolArrow)] = "Arrow (A)",
        [nameof(ToolText)] = "Text (T)",
        [nameof(ToolCrop)] = "Crop (C): Enter applies, Esc cancels",
        [nameof(ToolBlur)] = "Blur (B): drag over the area to redact",
        [nameof(ToolEmoji)] = "Emoji and stickers: pick one, then click to stamp",
        [nameof(ToolRotateLeft)] = "Rotate left",
        [nameof(ToolRotateRight)] = "Rotate right",
        [nameof(ToolRemoveBg)] = "Remove background (solid backgrounds connected to the edges become transparent)",
        [nameof(ToolUndo)] = "Undo (Ctrl+Z)",
        [nameof(ToolRedo)] = "Redo (Ctrl+Y)",
        [nameof(ToolAutoCopy)] = "Copy automatically on every edit",
        [nameof(ToolSaveAs)] = "Save as... (Ctrl+S)",
        [nameof(Thickness)] = "Thickness",
        [nameof(Zoom)] = "Zoom",
        [nameof(StatusCopied)] = "copied",
        [nameof(BgRemoved)] = "Background removed ({0} px transparent)",
        [nameof(BgNotFound)] = "No uniform background detected at the edges",
        [nameof(ToolTextActions)] = "Extract text (OCR)",
        [nameof(OcrUnavailable)] = "OCR unavailable: no language pack installed in Windows",
        [nameof(OcrWorking)] = "Reading text from the image...",
        [nameof(OcrNoText)] = "No text found in the image",
        [nameof(OcrCopied)] = "Text extracted and copied",
        [nameof(ToolQuickRedact)] = "Auto-redact sensitive data (email, phone, card)",
        [nameof(RedactWorking)] = "Looking for sensitive data...",
        [nameof(RedactNothing)] = "No sensitive data found",
        [nameof(RedactDone)] = "{0} item(s) redacted",
        [nameof(SaveImageFilter)] = "PNG image|*.png|JPEG image|*.jpg",

        [nameof(SettingsTitle)] = "Klip Settings",
        [nameof(SectionNativeHotkeys)] = "Native Windows shortcuts",
        [nameof(CardWinVTitle)] = "Use Win+V for Klip",
        [nameof(CardWinVCaption)] = "Disables the native history panel (DisabledHotkeys registry value) and registers Win+V for Klip. Restarts the desktop in the process. Reversible at any time.",
        [nameof(TakeWinV)] = "Take over Win+V",
        [nameof(CardCaptureKeyTitle)] = "Native capture shortcut",
        [nameof(CardCaptureKeyCaption)] = "Print Screen (recommended): frees the PrtSc key from the native Snipping Tool, no side effects. Win+Shift+S: also disables Win+S (Windows Search), a Windows limitation.",
        [nameof(UsePrintScreen)] = "Use Print Screen",
        [nameof(UseWinShiftS)] = "Use Win+Shift+S",
        [nameof(CardRevertTitle)] = "Revert everything",
        [nameof(CardRevertCaption)] = "Restores the native Windows shortcuts exactly as they were (automatic backup) and returns Klip to Ctrl+Shift+V / Ctrl+Shift+S.",
        [nameof(Revert)] = "Revert",
        [nameof(SectionAppHotkeys)] = "Klip shortcuts",
        [nameof(OpenHistoryHotkey)] = "Open history",
        [nameof(NewCaptureHotkey)] = "New capture",
        [nameof(HotkeyHint)] = "Click the field and press the combination",
        [nameof(SectionGeneral)] = "General",
        [nameof(StartWithWindows)] = "Start with Windows",
        [nameof(LanguageTitle)] = "Language",
        [nameof(LanguageCaption)] = "Default: Windows language. Changes apply instantly.",
        [nameof(LanguageSystem)] = "System",
        [nameof(LanguageRestart)] = "Language saved. Restart Klip to apply.",
        [nameof(ThemeTitle)] = "Theme",
        [nameof(ThemeSystem)] = "System",
        [nameof(ThemeLight)] = "Light",
        [nameof(ThemeDark)] = "Dark",
        [nameof(ExcludedAppsTitle)] = "Excluded apps",
        [nameof(ExcludedAppsCaption)] = "Nothing copied from these apps is saved to history. Enter the process name (e.g. keepass.exe).",
        [nameof(AddApp)] = "Add",
        [nameof(RemoveApp)] = "Remove",
        [nameof(SectionClipboard)] = "Clipboard history",
        [nameof(MaxItemsTitle)] = "Maximum items",
        [nameof(MaxItemsCaption)] = "Pinned and favorited items are never removed. 0 = unlimited.",
        [nameof(MaxAgeTitle)] = "Delete after (days)",
        [nameof(MaxAgeCaption)] = "Removes items older than this. 0 = no limit. Pinned and favorites stay.",
        [nameof(ScreenshotFolderTitle)] = "Screenshots folder",
        [nameof(ScreenshotFolderCaption)] = "Where captures are saved when auto-save is on.",
        [nameof(ChooseFolder)] = "Choose",
        [nameof(SectionCapture)] = "Screen capture",
        [nameof(AutoSaveTitle)] = "Save captures automatically",
        [nameof(AutoSaveCaption)] = "Pictures\\Screenshots folder, the Windows default",
        [nameof(CadenceTitle)] = "Scrolling capture cadence",
        [nameof(CadenceCaption)] = "Interval between frames while you scroll (default 150 ms; increase if your machine stutters)",
        [nameof(SectionAbout)] = "About",
        [nameof(SectionPrivacy)] = "Privacy",
        [nameof(SectionMaintenance)] = "Maintenance and backup",
        [nameof(SectionDiagnostics)] = "Diagnostics",
        [nameof(SkipSecretsTitle)] = "Don't store passwords and tokens",
        [nameof(SkipSecretsCaption)] = "Ignores items that look like secrets (API keys, JWT, passwords) and does not save them to history.",
        [nameof(RestoreClipboardTitle)] = "Restore clipboard after pasting",
        [nameof(RestoreClipboardCaption)] = "After pasting an item, puts back what was there before (paste without polluting).",
        [nameof(ClearOnExitTitle)] = "Clear history on exit",
        [nameof(ClearOnExitCaption)] = "When Klip closes, clears the history (keeps pinned and favorites).",
        [nameof(BackupTitle)] = "History backup",
        [nameof(BackupCaption)] = "Export the whole history (items and images) to a file, or import it back on another machine.",
        [nameof(ExportHistory)] = "Export",
        [nameof(ImportHistory)] = "Import",
        [nameof(CompactDb)] = "Compact database",
        [nameof(OpenDataFolder)] = "Open data folder",
        [nameof(DiagnosticsCaption)] = "System state: registered shortcuts, registry keys and database size.",
        [nameof(RunDiagnostics)] = "Check system state",
        [nameof(ExportDone)] = "Exported: {0} items, {1} files.",
        [nameof(ImportDone)] = "Imported: {0} new, {1} already existed.",
        [nameof(CompactDone)] = "Database compacted.",
        [nameof(BackupFilter)] = "Klip backup|*.zip",
        [nameof(OnboardWelcomeTitle)] = "Welcome to Klip",
        [nameof(OnboardWelcomeSubtitle)] = "Your clipboard history and screen capture, with the Windows 11 look. Everything stays on your computer.",
        [nameof(OnboardShortcutsTitle)] = "Shortcuts",
        [nameof(OnboardHistoryDesc)] = "Opens the clipboard history",
        [nameof(OnboardCaptureDesc)] = "Screen capture",
        [nameof(OnboardWinVTitle)] = "Use the native Win+V shortcut (optional)",
        [nameof(OnboardWinVDesc)] = "Klip can take over Win+V instead of the native Windows panel. It restarts the desktop and is reversible anytime in Settings.",
        [nameof(OnboardFinish)] = "Start using",
        [nameof(AboutText)] = "Klip: native Windows 11 clipboard history and screen capture. Local-first: nothing leaves your machine.",
        [nameof(ItemsInHistory)] = "{0} item(s) in history.",

        [nameof(WinVActive)] = "Active: Win+V opens Klip.",
        [nameof(WinVFreedNotBound)] = "Win+V is freed in the registry but not assigned to Klip.",
        [nameof(WinVNative)] = "Win+V belongs to Windows (native panel).",
        [nameof(ManagedPolicyWarning)] = " (Machine with corporate policies: changes may be reverted by the domain.)",
        [nameof(PrtScActive)] = "Active: Print Screen opens Klip capture.",
        [nameof(WinShiftSActive)] = "Active: Win+Shift+S opens Klip capture (Win+S disabled).",
        [nameof(PrtScFreeInfo)] = "PrtSc is free in the system; Klip capture is on {0}.",
        [nameof(PrtScNativeInfo)] = "PrtSc opens the native Snipping Tool; Klip capture is on {0}.",
        [nameof(ConfirmWinV)] = "Klip will:\n\n1. Add 'V' to the DisabledHotkeys registry value (HKCU), with automatic backup.\n2. Restart the desktop (Explorer windows close; the taskbar flashes).\n3. Register Win+V for Klip.\n\nThe native history panel stops opening. Reversible via the Revert button.\n\nContinue?",
        [nameof(ConfirmWinVTitle)] = "Take over Win+V",
        [nameof(ConfirmHklm)] = "Windows still holds Win+V (common on 24H2 or newer).\n\nPlan B disables the native history feature via HKLM (requires administrator). The 'Clipboard' page disappears from Windows Settings until reverted.\n\nApply plan B?",
        [nameof(ConfirmHklmTitle)] = "Plan B (requires administrator)",
        [nameof(ConfirmWinShiftS)] = "WARNING: due to a Windows limitation, disabling Win+Shift+S also disables Win+S (Windows Search).\n\nThe recommended route is the Print Screen key.\n\nContinue anyway? (Restarts the desktop)",
        [nameof(ConfirmWinShiftSTitle)] = "Take over Win+Shift+S",
        [nameof(ConfirmRevert)] = "Restores the native Windows shortcuts from backup and returns Klip to Ctrl+Shift+V / Ctrl+Shift+S. Restarts the desktop.\n\nContinue?",
        [nameof(ConfirmRevertTitle)] = "Revert",
        [nameof(BusyApplying)] = "Applying registry changes and restarting the desktop...",
        [nameof(BusyReverting)] = "Reverting registry changes and restarting the desktop...",
        [nameof(ResultWinVOk)] = "Done! Win+V now opens Klip.",
        [nameof(ResultUacCancelled)] = "Cancelled at UAC. Nothing was changed in HKLM.",
        [nameof(ResultWinVFail)] = "Could not register Win+V. Check the log at %LocalAppData%\\Klip.",
        [nameof(ResultPrtScOk)] = "Done! Print Screen now opens Klip capture.",
        [nameof(ResultPrtScConflict)] = "PrtSc freed, but another app holds the key.",
        [nameof(ResultWinShiftSOk)] = "Done! Win+Shift+S now opens Klip capture.",
        [nameof(ResultWinShiftSFail)] = "Could not register Win+Shift+S.",
        [nameof(ResultReverted)] = "Native Windows shortcuts restored.",
        [nameof(HotkeyUpdated)] = "Shortcut updated: {0}",
        [nameof(HotkeyConflict)] = "{0} conflicts with another application.",
    };
}
