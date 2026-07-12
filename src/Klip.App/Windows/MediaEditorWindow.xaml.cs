using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Klip.App.Localization;
using Klip.App.Services;
using Klip.Core.Clipboard;
using Klip.Core.Media.Editing;
using Klip.Core.Media.Gif;
using Klip.Core.Recording;
using Klip.Core.Settings;
using Wpf.Ui.Appearance;

namespace Klip.App.Windows;

/// <summary>
/// Editor de midia com timeline (spec F5): abre uma gravacao GIF ou MP4,
/// permite cortes multiplos, reposicionamento livre com gaps (RF-F5.17..19),
/// trim, audio (MP4) e exporta. GIF usa player proprio sobre o cache de
/// frames (RF-F5.02); MP4 usa MediaElement atras de IVideoPlayer (limitacao
/// v1 documentada em Q-F5.1). O playback percorre a timeline EDITADA via
/// TryMapToSource/TryMapFrameToSource: dentro de segmentos o relogio deriva
/// da posicao do player; dentro de gaps um relogio proprio (Stopwatch)
/// avanca o playhead com o preview preto (RF-F5.20).
/// </summary>
public partial class MediaEditorWindow
{
    private const double VideoStepSeconds = 1.0 / 30; // step aproximado (Q-F5.1)

    /// <summary>
    /// FPS do filler preto dos gaps no export MP4 (RF-F5.20). O gravador do
    /// Klip grava MP4 a 30 fps fixo (RecordingController); para arquivos
    /// externos e um fallback documentado - o MediaElement nao expoe o fps do
    /// stream e o concat do ffmpeg tolera fps distinto entre itens (parse via
    /// ffprobe fica como upgrade futuro).
    /// </summary>
    private const int GapFillerFallbackFps = 30;

    private readonly SettingsService _settings;
    private readonly ClipboardIngestService _ingest;
    private readonly MediaEditorExportService _export;

    private MediaEditorSession? _session;
    private string? _filePath;

    // ----- GIF -----
    private Media.GifFrameCache? _gifCache;
    private IReadOnlyList<GifSequenceBuilder.Entry>? _gifSequence;
    private readonly DispatcherTimer _gifTimer; // one-shot encadeado (delay por frame)
    private int _gifSequenceIndex;
    private Image? _gifImage;
    // bitmap REUTILIZADO do preview: WritePixels por frame, zero alocacao
    // de playback (correcao do bug de OOM do GifFrameCache)
    private System.Windows.Media.Imaging.WriteableBitmap? _gifPreviewBitmap;

    // ----- MP4 -----
    private Media.IVideoPlayer? _player;
    private readonly DispatcherTimer _mp4Timer;
    private int _mp4SegmentIndex;
    private bool _mediaReady;
    // dimensoes reais do source (MediaOpened) - filler preto dos gaps (RF-F5.20)
    private int _videoNaturalWidth;
    private int _videoNaturalHeight;

    // RF-F5.20: dentro de um gap o player fica pausado atras do overlay preto
    // e o playhead avanca por um relogio proprio da timeline EDITADA
    private bool _mp4InGap;
    private double _mp4GapStartUnits;
    private readonly System.Diagnostics.Stopwatch _mp4GapWatch = new();

    // RF-F5.09: estado de audio fora da pilha de undo (aplicado so no export);
    // o preview aproxima com MediaElement.Volume (0..1)
    private int _audioVolumePercent = 100;
    private bool _audioMuted;

    private double _playheadUnits; // timeline EDITADA: segundos (MP4) / frame (GIF)
    private bool _isPlaying;

    // drag do playhead em andamento (anti seek storm): durante o drag os
    // ticks de playback ficam parados e os seeks de MP4 sao COALESCIDOS via
    // IVideoPlayer.RequestSeek; o mouse-up faz um seek imediato final
    private bool _isScrubbing;

    /// <summary>Exportacao concluida; o App mostra o toast (RF-F5.15).</summary>
    public event Action<string>? ExportCompleted;

    /// <summary>Arquivo aberto/trocado (drag-and-drop, RF-F5.16); o App atualiza o mapa.</summary>
    public event Action<MediaEditorWindow, string>? FileOpened;

    public string? FilePath => _filePath;

    public MediaEditorWindow(SettingsService settings, ClipboardIngestService ingest)
    {
        _settings = settings;
        _ingest = ingest;
        _export = new MediaEditorExportService(settings);

        InitializeComponent();
        SystemThemeWatcher.Watch(this);

        _gifTimer = new DispatcherTimer(DispatcherPriority.Render);
        _gifTimer.Tick += (_, _) => GifTick();
        _mp4Timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _mp4Timer.Tick += (_, _) => Mp4Tick();

        WireToolbar();
        WireTimeline();
        WireAudioPanel();

        PreviewKeyDown += OnPreviewKeyDown;
        DragOver += OnFileDragOver;
        Drop += OnFileDrop;
        Closed += (_, _) => Teardown();

        UpdateUndoButtons();
    }

    // ----- abertura (RF-F5.16) -----

    /// <summary>
    /// Abre um .gif ou .mp4. Extensao nao suportada mostra uma mensagem
    /// localizada e fecha a janela graciosamente - a validacao mora AQUI para
    /// o comportamento ficar seguro independentemente do chamador (bug da
    /// janela vazia via OpenMediaEditor).
    /// </summary>
    public void OpenFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".gif" or ".mp4"))
        {
            var message = string.Format(Loc.MediaUnsupportedFile, Path.GetFileName(path));
            StatusText.Text = message;
            MessageBox.Show(message, Loc.MediaEditorTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            // Close adiado: o chamador pode ainda chamar Show() nesta janela
            Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.Background);
            return;
        }

        StopPlayback();
        Teardown();
        _filePath = path;
        _session = null;
        _playheadUnits = 0;
        StatusText.Text = Path.GetFileName(path);

        if (ext == ".gif")
            _ = OpenGifAsync(path);
        else
            OpenVideo(path);

        FileOpened?.Invoke(this, path);
    }

    private async Task OpenGifAsync(string path)
    {
        try
        {
            // decodifica TODOS os frames uma vez, fora da UI thread (RF-F5.02)
            var cache = await Task.Run(() => Media.GifFrameCache.Load(path));
            if (_filePath != path)
                return; // outro arquivo foi aberto enquanto decodificava

            _gifCache = cache;
            // um unico WriteableBitmap reutilizado para todos os frames
            _gifPreviewBitmap = cache.CreatePreviewBitmap();
            _gifImage = new Image
            {
                Stretch = System.Windows.Media.Stretch.Uniform,
                Source = _gifPreviewBitmap,
            };
            PreviewHost.Content = _gifImage;

            var project = MediaEditProject.CreateGif(path, cache.Frames.Count);
            AttachSession(new MediaEditorSession(project));
            Timeline.SetGifThumbnails(cache.Frames.Select(f => f.Thumbnail).ToArray());
            ConfigureUiForKind(MediaKind.Gif, hasAudio: false);
            ShowGifFrameAtPlayhead();
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("MediaEditorOpenGif", ex);
            StatusText.Text = string.Format(Loc.MediaOpenFailed, ex.Message);
        }
    }

    private void OpenVideo(string path)
    {
        var player = new Media.MediaElementVideoPlayer();
        _player = player;
        _mediaReady = false;
        PreviewHost.Content = player.Visual;

        player.MediaOpened += () =>
        {
            if (_mediaReady || _filePath != path)
                return; // MediaOpened pode disparar de novo em seeks
            _mediaReady = true;

            var duration = player.Duration;
            if (duration <= TimeSpan.Zero)
                duration = TimeSpan.FromSeconds(1);

            // RF-F5.20: dimensoes REAIS do source para o filler preto dos
            // gaps (o default 1920x1080 quebraria o concat se nao batesse)
            _videoNaturalWidth = player.NaturalVideoWidth;
            _videoNaturalHeight = player.NaturalVideoHeight;

            // v1: enumeracao de trilhas limitada ao MediaElement.HasAudio -
            // uma trilha "Audio" quando presente (RF-F5.09)
            var project = MediaEditProject.CreateVideo(path, duration, player.HasAudio ? 1 : 0);
            AttachSession(new MediaEditorSession(project));
            ConfigureUiForKind(MediaKind.Video, player.HasAudio);
            ApplyPreviewVolume();
        };
        player.MediaFailed += ex =>
        {
            StartupLog.WriteException("MediaEditorOpenVideo", ex);
            StatusText.Text = string.Format(Loc.MediaOpenFailed, ex.Message);
        };
        player.Open(path);
    }

    private void AttachSession(MediaEditorSession session)
    {
        _session = session;
        session.Changed += OnSessionChanged;
        OnSessionChanged();
    }

    private void ConfigureUiForKind(MediaKind kind, bool hasAudio)
    {
        var gif = kind == MediaKind.Gif;
        ReduceFpsButton.Visibility = gif ? Visibility.Visible : Visibility.Collapsed;
        ResizeButton.Visibility = gif ? Visibility.Visible : Visibility.Collapsed;
        ExportMp4Button.Visibility = gif ? Visibility.Collapsed : Visibility.Visible;
        AudioPanel.Visibility = !gif && hasAudio ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Teardown()
    {
        _gifTimer.Stop();
        _mp4Timer.Stop();
        _isPlaying = false;
        _isScrubbing = false;
        if (_session is { } session)
            session.Changed -= OnSessionChanged;
        _player?.Close();
        _player = null;
        _gifCache = null;
        _gifSequence = null;
        _gifImage = null;
        _gifPreviewBitmap = null;
        _videoNaturalWidth = 0;
        _videoNaturalHeight = 0;
        _mp4InGap = false;
        _mp4GapWatch.Reset();
        GapOverlay.Visibility = Visibility.Collapsed;
    }

    // ----- sessao / timeline -----

    private void OnSessionChanged()
    {
        if (_session is not { } session)
            return;

        _gifSequence = null; // recalculada sob demanda (segmentos ou FPS mudaram)
        Timeline.SetProject(session.Project);

        var total = TotalUnits;
        if (_playheadUnits > total)
            _playheadUnits = Math.Max(0, IsGif ? total - 1 : total);
        Timeline.SetPlayhead(_playheadUnits);

        // pausado: re-resolve o preview no playhead - o slot pode ter virado
        // gap (ou deixado de ser) apos remove/move/undo (RF-F5.18/20)
        if (!_isPlaying)
            SeekEdited(_playheadUnits);

        UpdateUndoButtons();
        UpdatePositionLabel();
        UpdateGifStatus();
    }

    private bool IsGif => _session?.Project.Kind == MediaKind.Gif;

    private double TotalUnits => _session is not { } session
        ? 0
        : session.Project.Kind == MediaKind.Gif
            ? session.Project.EditedFrameCount
            : session.Project.EditedDuration.TotalSeconds;

    private void WireTimeline()
    {
        Timeline.PlayheadScrubbed += OnPlayheadScrubbed;
        Timeline.PlayheadScrubEnded += OnPlayheadScrubEnded;
        Timeline.SegmentSelected += _ => UpdateUndoButtons();
        Timeline.SegmentMoveToRequested += (id, units) =>
        {
            // RF-F5.17: reposicionamento livre na timeline editada (o Core
            // clampa nos vizinhos, RF-F5.19); 1 snapshot de undo por gesto
            if (_session is { } session)
                session.Apply(IsGif
                    ? session.Project.MoveSegmentToFrame(id, (int)Math.Round(units))
                    : session.Project.MoveSegmentTo(id, TimeSpan.FromSeconds(units)));
        };
        Timeline.SegmentContextMenuRequested += ShowSegmentContextMenu;
        Timeline.SegmentTrimRequested += (id, start, end) =>
        {
            if (_session is not { } session)
                return;
            try
            {
                session.Apply(IsGif
                    ? session.Project.TrimSegmentFrames(id, (int)start, (int)end)
                    : session.Project.TrimSegment(id, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end)));
            }
            catch (ArgumentException)
            {
                // trim degenerado (clamp produziu faixa vazia): ignora o gesto
            }
        };
    }

    /// <summary>
    /// Menu de contexto do segmento: excluir deixando gap (RF-F5.18), excluir
    /// e fechar (ripple) e a reordenacao por indice (RF-F5.07) que saiu do
    /// drag - MoveSegment re-empacota a timeline contigua (gaps descartados,
    /// semantica documentada no Core).
    /// </summary>
    private void ShowSegmentContextMenu(Guid id)
    {
        if (_session is not { } session)
            return;
        var segments = session.Project.Segments;
        var index = -1;
        for (var i = 0; i < segments.Count; i++)
        {
            if (segments[i].Id == id)
            {
                index = i;
                break;
            }
        }
        if (index < 0)
            return;

        var canRemove = segments.Count > 1;
        var menu = new ContextMenu { PlacementTarget = Timeline };
        AddCommandItem(menu, Loc.MediaRemoveSegment, canRemove, () => RemoveSegmentById(id, ripple: false));
        AddCommandItem(menu, Loc.MediaRemoveSegmentRipple, canRemove, () => RemoveSegmentById(id, ripple: true));
        menu.Items.Add(new Separator());
        AddCommandItem(menu, Loc.MediaSwapPrevious, index > 0,
            () => { _session?.Apply(_session.Project.MoveSegment(id, index - 1)); });
        AddCommandItem(menu, Loc.MediaSwapNext, index < segments.Count - 1,
            () => { _session?.Apply(_session.Project.MoveSegment(id, index + 1)); });
        menu.IsOpen = true;
    }

    private static void AddCommandItem(ContextMenu menu, string header, bool enabled, Action onClick)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => onClick();
        menu.Items.Add(item);
    }

    // ----- toolbar -----

    private void WireToolbar()
    {
        PlayButton.Click += (_, _) => TogglePlayback();
        StepBackButton.Click += (_, _) => Step(-1);
        StepForwardButton.Click += (_, _) => Step(1);
        SplitButton.Click += (_, _) => SplitAtPlayhead();
        RemoveSegmentButton.Click += (_, _) => RemoveSelectedSegment();
        UndoButton.Click += (_, _) => { _session?.Undo(); };
        RedoButton.Click += (_, _) => { _session?.Redo(); };
        ReduceFpsButton.Click += (_, _) => ShowReduceFpsMenu();
        ResizeButton.Click += (_, _) => ShowResizeMenu();
        ExportGifButton.Click += async (_, _) => await ExportGifAsync();
        ExportMp4Button.Click += async (_, _) => await ExportMp4Async();
    }

    private void UpdateUndoButtons()
    {
        UndoButton.IsEnabled = _session?.CanUndo == true;
        RedoButton.IsEnabled = _session?.CanRedo == true;
        RemoveSegmentButton.IsEnabled =
            Timeline.SelectedSegmentId is not null && _session?.Project.Segments.Count > 1;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_session is null)
            return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Space:
                TogglePlayback();
                e.Handled = true;
                break;
            case Key.S when !ctrl:
                SplitAtPlayhead(); // RF-F5.06
                e.Handled = true;
                break;
            case Key.Delete:
                // RF-F5.18: Del deixa gap; Shift+Del fecha o espaco (ripple)
                RemoveSelectedSegment(ripple: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                e.Handled = true;
                break;
            case Key.Left:
                Step(-1);
                e.Handled = true;
                break;
            case Key.Right:
                Step(1);
                e.Handled = true;
                break;
            case Key.Z when ctrl:
                _session.Undo(); // RF-F5.10
                e.Handled = true;
                break;
            case Key.Y when ctrl:
                _session.Redo();
                e.Handled = true;
                break;
        }
    }

    // ----- operacoes de edicao (RF-F5.06) -----

    private void SplitAtPlayhead()
    {
        if (_session is not { } session)
            return;
        session.Apply(IsGif
            ? session.Project.SplitAtFrame((int)Math.Round(_playheadUnits))
            : session.Project.SplitAt(TimeSpan.FromSeconds(_playheadUnits)));
    }

    /// <summary>
    /// RF-F5.18: por padrao remover DEIXA UM GAP no lugar; ripple = "excluir
    /// e fechar espaco" (comporta como a v1). Ambas viram 1 snapshot de undo.
    /// </summary>
    private void RemoveSelectedSegment(bool ripple = false)
    {
        if (Timeline.SelectedSegmentId is { } id)
            RemoveSegmentById(id, ripple);
    }

    private void RemoveSegmentById(Guid id, bool ripple)
    {
        if (_session is not { } session)
            return;
        if (session.Project.Segments.Count <= 1)
            return; // a timeline mantem pelo menos um segmento
        session.Apply(ripple
            ? session.Project.RemoveSegmentRipple(id)
            : session.Project.RemoveSegment(id));
    }

    // ----- RF-F5.08: reducao de FPS e redimensionamento (GIF) -----

    private void ShowReduceFpsMenu()
    {
        if (_session is not { } session)
            return;
        var menu = new ContextMenu { PlacementTarget = ReduceFpsButton };
        AddCheckItem(menu, Loc.MediaFpsOriginal, session.GifTargetFps is null,
            () => session.SetGifTargetFps(null));
        foreach (var fps in new[] { 10, 15, 20 })
        {
            var value = fps;
            AddCheckItem(menu, $"{fps} fps", session.GifTargetFps == fps,
                () => session.SetGifTargetFps(value));
        }
        menu.IsOpen = true;
    }

    private void ShowResizeMenu()
    {
        if (_session is not { } session)
            return;
        var menu = new ContextMenu { PlacementTarget = ResizeButton };
        foreach (var percent in new[] { 100, 75, 50, 25 })
        {
            var value = percent;
            AddCheckItem(menu, $"{percent}%", session.GifScalePercent == percent,
                () => session.SetGifScalePercent(value));
        }
        menu.IsOpen = true;
    }

    private static void AddCheckItem(ContextMenu menu, string header, bool isChecked, Action onClick)
    {
        var item = new MenuItem { Header = header, IsChecked = isChecked };
        item.Click += (_, _) => onClick();
        menu.Items.Add(item);
    }

    private void UpdateGifStatus()
    {
        if (_session is not { } session || !IsGif)
            return;
        var parts = new List<string> { Path.GetFileName(_filePath ?? "") };
        if (session.GifTargetFps is { } fps)
            parts.Add($"{Loc.MediaReduceFps}: {fps} fps");
        if (session.GifScalePercent != 100)
            parts.Add($"{Loc.MediaResize}: {session.GifScalePercent}%");
        StatusText.Text = string.Join(" · ", parts);
    }

    // ----- playback -----

    private void TogglePlayback()
    {
        if (_isPlaying)
            StopPlayback();
        else
            StartPlayback();
    }

    private void StartPlayback()
    {
        if (_session is not { } session)
            return;
        _isPlaying = true;
        PlayGlyph.Text = "\uE769"; // Pause

        if (IsGif)
        {
            var seq = GetGifSequence();
            if (seq.Count == 0)
            {
                StopPlayback();
                return;
            }
            _gifSequenceIndex = FindGifSequenceIndex(seq, (int)_playheadUnits);
            ShowGifEntry(seq[_gifSequenceIndex]);
            ScheduleGifTick(seq[_gifSequenceIndex].DelayMs);
        }
        else if (_player is { } player)
        {
            if (_playheadUnits >= TotalUnits - 1e-3)
                _playheadUnits = 0; // play no fim recomeca
            var (index, sourceTime, inGap) = LocateVideo(_playheadUnits);
            _mp4SegmentIndex = index;
            if (inGap)
            {
                // RF-F5.20: comecar dentro de um gap - relogio proprio + preto
                EnterGap(_playheadUnits);
            }
            else
            {
                LeaveGap();
                player.Position = sourceTime;
                player.Play();
            }
            _mp4Timer.Start();
        }
    }

    private void StopPlayback()
    {
        _isPlaying = false;
        PlayGlyph.Text = "\uE768"; // Play
        _gifTimer.Stop();
        _mp4Timer.Stop();
        _mp4GapWatch.Reset(); // relogio de gap nao corre pausado (seek storm)
        _player?.Pause();
    }

    private void Step(int direction)
    {
        if (_session is null)
            return;
        StopPlayback();
        var delta = IsGif ? direction : direction * VideoStepSeconds;
        SeekEdited(_playheadUnits + delta);
    }

    /// <summary>
    /// Scrub do playhead (MouseMove da timeline). Correcao do seek storm:
    /// arrastar o playhead com o MP4 pausado setava Position a cada evento,
    /// enfileirando dezenas de decodes assincronos que o MediaElement
    /// processava em serie ("play em camera lenta" pausado, UI travada). No
    /// primeiro evento do drag os ticks de playback param e o player pausa;
    /// os seeks passam a ser coalescidos dentro de SeekEdited.
    /// </summary>
    private void OnPlayheadScrubbed(double units)
    {
        if (!_isScrubbing)
        {
            _isScrubbing = true;
            _gifTimer.Stop();
            _mp4Timer.Stop();
            _player?.Pause();
        }
        SeekEdited(units);
    }

    /// <summary>
    /// Fim do drag do playhead: seek imediato final para a posicao exata
    /// (drena o coalescing) e retomada do playback se estava tocando.
    /// </summary>
    private void OnPlayheadScrubEnded(double units)
    {
        if (!_isScrubbing)
            return;
        _isScrubbing = false;
        SeekEdited(units); // com _isScrubbing=false o seek e imediato e re-toca se preciso
        if (_isPlaying && !IsGif)
            _mp4Timer.Start(); // GIF: SeekEdited ja re-ancorou e agendou o tick
    }

    /// <summary>Seek em unidades da timeline EDITADA (scrub, step, teclas).</summary>
    private void SeekEdited(double units)
    {
        if (_session is not { } session)
            return;
        var max = IsGif ? Math.Max(0, TotalUnits - 1) : TotalUnits;
        _playheadUnits = Math.Clamp(IsGif ? Math.Floor(units) : units, 0, max);
        Timeline.SetPlayhead(_playheadUnits);
        UpdatePositionLabel();

        if (IsGif)
        {
            if (_isPlaying && !_isScrubbing)
            {
                // re-ancora a sequencia no novo ponto
                var seq = GetGifSequence();
                _gifTimer.Stop();
                _gifSequenceIndex = FindGifSequenceIndex(seq, (int)_playheadUnits);
                if (seq.Count > 0)
                {
                    ShowGifEntry(seq[_gifSequenceIndex]);
                    ScheduleGifTick(seq[_gifSequenceIndex].DelayMs);
                }
            }
            else
            {
                // pausado OU arrastando o playhead: so troca o frame exibido
                // (WritePixels no bitmap reutilizado - barato por MouseMove)
                ShowGifFrameAtPlayhead();
            }
        }
        else if (_player is { } player)
        {
            var (index, sourceTime, inGap) = LocateVideo(_playheadUnits);
            _mp4SegmentIndex = index;
            if (inGap)
            {
                // RF-F5.20: gap = preto; pausa o player atras do overlay (se
                // estiver tocando, o relogio do gap assume no proximo tick)
                EnterGap(_playheadUnits);
            }
            else
            {
                LeaveGap();
                if (_isScrubbing)
                {
                    // drag do playhead: seek COALESCIDO (max 1 real por ~90 ms,
                    // sempre a ultima posicao) - nunca Position direto por
                    // MouseMove (seek storm); tambem nao ha Play aqui, o drag
                    // roda com o player pausado e retoma no mouse-up
                    player.RequestSeek(sourceTime);
                }
                else
                {
                    player.Position = sourceTime;
                    if (_isPlaying)
                        player.Play(); // pode ter sido pausado por um gap anterior
                }
            }
        }
    }

    // ----- player GIF (RF-F5.02) -----

    private IReadOnlyList<GifSequenceBuilder.Entry> GetGifSequence()
    {
        if (_gifSequence is { } cached)
            return cached;
        if (_session is not { } session || _gifCache is not { } cache)
            return [];
        // preview e export compartilham a MESMA sequencia (CA-F5.4)
        _gifSequence = GifSequenceBuilder.Build(session.Project, cache.Delays, session.GifTargetFps);
        return _gifSequence;
    }

    private static int FindGifSequenceIndex(IReadOnlyList<GifSequenceBuilder.Entry> seq, int editedFrame)
    {
        for (var i = 0; i < seq.Count; i++)
        {
            if (seq[i].EditedFrame >= editedFrame)
                return i;
        }
        return 0;
    }

    private void GifTick()
    {
        _gifTimer.Stop();
        if (!_isPlaying)
            return;
        var seq = GetGifSequence();
        if (seq.Count == 0)
        {
            StopPlayback();
            return;
        }
        _gifSequenceIndex = (_gifSequenceIndex + 1) % seq.Count; // loop
        ShowGifEntry(seq[_gifSequenceIndex]);
        ScheduleGifTick(seq[_gifSequenceIndex].DelayMs);
    }

    private void ScheduleGifTick(int delayMs)
    {
        // minimo ~33 ms (30 fps): ticks de 10 ms redesenhavam a timeline a
        // ate 100 Hz sem ganho visual (correcao do re-render integral)
        _gifTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(33, delayMs));
        _gifTimer.Start();
    }

    private void ShowGifEntry(GifSequenceBuilder.Entry entry)
    {
        if (_gifCache is not { } cache || _gifPreviewBitmap is not { } bitmap)
            return;
        if (entry.IsGap)
            cache.CopyBlackTo(bitmap); // RF-F5.20: slot de gap = frame preto
        else
            cache.CopyFrameTo(bitmap, entry.SourceFrame); // WritePixels, sem alocacao
        _playheadUnits = entry.EditedFrame;
        Timeline.SetPlayhead(_playheadUnits);
        UpdatePositionLabel();
    }

    private void ShowGifFrameAtPlayhead()
    {
        if (_session is not { } session || _gifCache is not { } cache || _gifPreviewBitmap is not { } bitmap)
            return;
        // RF-F5.20: TryMapFrameToSource devolve false em gaps - pinta preto
        if (session.Project.TryMapFrameToSource((int)_playheadUnits, out var source))
            cache.CopyFrameTo(bitmap, source);
        else
            cache.CopyBlackTo(bitmap);
        UpdatePositionLabel();
    }

    // ----- player MP4 (timeline editada com gaps, RF-F5.17/20) -----

    /// <summary>
    /// Localiza uma posicao da timeline EDITADA: indice do segmento + tempo
    /// no source, ou InGap=true quando cai num gap (mesma semantica de
    /// TryMapToSource; Index/SourceTime apontam o PROXIMO segmento para a
    /// retomada do playback).
    /// </summary>
    private (int Index, TimeSpan SourceTime, bool InGap) LocateVideo(double editedSeconds)
    {
        var project = _session!.Project;
        var position = TimeSpan.FromSeconds(editedSeconds);
        for (var i = 0; i < project.Segments.Count; i++)
        {
            var seg = project.Segments[i];
            if (position < seg.TimelineStart)
                return (i, seg.SourceStart, true); // gap antes do segmento i
            if (position < seg.TimelineEnd)
                return (i, seg.SourceStart + (position - seg.TimelineStart), false);
        }
        return (project.Segments.Count - 1, project.Segments[^1].SourceEnd, false);
    }

    /// <summary>
    /// RF-F5.20: entra num gap - pausa o player, mostra o overlay preto e
    /// ancora o relogio proprio da timeline editada no ponto de entrada. O
    /// relogio (Stopwatch) SO corre durante o playback real: pausado ou
    /// arrastando o playhead ele fica zerado (parte da correcao do seek
    /// storm - seeks em pausa nao devem deixar relogio correndo).
    /// </summary>
    private void EnterGap(double editedUnits)
    {
        _mp4InGap = true;
        _mp4GapStartUnits = editedUnits;
        if (_isPlaying && !_isScrubbing)
            _mp4GapWatch.Restart();
        else
            _mp4GapWatch.Reset();
        _player?.Pause();
        GapOverlay.Visibility = Visibility.Visible;
    }

    private void LeaveGap()
    {
        _mp4InGap = false;
        _mp4GapWatch.Reset();
        GapOverlay.Visibility = Visibility.Collapsed;
    }

    private void Mp4Tick()
    {
        if (!_isPlaying || _session is not { } session || _player is not { } player)
            return;
        var project = session.Project;

        // RF-F5.20: dentro de um gap o relogio e o da TIMELINE EDITADA
        // (Stopwatch ancorado na entrada), nao o do source - o player fica
        // pausado atras do overlay preto e o playback atravessa o gap
        // respeitando a duracao dele
        if (_mp4InGap)
        {
            _playheadUnits = _mp4GapStartUnits + _mp4GapWatch.Elapsed.TotalSeconds;
            if (_playheadUnits >= TotalUnits - 1e-3)
            {
                // guarda: nunca ha gap no fim por invariante, mas o relogio
                // pode passar do total por drift
                LeaveGap();
                StopPlayback();
                _playheadUnits = TotalUnits;
            }
            else if (project.TryMapToSource(TimeSpan.FromSeconds(_playheadUnits), out var resume))
            {
                // saiu do gap: retoma o player no segmento seguinte
                var (index, _, _) = LocateVideo(_playheadUnits);
                _mp4SegmentIndex = index;
                LeaveGap();
                player.Position = resume;
                player.Play();
            }
            Timeline.SetPlayhead(_playheadUnits);
            UpdatePositionLabel();
            return;
        }

        if (_mp4SegmentIndex >= project.Segments.Count)
            _mp4SegmentIndex = project.Segments.Count - 1;

        var seg = project.Segments[_mp4SegmentIndex];
        var pos = player.Position;

        // cruzou a borda do segmento: proximo contiguo ou gap (RF-F5.17)
        if (pos >= seg.SourceEnd - TimeSpan.FromMilliseconds(30))
        {
            if (_mp4SegmentIndex + 1 < project.Segments.Count)
            {
                var next = project.Segments[_mp4SegmentIndex + 1];
                if (next.TimelineStart > seg.TimelineEnd)
                {
                    // ha um gap entre este segmento e o proximo
                    _playheadUnits = seg.TimelineEnd.TotalSeconds;
                    EnterGap(_playheadUnits);
                    Timeline.SetPlayhead(_playheadUnits);
                    UpdatePositionLabel();
                    return;
                }
                _mp4SegmentIndex++;
                player.Position = next.SourceStart;
                pos = player.Position;
                seg = next;
            }
            else
            {
                StopPlayback();
                _playheadUnits = TotalUnits;
                Timeline.SetPlayhead(_playheadUnits);
                UpdatePositionLabel();
                return;
            }
        }

        // relogio dentro do segmento: posicao explicita na timeline editada
        // (TimelineStart + offset no source, RF-F5.17)
        var within = pos - seg.SourceStart;
        if (within < TimeSpan.Zero)
            within = TimeSpan.Zero;
        _playheadUnits = (seg.TimelineStart + within).TotalSeconds;
        Timeline.SetPlayhead(_playheadUnits);
        UpdatePositionLabel();
    }

    private void UpdatePositionLabel()
    {
        if (_session is null)
        {
            PositionLabel.Text = "";
            return;
        }
        PositionLabel.Text = IsGif
            ? string.Format(Loc.MediaFrameOfTotal, (int)_playheadUnits + 1, (int)TotalUnits)
            : $"{FormatTime(_playheadUnits)} / {FormatTime(TotalUnits)}";
    }

    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss")
            : time.ToString(@"m\:ss\.f");
    }

    // ----- audio (RF-F5.09) -----

    private void WireAudioPanel()
    {
        VolumeSlider.ValueChanged += (_, _) =>
        {
            _audioVolumePercent = (int)VolumeSlider.Value;
            VolumeLabel.Text = $"{_audioVolumePercent}%";
            ApplyPreviewVolume();
        };
        MuteToggleButton.Checked += (_, _) => { _audioMuted = true; ApplyPreviewVolume(); };
        MuteToggleButton.Unchecked += (_, _) => { _audioMuted = false; ApplyPreviewVolume(); };
    }

    /// <summary>Preview aproxima o volume (MediaElement e 0..1; o mix real e no export).</summary>
    private void ApplyPreviewVolume()
    {
        if (_player is { } player)
            player.Volume = _audioMuted ? 0 : Math.Min(1.0, _audioVolumePercent / 100.0);
    }

    /// <summary>Projeto com o estado de audio da UI aplicado (usado no export).</summary>
    private MediaEditProject ProjectForExport()
    {
        var project = _session!.Project;
        if (project.Kind == MediaKind.Video && project.AudioTracks.Count > 0)
            project = project.WithAudioTrack(0, _audioVolumePercent / 100.0, _audioMuted);
        return project;
    }

    // ----- exportacao (RF-F5.11..15) -----

    private async Task ExportGifAsync()
    {
        if (_session is not { } session || _filePath is null)
            return;
        StopPlayback();

        if (!IsGif && !EnsureFfmpeg())
            return; // MP4->GIF precisa do ffmpeg (D-F5.4)

        var output = AskSavePath(Loc.MediaGifFilter, "gif");
        if (output is null)
            return;

        if (IsGif)
        {
            // fonte GIF: encoder proprio, sem FFmpeg (D-F5.4 / CA-F5.6)
            var buildFrames = BuildGifExportFrames();
            if (buildFrames is null)
                return;
            var sourcePath = _filePath;
            await RunExportWithDialogAsync(
                (progress, ct) => Task.Run(() =>
                {
                    // export le do ARQUIVO original em streaming (canvas
                    // reutilizado), nunca do cache de preview - o preview pode
                    // estar em escala reduzida pelo teto de memoria
                    using var reader = new Media.GifFileFrameReader(sourcePath);
                    MediaEditorExportService.ExportGifFrames(buildFrames(reader, progress, ct), output, ct);
                }, ct),
                output);
        }
        else
        {
            var project = _session.Project; // GIF nao leva audio
            // RF-F5.20: dimensoes REAIS do source para o filler preto dos gaps
            var (width, height) = SourceVideoDimensions();
            var gifSettings = new GifFromVideoSettings { Fps = 15, Width = width, Height = height };
            await RunExportWithDialogAsync(
                (progress, ct) => _export.ExportGifFromVideoAsync(project, gifSettings, output, progress, ct),
                output);
        }
    }

    private async Task ExportMp4Async()
    {
        if (_session is null || _filePath is null || IsGif)
            return;
        StopPlayback();

        if (!EnsureFfmpeg())
            return;

        var output = AskSavePath(Loc.MediaMp4Filter, "mp4");
        if (output is null)
            return;

        var project = ProjectForExport();
        // RF-F5.20: filler preto dos gaps com as dimensoes REAIS do source e
        // fps documentado (constante GapFillerFallbackFps)
        var (width, height) = SourceVideoDimensions();
        var exportSettings = new VideoExportSettings
        {
            Width = width,
            Height = height,
            Fps = GapFillerFallbackFps,
        };
        await RunExportWithDialogAsync(
            (progress, ct) => _export.ExportVideoAsync(project, exportSettings, output, progress, ct),
            output);
    }

    /// <summary>
    /// Dimensoes reais do video aberto (NaturalVideoWidth/Height capturados
    /// no MediaOpened), arredondadas para PAR - o encoder H.264 exige
    /// dimensoes pares e o filler dos gaps precisa casar com o source
    /// (RF-F5.20). Fallback 1920x1080 se o MediaElement nao reportou.
    /// </summary>
    private (int Width, int Height) SourceVideoDimensions()
    {
        var width = _videoNaturalWidth > 0 ? _videoNaturalWidth : 1920;
        var height = _videoNaturalHeight > 0 ? _videoNaturalHeight : 1080;
        return (EvenDimension(width), EvenDimension(height));
    }

    private static int EvenDimension(int value) => Math.Max(2, value - (value & 1));

    /// <summary>
    /// Prepara a materializacao lazy da sequencia GIF editada com ReduceFps e
    /// escala aplicados (RF-F5.08): os loaders redecodificam o ARQUIVO
    /// original via <see cref="Media.GifFileFrameReader"/> (streaming, buffer
    /// reutilizado) e observam o CancellationToken (cancelar aborta o encoder
    /// na proxima leitura). Slots de gap (Entry.IsGap, RF-F5.20) viram frames
    /// PRETOS materializados de um unico buffer reutilizado (o encoder so le
    /// os buffers de entrada). Progresso best-effort: o encoder proprio nao
    /// expoe progresso, entao reportamos pelas leituras dos loaders - o
    /// GifEncoder faz 2 passadas (histograma + encode), logo o total esperado
    /// e 2 x frames (gaps incluidos); a escrita final e rapida.
    /// </summary>
    private Func<Media.GifFileFrameReader, IProgress<double>, CancellationToken, IReadOnlyList<GifFrameSource>>?
        BuildGifExportFrames()
    {
        if (_session is not { } session || _gifCache is not { } cache)
            return null;
        var sequence = GetGifSequence();
        if (sequence.Count == 0)
            return null;

        var scale = session.GifScalePercent;
        // dimensoes do ARQUIVO (o preview pode estar em escala reduzida)
        var width = cache.SourceWidth;
        var height = cache.SourceHeight;
        var dstWidth = Math.Max(1, width * scale / 100);
        var dstHeight = Math.Max(1, height * scale / 100);
        var totalReads = sequence.Count * 2L; // 2 passadas do encoder
        var reads = 0L;
        // RF-F5.20: um unico buffer preto compartilhado por todos os gaps
        // (lazy; o encoder e single-thread e nao muta as entradas)
        byte[]? blackFrame = null;

        return (reader, progress, ct) => sequence
            .Select(entry => GifFrameSource.FromLoader(() =>
            {
                ct.ThrowIfCancellationRequested();
                byte[] result;
                if (entry.IsGap)
                {
                    result = blackFrame ??= CreateBlackBgra(dstWidth, dstHeight);
                }
                else
                {
                    var bgra = reader.GetComposedFrame(entry.SourceFrame);
                    result = scale == 100 ? bgra : BgraScaler.Downscale(bgra, width, height, scale).Bgra;
                }
                var done = Interlocked.Increment(ref reads);
                progress.Report(Math.Min(1.0, (double)done / totalReads));
                return result;
            }, dstWidth, dstHeight, entry.DelayMs))
            .ToList();
    }

    /// <summary>Buffer BGRA32 preto opaco (filler de gaps do export GIF, RF-F5.20).</summary>
    private static byte[] CreateBlackBgra(int width, int height)
    {
        var bgra = new byte[width * height * 4];
        for (var i = 3; i < bgra.Length; i += 4)
            bgra[i] = 255;
        return bgra;
    }

    /// <summary>RF-F5.14 (parcial): sem ffmpeg, dialogo com escolha manual + link.</summary>
    private bool EnsureFfmpeg()
    {
        if (_export.LocateFfmpeg() is not null)
            return true;
        var dialog = new MediaEditorFfmpegDialog(this);
        if (dialog.ShowDialog() == true && dialog.SelectedPath is { } path)
        {
            _settings.Update(s => s.FfmpegPath = path);
            return _export.LocateFfmpeg() is not null;
        }
        return false;
    }

    private string? AskSavePath(string filter, string extension)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = filter,
            DefaultExt = extension,
            FileName = Path.GetFileNameWithoutExtension(_filePath) + "-edit." + extension,
            InitialDirectory = RecordingPaths.Resolve(_settings.Current.RecordingsFolder),
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private async Task RunExportWithDialogAsync(
        Func<IProgress<double>, CancellationToken, Task> export, string outputPath)
    {
        var dialog = new MediaEditorExportDialog(this);
        var progress = new Progress<double>(dialog.ReportProgress);
        var task = export(progress, dialog.CancellationToken);
        _ = task.ContinueWith(
            _ => Dispatcher.BeginInvoke(new Action(dialog.CloseCompleted)),
            TaskScheduler.Default);
        dialog.ShowDialog();

        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            return; // CA-F5.7: temp ja apagado pelo servico
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("MediaEditorExport", ex);
            StatusText.Text = string.Format(Loc.MediaExportFailed, ex.Message);
            return;
        }

        OnExportSucceeded(outputPath);
    }

    /// <summary>RF-F5.15: novo item no historico + toast via App.</summary>
    private void OnExportSucceeded(string outputPath)
    {
        try
        {
            _ingest.Ingest(new ClipboardSnapshot
            {
                Files = [outputPath],
                SourceApp = "Klip",
                SourceTitle = Loc.RecordingItemTitle,
                Origin = Core.Storage.ClipboardItemOrigin.Recording,
            });
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("MediaEditorIngest", ex);
        }

        var message = string.Format(Loc.MediaExportDone, Path.GetFileName(outputPath));
        StatusText.Text = message;
        ExportCompleted?.Invoke(message);
    }

    // ----- drag-and-drop (RF-F5.16) -----

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDroppedMediaFile(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        if (GetDroppedMediaFile(e) is { } path)
            OpenFile(path);
    }

    private static string? GetDroppedMediaFile(DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
            return null;
        return files.FirstOrDefault(f =>
            Path.GetExtension(f).ToLowerInvariant() is ".gif" or ".mp4");
    }
}
