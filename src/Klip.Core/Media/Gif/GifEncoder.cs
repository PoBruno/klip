namespace Klip.Core.Media.Gif;

/// <summary>
/// Encoder GIF89a two-pass proprio (D-F4.1, sem dependencias externas).
/// Pass 1 (RF-F4.07): histograma global exato; paleta lossless quando o
/// conteudo cabe em 256 cores, senao median cut variante palettegen.
/// Pass 2 (RF-F4.08): delta frames com disposal 1, sub-retangulo dirty e
/// indice transparente para pixels inalterados; LZW proprio; loop via
/// NETSCAPE2.0. Tolerancia de diff opcional (RF-F4.10) e Bayer 8x8
/// opcional (RF-F4.09).
/// </summary>
public sealed class GifEncoder : IGifEncoder
{
    /// <summary>Delay maximo acumulavel por frame: 65535 cs = 655350 ms.</summary>
    private const long MaxDelayMs = 655_350;

    public void Encode(Stream output, IReadOnlyList<GifFrameSource> frames, GifEncodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(options.DiffTolerance);
        if (frames.Count == 0)
            throw new ArgumentException("Sequencia de frames vazia.", nameof(frames));

        var width = frames[0].Width;
        var height = frames[0].Height;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, ushort.MaxValue);
        foreach (var frame in frames)
        {
            if (frame.Width != width || frame.Height != height)
                throw new ArgumentException("Todos os frames devem ter as mesmas dimensoes.", nameof(frames));
        }

        // ---- Pass 1 (RF-F4.07): histograma global exato sobre RGB888 ----
        var histogram = BuildHistogram(frames);

        // Multi-frame precisa reservar 1 indice transparente para o delta.
        var needsTransparency = frames.Count > 1;
        var maxColors = needsTransparency ? 255 : 256;
        var lossless = histogram.Count <= maxColors;
        var palette = MedianCutQuantizer.BuildPalette(histogram, maxColors);

        var transparentIndex = needsTransparency ? palette.Length : -1;
        var usedSlots = palette.Length + (needsTransparency ? 1 : 0);
        var tableSize = NextPowerOfTwo(usedSlots);
        var bitsPerPixel = int.Log2(tableSize);
        var minCodeSize = Math.Max(2, bitsPerPixel);

        // Dithering so faz sentido com quantizacao: no caminho lossless toda
        // cor existe exata na paleta (CA-F4.3), offset so degradaria.
        var dithering = lossless ? GifDithering.None : options.Dithering;
        var mapper = new GifColorMapper(palette, dithering);

        GifWriter.WriteHeader(output);
        GifWriter.WriteLogicalScreenDescriptor(output, width, height, bitsPerPixel - 1);
        GifWriter.WriteGlobalColorTable(output, palette, tableSize);
        if (options.LoopForever)
            GifWriter.WriteNetscapeLoop(output);

        // ---- Pass 2 (RF-F4.08): delta frames com emissao adiada ----
        // "pending" e o proximo frame a emitir; frames seguintes iguais dentro
        // da tolerancia acumulam delay nele (CA-F4.2: duracao preservada).
        byte[]? previousEmitted = null;
        var pending = frames[0].Bgra;
        long pendingDelay = frames[0].DelayMs;

        for (var i = 1; i < frames.Count; i++)
        {
            var frame = frames[i];
            var bgra = frame.Bgra;
            var diff = DirtyRectDetector.Compute(bgra, pending, width, height, options.DiffTolerance);
            if (diff.IsEmpty)
            {
                pendingDelay = Math.Min(pendingDelay + frame.DelayMs, MaxDelayMs);
                continue;
            }

            EmitFrame(output, pending, previousEmitted, pendingDelay, width, height, options.DiffTolerance,
                mapper, transparentIndex, minCodeSize);
            previousEmitted = pending;
            pending = bgra;
            pendingDelay = frame.DelayMs;
        }

        EmitFrame(output, pending, previousEmitted, pendingDelay, width, height, options.DiffTolerance,
            mapper, transparentIndex, minCodeSize);

        GifWriter.WriteTrailer(output);
    }

    private static Dictionary<uint, long> BuildHistogram(IReadOnlyList<GifFrameSource> frames)
    {
        var histogram = new Dictionary<uint, long>(4096);
        foreach (var frame in frames)
        {
            var bgra = frame.Bgra;
            for (var i = 0; i < bgra.Length; i += 4)
            {
                // BGRA -> chave RGB888 (alpha ignorado).
                var rgb = ((uint)bgra[i + 2] << 16) | ((uint)bgra[i + 1] << 8) | bgra[i];
                System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(histogram, rgb, out _)++;
            }
        }
        return histogram;
    }

    private static void EmitFrame(
        Stream output,
        byte[] source,
        byte[]? previous,
        long delayMs,
        int width,
        int height,
        int tolerance,
        GifColorMapper mapper,
        int transparentIndex,
        int minCodeSize)
    {
        // Primeiro frame: sempre full e sem transparencia (nao ha "anterior"
        // para o pixel transparente mostrar).
        var rect = previous is null
            ? DirtyRect.Full(width, height)
            : DirtyRectDetector.Compute(source, previous, width, height, tolerance);
        if (rect.IsEmpty)
            rect = new DirtyRect(0, 0, 1, 1); // defensivo; chamador garante diff nao vazio

        var useTransparency = previous is not null && transparentIndex >= 0;
        var indices = new byte[rect.Width * rect.Height];
        var stride = width * 4;
        var n = 0;

        for (var y = rect.Y; y < rect.Y + rect.Height; y++)
        {
            var row = y * stride;
            for (var x = rect.X; x < rect.X + rect.Width; x++)
            {
                var offset = row + x * 4;
                if (useTransparency && DirtyRectDetector.PixelsEqual(source, previous!, offset, tolerance))
                {
                    indices[n++] = (byte)transparentIndex;
                }
                else
                {
                    indices[n++] = mapper.Map(source[offset + 2], source[offset + 1], source[offset], x, y);
                }
            }
        }

        // Delay em centesimos: nunca < 2 cs (players clampam abaixo disso).
        var delayCs = Math.Clamp((int)Math.Round(delayMs / 10.0), 2, ushort.MaxValue);

        GifWriter.WriteGraphicControl(output, delayCs, useTransparency, (byte)Math.Max(0, transparentIndex));
        GifWriter.WriteImageDescriptor(output, rect);
        GifLzwEncoder.Encode(output, indices, minCodeSize);
    }

    private static int NextPowerOfTwo(int value)
    {
        var result = 2; // GCT minima de 2 entradas
        while (result < value)
            result <<= 1;
        return result;
    }
}
