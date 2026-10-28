namespace Klip.Core.Settings;

/// <summary>Everything that gets saved into settings.json.</summary>
public sealed class AppSettings
{
    // General
    public bool StartWithWindows { get; set; } = true;
    public bool StartMinimized { get; set; } = true;
    public string Theme { get; set; } = "system"; // system | light | dark
    public string Language { get; set; } = "system";

    // default hotkeys; changed in settings
    public string HotkeyHistory { get; set; } = "Ctrl+Shift+V";
    public string HotkeyCapture { get; set; } = "Ctrl+Shift+S";

    // retention: pinned and favorites never get evicted
    public int RetentionMaxItems { get; set; } = 1_000;
    public int RetentionMaxAgeDays { get; set; } = 0;          // 0 = no limit
    public long RetentionMaxItemBytes { get; set; } = 50 * 1024 * 1024;
    public long RetentionMaxTotalBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    // Clipboard
    public bool CaptureText { get; set; } = true;
    public bool CaptureImages { get; set; } = true;
    public bool CaptureFiles { get; set; } = true;
    public bool CaptureHtml { get; set; } = true;
    public List<string> ExcludedApps { get; set; } = [];
    public bool RestoreClipboardAfterPaste { get; set; }
    public bool SkipSecrets { get; set; } = true;          // skips tokens, passwords and the like
    public bool ClearHistoryOnExit { get; set; }

    // Screen capture
    public bool AutoSaveScreenshots { get; set; }
    public string? ScreenshotsFolder { get; set; }

    // modificador segurado no MouseUp da seleção abre direto no editor
    public CaptureEditorModifier EditorModifierKey { get; set; } = CaptureEditorModifier.Control;

    // toda captura estática abre no editor (modificador vira irrelevante)
    public bool AlwaysOpenEditorAfterCapture { get; set; }

    // Editor
    public bool EditorAutoCopy { get; set; } = true;

    // Flyout size (the user can resize the Win+V window; it sticks)
    public double FlyoutWidth { get; set; } = 360;
    public double FlyoutHeight { get; set; } = 460;

    // show the emoji tab in the flyout; off hides it entirely (no emoji cost)
    public bool ShowEmojiTab { get; set; } = true;

    // backup do registro, escrito uma vez so antes de mexer nas chaves
    public string? RegistryBackupDisabledHotkeys { get; set; }
    public bool RegistryBackupTaken { get; set; }
    public int? RegistryBackupPrintScreen { get; set; }
    public bool RegistryBackupPrintScreenTaken { get; set; }
    public bool RegistryHklmClipboardOffApplied { get; set; }
    public bool OnboardingCompleted { get; set; }

    // delay between frames on scrolling capture
    public int ScrollCaptureDelayMs { get; set; } = 150;

    // ----- Gravacao de tela (specs F3/F4) -----

    // null = Videos\Gravacoes de Tela (criada sob demanda)
    public string? RecordingsFolder { get; set; }

    // FPS da gravacao GIF (10/15/20; a UI mostra o efetivo)
    public int GifFps { get; set; } = 15;

    // escala da gravacao GIF (100/75/50)
    public int GifScalePercent { get; set; } = 100;

    // modo reuniao - some borda/toolbar; tray indica e para
    public bool HideRecordingBorder { get; set; }

    //  (resolvida como presets): 0 = automatico pela resolucao
    public int Mp4BitrateKbps { get; set; }

    // UX submenu de gravacao: som do sistema na gravacao MP4 (configurado no
    // painel inline do overlay; substitui o painel pre-gravacao)
    public bool Mp4CaptureSystemAudio { get; set; } = true;

    // UX submenu de gravacao: ids WASAPI dos microfones marcados; ids que nao
    // existirem mais entre os dispositivos ativos sao ignorados ao gravar
    public List<string> Mp4MicrophoneIds { get; set; } = [];

    // atalho global de parar, registrado SO durante a gravacao
    public string StopRecordingHotkey { get; set; } = "Ctrl+Shift+X";

    // ultima posicao da toolbar de gravacao em PIXELS FISICOS, no
    // formato invariante "x,y,w,h" (w/h so para validar a intersecao com o
    // monitor na restauracao - a janela dimensiona pelo conteudo). null/vazio
    // ou monitor removido -> posicao default fora da regiao.
    public string? RecordingToolbarPosition { get; set; }

    // caminho do ffmpeg.exe para o editor de midia; vazio = deteccao
    // automatica (pasta de dados do app ou PATH)
    public string FfmpegPath { get; set; } = "";

    // bumped when a default changes and old saved values need a one-time fix
    public int SettingsVersion { get; set; }

    // ----- History -----

    // images eat the disk way faster than text, so they get their own caps
    public int RetentionMaxImages { get; set; } = 200;
    public long RetentionMaxMediaBytes { get; set; } = 1L * 1024 * 1024 * 1024;

    // off = Win+V always opens at the top with the newest item selected
    public bool FlyoutRememberScroll { get; set; }

    // open next to the text caret like the native Win+V; falls back to the mouse
    public bool FlyoutOpenAtCaret { get; set; } = true;

    // what Enter does on a flyout item: "paste" or "copy"
    public string FlyoutEnterAction { get; set; } = "paste";

    // paste everything as plain text, except in the apps listed below
    public bool AlwaysPastePlainText { get; set; }
    public List<string> RichPasteApps { get; set; } = [];

    public bool ShowSnippetsTab { get; set; } = true;

    // ----- Privacy -----

    // 0 = secrets are skipped like before; > 0 = kept that many seconds, then wiped
    public int SecretKeepSeconds { get; set; }

    // ----- Paste image as file -----

    // our own images go to the clipboard with a virtual png file attached
    public bool AttachFileToCopiedImages { get; set; } = true;

    // Ctrl+V on an Explorer folder or the desktop saves the clipboard image as a file
    public bool PasteImageAsFileInExplorer { get; set; } = true;

    // file name for pasted/saved images; tokens: {date} {time} {app}
    public string ImageFileNameTemplate { get; set; } = "Klip {date} {time}";

    // ----- Capture -----

    // off = the overlay always starts on rectangle with no delay
    public bool RememberLastCaptureMode { get; set; }
    public CaptureMode LastCaptureMode { get; set; } = CaptureMode.Rectangle;

    // last selection for "repeat last region", physical px (see SavedCaptureRegion)
    public string? LastCaptureRegion { get; set; }

    // magnifier next to the cursor while selecting
    public bool CaptureMagnifier { get; set; } = true;

    // color picker output: "hex", "rgb" or "hsl"
    public string ColorPickerFormat { get; set; } = "hex";

    // optional direct hotkeys; empty = not registered
    public string HotkeyCaptureText { get; set; } = "";
    public string HotkeyRepeatRegion { get; set; } = "";
    public string HotkeyRecordVideo { get; set; } = "";

    // ----- Video recording -----

    public int Mp4Fps { get; set; } = 30;

    // "small", "balanced" or "high"
    public string Mp4Quality { get; set; } = "balanced";

    // "native", "1440", "1080" or "720" (only scales down, never up)
    public string Mp4OutputResolution { get; set; } = "native";

    // "auto", "h264", "hevc" or "av1" (auto = h264, hevc when wider than 4096)
    public string Mp4Codec { get; set; } = "auto";

    public bool Mp4ShowCursor { get; set; } = true;

    // with a window target, record only that app's sound
    public bool Mp4AppAudioOnly { get; set; } = true;
}
