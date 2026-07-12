using Klip.Core.Settings;

namespace Klip.Core.Capture;

/// <summary>
/// Política pura da spec F1 (fluxo rápido de captura): decide quando uma
/// captura estática abre direto no editor. Fica no Core para ser testável
/// sem UI (D-F1.2: o overlay só reporta o estado físico da tecla).
/// </summary>
public static class EditorOpenDecision
{
    /// <summary>
    /// Decisão final do controller: pedido explícito do modificador (RF-F1.01)
    /// OU o toggle "sempre abrir no editor" ligado (RF-F1.05).
    /// </summary>
    public static bool ShouldOpenEditor(bool openEditorRequested, bool alwaysOpenEditorAfterCapture) =>
        openEditorRequested || alwaysOpenEditorAfterCapture;

    /// <summary>
    /// Pedido do modificador é válido no MouseUp (RF-F1.03) somente se:
    /// há modificador configurado, ele está fisicamente pressionado e não é
    /// resquício do hotkey de captura (RF-F1.07: precisa soltar e pressionar
    /// de novo depois que o overlay abriu).
    /// </summary>
    public static bool IsModifierRequestValid(
        CaptureEditorModifier configured, bool modifierDown, bool heldSinceOpen) =>
        configured != CaptureEditorModifier.None && modifierDown && !heldSinceOpen;
}
