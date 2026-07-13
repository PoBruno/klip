namespace Klip.Core.Recording;

/// <summary>
/// Matematica de cadencia da gravacao GIF (RF-F4.02, Q-F4.2 resolvida:
/// aceitar o FPS efetivo do formato e mostrar na UI). O GIF armazena delay em
/// centesimos de segundo, entao os FPS reais possiveis sao 100/round(100/fps):
/// 10 fps exato (10 cs), 15 vira 14,3 (7 cs), 20 exato (5 cs).
/// </summary>
public static class GifRecordingMath
{
    /// <summary>Delay por frame em centesimos, clampado a 2 cs (players clampam abaixo).</summary>
    public static int DelayCentiseconds(int fps)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fps);
        return Math.Max(2, (int)Math.Round(100.0 / fps));
    }

    /// <summary>FPS efetivo de reproducao dado o delay em centesimos.</summary>
    public static double EffectiveFps(int fps) => 100.0 / DelayCentiseconds(fps);

    /// <summary>
    /// Delay por frame em ms, multiplo exato de 10 para que o dedupe do
    /// <see cref="Media.Gif.GifFrameBuffer"/> acumule centesimos inteiros.
    /// </summary>
    public static int FrameDelayMs(int fps) => DelayCentiseconds(fps) * 10;
}
