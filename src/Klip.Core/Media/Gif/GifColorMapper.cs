namespace Klip.Core.Media.Gif;

/// <summary>
/// Mapeia cores RGB para indices da paleta global (RF-F4.08): cache
/// Dictionary cor-&gt;indice com fallback de busca do vizinho mais proximo
/// (distancia euclidiana RGB, primeiro indice em caso de empate).
/// Suporta dithering ordenado Bayer 8x8 (RF-F4.09): o offset e aplicado a
/// cor ANTES do cache, entao o cache continua valido por cor ajustada.
/// </summary>
internal sealed class GifColorMapper(uint[] palette, GifDithering dithering)
{
    /// <summary>
    /// Matriz Bayer 8x8 classica (valores 0..63), aplicada por posicao de
    /// tela - deterministica, nao "nada" entre frames como error diffusion.
    /// </summary>
    private static readonly byte[,] Bayer8x8 =
    {
        {  0, 32,  8, 40,  2, 34, 10, 42 },
        { 48, 16, 56, 24, 50, 18, 58, 26 },
        { 12, 44,  4, 36, 14, 46,  6, 38 },
        { 60, 28, 52, 20, 62, 30, 54, 22 },
        {  3, 35, 11, 43,  1, 33,  9, 41 },
        { 51, 19, 59, 27, 49, 17, 57, 25 },
        { 15, 47,  7, 39, 13, 45,  5, 37 },
        { 63, 31, 55, 23, 61, 29, 53, 21 },
    };

    /// <summary>Amplitude do offset de dithering (+/- metade disso por canal).</summary>
    private const double DitherSpread = 32.0;

    private readonly uint[] _palette = palette;
    private readonly GifDithering _dithering = dithering;
    private readonly Dictionary<uint, byte> _cache = new(1024);

    /// <summary>Mapeia (r,g,b) para o indice da paleta; (x,y) alimenta o dithering.</summary>
    public byte Map(byte r, byte g, byte b, int x, int y)
    {
        if (_dithering == GifDithering.Bayer8x8)
        {
            var offset = ((Bayer8x8[y & 7, x & 7] + 0.5) / 64.0 - 0.5) * DitherSpread;
            r = ClampToByte(r + offset);
            g = ClampToByte(g + offset);
            b = ClampToByte(b + offset);
        }

        var rgb = ((uint)r << 16) | ((uint)g << 8) | b;
        if (_cache.TryGetValue(rgb, out var cached))
            return cached;

        var index = FindNearest(r, g, b);
        _cache[rgb] = index;
        return index;
    }

    private byte FindNearest(int r, int g, int b)
    {
        var bestIndex = 0;
        var bestDistance = int.MaxValue;
        for (var i = 0; i < _palette.Length; i++)
        {
            var c = _palette[i];
            var dr = r - (int)((c >> 16) & 0xFF);
            var dg = g - (int)((c >> 8) & 0xFF);
            var db = b - (int)(c & 0xFF);
            var distance = dr * dr + dg * dg + db * db;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
                if (distance == 0)
                    break;
            }
        }
        return (byte)bestIndex;
    }

    private static byte ClampToByte(double value)
        => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
}
