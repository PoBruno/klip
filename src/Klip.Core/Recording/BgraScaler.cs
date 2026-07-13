namespace Klip.Core.Recording;

/// <summary>
/// Downscale de frames BGRA32 para a gravacao GIF (RF-F4.03: escala 100/75/50
/// aplicada ANTES da retencao). Filtro box (media dos pixels de origem de cada
/// pixel destino): barato, sem dependencias e bom o suficiente para screencast;
/// resamplers de alta qualidade ficam para o editor (F5).
/// </summary>
public static class BgraScaler
{
    /// <summary>
    /// Reduz um frame BGRA32 top-down (stride = width*4) para a escala pedida.
    /// <paramref name="percent"/> em (0, 100]; 100 devolve uma copia 1:1.
    /// </summary>
    public static (byte[] Bgra, int Width, int Height) Downscale(
        ReadOnlySpan<byte> bgra, int width, int height, int percent)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (percent is <= 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(percent), "Escala deve estar em (0, 100].");
        if (bgra.Length != width * height * 4)
            throw new ArgumentException("Buffer BGRA deve ter exatamente width*height*4 bytes.", nameof(bgra));

        if (percent == 100)
            return (bgra.ToArray(), width, height);

        int dstW = Math.Max(1, width * percent / 100);
        int dstH = Math.Max(1, height * percent / 100);
        var dst = new byte[dstW * dstH * 4];

        for (int dy = 0; dy < dstH; dy++)
        {
            // faixa de linhas de origem deste pixel destino (box)
            int y0 = dy * height / dstH;
            int y1 = Math.Max(y0 + 1, (dy + 1) * height / dstH);

            for (int dx = 0; dx < dstW; dx++)
            {
                int x0 = dx * width / dstW;
                int x1 = Math.Max(x0 + 1, (dx + 1) * width / dstW);

                int b = 0, g = 0, r = 0, a = 0;
                int count = (x1 - x0) * (y1 - y0);
                for (int sy = y0; sy < y1; sy++)
                {
                    int row = (sy * width + x0) * 4;
                    for (int sx = x0; sx < x1; sx++)
                    {
                        b += bgra[row];
                        g += bgra[row + 1];
                        r += bgra[row + 2];
                        a += bgra[row + 3];
                        row += 4;
                    }
                }

                int di = (dy * dstW + dx) * 4;
                dst[di] = (byte)(b / count);
                dst[di + 1] = (byte)(g / count);
                dst[di + 2] = (byte)(r / count);
                dst[di + 3] = (byte)(a / count);
            }
        }

        return (dst, dstW, dstH);
    }
}
