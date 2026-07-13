namespace Klip.Core.Media.Gif;

/// <summary>
/// Median cut variante palettegen do FFmpeg (RF-F4.07, D-F4.1 - referencia
/// blog.pkh.me): split do box de maior VARIANCIA ponderada (nao maior
/// volume), eixo = canal de maior amplitude (empate G &gt; R &gt; B), corte na
/// mediana ponderada por contagem, cor final = media ponderada.
/// </summary>
public static class MedianCutQuantizer
{
    private readonly record struct ColorEntry(uint Rgb, long Count)
    {
        public int R => (int)((Rgb >> 16) & 0xFF);
        public int G => (int)((Rgb >> 8) & 0xFF);
        public int B => (int)(Rgb & 0xFF);
    }

    private struct Box
    {
        public int Start;
        public int Length;
        public double WeightedVariance;
    }

    /// <summary>
    /// Reduz o histograma (chave RGB888, valor contagem) a no maximo
    /// <paramref name="maxColors"/> cores. Se o histograma ja cabe, devolve
    /// as proprias cores (caminho lossless, ordenadas por valor RGB).
    /// </summary>
    public static uint[] BuildPalette(IReadOnlyDictionary<uint, long> histogram, int maxColors)
    {
        ArgumentNullException.ThrowIfNull(histogram);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxColors, 1);
        if (histogram.Count == 0)
            throw new ArgumentException("Histograma vazio.", nameof(histogram));

        if (histogram.Count <= maxColors)
        {
            var exact = histogram.Keys.ToArray();
            Array.Sort(exact); // determinismo do indice
            return exact;
        }

        var entries = new ColorEntry[histogram.Count];
        var n = 0;
        foreach (var (rgb, count) in histogram)
            entries[n++] = new ColorEntry(rgb, count);
        // Ordena para tornar o resultado independente da ordem do dicionario.
        Array.Sort(entries, static (a, b) => a.Rgb.CompareTo(b.Rgb));

        var boxes = new List<Box>(maxColors)
        {
            MakeBox(entries, 0, entries.Length),
        };

        while (boxes.Count < maxColors)
        {
            // Box de maior variancia ponderada; boxes de 1 cor nao dividem.
            var bestIndex = -1;
            var bestVariance = 0.0;
            for (var i = 0; i < boxes.Count; i++)
            {
                if (boxes[i].Length > 1 && boxes[i].WeightedVariance > bestVariance)
                {
                    bestVariance = boxes[i].WeightedVariance;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
                break; // nada mais divisivel

            var box = boxes[bestIndex];
            var axis = LargestAmplitudeAxis(entries, box.Start, box.Length);
            SortByAxis(entries, box.Start, box.Length, axis);
            var splitLength = WeightedMedianSplit(entries, box.Start, box.Length);

            boxes[bestIndex] = MakeBox(entries, box.Start, splitLength);
            boxes.Add(MakeBox(entries, box.Start + splitLength, box.Length - splitLength));
        }

        var palette = new uint[boxes.Count];
        for (var i = 0; i < boxes.Count; i++)
            palette[i] = WeightedAverage(entries, boxes[i].Start, boxes[i].Length);
        Array.Sort(palette);
        return palette;
    }

    private static Box MakeBox(ColorEntry[] entries, int start, int length)
        => new() { Start = start, Length = length, WeightedVariance = ComputeWeightedVariance(entries, start, length) };

    /// <summary>
    /// Soma nas 3 dimensoes da variancia ponderada pela contagem
    /// (sum(w*c^2) - sum(w*c)^2/sum(w)): prioriza boxes grandes E espalhados.
    /// </summary>
    private static double ComputeWeightedVariance(ColorEntry[] entries, int start, int length)
    {
        if (length <= 1)
            return 0;

        double sumW = 0;
        double sumR = 0, sumG = 0, sumB = 0;
        double sumR2 = 0, sumG2 = 0, sumB2 = 0;
        for (var i = start; i < start + length; i++)
        {
            var e = entries[i];
            double w = e.Count;
            sumW += w;
            sumR += w * e.R;
            sumG += w * e.G;
            sumB += w * e.B;
            sumR2 += w * e.R * e.R;
            sumG2 += w * e.G * e.G;
            sumB2 += w * e.B * e.B;
        }

        var varR = sumR2 - sumR * sumR / sumW;
        var varG = sumG2 - sumG * sumG / sumW;
        var varB = sumB2 - sumB * sumB / sumW;
        return varR + varG + varB;
    }

    /// <summary>Canal de maior amplitude (max-min); empate resolve G &gt; R &gt; B.</summary>
    private static int LargestAmplitudeAxis(ColorEntry[] entries, int start, int length)
    {
        int minR = 255, maxR = 0, minG = 255, maxG = 0, minB = 255, maxB = 0;
        for (var i = start; i < start + length; i++)
        {
            var e = entries[i];
            if (e.R < minR) minR = e.R;
            if (e.R > maxR) maxR = e.R;
            if (e.G < minG) minG = e.G;
            if (e.G > maxG) maxG = e.G;
            if (e.B < minB) minB = e.B;
            if (e.B > maxB) maxB = e.B;
        }

        var ampR = maxR - minR;
        var ampG = maxG - minG;
        var ampB = maxB - minB;

        if (ampG >= ampR && ampG >= ampB)
            return 1; // G vence empates
        if (ampR >= ampB)
            return 0; // R vence de B
        return 2;
    }

    private static void SortByAxis(ColorEntry[] entries, int start, int length, int axis)
    {
        Comparison<ColorEntry> cmp = axis switch
        {
            0 => static (a, b) => a.R != b.R ? a.R.CompareTo(b.R) : a.Rgb.CompareTo(b.Rgb),
            1 => static (a, b) => a.G != b.G ? a.G.CompareTo(b.G) : a.Rgb.CompareTo(b.Rgb),
            _ => static (a, b) => a.B != b.B ? a.B.CompareTo(b.B) : a.Rgb.CompareTo(b.Rgb),
        };
        Array.Sort(entries, start, length, Comparer<ColorEntry>.Create(cmp));
    }

    /// <summary>
    /// Indice de corte na mediana ponderada por contagem, garantindo pelo
    /// menos 1 cor em cada lado. Retorna o tamanho da metade esquerda.
    /// </summary>
    private static int WeightedMedianSplit(ColorEntry[] entries, int start, int length)
    {
        long total = 0;
        for (var i = start; i < start + length; i++)
            total += entries[i].Count;

        var half = total / 2;
        long acc = 0;
        for (var i = 0; i < length - 1; i++)
        {
            acc += entries[start + i].Count;
            if (acc >= half)
                return i + 1;
        }
        return length - 1;
    }

    private static uint WeightedAverage(ColorEntry[] entries, int start, int length)
    {
        long sumW = 0;
        long sumR = 0, sumG = 0, sumB = 0;
        for (var i = start; i < start + length; i++)
        {
            var e = entries[i];
            sumW += e.Count;
            sumR += e.Count * e.R;
            sumG += e.Count * e.G;
            sumB += e.Count * e.B;
        }

        var r = (uint)((sumR + sumW / 2) / sumW);
        var g = (uint)((sumG + sumW / 2) / sumW);
        var b = (uint)((sumB + sumW / 2) / sumW);
        return (r << 16) | (g << 8) | b;
    }
}
