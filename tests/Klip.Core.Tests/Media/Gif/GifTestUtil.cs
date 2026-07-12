namespace Klip.Core.Tests.Media.Gif;

/// <summary>Helpers compartilhados dos testes do encoder GIF.</summary>
internal static class GifTestUtil
{
    /// <summary>Gera um buffer BGRA32 top-down a partir de uma funcao (x,y) -&gt; RGB.</summary>
    public static byte[] MakeBgra(int width, int height, Func<int, int, (byte R, byte G, byte B)> pixel)
    {
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = pixel(x, y);
                var o = (y * width + x) * 4;
                bgra[o] = b;
                bgra[o + 1] = g;
                bgra[o + 2] = r;
                bgra[o + 3] = 255;
            }
        }
        return bgra;
    }

    /// <summary>Converte BGRA para o formato do canvas do decoder (RGB888 por pixel).</summary>
    public static uint[] ToRgb(byte[] bgra)
    {
        var rgb = new uint[bgra.Length / 4];
        for (var i = 0; i < rgb.Length; i++)
        {
            var o = i * 4;
            rgb[i] = ((uint)bgra[o + 2] << 16) | ((uint)bgra[o + 1] << 8) | bgra[o];
        }
        return rgb;
    }

    /// <summary>Distancia euclidiana ao quadrado entre duas cores RGB888.</summary>
    public static int DistanceSquared(uint a, uint b)
    {
        var dr = (int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF);
        var dg = (int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF);
        var db = (int)(a & 0xFF) - (int)(b & 0xFF);
        return dr * dr + dg * dg + db * db;
    }

    /// <summary>
    /// Vizinho mais proximo com o mesmo tie-break do encoder (primeiro indice
    /// com distancia estritamente menor vence).
    /// </summary>
    public static uint NearestPaletteColor(uint color, uint[] palette)
    {
        var best = palette[0];
        var bestDistance = int.MaxValue;
        foreach (var candidate in palette)
        {
            var distance = DistanceSquared(color, candidate);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }
        return best;
    }
}
