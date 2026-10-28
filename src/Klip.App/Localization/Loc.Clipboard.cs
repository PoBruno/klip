namespace Klip.App.Localization;

// strings for paste image as file and privacy
public static partial class Loc
{
    public static string PasteAsFileSection => Get(nameof(PasteAsFileSection));
    public static string PasteAsFileAttachTitle => Get(nameof(PasteAsFileAttachTitle));
    public static string PasteAsFileAttachCaption => Get(nameof(PasteAsFileAttachCaption));
    public static string PasteAsFileExplorerTitle => Get(nameof(PasteAsFileExplorerTitle));
    public static string PasteAsFileExplorerCaption => Get(nameof(PasteAsFileExplorerCaption));
    public static string PasteAsFileNameTitle => Get(nameof(PasteAsFileNameTitle));
    public static string PasteAsFileNameCaption => Get(nameof(PasteAsFileNameCaption));
    public static string PasteAsFileNameTokens => Get(nameof(PasteAsFileNameTokens));
    public static string PasteAsFileNamePreview => Get(nameof(PasteAsFileNamePreview));

    private static readonly Dictionary<string, string> PtClipboard = new()
    {
        [nameof(PasteAsFileSection)] = "Colar imagem como arquivo",
        [nameof(PasteAsFileAttachTitle)] = "Anexar arquivo às imagens copiadas pelo Klip",
        [nameof(PasteAsFileAttachCaption)] = "Capturas e imagens do histórico podem ser coladas numa pasta do Explorer como arquivo PNG. Word, Paint e chats continuam recebendo a imagem.",
        [nameof(PasteAsFileExplorerTitle)] = "Ctrl+V no Explorer cria o arquivo PNG",
        [nameof(PasteAsFileExplorerCaption)] = "Com uma imagem de qualquer app na área de transferência, Ctrl+V numa pasta ou na área de trabalho salva o PNG ali. Na barra de endereço, na busca ou ao renomear, o Ctrl+V continua normal.",
        [nameof(PasteAsFileNameTitle)] = "Nome do arquivo",
        [nameof(PasteAsFileNameCaption)] = "Usado quando uma imagem vira arquivo. Se o nome já existir na pasta, entra \" (2)\" no final.",
        [nameof(PasteAsFileNameTokens)] = "{date} = data (2026-10-27)   {time} = hora (14.03.12)   {app} = app de origem",
        [nameof(PasteAsFileNamePreview)] = "Exemplo: {0}",
    };

    private static readonly Dictionary<string, string> EnClipboard = new()
    {
        [nameof(PasteAsFileSection)] = "Paste image as file",
        [nameof(PasteAsFileAttachTitle)] = "Attach a file to images copied by Klip",
        [nameof(PasteAsFileAttachCaption)] = "Captures and history images can be pasted into a File Explorer folder as a PNG file. Word, Paint and chat apps still get the image.",
        [nameof(PasteAsFileExplorerTitle)] = "Ctrl+V in File Explorer creates the PNG file",
        [nameof(PasteAsFileExplorerCaption)] = "With an image from any app on the clipboard, Ctrl+V on a folder or the desktop saves the PNG there. In the address bar, the search box or while renaming, Ctrl+V works as usual.",
        [nameof(PasteAsFileNameTitle)] = "File name",
        [nameof(PasteAsFileNameCaption)] = "Used when an image becomes a file. If the name is already taken in the folder, \" (2)\" is added at the end.",
        [nameof(PasteAsFileNameTokens)] = "{date} = date (2026-10-27)   {time} = time (14.03.12)   {app} = source app",
        [nameof(PasteAsFileNamePreview)] = "Example: {0}",
    };
}
