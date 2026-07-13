namespace Klip.Interop.Recording;

/// <summary>
/// Frame na CPU para o caminho GIF (D-F2.3). Emitido apenas em modo CFR
/// (FixedFps != null, RF-F2.05). O evento e invocado sincronamente e o buffer
/// <see cref="Bgra"/> e REUTILIZADO pelo engine entre frames: copie dentro do
/// handler tudo o que precisar reter.
/// </summary>
public sealed class CpuFrame
{
    internal CpuFrame(byte[] bgra, int width, int height, TimeSpan timestamp)
    {
        Bgra = bgra;
        Width = width;
        Height = height;
        Timestamp = timestamp;
    }

    /// <summary>
    /// Pixels BGRA32 top-down, stride exato = Width * 4 (sem padding).
    /// ATENCAO: o buffer pertence ao engine e e reutilizado no proximo frame -
    /// e valido APENAS durante a invocacao do handler de
    /// <see cref="FrameCaptureEngine.CpuFrameArrived"/>. Consumidores devem
    /// copiar o que precisarem antes de retornar; nao guardar a referencia.
    /// </summary>
    public byte[] Bgra { get; }

    /// <summary>Largura em px fisicos (sempre PAR, RF-F2.03).</summary>
    public int Width { get; }

    /// <summary>Altura em px fisicos (sempre PAR, RF-F2.03).</summary>
    public int Height { get; }

    /// <summary>
    /// Timestamp CFR na grade n * (1/FPS), ancorado no relogio real desde o
    /// primeiro tick da sessao (RF-F2.05). Indices da grade podem ser pulados
    /// sob carga; o timestamp nunca sai da grade.
    /// </summary>
    public TimeSpan Timestamp { get; }
}
