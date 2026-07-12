namespace Klip.Core.Media.Gif;

/// <summary>
/// Um frame de entrada para o encoder GIF: buffer BGRA32 top-down
/// (stride = width * 4) com o delay desejado em milissegundos.
/// A materializacao do buffer pode ser lazy (frames em spill de disco,
/// RF-F4.06): cada leitura de <see cref="Bgra"/> pode reler o arquivo,
/// entao consumidores devem capturar o array uma vez por passada.
/// </summary>
public sealed class GifFrameSource
{
    private readonly byte[]? _bgra;
    private readonly Func<byte[]>? _loader;

    public int Width { get; }

    public int Height { get; }

    /// <summary>Delay desejado em milissegundos (convertido para centesimos na escrita).</summary>
    public int DelayMs { get; }

    /// <summary>
    /// Pixels BGRA32 top-down. Para frames em spill, cada get pode reler o
    /// disco (por design: evita materializar a gravacao inteira em RAM).
    /// </summary>
    public byte[] Bgra => _bgra ?? LoadFromLoader();

    public GifFrameSource(byte[] bgra, int width, int height, int delayMs)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        ValidateDimensions(width, height, delayMs);
        if (bgra.Length != width * height * 4)
            throw new ArgumentException("Buffer BGRA deve ter exatamente width*height*4 bytes.", nameof(bgra));

        _bgra = bgra;
        Width = width;
        Height = height;
        DelayMs = delayMs;
    }

    private GifFrameSource(Func<byte[]> loader, int width, int height, int delayMs)
    {
        _loader = loader;
        Width = width;
        Height = height;
        DelayMs = delayMs;
    }

    /// <summary>
    /// Cria um frame cujo buffer e carregado sob demanda (spill em disco).
    /// O loader deve devolver exatamente width*height*4 bytes.
    /// </summary>
    public static GifFrameSource FromLoader(Func<byte[]> loader, int width, int height, int delayMs)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ValidateDimensions(width, height, delayMs);
        return new GifFrameSource(loader, width, height, delayMs);
    }

    private byte[] LoadFromLoader()
    {
        var data = _loader!();
        if (data.Length != Width * Height * 4)
            throw new InvalidOperationException("Loader devolveu buffer com tamanho diferente de width*height*4.");
        return data;
    }

    private static void ValidateDimensions(int width, int height, int delayMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegative(delayMs);
    }
}
