namespace Klip.Core.Imaging;

/// <summary>
/// Mosaic pixelation on a BGRA32 buffer. Used by the editor blur/redact tool.
/// Mosaic (not gaussian) on purpose: a redaction must not be reversible.
/// Pure and testable without UI.
/// </summary>
public static class Pixelator
{
    /// <summary>
    /// Averages each block into one flat color, in-place on the BGRA buffer
    /// (4 bytes/pixel, row by row). Block is clamped to at least 1.
    /// </summary>
    public static void Pixelate(byte[] bgra, int width, int height, int block)
    {
        if (bgra.Length < width * height * 4)
            throw new ArgumentException("Buffer menor que width*height*4", nameof(bgra));

        block = Math.Max(1, block);
        var stride = width * 4;

        for (var by = 0; by < height; by += block)
        {
            for (var bx = 0; bx < width; bx += block)
            {
                long sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                var count = 0;
                var maxY = Math.Min(by + block, height);
                var maxX = Math.Min(bx + block, width);

                for (var py = by; py < maxY; py++)
                {
                    var row = py * stride;
                    for (var px = bx; px < maxX; px++)
                    {
                        var i = row + px * 4;
                        sumB += bgra[i];
                        sumG += bgra[i + 1];
                        sumR += bgra[i + 2];
                        sumA += bgra[i + 3];
                        count++;
                    }
                }
                if (count == 0)
                    continue;

                var avgB = (byte)(sumB / count);
                var avgG = (byte)(sumG / count);
                var avgR = (byte)(sumR / count);
                var avgA = (byte)(sumA / count);

                for (var py = by; py < maxY; py++)
                {
                    var row = py * stride;
                    for (var px = bx; px < maxX; px++)
                    {
                        var i = row + px * 4;
                        bgra[i] = avgB;
                        bgra[i + 1] = avgG;
                        bgra[i + 2] = avgR;
                        bgra[i + 3] = avgA;
                    }
                }
            }
        }
    }
}
