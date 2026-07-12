namespace Klip.Core.Media.Editing;

/// <summary>
/// Materializa a sequencia de frames da timeline GIF editada (RF-F5.05/08):
/// percorre os segmentos na ordem da timeline, resolve cada frame editado
/// para o indice no source (delays vem do arquivo original) e aplica a
/// reducao de FPS opcional via <see cref="GifTimelineOps.ReduceFps"/>.
/// O preview e a exportacao usam a MESMA sequencia (CA-F5.4).
///
/// RF-F5.20: slots da timeline dentro de gaps viram entradas PRETAS -
/// <see cref="Entry.SourceFrame"/> = -1 e <see cref="Entry.IsGap"/> = true;
/// o App pinta preto ao encontra-las. Cadencia dos gaps: cada frame preto
/// herda o delay do frame REAL anterior na sequencia (gap inicial herda o
/// delay do primeiro frame real do primeiro segmento), mantendo a cadencia
/// local do arquivo.
/// </summary>
public static class GifSequenceBuilder
{
    /// <summary>
    /// Um frame da sequencia final: posicao na timeline editada (para o
    /// playhead), indice do frame no source (para buscar os pixels; -1 para
    /// frames pretos de gap, RF-F5.20) e delay.
    /// </summary>
    public readonly record struct Entry(int EditedFrame, int SourceFrame, int DelayMs)
    {
        /// <summary>True quando o slot cai num gap: o App renderiza preto (RF-F5.20).</summary>
        public bool IsGap => SourceFrame < 0;
    }

    /// <summary>
    /// Constroi a sequencia editada. <paramref name="sourceDelaysMs"/> e o
    /// delay de cada frame do ARQUIVO original (indexado por frame do source);
    /// <paramref name="targetFps"/> nulo = sem reducao (RF-F5.08). A contagem
    /// de entradas e sempre <see cref="MediaEditProject.EditedFrameCount"/>
    /// antes da reducao (um Entry por slot da timeline, gaps incluidos).
    /// </summary>
    public static IReadOnlyList<Entry> Build(
        MediaEditProject project, IReadOnlyList<int> sourceDelaysMs, int? targetFps)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sourceDelaysMs);
        if (project.Kind != MediaKind.Gif)
            throw new ArgumentException("A sequencia GIF requer um projeto Gif.", nameof(project));
        if (sourceDelaysMs.Count < project.SourceFrameCount)
            throw new ArgumentException("Delays insuficientes para o total de frames do source.", nameof(sourceDelaysMs));

        // timeline editada: um Entry por slot; slots em gap viram frames pretos
        // com a cadencia local (RF-F5.20)
        var edited = new List<Entry>(project.EditedFrameCount);
        var editedIndex = 0;
        var gapDelay = sourceDelaysMs[project.Segments[0].FrameStart];
        foreach (var segment in project.Segments)
        {
            while (editedIndex < segment.TimelineFrameStart)
                edited.Add(new Entry(editedIndex++, -1, gapDelay));

            for (var src = segment.FrameStart; src < segment.FrameEnd; src++)
                edited.Add(new Entry(editedIndex++, src, sourceDelaysMs[src]));

            gapDelay = sourceDelaysMs[segment.FrameEnd - 1];
        }

        if (targetFps is not int fps)
            return edited;

        // decimacao com redistribuicao de delay (duracao preservada, CA-F5.4);
        // frames de gap participam da decimacao como frames normais
        var delays = new int[edited.Count];
        for (var i = 0; i < edited.Count; i++)
            delays[i] = edited[i].DelayMs;

        var reduced = GifTimelineOps.ReduceFps(delays, fps);
        var result = new List<Entry>(reduced.Count);
        foreach (var (frameIndex, delayMs) in reduced)
            result.Add(edited[frameIndex] with { DelayMs = delayMs });
        return result;
    }
}
