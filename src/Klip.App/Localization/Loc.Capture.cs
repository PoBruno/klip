namespace Klip.App.Localization;

// strings for capture overlay: modes, magnifier, color picker, text, pin, repeat region
public static partial class Loc
{
    public static string RememberCaptureModeTitle => Get(nameof(RememberCaptureModeTitle));
    public static string RememberCaptureModeCaption => Get(nameof(RememberCaptureModeCaption));

    // overlay modes
    public static string ModeText => Get(nameof(ModeText));
    public static string ModeColor => Get(nameof(ModeColor));

    // results
    public static string NotifyTextCopied => Get(nameof(NotifyTextCopied));
    public static string NotifyNoText => Get(nameof(NotifyNoText));
    public static string NotifyColorCopied => Get(nameof(NotifyColorCopied));

    // post-capture toast
    public static string ToastPin => Get(nameof(ToastPin));
    public static string ToastDismiss => Get(nameof(ToastDismiss));

    // pinned image
    public static string PinCopy => Get(nameof(PinCopy));
    public static string PinSave => Get(nameof(PinSave));
    public static string PinClickThrough => Get(nameof(PinClickThrough));
    public static string PinClose => Get(nameof(PinClose));
    public static string PinnedImageTooltip => Get(nameof(PinnedImageTooltip));

    // tray
    public static string TrayRepeatRegion => Get(nameof(TrayRepeatRegion));
    public static string TrayPinnedImages => Get(nameof(TrayPinnedImages));
    public static string TrayPinnedCloseAll => Get(nameof(TrayPinnedCloseAll));
    public static string TrayPinnedStopClickThrough => Get(nameof(TrayPinnedStopClickThrough));

    // hotkeys page
    public static string HotkeyCaptureTextTitle => Get(nameof(HotkeyCaptureTextTitle));
    public static string HotkeyRepeatRegionTitle => Get(nameof(HotkeyRepeatRegionTitle));
    public static string HotkeyRecordVideoTitle => Get(nameof(HotkeyRecordVideoTitle));
    public static string HotkeyOptionalHint => Get(nameof(HotkeyOptionalHint));
    public static string HotkeyNotSet => Get(nameof(HotkeyNotSet));
    public static string HotkeyClear => Get(nameof(HotkeyClear));
    public static string HotkeyCleared => Get(nameof(HotkeyCleared));

    // capture page
    public static string MagnifierTitle => Get(nameof(MagnifierTitle));
    public static string MagnifierCaption => Get(nameof(MagnifierCaption));
    public static string ColorFormatTitle => Get(nameof(ColorFormatTitle));
    public static string ColorFormatCaption => Get(nameof(ColorFormatCaption));

    // editor
    public static string ToolPinToScreen => Get(nameof(ToolPinToScreen));

    private static readonly Dictionary<string, string> PtCapture = new()
    {
        [nameof(RememberCaptureModeTitle)] = "Lembrar o último modo de captura",
        [nameof(RememberCaptureModeCaption)] = "A captura abre no último modo usado (janela, tela cheia, GIF, MP4...). Desligado, sempre começa no retângulo.",
        [nameof(ModeText)] = "Texto: selecione uma área para copiar o texto",
        [nameof(ModeColor)] = "Cor: clique para copiar a cor do pixel",
        [nameof(NotifyTextCopied)] = "Texto copiado ({0} caracteres)",
        [nameof(NotifyNoText)] = "Nenhum texto encontrado",
        [nameof(NotifyColorCopied)] = "Cor copiada: {0}",
        [nameof(ToastPin)] = "Fixar na tela",
        [nameof(ToastDismiss)] = "Fechar",
        [nameof(PinCopy)] = "Copiar",
        [nameof(PinSave)] = "Salvar como...",
        [nameof(PinClickThrough)] = "Atravessar cliques",
        [nameof(PinClose)] = "Fechar",
        [nameof(PinnedImageTooltip)] = "Arraste para mover. Roda: zoom. Ctrl+roda: opacidade. Duplo clique ou Esc: fechar.",
        [nameof(TrayRepeatRegion)] = "Capturar a última região",
        [nameof(TrayPinnedImages)] = "Imagens fixadas",
        [nameof(TrayPinnedCloseAll)] = "Fechar todas",
        [nameof(TrayPinnedStopClickThrough)] = "Parar de atravessar cliques",
        [nameof(HotkeyCaptureTextTitle)] = "Capturar texto",
        [nameof(HotkeyRepeatRegionTitle)] = "Repetir a última região",
        [nameof(HotkeyRecordVideoTitle)] = "Gravar vídeo",
        [nameof(HotkeyOptionalHint)] = "Opcional. Clique nas teclas e pressione a combinação",
        [nameof(HotkeyNotSet)] = "Nenhum",
        [nameof(HotkeyClear)] = "Remover atalho",
        [nameof(HotkeyCleared)] = "Atalho removido",
        [nameof(MagnifierTitle)] = "Lupa ao selecionar",
        [nameof(MagnifierCaption)] = "Mostra uma lupa de 8x junto ao cursor, com as coordenadas e a cor do pixel. As setas movem o cursor 1 px (Shift: 10 px).",
        [nameof(ColorFormatTitle)] = "Formato da cor",
        [nameof(ColorFormatCaption)] = "Como o modo Cor copia a cor escolhida.",
        [nameof(ToolPinToScreen)] = "Fixar na tela",
    };

    private static readonly Dictionary<string, string> EnCapture = new()
    {
        [nameof(RememberCaptureModeTitle)] = "Remember the last capture mode",
        [nameof(RememberCaptureModeCaption)] = "Capture opens in the mode you used last (window, full screen, GIF, MP4...). When off, it always starts with rectangle.",
        [nameof(ModeText)] = "Text: select an area to copy its text",
        [nameof(ModeColor)] = "Color: click to copy the pixel color",
        [nameof(NotifyTextCopied)] = "Text copied ({0} characters)",
        [nameof(NotifyNoText)] = "No text found",
        [nameof(NotifyColorCopied)] = "Color copied: {0}",
        [nameof(ToastPin)] = "Pin to screen",
        [nameof(ToastDismiss)] = "Dismiss",
        [nameof(PinCopy)] = "Copy",
        [nameof(PinSave)] = "Save as...",
        [nameof(PinClickThrough)] = "Click through",
        [nameof(PinClose)] = "Close",
        [nameof(PinnedImageTooltip)] = "Drag to move. Wheel: zoom. Ctrl+wheel: opacity. Double-click or Esc: close.",
        [nameof(TrayRepeatRegion)] = "Capture the last region",
        [nameof(TrayPinnedImages)] = "Pinned images",
        [nameof(TrayPinnedCloseAll)] = "Close all",
        [nameof(TrayPinnedStopClickThrough)] = "Stop clicking through",
        [nameof(HotkeyCaptureTextTitle)] = "Capture text",
        [nameof(HotkeyRepeatRegionTitle)] = "Repeat the last region",
        [nameof(HotkeyRecordVideoTitle)] = "Record video",
        [nameof(HotkeyOptionalHint)] = "Optional. Click the keys and press the combination",
        [nameof(HotkeyNotSet)] = "None",
        [nameof(HotkeyClear)] = "Remove shortcut",
        [nameof(HotkeyCleared)] = "Shortcut removed",
        [nameof(MagnifierTitle)] = "Magnifier while selecting",
        [nameof(MagnifierCaption)] = "Shows an 8x magnifier next to the cursor with the coordinates and the pixel color. Arrow keys move the cursor 1 px (Shift: 10 px).",
        [nameof(ColorFormatTitle)] = "Color format",
        [nameof(ColorFormatCaption)] = "How the Color mode copies the picked color.",
        [nameof(ToolPinToScreen)] = "Pin to screen",
    };
}
