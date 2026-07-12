using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Klip.Core.Media.Editing;

namespace Klip.App.Windows;

/// <summary>
/// Controle proprio de timeline do editor de midia (D-F5.2, RF-F5.05..07,
/// RF-F5.17..19): regua em unidades da timeline EDITADA (segundos no MP4,
/// frames no GIF), segmentos como blocos POSICIONADOS por TimelineStart /
/// TimelineFrameStart (timeline livre, RF-F5.17), gaps com fundo mais escuro
/// e hachura diagonal sutil, handles de trim nas bordas e playhead
/// arrastavel. Arrastar o CORPO do segmento reposiciona-o na timeline
/// (MoveSegmentTo), com clamp visual nos vizinhos e snapping nas bordas dos
/// vizinhos e no playhead (RF-F5.19, ~8 px); cruzar vizinhos no drag nao e
/// suportado - a reordenacao por indice (RF-F5.07) fica no menu de contexto
/// da janela (SegmentContextMenuRequested). Renderizacao via OnRender e hit
/// test manual - controle custom, code-behind pesado e esperado aqui.
/// Durante trim/move o controle mostra um preview local e so emite o evento
/// de commit no mouse-up (um snapshot de undo por gesto, RF-F5.10).
/// Desempenho (correcao do re-render integral a cada tick do playhead): a
/// parte ESTATICA (regua + segmentos + gaps + thumbnails) fica cacheada num
/// DrawingGroup invalidado apenas quando projeto/selecao/gesto/tamanho mudam;
/// mover o playhead so replays o cache + redesenha a linha. Os FormattedText
/// da regua tambem sao cacheados por rotulo.
/// </summary>
public sealed class MediaEditorTimelineControl : FrameworkElement
{
    private const double RulerHeight = 20;
    private const double TrimHandleWidth = 6;
    private const double DragThreshold = 4;
    private const double MinVideoTrimSeconds = 0.1;

    /// <summary>Raio de snapping do move em pixels (RF-F5.19).</summary>
    private const double SnapPixels = 8;

    private static readonly Typeface RulerTypeface = new("Segoe UI Variable Text");

    private enum DragMode { None, Scrub, MaybeDrag, Move, TrimLeft, TrimRight }

    private MediaEditProject? _project;
    private double _playheadUnits;
    private Guid? _selectedId;
    private IReadOnlyList<BitmapSource>? _gifThumbnails; // por frame do SOURCE

    // cache do desenho estatico (regua + segmentos); null = reconstruir
    private DrawingGroup? _staticDrawing;
    // cache dos FormattedText da regua, por rotulo (limpo se brush/dpi mudam)
    private readonly Dictionary<string, FormattedText> _rulerTextCache = new();
    private Brush? _rulerTextBrush;
    private double _rulerTextDpi;

    // estado do gesto corrente
    private DragMode _mode;
    private int _dragIndex = -1;
    private Point _dragStart;
    private double _pendingTrimStartUnits;
    private double _pendingTrimEndUnits;
    // RF-F5.17: inicio pendente do segmento arrastado na timeline editada
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
    /// Commit do reposicionamento livre (RF-F5.17): id + novo inicio na
    /// timeline EDITADA (segundos no video, frame no GIF). O chamador aplica
    /// MoveSegmentTo/MoveSegmentToFrame (1 snapshot de undo).
    /// </summary>
    public event Action<Guid, double>? SegmentMoveToRequested;

    /// <summary>Commit de trim (RF-F5.06): novos limites em unidades do SOURCE.</summary>
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
        if (_selectedId is { } id && project?.Segments.All(s => s.Id != id) != false)
            _selectedId = null;
        InvalidateStaticDrawing();
    }

    public void SetPlayhead(double units)
    {
        _playheadUnits = units;
        // so o playhead muda: replay do cache estatico + linha (barato)
        InvalidateVisual();
    }

    /// <summary>Miniaturas por frame do source para a strip GIF (RF-F5.03).</summary>
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

    /// <summary>Total COMMITADO da timeline editada, gaps incluidos (RF-F5.17).</summary>
    private double TotalUnits => _project is null
        ? 0
        : IsGif ? _project.EditedFrameCount : _project.EditedDuration.TotalSeconds;

    private double SourceTotalUnits => _project is null
        ? 0
        : IsGif ? _project.SourceFrameCount : _project.SourceDuration.TotalSeconds;

    private static double SegSourceStart(TimelineSegment seg, bool gif) =>
        gif ? seg.FrameStart : seg.SourceStart.TotalSeconds;

    private static double SegSourceEnd(TimelineSegment seg, bool gif) =>
        gif ? seg.FrameEnd : seg.SourceEnd.TotalSeconds;

    private static double SegTimelineStart(TimelineSegment seg, bool gif) =>
        gif ? seg.TimelineFrameStart : seg.TimelineStart.TotalSeconds;

    private static double SegTimelineEnd(TimelineSegment seg, bool gif) =>
        gif ? seg.TimelineFrameEnd : seg.TimelineEnd.TotalSeconds;

    /// <summary>
    /// Spans (inicio, comprimento) dos segmentos na timeline EDITADA
    /// (RF-F5.17), com o gesto pendente (trim/move) aplicado como preview.
    /// </summary>
    private (double Start, double Length)[] SegmentSpans()
    {
        var project = _project!;
        var gif = IsGif;
        var spans = new (double Start, double Length)[project.Segments.Count];
        for (var i = 0; i < spans.Length; i++)
        {
            var seg = project.Segments[i];
            var start = SegTimelineStart(seg, gif);
            var length = SegSourceEnd(seg, gif) - SegSourceStart(seg, gif);
            if (i == _dragIndex && _mode is DragMode.TrimLeft or DragMode.TrimRight)
            {
                // semantica do Core: TimelineStart desloca junto com o source
                // start (trim esquerdo mantem a borda direita fixa)
                start += _pendingTrimStartUnits - SegSourceStart(seg, gif);
                length = _pendingTrimEndUnits - _pendingTrimStartUnits;
            }
            else if (i == _dragIndex && _mode == DragMode.Move)
            {
                start = _pendingMoveStartUnits;
            }
            spans[i] = (start, length);
        }
        return spans;
    }

    /// <summary>
    /// Total da regua durante o render: fim do ultimo span com o gesto
    /// pendente aplicado (nunca menor que o total commitado - arrastar o
    /// ultimo segmento para a direita ESTENDE a timeline, RF-F5.17).
    /// </summary>
    private double RenderTotalUnits()
    {
        var total = TotalUnits;
        foreach (var (start, length) in SegmentSpans())
            total = Math.Max(total, start + length);
        return total;
    }

    private IReadOnlyList<TimelineLayout.Block> ComputeBlocks() =>
        TimelineLayout.ComputePositionedBlocks(SegmentSpans(), RenderTotalUnits(), ActualWidth);

    // ----- input -----

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_project is null)
            return;

        var pos = e.GetPosition(this);
        var blocks = ComputeBlocks();

        if (pos.Y <= RulerHeight)
        {
            BeginScrub(pos);
        }
        else
        {
            var index = TimelineLayout.HitTest(blocks, pos.X);
            if (index >= 0)
            {
                var block = blocks[index];
                var seg = _project.Segments[index];
                Select(seg.Id);

                if (pos.X - block.X <= TrimHandleWidth)
                    BeginTrim(index, DragMode.TrimLeft, pos);
                else if (block.Right - pos.X <= TrimHandleWidth)
                    BeginTrim(index, DragMode.TrimRight, pos);
                else
                {
                    // corpo do segmento: candidato a MOVE na timeline (RF-F5.17)
                    _mode = DragMode.MaybeDrag;
                    _dragIndex = index;
                    _dragStart = pos;
                    _pendingMoveStartUnits = SegTimelineStart(seg, IsGif);
                    CaptureGestureScale();
                    CaptureMouse();
                }
            }
            else
            {
                // clique em gap ou fora dos blocos: posiciona o playhead
                BeginScrub(pos);
            }
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
        var index = TimelineLayout.HitTest(ComputeBlocks(), pos.X);
        if (index < 0)
            return;

        var id = _project.Segments[index].Id;
        Select(id);
        SegmentContextMenuRequested?.Invoke(id); // RF-F5.18: remover/ripple no menu
        e.Handled = true;
    }

    private void BeginScrub(Point pos)
    {
        _mode = DragMode.Scrub;
        CaptureMouse();
        RaiseScrub(pos.X);
    }

    private void BeginTrim(int index, DragMode mode, Point pos)
    {
        var seg = _project!.Segments[index];
        _mode = mode;
        _dragIndex = index;
        _dragStart = pos;
        _pendingTrimStartUnits = SegSourceStart(seg, IsGif);
        _pendingTrimEndUnits = SegSourceEnd(seg, IsGif);
        CaptureGestureScale();
        CaptureMouse();
    }

    /// <summary>Congela a escala unidades/pixel para o gesto corrente.</summary>
    private void CaptureGestureScale()
    {
        _gestureUnitsPerPixel = TotalUnits <= 0 || ActualWidth <= 0 ? 0 : TotalUnits / ActualWidth;
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

            case DragMode.MaybeDrag when Math.Abs(pos.X - _dragStart.X) > DragThreshold:
                _mode = DragMode.Move;
                goto case DragMode.Move;

            case DragMode.Move:
                UpdateMovePreview(pos.X);
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
                var seg = _project.Segments[_dragIndex];
                var target = _pendingMoveStartUnits;
                var changed = Math.Abs(target - SegTimelineStart(seg, IsGif)) > 1e-9;
                var id = seg.Id;
                ResetGesture();
                if (changed)
                    SegmentMoveToRequested?.Invoke(id, target); // RF-F5.17
                break;
            }
            case DragMode.TrimLeft or DragMode.TrimRight:
            {
                var seg = _project.Segments[_dragIndex];
                var start = _pendingTrimStartUnits;
                var end = _pendingTrimEndUnits;
                var changed = Math.Abs(start - SegSourceStart(seg, IsGif)) > 1e-9 ||
                              Math.Abs(end - SegSourceEnd(seg, IsGif)) > 1e-9;
                var id = seg.Id;
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
        _dragIndex = -1;
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
            units = Math.Floor(units); // snapping a frame (RF-F5.06)
        _playheadUnits = units;
        PlayheadScrubbed?.Invoke(units);
        InvalidateVisual();
    }

    /// <summary>
    /// Preview do reposicionamento livre (RF-F5.17): converte o delta do
    /// mouse em unidades com a escala congelada do gesto, aplica snapping nas
    /// bordas dos vizinhos e no playhead (~8 px) e clampa nos vizinhos
    /// espelhando MoveSegmentTo (RF-F5.19) - cruzar vizinhos nao e suportado.
    /// </summary>
    private void UpdateMovePreview(double x)
    {
        var project = _project!;
        var gif = IsGif;
        var seg = project.Segments[_dragIndex];
        var length = SegSourceEnd(seg, gif) - SegSourceStart(seg, gif);
        var target = SegTimelineStart(seg, gif) + (x - _dragStart.X) * _gestureUnitsPerPixel;

        var lower = _dragIndex > 0 ? SegTimelineEnd(project.Segments[_dragIndex - 1], gif) : 0;
        double? nextStart = _dragIndex < project.Segments.Count - 1
            ? SegTimelineStart(project.Segments[_dragIndex + 1], gif)
            : null;

        // snapping: candidato mais proximo dentro do raio (borda esquerda no
        // fim do vizinho anterior/playhead; borda direita no inicio do
        // proximo/playhead)
        var snapRadius = SnapPixels * _gestureUnitsPerPixel;
        var candidates = new List<double> { lower, _playheadUnits, _playheadUnits - length };
        if (nextStart is { } next)
            candidates.Add(next - length);
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

        // clamp nos vizinhos (RF-F5.19), o mesmo do Core; ultimo segmento e
        // livre a direita (estende a timeline)
        var upper = nextStart is { } n ? Math.Max(lower, n - length) : double.MaxValue;
        target = Math.Clamp(target, lower, upper);
        if (gif)
            target = Math.Clamp(Math.Round(target), lower, upper);

        _pendingMoveStartUnits = target;
        InvalidateStaticDrawing(); // blocos e gaps mudam de lugar no preview
    }

    /// <summary>
    /// Trim com clamp aos limites do source, aos vizinhos na timeline
    /// (RF-F5.19) e snapping (RF-F5.06).
    /// </summary>
    private void UpdateTrimPreview(double x)
    {
        var project = _project!;
        var seg = project.Segments[_dragIndex];
        var gif = IsGif;
        var deltaUnits = (x - _dragStart.X) * _gestureUnitsPerPixel;

        var srcStart = SegSourceStart(seg, gif);
        var srcEnd = SegSourceEnd(seg, gif);
        var tlStart = SegTimelineStart(seg, gif);
        var minLength = gif ? 1 : MinVideoTrimSeconds;

        if (_mode == DragMode.TrimLeft)
        {
            var next = srcStart + deltaUnits;
            next = gif ? Math.Round(next) : Math.Round(next, 1); // snap frame / 0,1 s
            // RF-F5.19: a borda esquerda (que desloca TimelineStart junto com
            // o source start) nao pode invadir o vizinho anterior
            var prevEnd = _dragIndex > 0 ? SegTimelineEnd(project.Segments[_dragIndex - 1], gif) : 0;
            var lowerBound = Math.Min(Math.Max(0, srcStart + (prevEnd - tlStart)), srcEnd - minLength);
            _pendingTrimStartUnits = Math.Clamp(next, lowerBound, srcEnd - minLength);
            _pendingTrimEndUnits = srcEnd;
        }
        else
        {
            var next = srcEnd + deltaUnits;
            next = gif ? Math.Round(next) : Math.Round(next, 1);
            // RF-F5.19: a borda direita nao pode invadir o vizinho seguinte
            var upperBound = _dragIndex < project.Segments.Count - 1
                ? Math.Min(SourceTotalUnits, srcStart + (SegTimelineStart(project.Segments[_dragIndex + 1], gif) - tlStart))
                : SourceTotalUnits;
            upperBound = Math.Max(upperBound, srcStart + minLength);
            _pendingTrimStartUnits = srcStart;
            _pendingTrimEndUnits = Math.Clamp(next, srcStart + minLength, upperBound);
        }
        InvalidateStaticDrawing(); // segmentos mudam de largura no preview do trim
    }

    private void UpdateCursor(Point pos)
    {
        if (pos.Y <= RulerHeight)
        {
            Cursor = Cursors.Arrow;
            return;
        }
        var blocks = ComputeBlocks();
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

        // regua + segmentos saem do cache; so o playhead e redesenhado por tick
        if (_staticDrawing is null)
        {
            var group = new DrawingGroup();
            using (var ctx = group.Open())
            {
                DrawRuler(ctx);
                DrawSegments(ctx);
            }
            _staticDrawing = group;
        }
        dc.DrawDrawing(_staticDrawing);
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

    private void DrawSegments(DrawingContext dc)
    {
        var project = _project!;
        var blocks = ComputeBlocks();
        var accent = FindBrush("AccentFillColorDefaultBrush", Color.FromRgb(0x00, 0x78, 0xD4));
        var fill = FindBrush("ControlFillColorDefaultBrush", Color.FromArgb(0x30, 0x80, 0x80, 0x80));
        var borderPen = new Pen(FindBrush("ControlElevationBorderBrush", Color.FromArgb(0x60, 0x80, 0x80, 0x80)), 1);
        borderPen.Freeze();
        var selectedPen = new Pen(accent, 2);
        selectedPen.Freeze();
        var textBrush = FindBrush("TextFillColorPrimaryBrush", Colors.White);

        var top = RulerHeight + 6;
        var height = Math.Max(10, ActualHeight - top - 6);

        DrawGaps(dc, blocks, top, height); // RF-F5.17: areas de gap

        for (var i = 0; i < blocks.Count; i++)
        {
            var isDragged = _mode == DragMode.Move && i == _dragIndex;
            if (isDragged)
                dc.PushOpacity(0.75); // preview do move: bloco levemente translucido
            DrawSegmentBlock(dc, project.Segments[i], blocks[i], top, height,
                fill, project.Segments[i].Id == _selectedId ? selectedPen : borderPen, textBrush);
            if (isDragged)
                dc.Pop();
        }
    }

    /// <summary>
    /// Gaps da timeline editada (RF-F5.17): trechos da faixa de segmentos nao
    /// cobertos por nenhum bloco recebem fundo mais escuro + hachura diagonal
    /// sutil (renderizam preto no preview/export, RF-F5.20). Nao existe gap
    /// depois do ultimo segmento por invariante do Core.
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
        }
        else if (!IsGif)
        {
            // LIMITACAO v1: sem thumbnails de MP4 (MF Source Reader e upgrade
            // futuro, RF-F5.03) - blocos mostram o intervalo de tempo do source
            var gif = IsGif;
            var start = _mode is DragMode.TrimLeft or DragMode.TrimRight && _project!.Segments.IndexOf(seg) == _dragIndex
                ? _pendingTrimStartUnits : SegSourceStart(seg, gif);
            var end = _mode is DragMode.TrimLeft or DragMode.TrimRight && _project!.Segments.IndexOf(seg) == _dragIndex
                ? _pendingTrimEndUnits : SegSourceEnd(seg, gif);
            var label = $"{FormatTime(start)} - {FormatTime(end)}";
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
    }

    /// <summary>Strip de miniaturas dentro do bloco (RF-F5.03, frames do cache GIF).</summary>
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

file static class SegmentListExtensions
{
    public static int IndexOf(this IReadOnlyList<TimelineSegment> segments, TimelineSegment segment)
    {
        for (var i = 0; i < segments.Count; i++)
        {
            if (ReferenceEquals(segments[i], segment))
                return i;
        }
        return -1;
    }
}
