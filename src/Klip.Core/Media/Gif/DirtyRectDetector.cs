namespace Klip.Core.Media.Gif;

/// <summary>
/// Retangulo de pixels alterados entre dois frames (bounding box).
/// Coordenadas em pixels, origem no canto superior esquerdo.
/// </summary>
public readonly record struct DirtyRect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static DirtyRect Empty => new(0, 0, 0, 0);

    public static DirtyRect Full(int width, int height) => new(0, 0, width, height);
}

/// <summary>
/// Deteccao de dirty-rect entre frames BGRA32 (RF-F4.06/RF-F4.08):
/// bounding box das linhas/colunas com pixels diferentes, com tolerancia
/// opcional |dR|+|dG|+|dB| &lt;= N (RF-F4.10). Alpha e ignorado.
/// </summary>
public static class DirtyRectDetector
{
    /// <summary>
    /// Computa o bounding box dos pixels que diferem entre os dois buffers.
    /// Retorna <see cref="DirtyRect.Empty"/> quando os frames sao iguais
    /// dentro da tolerancia.
    /// </summary>
    public static DirtyRect Compute(
        ReadOnlySpan<byte> current,
        ReadOnlySpan<byte> previous,
        int width,
        int height,
        int tolerance)
    {
        var expected = width * height * 4;
        if (current.Length != expected || previous.Length != expected)
            throw new ArgumentException("Buffers devem ter width*height*4 bytes.");
        ArgumentOutOfRangeException.ThrowIfNegative(tolerance);

        var stride = width * 4;
        var minY = -1;
        var maxY = -1;
        var minX = width;
        var maxX = -1;

        for (var y = 0; y < height; y++)
        {
            var rowCur = current.Slice(y * stride, stride);
            var rowPrev = previous.Slice(y * stride, stride);

            // Fast path vetorizado: linha byte a byte identica (inclui alpha,
            // mas se bater esta limpa de qualquer forma).
            if (tolerance == 0 && rowCur.SequenceEqual(rowPrev))
                continue;

            var first = FirstDifference(rowCur, rowPrev, width, tolerance);
            if (first < 0)
                continue; // so alpha diferia, ou diferenca dentro da tolerancia

            var last = LastDifference(rowCur, rowPrev, width, tolerance);

            if (minY < 0)
                minY = y;
            maxY = y;
            if (first < minX)
                minX = first;
            if (last > maxX)
                maxX = last;
        }

        return minY < 0
            ? DirtyRect.Empty
            : new DirtyRect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>Compara um pixel BGRA (offset em bytes) com tolerancia sobre BGR.</summary>
    public static bool PixelsEqual(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int offset, int tolerance)
    {
        var diff = Math.Abs(a[offset] - b[offset])
                 + Math.Abs(a[offset + 1] - b[offset + 1])
                 + Math.Abs(a[offset + 2] - b[offset + 2]);
        return diff <= tolerance;
    }

    private static int FirstDifference(ReadOnlySpan<byte> rowCur, ReadOnlySpan<byte> rowPrev, int width, int tolerance)
    {
        for (var x = 0; x < width; x++)
        {
            if (!PixelsEqual(rowCur, rowPrev, x * 4, tolerance))
                return x;
        }
        return -1;
    }

    private static int LastDifference(ReadOnlySpan<byte> rowCur, ReadOnlySpan<byte> rowPrev, int width, int tolerance)
    {
        for (var x = width - 1; x >= 0; x--)
        {
            if (!PixelsEqual(rowCur, rowPrev, x * 4, tolerance))
                return x;
        }
        return -1;
    }
}
