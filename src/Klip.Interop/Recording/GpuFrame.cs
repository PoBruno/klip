using Vortice.Direct3D11;

namespace Klip.Interop.Recording;

/// <summary>
/// Frame na GPU para o caminho MP4 (D-F2.3): textura BGRA propria do engine,
/// ja recortada para a regiao (dimensoes PARES, RF-F2.03), criada no
/// <see cref="FrameCaptureEngine.Device"/> compartilhado com o encoder.
/// A POSSE e do consumidor: chamar <see cref="Dispose"/> devolve a textura ao
/// pool do engine (RF-F2.04). Nao chamar Dispose = novos frames sao descartados.
/// NAO descartar a textura diretamente.
/// </summary>
public sealed class GpuFrame : IDisposable
{
    private Action<ID3D11Texture2D>? _release;

    internal GpuFrame(ID3D11Texture2D texture, TimeSpan timestamp, Action<ID3D11Texture2D> release)
    {
        Texture = texture;
        Timestamp = timestamp;
        _release = release;
    }

    /// <summary>Textura BGRA (B8G8R8A8_UNorm) do frame recortado.</summary>
    public ID3D11Texture2D Texture { get; }

    /// <summary>
    /// VFR: SystemRelativeTime do frame (QPC, RF-F2.01).
    /// CFR: timestamp na grade n * (1/FPS), ancorado no relogio real desde o
    /// primeiro tick da sessao (RF-F2.05). Indices atrasados sao preenchidos
    /// com frames duplicados (catch-up, com teto); sob atraso extremo slots
    /// podem ser perdidos. O timestamp nunca sai da grade.
    /// </summary>
    public TimeSpan Timestamp { get; }

    /// <summary>Devolve a textura ao pool do engine. Idempotente.</summary>
    public void Dispose()
    {
        var release = Interlocked.Exchange(ref _release, null);
        release?.Invoke(Texture);
    }
}
