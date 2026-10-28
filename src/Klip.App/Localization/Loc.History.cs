namespace Klip.App.Localization;

// strings for retention, scroll and flyout performance settings
public static partial class Loc
{
    // ----- History page: sections -----
    public static string HistorySectionRetention => Get(nameof(HistorySectionRetention));
    public static string HistorySectionFlyout => Get(nameof(HistorySectionFlyout));

    // ----- Retention -----
    public static string MaxImagesTitle => Get(nameof(MaxImagesTitle));
    public static string MaxImagesCaption => Get(nameof(MaxImagesCaption));
    public static string MaxMediaTitle => Get(nameof(MaxMediaTitle));
    public static string MaxMediaCaption => Get(nameof(MaxMediaCaption));
    public static string MaxMediaUnit => Get(nameof(MaxMediaUnit));

    // ----- Win+V panel -----
    public static string FlyoutOpenAtCaretTitle => Get(nameof(FlyoutOpenAtCaretTitle));
    public static string FlyoutOpenAtCaretCaption => Get(nameof(FlyoutOpenAtCaretCaption));
    public static string FlyoutRememberScrollTitle => Get(nameof(FlyoutRememberScrollTitle));
    public static string FlyoutRememberScrollCaption => Get(nameof(FlyoutRememberScrollCaption));
    public static string FlyoutEnterActionTitle => Get(nameof(FlyoutEnterActionTitle));
    public static string FlyoutEnterActionCaption => Get(nameof(FlyoutEnterActionCaption));
    public static string FlyoutEnterPaste => Get(nameof(FlyoutEnterPaste));
    public static string FlyoutEnterCopy => Get(nameof(FlyoutEnterCopy));
    public static string FlyoutSizeTitle => Get(nameof(FlyoutSizeTitle));
    public static string FlyoutSizeCaption => Get(nameof(FlyoutSizeCaption));
    public static string FlyoutWidthLabel => Get(nameof(FlyoutWidthLabel));
    public static string FlyoutHeightLabel => Get(nameof(FlyoutHeightLabel));
    public static string AlwaysPlainTextTitle => Get(nameof(AlwaysPlainTextTitle));
    public static string AlwaysPlainTextCaption => Get(nameof(AlwaysPlainTextCaption));
    public static string RichPasteAppsTitle => Get(nameof(RichPasteAppsTitle));
    public static string RichPasteAppsCaption => Get(nameof(RichPasteAppsCaption));
    public static string FooterHintsCopy => Get(nameof(FooterHintsCopy));

    private static readonly Dictionary<string, string> PtHistory = new()
    {
        [nameof(HistorySectionRetention)] = "Retenção",
        [nameof(HistorySectionFlyout)] = "Painel Win+V",
        [nameof(MaxImagesTitle)] = "Limite de imagens",
        [nameof(MaxImagesCaption)] = "Imagens ocupam muito mais espaço que texto. As mais antigas saem primeiro. 0 = sem limite.",
        [nameof(MaxMediaTitle)] = "Espaço máximo de mídia",
        [nameof(MaxMediaCaption)] = "Tamanho total das imagens guardadas no histórico. 0 = sem limite.",
        [nameof(MaxMediaUnit)] = "GB",
        [nameof(FlyoutOpenAtCaretTitle)] = "Abrir junto do cursor de texto",
        [nameof(FlyoutOpenAtCaretCaption)] = "O painel aparece perto de onde você está digitando. Sem cursor de texto, abre junto do mouse.",
        [nameof(FlyoutRememberScrollTitle)] = "Lembrar posição da rolagem",
        [nameof(FlyoutRememberScrollCaption)] = "Ao reabrir, volta para onde você parou em vez de mostrar os itens mais recentes.",
        [nameof(FlyoutEnterActionTitle)] = "Ao escolher um item",
        [nameof(FlyoutEnterActionCaption)] = "O que Enter ou um clique fazem no item selecionado.",
        [nameof(FlyoutEnterPaste)] = "Colar",
        [nameof(FlyoutEnterCopy)] = "Só copiar",
        [nameof(FlyoutSizeTitle)] = "Tamanho do painel",
        [nameof(FlyoutSizeCaption)] = "Largura e altura em pixels. Também dá para arrastar a borda do painel.",
        [nameof(FlyoutWidthLabel)] = "Largura",
        [nameof(FlyoutHeightLabel)] = "Altura",
        [nameof(AlwaysPlainTextTitle)] = "Colar sempre como texto puro",
        [nameof(AlwaysPlainTextCaption)] = "Remove fontes, cores e links ao colar. Os apps da lista abaixo continuam recebendo a formatação.",
        [nameof(RichPasteAppsTitle)] = "Apps que mantêm a formatação",
        [nameof(RichPasteAppsCaption)] = "Nome do processo, por exemplo WINWORD.EXE ou OUTLOOK.EXE.",
        [nameof(FooterHintsCopy)] = "Enter copia · Shift+Enter texto puro · Del exclui",
    };

    private static readonly Dictionary<string, string> EnHistory = new()
    {
        [nameof(HistorySectionRetention)] = "Retention",
        [nameof(HistorySectionFlyout)] = "Win+V panel",
        [nameof(MaxImagesTitle)] = "Image limit",
        [nameof(MaxImagesCaption)] = "Images take far more space than text. The oldest go first. 0 = no limit.",
        [nameof(MaxMediaTitle)] = "Maximum media space",
        [nameof(MaxMediaCaption)] = "Total size of the images kept in history. 0 = no limit.",
        [nameof(MaxMediaUnit)] = "GB",
        [nameof(FlyoutOpenAtCaretTitle)] = "Open next to the text cursor",
        [nameof(FlyoutOpenAtCaretCaption)] = "The panel shows up near where you are typing. With no text cursor, it opens next to the mouse.",
        [nameof(FlyoutRememberScrollTitle)] = "Remember scroll position",
        [nameof(FlyoutRememberScrollCaption)] = "When reopened, goes back to where you left off instead of showing the newest items.",
        [nameof(FlyoutEnterActionTitle)] = "When you pick an item",
        [nameof(FlyoutEnterActionCaption)] = "What Enter or a click does with the selected item.",
        [nameof(FlyoutEnterPaste)] = "Paste",
        [nameof(FlyoutEnterCopy)] = "Copy only",
        [nameof(FlyoutSizeTitle)] = "Panel size",
        [nameof(FlyoutSizeCaption)] = "Width and height in pixels. You can also drag the panel border.",
        [nameof(FlyoutWidthLabel)] = "Width",
        [nameof(FlyoutHeightLabel)] = "Height",
        [nameof(AlwaysPlainTextTitle)] = "Always paste as plain text",
        [nameof(AlwaysPlainTextCaption)] = "Drops fonts, colors and links when pasting. The apps listed below still get the formatting.",
        [nameof(RichPasteAppsTitle)] = "Apps that keep formatting",
        [nameof(RichPasteAppsCaption)] = "Process name, for example WINWORD.EXE or OUTLOOK.EXE.",
        [nameof(FooterHintsCopy)] = "Enter copies · Shift+Enter plain text · Del deletes",
    };
}
