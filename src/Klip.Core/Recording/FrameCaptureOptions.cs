namespace Klip.Core.Recording;

/// <summary>Opcoes do motor de captura de frames (spec F2).</summary>
public sealed class FrameCaptureOptions
{
    /// <summary>Incluir o cursor nos frames (RF-F2.06). Efetivo em builds 19041+.</summary>
    public bool CaptureCursor { get; init; } = true;

    /// <summary>
    /// Pedir remocao da borda amarela do sistema (RF-F2.07, Q-F2.1).
    /// Se o consentimento for negado ou a API nao existir, a captura segue com borda.
    /// </summary>
    public bool RequestBorderless { get; init; } = true;

    /// <summary>
    /// null = VFR (timestamps QPC, o conteudo dita a taxa).
    /// Valor = CFR (RF-F2.05): frames reamostrados em 1/FPS, com duplicacao do
    /// ultimo frame em tela parada e readback BGRA para CPU (modo GIF, D-F2.3).
    /// </summary>
    public int? FixedFps { get; init; }
}
