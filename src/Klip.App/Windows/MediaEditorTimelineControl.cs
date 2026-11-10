using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Klip.Core.Media.Editing;

namespace Klip.App.Windows;

/// <summary>
/// Controle proprio de timeline do editor de midia: regua em unidades da
/// timeline EDITADA (segundos no MP4, frames no GIF), uma LANE horizontal por
/// trilha de video (GIF so tem a trilha 0), segmentos como blocos
/// POSICIONADOS por TimelineStart / TimelineFrameStart (timeline livre
/// dentro da propria lane), gaps com fundo mais escuro e hachura diagonal
/// sutil, handles de trim nas bordas e playhead arrastavel.
/// <para>
/// Arrastar o CORPO de um segmento reposiciona-o dentro da mesma lane
/// (MoveSegmentTo) com snapping nas bordas dos vizinhos/playhead; soltar
/// numa lane DIFERENTE (ou abaixo da ultima) muda a trilha do segmento
/// (MoveSegmentToTrack) - arrastar para baixo da ultima lane cria uma trilha
/// nova automaticamente, ja que trilhas sao so o agrupamento por TrackIndex
/// no Core (nenhuma segue "vazia": sair da ultima faz ela sumir sozinha).
/// Durante o drag o segmento arrastado some do layout estatico da lane de
/// origem e um "ghost" translucido segue o mouse, sempre na lane-alvo atual
/// (ou numa linha tracejada abaixo de tudo quando o alvo e uma trilha nova).
/// </para>
/// <para>
/// Renderizacao via OnRender e hit test manual - controle custom, code-behind
/// pesado e esperado aqui. Durante trim/move o controle mostra um preview
/// local e so emite o evento de commit no mouse-up (um snapshot de undo por
/// gesto). A parte ESTATICA (regua + lanes + segmentos + gaps + thumbnails)
/// fica cacheada num DrawingGroup invalidado apenas quando
/// projeto/selecao/gesto/tamanho mudam; mover o playhead so troca o cache +
/// redesenha a linha. Os FormattedText da regua tambem sao cacheados.
/// </para>
/// </summary>
public sealed class MediaEditorTimelineControl : FrameworkElement
{
    private const double RulerHeight = 20;
    private const double TrimHandleWidth = 6;
    private const double DragThreshold = 4;
    private const double MinVideoTrimSeconds = 0.1;
    private const double MinLaneHeight = 36;
    private const double LaneGap = 3;
    private const double NewLaneStripHeight = 28;

    /// <summary>Raio de snapping do move em pixels.</summary>
    private const double SnapPixels = 8;

    private static readonly Typeface RulerTypeface = new("Segoe UI Variable Text");

    private enum DragMode { None, Scrub, MaybeDrag, Move, TrimLeft, TrimRight }

    private MediaEditProject? _project;
    private double _playheadUnits;
    private Guid? _selectedId;
    private IReadOnlyList<BitmapSource>? _gifThumbnails; // por frame do SOURCE (so ha trilha 0 em GIF)

    // cache do desenho estatico (regua + lanes + segmentos); null = reconstruir
    private DrawingGroup? _staticDrawing;
    // cache dos FormattedText da regua, por rotulo (limpo se brush/dpi mudam)
    private readonly Dictionary<string, FormattedText> _rulerTextCache = new();
    private Brush? _rulerTextBrush;
    private double _rulerTextDpi;

    // estado do gesto corrente
    private DragMode _mode;
    private Guid? _dragSegmentId;
    private int _dragOriginTrack; // lane de onde o segmento saiu (para sumir do estatico)
    private int _dragCurrentTrack; // lane-alvo corrente do preview (pode ser Lanes().Count = trilha nova)
    private Point _dragStart;
    private double _pendingTrimStartUnits;
    private double _pendingTrimEndUnits;
    // inicio pendente do segmento arrastado na timeline editada (dentro da lane-alvo)
    private double _pendingMoveStartUnits;
    // escala congelada no inicio do gesto: a regua pode reescalar durante o
    // preview e o delta do mouse precisa de uma conversao estavel
    private double _gestureUnitsPerPixel;

    /// <summary>Usuario moveu o playhead (unidades da timeline EDITADA).</summary>
    public event Action<double>? PlayheadScrubbed;

    /// <summary>
    /// Fim do drag do playhead (mouse-up ou perda de captura), com a posicao
    /// final. A janela usa seeks COALESCIDOS durante o drag (anti seek storm)
    /// e este evento para o seek imediato final na posicao exata.
    /// </summary>
    public event Action<double>? PlayheadScrubEnded;

    /// <summary>Selecao mudou (null = nada selecionado).</summary>
    public event Action<Guid?>? SegmentSelected;

    /// <summary>
    /// Commit do reposicionamento livre DENTRO DA MESMA trilha: id + novo
    /// inicio na timeline EDITADA (segundos no video, frame no GIF). O
    /// chamador aplica MoveSegmentTo/MoveSegmentToFrame (1 snapshot de undo).
    /// </summary>
    public event Action<Guid, double>? SegmentMoveToRequested;

    /// <summary>
    /// Commit do reposicionamento para OUTRA trilha (lane diferente, inclusive
    /// uma nova): id + indice da trilha de destino + novo inicio. Video only
    /// (GIF so tem uma lane, este evento nunca dispara para GIF).
    /// </summary>
    public event Action<Guid, int, double>? SegmentMoveToTrackRequested;

    /// <summary>Commit de trim: novos limites em unidades do SOURCE.</summary>
    public event Action<Guid, double, double>? SegmentTrimRequested;

    /// <summary>Clique direito num segmento: a janela mostra o menu (remover/ripple/reordenar).</summary>
    public event Action<Guid>? SegmentContextMenuRequested;

    public MediaEditorTimelineControl()
    {
        Focusable = false;
        ClipToBounds = true;
    }

    public Guid? SelectedSegmentId => _selectedId;

    public void SetProject(MediaEditProject? project)
    {
        _project = project;
        if (_selectedId is { } id && project?.Segments.Any(s => s.Id == id) != true)
            _selectedId = null;
        InvalidateStaticDrawing();
    }

    public void SetPlayhead(double units)
    {
        _playheadUnits = units;
        // so o playhead muda: replay do cache estatico + linha (barato)
        InvalidateVisual();
    }

    /// <summary>Miniaturas por frame do source para a strip GIF (so a trilha 0 existe em GIF).</summary>
    public void SetGifThumbnails(IReadOnlyList<BitmapSource> thumbnails)
    {
        _gifThumbnails = thumbnails;
        InvalidateStaticDrawing();
    }

    /// <summary>Descarta o cache estatico (projeto/gesto/selecao/tamanho mudou).</summary>
    private void InvalidateStaticDrawing()
    {
        _staticDrawing = null;
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _staticDrawing = null;
    }

    // ----- geometria -----

    private bool IsGif => _project?.Kind == MediaKind.Gif;

    /// <summary>Total COMMITADO da timeline editada (maior trilha), gaps incluidos.</summary>
    private double TotalUnits => _project is null
        ? 0
        : IsGif ? _project.EditedFrameCount : _project.EditedDuration.TotalSeconds;

    private double SourceTotalUnits(TimelineSegment seg) => IsGif ? _project!.SourceFrameCount : seg.SourceFileDuration.TotalSeconds;

    private static double SegSourceStart(TimelineSegment seg, bool gif) =>
        gif ? seg.FrameStart : seg.SourceStart.TotalSeconds;

    private static double SegSourceEnd(TimelineSegment seg, bool gif) =>
        gif ? seg.FrameEnd : seg.SourceEnd.TotalSeconds;

    private static double SegTimelineStart(TimelineSegment seg, bool gif) =>
        gif ? seg.TimelineFrameStart : seg.TimelineStart.TotalSeconds;

    private static double SegTimelineEnd(TimelineSegment seg, bool gif) =>
        gif ? seg.TimelineFrameEnd : seg.TimelineEnd.TotalSeconds;

    /// <summary>
    /// Trilhas em ordem visual (lane 0 no topo); GIF sempre devolve so [0].
    /// Ordem DESCENDENTE de TrackIndex de proposito: o export compoe de
    /// tras pra frente em ordem ASCENDENTE (a trilha com indice mais baixo e
    /// o fundo, cada indice maior cobre por cima - ver FfmpegArgsBuilder), e
    /// a regra pedida foi "o que esta em cima na timeline fica em cima no
    /// render" - entao a lane do topo aqui precisa ser o indice MAIOR.
    /// </summary>
    private IReadOnlyList<int> Lanes() => _project?.GetTrackIndices().Reverse().ToList() ?? [];

    private double LaneHeight()
    {
        var count = Math.Max(1, Lanes().Count);
        var available = Math.Max(0, ActualHeight - RulerHeight - NewLaneStripReserved());
        return Math.Max(MinLaneHeight, available / count);
    }

    private double LaneTop(int laneSlot) => RulerHeight + laneSlot * LaneHeight();

    /// <summary>
    /// Tira reservada abaixo da ultima lane para o convite "+ nova trilha"
    /// (ghost de arrastar-para-baixo ficava fora da area visivel - clipada -
    /// quando so havia uma lane preenchendo 100% da altura). So 0 (degrada
    /// sem a tira) se a janela for baixa demais pra manter a altura minima
    /// de cada lane existente.
    /// </summary>
    private double NewLaneStripReserved()
    {
        if (_project is null || IsGif)
            return 0;
        var count = Math.Max(1, Lanes().Count);
        var available = Math.Max(0, ActualHeight - RulerHeight);
        return available - count * MinLaneHeight >= NewLaneStripHeight ? NewLaneStripHeight : 0;
    }

    /// <summary>
    /// Resolve em qual lane (0-based, posicao na lista de <see cref="Lanes"/>)
    /// um Y cai; um Y abaixo de todas as lanes devolve Lanes().Count (pedido
    /// de trilha nova).
    /// </summary>
    private int LaneSlotFromY(double y)
    {
        var relative = Math.Max(0, y - RulerHeight);
        var slot = (int)(relative / LaneHeight());
        return Math.Min(slot, Lanes().Count); // clampa em "uma lane nova"
    }

    /// <summary>TrackIndex real correspondente a um slot (slot == Lanes().Count pede trilha nova).</summary>
    private int TrackIndexForSlot(int slot)
    {
        var lanes = Lanes();
        if (slot < lanes.Count)
            return lanes[slot];
        return lanes.Count == 0 ? 0 : lanes.Max() + 1;
    }

    /// <summary>
    /// Spans (inicio, comprimento) dos segmentos de UMA trilha na timeline
    /// EDITADA, com o gesto pendente de TRIM aplicado como preview (move
    /// vira um "ghost" fora do layout normal - ver <see cref="DrawGhost"/> -
    /// e some daqui enquanto arrasta, inclusive da lane de origem).
    /// </summary>
    private (double Start, double Length, TimelineSegment Segment)[] SegmentSpansForTrack(int trackIndex)
    {
        var gif = IsGif;
        var segs = _project!.SegmentsOnTrack(trackIndex);
        var spans = new List<(double, double, TimelineSegment)>(segs.Count);
        foreach (var seg in segs)
        {
            if (_mode == DragMode.Move && seg.Id == _dragSegmentId)
                continue; // some da lane de origem enquanto vira ghost
            var start = SegTimelineStart(seg, gif);
            var length = SegSourceEnd(seg, gif) - SegSourceStart(seg, gif);
            if (seg.Id == _dragSegmentId && _mode is DragMode.TrimLeft or DragMode.TrimRight)
            {
                // semantica do Core: TimelineStart desloca junto com o source
                // start (trim esquerdo mantem a borda direita fixa)
                start += _pendingTrimStartUnits - SegSourceStart(seg, gif);
                length = _pendingTrimEndUnits - _pendingTrimStartUnits;
            }
            spans.Add((start, length, seg));
        }
        return spans.ToArray();
    }

    private IReadOnlyList<TimelineLayout.Block> ComputeBlocksForTrack(int trackIndex)
    {
        var spans = SegmentSpansForTrack(trackIndex);
        var plain = spans.Select(s => (s.Start, s.Length)).ToArray();
        return TimelineLayout.ComputePositionedBlocks(plain, RenderTotalUnits(), ActualWidth);
    }

    /// <summary>
    /// Total da regua durante o render: fim do ultimo span de QUALQUER
    /// trilha, com o gesto pendente aplicado (nunca menor que o total
    /// commitado - arrastar/trimar o ultimo segmento para a direita ESTENDE a
    /// timeline).
    /// </summary>
    private double RenderTotalUnits()
    {
        var total = TotalUnits;
        if (_project is null)
            return total;
        var gif = IsGif;

        if (_mode == DragMode.Move && _dragSegmentId is { } movingId)
        {
            var seg = _project.Segments.First(s => s.Id == movingId);
            var length = SegSourceEnd(seg, gif) - SegSourceStart(seg, gif);
            total = Math.Max(total, _pendingMoveStartUnits + length);
        }
        else if (_mode is DragMode.TrimLeft or DragMode.TrimRight && _dragSegmentId is { } trimId)
        {
            var seg = _project.Segments.First(s => s.Id == trimId);
            var tlStart = SegTimelineStart(seg, gif) + (_pendingTrimStartUnits - SegSourceStart(seg, gif));
            total = Math.Max(total, tlStart + (_pendingTrimEndUnits - _pendingTrimStartUnits));
        }
        return total;
    }

    // ----- input -----

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_project is null)
            return;

        var pos = e.GetPosition(this);
        if (pos.Y <= RulerHeight)
        {
            BeginScrub(pos);
            e.Handled = true;
            return;
        }

        var slot = LaneSlotFromY(pos.Y);
        if (slot >= Lanes().Count)
        {
            e.Handled = true;
            return; // clique abaixo de todas as lanes: nada para clicar ainda
        }
        var trackIndex = Lanes()[slot];
        var blocks = ComputeBlocksForTrack(trackIndex);
        var hitIndex = TimelineLayout.HitTest(blocks, pos.X);
        if (hitIndex < 0)
        {
            BeginScrub(pos); // clique em gap: posiciona o playhead
            e.Handled = true;
            return;
        }

        var seg = SegmentSpansForTrack(trackIndex)[hitIndex].Segment;
        var block = blocks[hitIndex];
        Select(seg.Id);

        if (pos.X - block.X <= TrimHandleWidth)
            BeginTrim(seg, DragMode.TrimLeft, pos);
        else if (block.Right - pos.X <= TrimHandleWidth)
            BeginTrim(seg, DragMode.TrimRight, pos);
        else
        {
            // corpo do segmento: candidato a MOVE (mesma trilha ou outra)
            _mode = DragMode.MaybeDrag;
            _dragSegmentId = seg.Id;
            _dragOriginTrack = trackIndex;
            _dragCurrentTrack = trackIndex;
            _dragStart = pos;
            _pendingMoveStartUnits = SegTimelineStart(seg, IsGif);
            CaptureGestureScale();
            CaptureMouse();
        }
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (_project is null || _mode != DragMode.None)
            return;

        var pos = e.GetPosition(this);
        if (pos.Y <= RulerHeight)
            return;
        var slot = LaneSlotFromY(pos.Y);
        if (slot >= Lanes().Count)
            return;
        var trackIndex = Lanes()[slot];
        var blocks = ComputeBlocksForTrack(trackIndex);
        var hitIndex = TimelineLayout.HitTest(blocks, pos.X);
        if (hitIndex < 0)
            return;

        var id = SegmentSpansForTrack(trackIndex)[hitIndex].Segment.Id;
        Select(id);
        SegmentContextMenuRequested?.Invoke(id); // remover/ripple/reordenar no menu
        e.Handled = true;
    }

    private void BeginScrub(Point pos)
    {
        _mode = DragMode.Scrub;
        CaptureMouse();
        RaiseScrub(pos.X);
    }

    private void BeginTrim(TimelineSegment seg, DragMode mode, Point pos)
    {
        _mode = mode;
        _dragSegmentId = seg.Id;
        _dragOriginTrack = seg.TrackIndex;
        _dragCurrentTrack = seg.TrackIndex;
        _dragStart = pos;
        _pendingTrimStartUnits = SegSourceStart(seg, IsGif);
        _pendingTrimEndUnits = SegSourceEnd(seg, IsGif);
        CaptureGestureScale();
        CaptureMouse();
    }

    /// <summary>Congela a escala unidades/pixel para o gesto corrente.</summary>
    private void CaptureGestureScale()
    {
        var total = RenderTotalUnits();
        _gestureUnitsPerPixel = total <= 0 || ActualWidth <= 0 ? 0 : total / ActualWidth;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_project is null)
            return;

        var pos = e.GetPosition(this);
        switch (_mode)
        {
            case DragMode.Scrub:
                RaiseScrub(pos.X);
                break;

            case DragMode.MaybeDrag when Math.Abs(pos.X - _dragStart.X) > DragThreshold ||
                                          Math.Abs(pos.Y - _dragStart.Y) > DragThreshold:
                _mode = DragMode.Move;
                goto case DragMode.Move;

            case DragMode.Move:
                UpdateMovePreview(pos);
                break;

            case DragMode.TrimLeft or DragMode.TrimRight:
                UpdateTrimPreview(pos.X);
                break;

            default:
                UpdateCursor(pos);
                break;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_project is null)
        {
            ResetGesture();
            return;
        }

        switch (_mode)
        {
            case DragMode.Scrub:
            {
                // fim do drag do playhead: a janela faz o seek final imediato
                var units = _playheadUnits;
                ResetGesture();
                PlayheadScrubEnded?.Invoke(units);
                break;
            }
            case DragMode.Move:
            {
                var id = _dragSegmentId!.Value;
                var seg = _project.Segments.First(s => s.Id == id);
                var sameTrack = _dragCurrentTrack == _dragOriginTrack;
                var target = _pendingMoveStartUnits;
                var changedPosition = Math.Abs(target - SegTimelineStart(seg, IsGif)) > 1e-9;
                var destinationTrack = TrackIndexForSlotSafe(_dragCurrentTrack);
                ResetGesture();
                if (sameTrack && changedPosition)
                    SegmentMoveToRequested?.Invoke(id, target);
                else if (!sameTrack)
                    SegmentMoveToTrackRequested?.Invoke(id, destinationTrack, target);
                break;
            }
            case DragMode.TrimLeft or DragMode.TrimRight:
            {
                var id = _dragSegmentId!.Value;
                var seg = _project.Segments.First(s => s.Id == id);
                var start = _pendingTrimStartUnits;
                var end = _pendingTrimEndUnits;
                var changed = Math.Abs(start - SegSourceStart(seg, IsGif)) > 1e-9 ||
                              Math.Abs(end - SegSourceEnd(seg, IsGif)) > 1e-9;
                ResetGesture();
                if (changed)
                    SegmentTrimRequested?.Invoke(id, start, end);
                break;
            }
            default:
                ResetGesture();
                break;
        }
    }

    /// <summary>_dragCurrentTrack guarda um SLOT (posicao na lista de lanes), nao o TrackIndex cru.</summary>
    private int TrackIndexForSlotSafe(int slot) => TrackIndexForSlot(slot);

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_mode == DragMode.Scrub)
        {
            // perda de captura no meio do drag do playhead: encerra o scrub
            // para a janela drenar o coalescing com um seek final (seek storm)
            var units = _playheadUnits;
            ResetGesture();
            PlayheadScrubEnded?.Invoke(units);
        }
        else if (_mode != DragMode.None)
        {
            ResetGesture();
        }
    }

    private void ResetGesture()
    {
        _mode = DragMode.None;
        _dragSegmentId = null;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        InvalidateStaticDrawing();
    }

    private void Select(Guid id)
    {
        if (_selectedId == id)
            return;
        _selectedId = id;
        SegmentSelected?.Invoke(id);
        InvalidateStaticDrawing();
    }

    private void RaiseScrub(double x)
    {
        var units = TimelineLayout.XToUnits(x, RenderTotalUnits(), ActualWidth);
        if (IsGif)
            units = Math.Floor(units); // snapping a frame
        _playheadUnits = units;
        PlayheadScrubbed?.Invoke(units);
        InvalidateVisual();
    }

    /// <summary>
    /// Preview do reposicionamento livre (X dentro da lane-alvo corrente,
    /// snapping nas bordas dos vizinhos DESSA lane e no playhead; Y escolhe a
    /// lane-alvo, inclusive "uma lane nova" abaixo da ultima). Cruzar vizinhos
    /// dentro da mesma lane nao e suportado - a reordenacao por indice fica
    /// no menu de contexto.
    /// </summary>
    private void UpdateMovePreview(Point pos)
    {
        var gif = IsGif;
        var seg = _project!.Segments.First(s => s.Id == _dragSegmentId);
        var length = SegSourceEnd(seg, gif) - SegSourceStart(seg, gif);

        _dragCurrentTrack = gif ? 0 : LaneSlotFromY(pos.Y); // GIF: sempre a unica lane
        var targetTrackIndex = TrackIndexForSlot(_dragCurrentTrack);
        var sameTrack = targetTrackIndex == _dragOriginTrack;

        // vizinhos da lane-alvo (excluindo o proprio segmento se ainda esta nela)
        var neighbours = _project.SegmentsOnTrack(targetTrackIndex)
            .Where(s => s.Id != seg.Id)
            .OrderBy(s => SegTimelineStart(s, gif))
            .ToArray();

        var target = SegTimelineStart(seg, gif) + (pos.X - _dragStart.X) * _gestureUnitsPerPixel;
        if (!sameTrack)
            target = Math.Max(0, target); // noutra lane nao ha vizinho anterior conhecido de antemao

        var prev = neighbours.LastOrDefault(n => SegTimelineEnd(n, gif) <= target);
        var next = neighbours.FirstOrDefault(n => SegTimelineStart(n, gif) >= (prev is null ? 0 : SegTimelineEnd(prev, gif)));

        var lower = prev is null ? 0 : SegTimelineEnd(prev, gif);
        double? nextStart = next is null ? null : SegTimelineStart(next, gif);

        var snapRadius = SnapPixels * _gestureUnitsPerPixel;
        var candidates = new List<double> { lower, _playheadUnits, _playheadUnits - length };
        if (nextStart is { } n2)
            candidates.Add(n2 - length);
        var bestDist = snapRadius;
        var best = double.NaN;
        foreach (var candidate in candidates)
        {
            var dist = Math.Abs(candidate - target);
            if (dist <= bestDist)
            {
                bestDist = dist;
                best = candidate;
            }
        }
        if (!double.IsNaN(best))
            target = best;

        var upper = nextStart is { } nn ? Math.Max(lower, nn - length) : double.MaxValue;
        target = Math.Clamp(target, lower, upper);
        if (gif)
            target = Math.Clamp(Math.Round(target), lower, upper);

        _pendingMoveStartUnits = target;
        InvalidateStaticDrawing(); // lanes, ghost e gaps mudam de lugar no preview
    }

    /// <summary>Trim com clamp aos limites do SOURCE DO SEGMENTO, aos vizinhos na mesma trilha e snapping.</summary>
    private void UpdateTrimPreview(double x)
    {
        var seg = _project!.Segments.First(s => s.Id == _dragSegmentId);
        var gif = IsGif;
        var deltaUnits = (x - _dragStart.X) * _gestureUnitsPerPixel;

        var srcStart = SegSourceStart(seg, gif);
        var srcEnd = SegSourceEnd(seg, gif);
        var tlStart = SegTimelineStart(seg, gif);
        var minLength = gif ? 1 : MinVideoTrimSeconds;
        var track = _project.SegmentsOnTrack(seg.TrackIndex);
        var index = track.ToList().FindIndex(s => s.Id == seg.Id);

        if (_mode == DragMode.TrimLeft)
        {
            var next = srcStart + deltaUnits;
            next = gif ? Math.Round(next) : Math.Round(next, 1); // snap frame / 0,1 s
            var prevEnd = index > 0 ? SegTimelineEnd(track[index - 1], gif) : 0;
            var lowerBound = Math.Min(Math.Max(0, srcStart + (prevEnd - tlStart)), srcEnd - minLength);
            _pendingTrimStartUnits = Math.Clamp(next, lowerBound, srcEnd - minLength);
            _pendingTrimEndUnits = srcEnd;
        }
        else
        {
            var next = srcEnd + deltaUnits;
            next = gif ? Math.Round(next) : Math.Round(next, 1);
            var sourceTotal = SourceTotalUnits(seg);
            var upperBound = index < track.Count - 1
                ? Math.Min(sourceTotal, srcStart + (SegTimelineStart(track[index + 1], gif) - tlStart))
                : sourceTotal;
            upperBound = Math.Max(upperBound, srcStart + minLength);
            _pendingTrimStartUnits = srcStart;
            _pendingTrimEndUnits = Math.Clamp(next, srcStart + minLength, upperBound);
        }
        InvalidateStaticDrawing(); // segmentos mudam de largura no preview do trim
    }

    private void UpdateCursor(Point pos)
    {
        if (pos.Y <= RulerHeight || _project is null)
        {
            Cursor = Cursors.Arrow;
            return;
        }
        var slot = LaneSlotFromY(pos.Y);
        if (slot >= Lanes().Count)
        {
            Cursor = Cursors.Arrow;
            return;
        }
        var trackIndex = Lanes()[slot];
        var blocks = ComputeBlocksForTrack(trackIndex);
        var index = TimelineLayout.HitTest(blocks, pos.X);
        if (index < 0)
        {
            Cursor = Cursors.Arrow;
            return;
        }
        var block = blocks[index];
        Cursor = pos.X - block.X <= TrimHandleWidth || block.Right - pos.X <= TrimHandleWidth
            ? Cursors.SizeWE
            : Cursors.Hand;
    }

    // ----- render -----

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var background = FindBrush("ControlFillColorInputActiveBrush", Color.FromArgb(0x10, 0x80, 0x80, 0x80));
        dc.DrawRectangle(background, null, new Rect(0, 0, ActualWidth, ActualHeight));

        if (_project is null || ActualWidth <= 0 || ActualHeight <= RulerHeight)
            return;

        // regua + lanes saem do cache; so o playhead e o ghost (durante um
        // drag) sao redesenhados a cada frame
        if (_staticDrawing is null)
        {
            var group = new DrawingGroup();
            using (var ctx = group.Open())
            {
                DrawRuler(ctx);
                foreach (var trackIndex in Lanes())
                    DrawLane(ctx, trackIndex);
                DrawNewLaneInvite(ctx);
            }
            _staticDrawing = group;
        }
        dc.DrawDrawing(_staticDrawing);
        if (_mode == DragMode.Move)
            DrawGhost(dc);
        DrawPlayhead(dc);
    }

    private void DrawRuler(DrawingContext dc)
    {
        var textBrush = FindBrush("TextFillColorSecondaryBrush", Colors.Gray);
        var tickPen = new Pen(FindBrush("DividerStrokeColorDefaultBrush", Color.FromArgb(0x40, 0x80, 0x80, 0x80)), 1);
        tickPen.Freeze();

        var total = RenderTotalUnits();
        if (total <= 0)
            return;

        var step = TimelineLayout.RulerStep(total / ActualWidth);
        if (IsGif)
            step = Math.Max(1, Math.Ceiling(step)); // regua de frames: passo inteiro

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (double units = 0; units <= total + 1e-6; units += step)
        {
            var x = TimelineLayout.UnitsToX(units, total, ActualWidth);
            dc.DrawLine(tickPen, new Point(x, RulerHeight - 5), new Point(x, RulerHeight));
            var label = IsGif
                ? ((int)units).ToString(CultureInfo.CurrentUICulture)
                : FormatTime(units);
            var text = GetRulerText(label, textBrush, dpi);
            dc.DrawText(text, new Point(Math.Min(x + 3, ActualWidth - text.Width - 2), 1));
        }
        dc.DrawLine(tickPen, new Point(0, RulerHeight), new Point(ActualWidth, RulerHeight));
    }

    /// <summary>FormattedText da regua cacheado por rotulo (recriar a cada trim custa caro).</summary>
    private FormattedText GetRulerText(string label, Brush brush, double dpi)
    {
        if (!ReferenceEquals(brush, _rulerTextBrush) || dpi != _rulerTextDpi)
        {
            _rulerTextCache.Clear();
            _rulerTextBrush = brush;
            _rulerTextDpi = dpi;
        }
        if (!_rulerTextCache.TryGetValue(label, out var text))
        {
            text = new FormattedText(label, CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, RulerTypeface, 10, brush, dpi);
            _rulerTextCache[label] = text;
        }
        return text;
    }

    /// <summary>Desenha uma lane inteira: divisor, gaps e blocos de uma trilha.</summary>
    private void DrawLane(DrawingContext dc, int trackIndex)
    {
        var laneSlot = Lanes().ToList().IndexOf(trackIndex);
        var top = LaneTop(laneSlot) + LaneGap;
        var height = Math.Max(10, LaneHeight() - LaneGap * 2);

        if (laneSlot > 0)
        {
            var dividerPen = new Pen(FindBrush("DividerStrokeColorDefaultBrush", Color.FromArgb(0x30, 0x80, 0x80, 0x80)), 1);
            dividerPen.Freeze();
            dc.DrawLine(dividerPen, new Point(0, LaneTop(laneSlot)), new Point(ActualWidth, LaneTop(laneSlot)));
        }

        var blocks = ComputeBlocksForTrack(trackIndex);
        DrawGaps(dc, blocks, top, height);

        var accent = FindBrush("AccentFillColorDefaultBrush", Color.FromRgb(0x00, 0x78, 0xD4));
        var fill = FindBrush("ControlFillColorDefaultBrush", Color.FromArgb(0x30, 0x80, 0x80, 0x80));
        var borderPen = new Pen(FindBrush("ControlElevationBorderBrush", Color.FromArgb(0x60, 0x80, 0x80, 0x80)), 1);
        borderPen.Freeze();
        var selectedPen = new Pen(accent, 2);
        selectedPen.Freeze();
        var textBrush = FindBrush("TextFillColorPrimaryBrush", Colors.White);

        var spans = SegmentSpansForTrack(trackIndex);
        for (var i = 0; i < blocks.Count; i++)
        {
            var seg = spans[i].Segment;
            DrawSegmentBlock(dc, seg, blocks[i], top, height, fill,
                seg.Id == _selectedId ? selectedPen : borderPen, textBrush);
        }
    }

    /// <summary>
    /// Gaps da timeline editada DE UMA LANE: trechos nao cobertos por nenhum
    /// bloco recebem fundo mais escuro + hachura diagonal sutil (renderizam
    /// preto no preview/export, so na trilha 0 - trilhas extras nao tocam ao
    /// vivo). Nao existe gap depois do ultimo segmento por invariante do Core.
    /// </summary>
    private static void DrawGaps(DrawingContext dc, IReadOnlyList<TimelineLayout.Block> blocks,
        double top, double height)
    {
        var fill = new SolidColorBrush(Color.FromArgb(0x38, 0x00, 0x00, 0x00));
        fill.Freeze();
        var hatchPen = new Pen(new SolidColorBrush(Color.FromArgb(0x24, 0x80, 0x80, 0x80)), 1);
        hatchPen.Freeze();

        var x = 0.0;
        foreach (var block in blocks) // blocos em ordem de timeline, sem overlap
        {
            if (block.X > x + 0.5)
                DrawGapRect(dc, new Rect(x, top, block.X - x, height), fill, hatchPen);
            x = Math.Max(x, block.Right);
        }
    }

    private static void DrawGapRect(DrawingContext dc, Rect rect, Brush fill, Pen hatchPen)
    {
        dc.DrawRoundedRectangle(fill, null, rect, 2, 2);
        dc.PushClip(new RectangleGeometry(rect));
        for (var x = rect.X - rect.Height; x < rect.Right; x += 8)
            dc.DrawLine(hatchPen, new Point(x, rect.Bottom), new Point(x + rect.Height, rect.Y));
        dc.Pop();
    }

    private void DrawSegmentBlock(DrawingContext dc, TimelineSegment seg, TimelineLayout.Block block,
        double top, double height, Brush fill, Pen pen, Brush textBrush)
    {
        var rect = new Rect(block.X + 1, top, Math.Max(0, block.Width - 2), height);
        if (rect.Width <= 0)
            return;
        dc.DrawRoundedRectangle(fill, pen, rect, 4, 4);

        if (IsGif && _gifThumbnails is { Count: > 0 } thumbs)
        {
            DrawGifStrip(dc, seg, rect, thumbs);
            return;
        }

        var isDragging = seg.Id == _dragSegmentId && _mode is DragMode.TrimLeft or DragMode.TrimRight;
        var start = isDragging ? _pendingTrimStartUnits : SegSourceStart(seg, IsGif);
        var end = isDragging ? _pendingTrimEndUnits : SegSourceEnd(seg, IsGif);
        var label = seg.TrackIndex == 0
            ? $"{FormatTime(start)} - {FormatTime(end)}"
            : System.IO.Path.GetFileName(seg.SourcePath); // trilhas extras: arquivo inteiro, mostra o nome
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI Variable Text"), 11, textBrush, dpi)
        {
            MaxTextWidth = Math.Max(0, rect.Width - 8),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        dc.DrawText(text, new Point(rect.X + 6, rect.Y + (rect.Height - text.Height) / 2));
    }

    /// <summary>Strip de miniaturas dentro do bloco (frames do cache GIF).</summary>
    private void DrawGifStrip(DrawingContext dc, TimelineSegment seg, Rect rect, IReadOnlyList<BitmapSource> thumbs)
    {
        var sample = thumbs[Math.Min(seg.FrameStart, thumbs.Count - 1)];
        var thumbWidth = rect.Height * sample.PixelWidth / Math.Max(1, sample.PixelHeight);
        if (thumbWidth <= 0)
            return;
        var count = Math.Max(1, (int)Math.Ceiling(rect.Width / thumbWidth));

        dc.PushClip(new RectangleGeometry(rect, 4, 4));
        for (var i = 0; i < count; i++)
        {
            // frames igualmente espacados dentro do segmento
            var frame = seg.FrameStart + (int)((long)i * seg.FrameCount / count);
            frame = Math.Clamp(frame, 0, thumbs.Count - 1);
            dc.DrawImage(thumbs[frame], new Rect(rect.X + i * thumbWidth, rect.Y, thumbWidth, rect.Height));
        }
        dc.Pop();
    }

    /// <summary>
    /// Convite sempre visivel pra criar uma trilha nova arrastando um clipe
    /// pra baixo da ultima lane. Sem isso a faixa ficava inteiramente fora da
    /// area visivel quando so havia uma lane (ela ocupava 100% da altura e o
    /// ClipToBounds cortava o ghost no fundo, invisivel mesmo com a logica
    /// de drop certa).
    /// </summary>
    private void DrawNewLaneInvite(DrawingContext dc)
    {
        var reserved = NewLaneStripReserved();
        if (reserved <= 0)
            return;
        var top = RulerHeight + Lanes().Count * LaneHeight();
        var rect = new Rect(0, top + LaneGap, ActualWidth, Math.Max(10, reserved - LaneGap * 2));

        var dashPen = new Pen(FindBrush("DividerStrokeColorDefaultBrush", Color.FromArgb(0x50, 0x80, 0x80, 0x80)), 1)
        {
            DashStyle = DashStyles.Dash,
        };
        dashPen.Freeze();
        dc.DrawRoundedRectangle(null, dashPen, rect, 4, 4);

        var glyphBrush = FindBrush("TextFillColorSecondaryBrush", Colors.Gray);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var glyph = new FormattedText("", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe Fluent Icons"), 12, glyphBrush, dpi);
        dc.DrawText(glyph, new Point(rect.X + (rect.Width - glyph.Width) / 2, rect.Y + (rect.Height - glyph.Height) / 2));
    }

    /// <summary>
    /// Bloco translucido que segue o mouse durante um Move: sempre na lane-alvo
    /// corrente (<see cref="_dragCurrentTrack"/>), com borda tracejada quando
    /// o alvo e uma trilha nova (slot == Lanes().Count).
    /// </summary>
    private void DrawGhost(DrawingContext dc)
    {
        if (_dragSegmentId is not { } id || _project is null)
            return;
        var seg = _project.Segments.First(s => s.Id == id);
        var gif = IsGif;
        var length = SegSourceEnd(seg, gif) - SegSourceStart(seg, gif);
        var total = RenderTotalUnits();

        var x0 = TimelineLayout.UnitsToX(_pendingMoveStartUnits, total, ActualWidth);
        var x1 = TimelineLayout.UnitsToX(_pendingMoveStartUnits + length, total, ActualWidth);
        var isNewLane = _dragCurrentTrack >= Lanes().Count;
        var top = LaneTop(_dragCurrentTrack) + LaneGap;
        var height = isNewLane
            ? Math.Max(10, NewLaneStripHeight - LaneGap * 2)
            : Math.Max(10, LaneHeight() - LaneGap * 2);
        var rect = new Rect(x0, top, Math.Max(1, x1 - x0), height);

        var accent = FindBrush("AccentFillColorDefaultBrush", Color.FromRgb(0x00, 0x78, 0xD4));
        var fill = new SolidColorBrush(((SolidColorBrush)Clone(accent)).Color) { Opacity = 0.35 };
        fill.Freeze();
        var pen = new Pen(accent, 1.5);
        if (isNewLane)
            pen.DashStyle = DashStyles.Dash;
        pen.Freeze();
        dc.DrawRoundedRectangle(fill, pen, rect, 4, 4);
    }

    private static Brush Clone(Brush brush) => brush is SolidColorBrush s ? new SolidColorBrush(s.Color) : brush;

    private void DrawPlayhead(DrawingContext dc)
    {
        var accent = FindBrush("AccentFillColorDefaultBrush", Color.FromRgb(0x00, 0x78, 0xD4));
        var x = TimelineLayout.UnitsToX(_playheadUnits, RenderTotalUnits(), ActualWidth);
        var pen = new Pen(accent, 1.5);
        pen.Freeze();
        dc.DrawLine(pen, new Point(x, 0), new Point(x, ActualHeight));
        // cabeca triangular na regua
        var head = new StreamGeometry();
        using (var ctx = head.Open())
        {
            ctx.BeginFigure(new Point(x - 5, 0), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(x + 5, 0), true, false);
            ctx.LineTo(new Point(x, 8), true, false);
        }
        head.Freeze();
        dc.DrawGeometry(accent, null, head);
    }

    private Brush FindBrush(string key, Color fallback)
    {
        if (TryFindResource(key) is Brush brush)
            return brush;
        var solid = new SolidColorBrush(fallback);
        solid.Freeze();
        return solid;
    }

    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", CultureInfo.CurrentUICulture)
            : time.ToString(@"m\:ss\.f", CultureInfo.CurrentUICulture);
    }
}
