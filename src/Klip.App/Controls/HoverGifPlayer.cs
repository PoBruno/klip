using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Klip.App.Controls;

/// <summary>
/// RF-F4.05: preview de GIF no card do historico. Mostra a thumbnail estatica
/// (primeiro frame) e anima SOMENTE enquanto o mouse esta sobre o card:
/// MouseEnter dispara o decode (in-box, GifBitmapDecoder) numa thread de fundo
/// e um DispatcherTimer avanca os frames respeitando os delays; MouseLeave
/// volta a estatica. No maximo UMA animacao roda por vez (a ultima com hover
/// vence). Guardas: arquivo &gt; 20 MB ou &gt; 300 frames nao anima; no caminho
/// ESTATICO, arquivo &gt; 50 MB nem decodifica (fica so o fundo do card).
///
/// A thumbnail estatica tambem decodifica FORA da UI thread (bug do decode
/// sincrono no bind/reciclagem durante o scroll): placeholder imediato
/// (fundo), Task.Run com token por troca de Source e aplicacao no retorno.
///
/// Ciclo de vida com virtualizacao (VirtualizationMode=Recycling):
/// - container reciclado: o DataContext troca e o binding de SourcePath muda
///   -&gt; para a animacao, solta os frames e mostra a estatica do novo item;
/// - container removido da arvore: Unloaded -&gt; para e solta tudo.
/// Os frames decodificados ficam retidos apenas no player ativo; quando outro
/// card assume o hover, o anterior libera a memoria - e o proprio ativo
/// libera apos ~10 s sem hover (debounce), para nao reter frames com o
/// flyout fechado (bug do _active estatico).
/// </summary>
public sealed class HoverGifPlayer : System.Windows.Controls.Image
{
    private const long MaxAnimatedFileBytes = 20L * 1024 * 1024; // RF-F4.05: guarda de tamanho
    private const long MaxStaticFileBytes = 50L * 1024 * 1024;   // guarda do caminho estatico
    private const int MaxAnimatedFrames = 300;
    private const int DecodeWidth = 256; // frames do hover decodificados pequenos (card mostra ~96px)
    private static readonly TimeSpan ReleaseDebounce = TimeSpan.FromSeconds(10);

    // no maximo uma animacao simultanea: quem comeca derruba o anterior
    private static HoverGifPlayer? _active;

    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(
        nameof(SourcePath), typeof(string), typeof(HoverGifPlayer),
        new PropertyMetadata(null, static (d, e) => ((HoverGifPlayer)d).OnSourcePathChanged((string?)e.NewValue)));

    /// <summary>Caminho absoluto do .gif no disco.</summary>
    public string? SourcePath
    {
        get => (string?)GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    private sealed record HoverFrame(BitmapSource Bitmap, int DelayMs);

    private FrameworkElement? _host;   // o card (ListBoxItem) que recebe o hover
    private ImageSource? _staticThumb; // primeiro frame, sempre visivel fora do hover
    private List<HoverFrame>? _frames; // decodificados sob demanda no primeiro hover
    private DispatcherTimer? _timer;
    private DispatcherTimer? _releaseTimer; // debounce: solta os frames apos MouseLeave
    private int _index;
    private int _hoverToken;  // invalida decodes assincronos de hovers antigos
    private int _staticToken; // invalida decodes assincronos da thumbnail estatica

    public HoverGifPlayer()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnSourcePathChanged(string? path)
    {
        // troca de item (inclui reciclagem de container): para e solta o antigo
        Release();
        var token = ++_staticToken;
        _staticThumb = null;
        Source = null; // placeholder imediato: fundo do card ate o decode chegar

        if (path is null)
            return;

        // cache LRU quente: aplica direto, sem ida a thread de fundo
        if (PathToThumbnailConverter.TryGetCached(path, out var cached))
        {
            _staticThumb = cached;
            Source = cached;
            return;
        }

        _ = LoadStaticAsync(path, token);
    }

    /// <summary>
    /// Decode da thumbnail estatica FORA da UI thread (mesmo padrao do hover):
    /// token por troca de Source; guarda de tamanho (&gt; 50 MB fica so o fundo).
    /// </summary>
    private async Task LoadStaticAsync(string path, int token)
    {
        ImageSource? thumb;
        try
        {
            thumb = await Task.Run(() =>
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaxStaticFileBytes)
                    return null;
                return (ImageSource?)PathToThumbnailConverter.DecodeAndCache(path, DecodeWidth);
            });
        }
        catch (Exception)
        {
            return; // arquivo apagado/corrompido: mantem o placeholder
        }

        // item trocou (recycle) enquanto decodificava: descarta
        if (token != _staticToken || thumb is null)
            return;
        _staticThumb = thumb;
        if (_timer?.IsEnabled != true)
            Source = thumb;
    }

    // ----- Hover no card -----

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_host is not null)
            return;
        // o hover vale para o card inteiro, nao so para a area da imagem
        _host = FindListBoxItem() ?? Parent as FrameworkElement ?? this;
        _host.MouseEnter += OnHostMouseEnter;
        _host.MouseLeave += OnHostMouseLeave;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // container saiu da arvore (limpeza da virtualizacao): solta os recursos
        Release();
        if (_host is not null)
        {
            _host.MouseEnter -= OnHostMouseEnter;
            _host.MouseLeave -= OnHostMouseLeave;
            _host = null;
        }
    }

    private async void OnHostMouseEnter(object sender, MouseEventArgs e)
    {
        _releaseTimer?.Stop(); // re-hover dentro do debounce mantem os frames
        var path = SourcePath;
        if (path is null || _timer?.IsEnabled == true)
            return;

        var token = ++_hoverToken;

        // ultima com hover vence: derruba a animacao (e os frames) do player anterior
        if (_active != this)
        {
            _active?.Release();
            _active = this;
        }

        if (_frames is null)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaxAnimatedFileBytes)
                    return; // guarda de tamanho: fica so na thumbnail estatica
            }
            catch (IOException)
            {
                return;
            }

            List<HoverFrame>? frames;
            try
            {
                // decode fora da UI thread; bitmaps saem Freeze() e cruzam threads
                frames = await Task.Run(() => Decode(path));
            }
            catch (Exception)
            {
                return; // arquivo corrompido/apagado: mantem a estatica
            }

            // o mouse ja saiu, o item trocou ou o gif estourou a guarda de frames
            if (token != _hoverToken || frames is null ||
                !string.Equals(path, SourcePath, StringComparison.Ordinal))
                return;
            _frames = frames;
        }

        if (_frames.Count <= 1)
            return;

        _timer ??= CreateTimer();
        _index = 0;
        Source = _frames[0].Bitmap;
        _timer.Interval = TimeSpan.FromMilliseconds(_frames[0].DelayMs);
        _timer.Start();
    }

    private void OnHostMouseLeave(object sender, MouseEventArgs e)
    {
        _hoverToken++; // cancela um decode em andamento
        StopAnimation();
        // bug do _active retido: sem re-hover em ~10 s, solta os frames (o
        // flyout pode ter fechado; o Unloaded nao dispara em janela oculta)
        _releaseTimer ??= CreateReleaseTimer();
        _releaseTimer.Stop();
        _releaseTimer.Start();
    }

    private DispatcherTimer CreateReleaseTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ReleaseDebounce };
        timer.Tick += (_, _) => Release();
        return timer;
    }

    private DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background);
        timer.Tick += (_, _) =>
        {
            if (_frames is null || _frames.Count == 0)
            {
                StopAnimation();
                return;
            }
            _index = (_index + 1) % _frames.Count;
            Source = _frames[_index].Bitmap;
            _timer!.Interval = TimeSpan.FromMilliseconds(_frames[_index].DelayMs);
        };
        return timer;
    }

    /// <summary>Para a animacao e volta a estatica; mantem os frames para re-hover.</summary>
    private void StopAnimation()
    {
        _timer?.Stop();
        _index = 0;
        Source = _staticThumb;
    }

    /// <summary>Para e libera os frames decodificados (recycle/unload/outro hover/debounce).</summary>
    private void Release()
    {
        _hoverToken++;
        _releaseTimer?.Stop();
        StopAnimation();
        _frames = null;
        if (_active == this)
            _active = null; // garante que o estatico nao retem este player
    }

    private ListBoxItem? FindListBoxItem()
    {
        DependencyObject? current = this;
        while (current is not null)
        {
            if (current is ListBoxItem item)
                return item;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    // ----- Decode -----

    /// <summary>
    /// Decodifica e compoe os frames do GIF (delta + offset + disposal) ja
    /// reduzidos para ~256 px. A composicao segue o mesmo esquema do
    /// GifFrameCache do editor de midia (RF-F5.02), mas aqui cada frame vira um
    /// bitmap PEQUENO e congelado: so o canvas de trabalho fica em tamanho
    /// cheio, entao o pico de memoria nao escala com o numero de frames.
    /// Retorna null quando o GIF estoura a guarda de 300 frames.
    /// </summary>
    private static List<HoverFrame>? Decode(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0 || decoder.Frames.Count > MaxAnimatedFrames)
            return null;

        var width = GetMetaUInt16(decoder.Metadata, "/logscrdesc/Width") ?? decoder.Frames[0].PixelWidth;
        var height = GetMetaUInt16(decoder.Metadata, "/logscrdesc/Height") ?? decoder.Frames[0].PixelHeight;
        if (width <= 0 || height <= 0)
            return null;
        var scale = Math.Min(1.0, (double)DecodeWidth / width);

        var canvas = new byte[width * height * 4]; // comeca transparente
        var frames = new List<HoverFrame>(decoder.Frames.Count);

        foreach (var frame in decoder.Frames)
        {
            var meta = frame.Metadata as BitmapMetadata;
            var left = GetMetaUInt16(meta, "/imgdesc/Left") ?? 0;
            var top = GetMetaUInt16(meta, "/imgdesc/Top") ?? 0;
            var delayCs = GetMetaUInt16(meta, "/grctlext/Delay") ?? 10;
            var disposal = GetMetaByte(meta, "/grctlext/Disposal") ?? 0;
            // convencao dos players: delay 0/1 cs vira 100 ms
            var delayMs = delayCs <= 1 ? 100 : delayCs * 10;

            var patchW = frame.PixelWidth;
            var patchH = frame.PixelHeight;
            var patch = new byte[patchW * patchH * 4];
            var converted = frame.Format == PixelFormats.Bgra32
                ? (BitmapSource)frame
                : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            converted.CopyPixels(patch, patchW * 4, 0);

            // disposal 3 (restaurar anterior): guarda o estado antes do blit
            byte[]? before = disposal == 3 ? (byte[])canvas.Clone() : null;

            BlitOver(canvas, width, height, patch, patchW, patchH, left, top);

            var full = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, canvas, width * 4);
            BitmapSource small = scale < 1.0
                ? new TransformedBitmap(full, new ScaleTransform(scale, scale))
                : full;
            var cached = new CachedBitmap(small, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            cached.Freeze();
            frames.Add(new HoverFrame(cached, delayMs));

            // prepara o canvas para o PROXIMO frame conforme o disposal
            switch (disposal)
            {
                case 2: // restaura a area do frame para o fundo (transparente)
                    ClearRect(canvas, width, height, left, top, patchW, patchH);
                    break;
                case 3 when before is not null:
                    Array.Copy(before, canvas, canvas.Length);
                    break;
                // 0/1: mantem o composto
            }
        }

        return frames;
    }

    /// <summary>Alpha-over simples: pixels transparentes do patch preservam o canvas.</summary>
    private static void BlitOver(byte[] canvas, int canvasW, int canvasH,
        byte[] patch, int patchW, int patchH, int left, int top)
    {
        for (var y = 0; y < patchH; y++)
        {
            var cy = top + y;
            if (cy < 0 || cy >= canvasH)
                continue;
            for (var x = 0; x < patchW; x++)
            {
                var cx = left + x;
                if (cx < 0 || cx >= canvasW)
                    continue;
                var src = (y * patchW + x) * 4;
                if (patch[src + 3] == 0)
                    continue; // GIF: alpha e binario (0 ou 255)
                var dst = (cy * canvasW + cx) * 4;
                canvas[dst] = patch[src];
                canvas[dst + 1] = patch[src + 1];
                canvas[dst + 2] = patch[src + 2];
                canvas[dst + 3] = 255;
            }
        }
    }

    private static void ClearRect(byte[] canvas, int canvasW, int canvasH,
        int left, int top, int rectW, int rectH)
    {
        for (var y = 0; y < rectH; y++)
        {
            var cy = top + y;
            if (cy < 0 || cy >= canvasH)
                continue;
            var start = (cy * canvasW + Math.Max(0, left)) * 4;
            var count = (Math.Min(canvasW, left + rectW) - Math.Max(0, left)) * 4;
            if (count > 0)
                Array.Clear(canvas, start, count);
        }
    }

    private static int? GetMetaUInt16(BitmapMetadata? metadata, string query)
        => GetMeta(metadata, query) is ushort value ? value : null;

    private static int? GetMetaByte(BitmapMetadata? metadata, string query)
        => GetMeta(metadata, query) is byte value ? value : null;

    private static object? GetMeta(BitmapMetadata? metadata, string query)
    {
        if (metadata is null)
            return null;
        try
        {
            return metadata.ContainsQuery(query) ? metadata.GetQuery(query) : null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
