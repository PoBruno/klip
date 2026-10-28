namespace Klip.Core.Settings;

/// <summary>
/// Modificador que, segurado ao soltar a seleção no overlay de captura,
/// abre o resultado direto no editor. Enum próprio do Core
/// para não vazar tipos WPF (ModifierKeys) para o domínio.
/// </summary>
public enum CaptureEditorModifier
{
    None = 0,
    Control = 1,
    Shift = 2,
    Alt = 3,
}
