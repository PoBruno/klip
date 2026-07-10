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
}
