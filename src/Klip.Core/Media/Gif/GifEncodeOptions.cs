namespace Klip.Core.Media.Gif;

/// <summary>
/// Modo de dithering do encoder (RF-F4.09). Error diffusion e proibido no
/// MVP: espalha erro entre areas estaticas e destroi o delta encoding.
/// </summary>
public enum GifDithering
{
    /// <summary>Sem dithering (default; melhor para screencast de UI).</summary>
    None = 0,

    /// <summary>Dithering ordenado Bayer 8x8 (opcional, para gradientes).</summary>
    Bayer8x8 = 1,
}

/// <summary>Opcoes do encode GIF two-pass.</summary>
public sealed class GifEncodeOptions
{
    /// <summary>Dithering aplicado no mapeamento de cores (RF-F4.09).</summary>
    public GifDithering Dithering { get; init; }

    /// <summary>
    /// Tolerancia de diff entre frames: pixels com |dR|+|dG|+|dB| &lt;= N contam
    /// como iguais (RF-F4.10, ruido de codec no caminho MP4-&gt;GIF). 0 = exato.
    /// </summary>
    public int DiffTolerance { get; init; }

    /// <summary>Emite a extensao NETSCAPE2.0 com loop infinito (RF-F4.08).</summary>
    public bool LoopForever { get; init; } = true;
}
