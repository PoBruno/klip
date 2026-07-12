namespace Klip.Core.Media.Gif;

/// <summary>
/// Encoder GIF89a two-pass (spec F4). Pass 1 constroi a paleta global vendo
/// todos os frames (RF-F4.07); pass 2 emite delta frames (RF-F4.08).
/// </summary>
public interface IGifEncoder
{
    /// <summary>
    /// Escreve um GIF89a completo no stream. A sequencia pode ser iterada
    /// duas vezes (two-pass), por isso o parametro e <see cref="IReadOnlyList{T}"/>.
    /// Todos os frames devem ter as mesmas dimensoes.
    /// </summary>
    void Encode(Stream output, IReadOnlyList<GifFrameSource> frames, GifEncodeOptions options);
}
