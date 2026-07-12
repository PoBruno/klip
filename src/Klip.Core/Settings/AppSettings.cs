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
    public int RetentionMaxItems { get; set; } = 10_000;
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

    // RF-F1.02: modificador segurado no MouseUp da seleção abre direto no editor
    public CaptureEditorModifier EditorModifierKey { get; set; } = CaptureEditorModifier.Control;

    // RF-F1.05: toda captura estática abre no editor (modificador vira irrelevante)
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

    // RF-F3.06: null = Videos\Gravacoes de Tela (criada sob demanda)
    public string? RecordingsFolder { get; set; }

    // RF-F4.02: FPS da gravacao GIF (10/15/20; a UI mostra o efetivo)
    public int GifFps { get; set; } = 15;

    // RF-F4.03: escala da gravacao GIF (100/75/50)
    public int GifScalePercent { get; set; } = 100;

    // RF-F3.04: modo reuniao - some borda/toolbar; tray indica e para
    public bool HideRecordingBorder { get; set; }

    // Q-F3.1 (resolvida como presets): 0 = automatico pela resolucao
    public int Mp4BitrateKbps { get; set; }

    // UX submenu de gravacao: som do sistema na gravacao MP4 (configurado no
    // painel inline do overlay; substitui o painel pre-gravacao)
    public bool Mp4CaptureSystemAudio { get; set; } = true;

    // UX submenu de gravacao: ids WASAPI dos microfones marcados; ids que nao
    // existirem mais entre os dispositivos ativos sao ignorados ao gravar
    public List<string> Mp4MicrophoneIds { get; set; } = [];

    // RF-F3.05: atalho global de parar, registrado SO durante a gravacao
    public string StopRecordingHotkey { get; set; } = "Ctrl+Shift+X";

    // RF-T2.02: ultima posicao da toolbar de gravacao em PIXELS FISICOS, no
    // formato invariante "x,y,w,h" (w/h so para validar a intersecao com o
    // monitor na restauracao - a janela dimensiona pelo conteudo). null/vazio
    // ou monitor removido -> posicao default fora da regiao (RF-T2.09).
    public string? RecordingToolbarPosition { get; set; }

    // RF-F5.14: caminho do ffmpeg.exe para o editor de midia; vazio = deteccao
    // automatica (pasta de dados do app ou PATH)
    public string FfmpegPath { get; set; } = "";
}
