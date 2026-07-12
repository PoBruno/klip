namespace Klip.Core.Media.Editing;

/// <summary>
/// Matematica pura de layout do controle de timeline (RF-F5.05/06/07):
/// segmentos viram blocos proporcionais a duracao, e posicoes em pixel
/// mapeiam de/para unidades da timeline EDITADA (segundos no video, frames
/// no GIF). Timeline livre (RF-F5.17): use
/// <see cref="ComputePositionedBlocks"/> para posicionar blocos por
/// TimelineStart (com gaps) e <see cref="HitTestGap"/> para detectar cliques
/// em gaps. Sem WPF - testavel em Klip.Core.Tests.
/// </summary>
public static class TimelineLayout
{
    /// <summary>Um bloco de segmento em pixels (X inicial e largura).</summary>
    public readonly record struct Block(double X, double Width)
    {
        public double Right => X + Width;
    }

    /// <summary>
    /// Blocos contiguos proporcionais aos comprimentos. Comprimentos nao
    /// positivos viram blocos de largura zero; total nao positivo ou largura
    /// nao positiva retornam lista vazia.
    /// </summary>
    public static IReadOnlyList<Block> ComputeBlocks(IReadOnlyList<double> lengths, double totalWidth)
    {
        ArgumentNullException.ThrowIfNull(lengths);
        if (lengths.Count == 0 || totalWidth <= 0)
            return [];

        double total = 0;
        foreach (var len in lengths)
            total += Math.Max(0, len);
        if (total <= 0)
            return [];

        var blocks = new Block[lengths.Count];
        double x = 0;
        for (var i = 0; i < lengths.Count; i++)
        {
            var width = Math.Max(0, lengths[i]) / total * totalWidth;
            blocks[i] = new Block(x, width);
            x += width;
        }
        return blocks;
    }

    /// <summary>
    /// Blocos POSICIONADOS pela timeline livre (RF-F5.17): cada span e
    /// (inicio, comprimento) em unidades da timeline editada (TimelineStart +
    /// duracao no video; TimelineFrameStart + contagem no GIF). A regua vai
    /// ate <paramref name="totalUnits"/> (EditedDuration/EditedFrameCount);
    /// espacos sem bloco sao gaps. Total ou largura nao positivos retornam
    /// lista vazia.
    /// </summary>
    public static IReadOnlyList<Block> ComputePositionedBlocks(
        IReadOnlyList<(double Start, double Length)> spans, double totalUnits, double totalWidth)
    {
        ArgumentNullException.ThrowIfNull(spans);
        if (spans.Count == 0 || totalUnits <= 0 || totalWidth <= 0)
            return [];

        var blocks = new Block[spans.Count];
        for (var i = 0; i < spans.Count; i++)
        {
            var x = Math.Max(0, spans[i].Start) / totalUnits * totalWidth;
            var width = Math.Max(0, spans[i].Length) / totalUnits * totalWidth;
            blocks[i] = new Block(x, width);
        }
        return blocks;
    }

    /// <summary>
    /// Hit-test de gap (RF-F5.17): true quando <paramref name="x"/> esta
    /// dentro da regua ([0, totalWidth)) mas fora de todos os blocos.
    /// </summary>
    public static bool HitTestGap(IReadOnlyList<Block> blocks, double x, double totalWidth)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        return x >= 0 && x < totalWidth && HitTest(blocks, x) < 0;
    }

    /// <summary>Indice do bloco sob <paramref name="x"/>, ou -1 fora de todos.</summary>
    public static int HitTest(IReadOnlyList<Block> blocks, double x)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        for (var i = 0; i < blocks.Count; i++)
        {
            if (x >= blocks[i].X && x < blocks[i].Right)
                return i;
        }
        return -1;
    }

    /// <summary>Converte unidades da timeline editada em pixel (clampado).</summary>
    public static double UnitsToX(double units, double totalUnits, double totalWidth)
    {
        if (totalUnits <= 0 || totalWidth <= 0)
            return 0;
        return Math.Clamp(units / totalUnits, 0, 1) * totalWidth;
    }

    /// <summary>Converte pixel em unidades da timeline editada (clampado).</summary>
    public static double XToUnits(double x, double totalUnits, double totalWidth)
    {
        if (totalUnits <= 0 || totalWidth <= 0)
            return 0;
        return Math.Clamp(x / totalWidth, 0, 1) * totalUnits;
    }

    /// <summary>
    /// Indice de insercao do drag-and-drop de reordenacao (RF-F5.07): conta os
    /// blocos (exceto o arrastado) cujo centro fica a esquerda de
    /// <paramref name="x"/>. O resultado e o newIndex esperado por
    /// <see cref="MediaEditProject.MoveSegment"/> (remove + insere).
    /// </summary>
    public static int DropIndex(IReadOnlyList<Block> blocks, int dragIndex, double x)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        var index = 0;
        for (var i = 0; i < blocks.Count; i++)
        {
            if (i == dragIndex)
                continue;
            var center = blocks[i].X + blocks[i].Width / 2;
            if (x > center)
                index++;
        }
        return index;
    }

    /// <summary>
    /// Passo "bonito" da regua para um alvo minimo de pixels entre marcas.
    /// <paramref name="unitsPerPixel"/> = totalUnits / totalWidth.
    /// </summary>
    public static double RulerStep(double unitsPerPixel, double minPixelsPerTick = 70)
    {
        if (unitsPerPixel <= 0)
            return 1;
        var minUnits = unitsPerPixel * minPixelsPerTick;
        double[] steps = [0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600];
        foreach (var step in steps)
        {
            if (step >= minUnits)
                return step;
        }
        // acima de 1 h por marca: multiplos de hora
        return Math.Ceiling(minUnits / 3600) * 3600;
    }
}
