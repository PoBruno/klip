using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Klip.App.Media;

/// <summary>Um frame de GIF decodificado e composto, retido como BGRA cru.</summary>
public sealed class GifCachedFrame(byte[] bgra, BitmapSource thumbnail, int delayMs)
{
    /// <summary>
    /// Pixels BGRA32 top-down do frame COMPOSTO, no tamanho do PREVIEW
    /// (pode estar em escala reduzida - ver PreviewDownscaleFactor do cache).
    /// </summary>
    public byte[] Bgra { get; } = bgra;

    /// <summary>Miniatura (~60 px de altura) para a strip da timeline (RF-F5.03).</summary>
    public BitmapSource Thumbnail { get; } = thumbnail;

    public int DelayMs { get; } = delayMs;
}

/// <summary>
/// RF-F5.02: cache de frames do player GIF proprio. Decodifica TODOS os
/// frames uma unica vez via GifBitmapDecoder (in-box), compondo os frames
/// parciais (delta + offset + disposal) em quadros completos BGRA.
/// Delays vem do metadata "/grctlext/Delay" (centesimos).
///
/// Estrategia de memoria (correcao do bug de OOM ao abrir o editor):
/// - Por frame fica retido APENAS o buffer BGRA (antes: BGRA + BitmapSource
///   do mesmo conteudo = 2x a memoria). O BitmapSource exibido e um unico
///   WriteableBitmap reutilizado (<see cref="CreatePreviewBitmap"/> +
///   <see cref="CopyFrameTo"/>): zero alocacao por frame de playback.
/// - Teto de ~1,5 GB para o preview: se W*H*4*frames estoura o teto, o
///   preview e retido em escala reduzida (fator 2, 4, 8... ate caber).
///   Ex.: GIF 1920x1080 com 450 frames = ~3,7 GB cheio -> fator 2 = ~0,9 GB.
/// - O EXPORT nunca le deste cache: ele redecodifica o ARQUIVO original em
///   passada streaming via <see cref="GifFileFrameReader"/> (resolucao cheia,
///   um canvas reutilizado), entao a escala do preview nao afeta a saida.
/// - Thumbnails da strip continuam pre-geradas (pequenas, ~60 px).
/// Carregue com Task.Run; os bitmaps sao Freeze() e cruzam threads.
/// </summary>
public sealed class GifFrameCache
{
    private const int ThumbnailHeight = 60;

    /// <summary>Teto de memoria dos buffers de preview (~1,5 GB).</summary>
    private const long PreviewMemoryBudgetBytes = 1_500L * 1024 * 1024;

    /// <summary>Largura do PREVIEW (source / PreviewDownscaleFactor).</summary>
    public int Width { get; }

    /// <summary>Altura do PREVIEW (source / PreviewDownscaleFactor).</summary>
    public int Height { get; }

    /// <summary>Largura da tela logica do ARQUIVO (usada pelo export).</summary>
    public int SourceWidth { get; }

    /// <summary>Altura da tela logica do ARQUIVO (usada pelo export).</summary>
    public int SourceHeight { get; }

    /// <summary>Fator de reducao do preview (1 = sem reducao; 2/4/8... quando estoura o teto).</summary>
    public int PreviewDownscaleFactor { get; }

    public IReadOnlyList<GifCachedFrame> Frames { get; }

    /// <summary>Delays por frame do source, na ordem do arquivo.</summary>
    public IReadOnlyList<int> Delays { get; }

    private GifFrameCache(int sourceWidth, int sourceHeight, int previewWidth, int previewHeight,
        int downscaleFactor, IReadOnlyList<GifCachedFrame> frames)
    {
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        Width = previewWidth;
        Height = previewHeight;
        PreviewDownscaleFactor = downscaleFactor;
        Frames = frames;
        Delays = frames.Select(f => f.DelayMs).ToArray();
    }

    /// <summary>Bitmap reutilizavel do player: um unico buffer para todos os frames.</summary>
    public WriteableBitmap CreatePreviewBitmap() =>
        new(Width, Height, 96, 96, PixelFormats.Bgra32, null);

    /// <summary>
    /// Copia o BGRA do frame para o bitmap reutilizado (WritePixels, sem
    /// alocacao). Chame da UI thread (WriteableBitmap tem afinidade).
    /// </summary>
    public void CopyFrameTo(WriteableBitmap target, int frameIndex)
    {
        var frame = Frames[Math.Clamp(frameIndex, 0, Frames.Count - 1)];
        target.WritePixels(new Int32Rect(0, 0, Width, Height), frame.Bgra, Width * 4, 0);
    }

    // buffer preto reutilizado pelos gaps do preview (RF-F5.20); lazy porque
    // a maioria dos projetos nunca cria gaps
    private byte[]? _blackFrame;

    /// <summary>
    /// Preenche o bitmap reutilizado com preto opaco - preview de slots de
    /// gap da timeline livre (RF-F5.20). O buffer preto e cacheado; nenhuma
    /// alocacao por frame de playback.
    /// </summary>
    public void CopyBlackTo(WriteableBitmap target)
    {
        if (_blackFrame is null)
        {
            _blackFrame = new byte[Width * Height * 4];
            for (var i = 3; i < _blackFrame.Length; i += 4)
                _blackFrame[i] = 255; // BGRA: preto opaco
        }
        target.WritePixels(new Int32Rect(0, 0, Width, Height), _blackFrame, Width * 4, 0);
    }

    public static GifFrameCache Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        // BitmapCacheOption.None: os patches sao decodificados do stream sob
        // demanda, sem o decoder reter todos os frames em memoria.
        var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
        if (decoder.Frames.Count == 0)
            throw new InvalidDataException("GIF sem frames.");

        // tamanho da tela logica; fallback para o primeiro frame
        var width = GifComposer.GetMetaUInt16(decoder.Metadata, "/logscrdesc/Width") ?? decoder.Frames[0].PixelWidth;
        var height = GifComposer.GetMetaUInt16(decoder.Metadata, "/logscrdesc/Height") ?? decoder.Frames[0].PixelHeight;

        var factor = ComputePreviewDownscaleFactor(width, height, decoder.Frames.Count);
        var previewW = Math.Max(1, width / factor);
        var previewH = Math.Max(1, height / factor);

        var canvas = new byte[width * height * 4]; // comeca transparente
        var frames = new List<GifCachedFrame>(decoder.Frames.Count);

        foreach (var frame in decoder.Frames)
        {
            var info = GifComposer.ReadFrameInfo(frame);

            // disposal 3 (restaurar anterior): guarda o estado antes do blit
            byte[]? before = info.Disposal == 3 ? (byte[])canvas.Clone() : null;

            GifComposer.BlitFrame(canvas, width, height, frame, info);

            // retem SO o BGRA do preview (reduzido quando estourou o teto)
            var preview = factor == 1
                ? (byte[])canvas.Clone()
                : DownscaleByFactor(canvas, width, height, factor, previewW, previewH);
            var thumb = BuildThumbnail(preview, previewW, previewH);
            frames.Add(new GifCachedFrame(preview, thumb, info.DelayMs));

            GifComposer.ApplyDisposal(canvas, width, height, info, before);
        }

        return new GifFrameCache(width, height, previewW, previewH, factor, frames);
    }

    /// <summary>
    /// Menor potencia de 2 tal que os buffers de preview cabem no teto de
    /// ~1,5 GB (fator maximo 16 como guarda pragmatica).
    /// </summary>
    private static int ComputePreviewDownscaleFactor(int width, int height, int frameCount)
    {
        var factor = 1;
        while (factor < 16 &&
               (long)Math.Max(1, width / factor) * Math.Max(1, height / factor) * 4 * frameCount
                   > PreviewMemoryBudgetBytes)
        {
            factor *= 2;
        }
        return factor;
    }

    /// <summary>Downscale box simples por fator inteiro (media do bloco fator x fator).</summary>
    private static byte[] DownscaleByFactor(byte[] source, int srcW, int srcH, int factor, int dstW, int dstH)
    {
        var dst = new byte[dstW * dstH * 4];
        for (var dy = 0; dy < dstH; dy++)
        {
            var y0 = dy * factor;
            var y1 = Math.Min(srcH, y0 + factor);
            for (var dx = 0; dx < dstW; dx++)
            {
                var x0 = dx * factor;
                var x1 = Math.Min(srcW, x0 + factor);
                int b = 0, g = 0, r = 0, a = 0;
                var count = (x1 - x0) * (y1 - y0);
                for (var sy = y0; sy < y1; sy++)
                {
                    var row = (sy * srcW + x0) * 4;
                    for (var sx = x0; sx < x1; sx++)
                    {
                        b += source[row];
                        g += source[row + 1];
                        r += source[row + 2];
                        a += source[row + 3];
                        row += 4;
                    }
                }
                var di = (dy * dstW + dx) * 4;
                dst[di] = (byte)(b / count);
                dst[di + 1] = (byte)(g / count);
                dst[di + 2] = (byte)(r / count);
                dst[di + 3] = (byte)(a / count);
            }
        }
        return dst;
    }

    private static BitmapSource BuildThumbnail(byte[] bgra, int width, int height)
    {
        // BitmapSource transitorio so para gerar a miniatura (nao fica retido)
        var full = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        var scale = (double)ThumbnailHeight / height;
        var thumb = new TransformedBitmap(full, new ScaleTransform(scale, scale));
        var cached = new CachedBitmap(thumb, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        cached.Freeze();
        return cached;
    }
}

/// <summary>
/// Leitor STREAMING dos frames compostos do arquivo GIF original, usado pela
/// exportacao GIF-para-GIF (RF-F5.13). Mantem UM canvas reutilizado em
/// resolucao cheia e avanca frame a frame decodificando os patches do disco
/// (BitmapCacheOption.None); pedir um frame anterior reabre o arquivo e
/// recompoe do zero. Cada chamada devolve uma COPIA do canvas porque o
/// GifEncoder retem ate dois frames simultaneamente (pending + anterior).
/// Os loaders lazy (<see cref="Klip.Core.Media.Gif.GifFrameSource.FromLoader"/>)
/// pedem os frames em ordem crescente dentro de cada segmento, entao o custo
/// tipico e uma unica passada sequencial por pass do encoder.
/// </summary>
public sealed class GifFileFrameReader : IDisposable
{
    private readonly string _path;
    private readonly object _gate = new();
    private FileStream? _stream;
    private GifBitmapDecoder? _decoder;
    private byte[] _canvas;
    private byte[]? _restoreState;                 // buffer reutilizado p/ disposal 3
    private GifComposer.FrameInfo _pendingDisposal; // disposal do ultimo frame composto
    private bool _hasPendingDisposal;
    private int _composedFrame = -1;               // indice do frame corrente no canvas

    public int Width { get; }
    public int Height { get; }
    public int FrameCount { get; }

    public GifFileFrameReader(string path)
    {
        _path = path;
        OpenDecoder();
        Width = GifComposer.GetMetaUInt16(_decoder!.Metadata, "/logscrdesc/Width") ?? _decoder.Frames[0].PixelWidth;
        Height = GifComposer.GetMetaUInt16(_decoder.Metadata, "/logscrdesc/Height") ?? _decoder.Frames[0].PixelHeight;
        FrameCount = _decoder.Frames.Count;
        _canvas = new byte[Width * Height * 4];
    }

    /// <summary>Devolve uma copia do frame composto em resolucao cheia (BGRA32 top-down).</summary>
    public byte[] GetComposedFrame(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, FrameCount);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_decoder is null, this);
            if (index < _composedFrame)
                Rewind();
            while (_composedFrame < index)
                AdvanceOne();
            return (byte[])_canvas.Clone();
        }
    }

    private void AdvanceOne()
    {
        var next = _composedFrame + 1;
        var frame = _decoder!.Frames[next];
        var info = GifComposer.ReadFrameInfo(frame);

        // aplica o disposal PENDENTE do frame anterior antes do proximo blit
        if (_hasPendingDisposal)
            GifComposer.ApplyDisposal(_canvas, Width, Height, _pendingDisposal, _restoreState);

        if (info.Disposal == 3)
        {
            _restoreState ??= new byte[_canvas.Length];
            Array.Copy(_canvas, _restoreState, _canvas.Length);
        }

        GifComposer.BlitFrame(_canvas, Width, Height, frame, info);
        _pendingDisposal = info;
        _hasPendingDisposal = true;
        _composedFrame = next;
    }

    private void Rewind()
    {
        CloseDecoder();
        OpenDecoder();
        Array.Clear(_canvas);
        _composedFrame = -1;
        _hasPendingDisposal = false;
    }

    private void OpenDecoder()
    {
        _stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _decoder = new GifBitmapDecoder(_stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
        if (_decoder.Frames.Count == 0)
            throw new InvalidDataException("GIF sem frames.");
    }

    private void CloseDecoder()
    {
        _stream?.Dispose();
        _stream = null;
        _decoder = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            CloseDecoder();
            _canvas = [];
            _restoreState = null;
        }
    }
}

/// <summary>
/// Composicao GIF compartilhada entre o cache de preview e o leitor de
/// export: leitura do metadata por frame, alpha-over do patch e disposal.
/// </summary>
internal static class GifComposer
{
    /// <summary>Metadata relevante de um frame: offset, delay e disposal.</summary>
    internal readonly record struct FrameInfo(int Left, int Top, int DelayMs, int Disposal, int PatchWidth, int PatchHeight);

    internal static FrameInfo ReadFrameInfo(BitmapFrame frame)
    {
        var meta = frame.Metadata as BitmapMetadata;
        var left = GetMetaUInt16(meta, "/imgdesc/Left") ?? 0;
        var top = GetMetaUInt16(meta, "/imgdesc/Top") ?? 0;
        var delayCs = GetMetaUInt16(meta, "/grctlext/Delay") ?? 10;
        var disposal = GetMetaByte(meta, "/grctlext/Disposal") ?? 0;
        // convencao dos players: delay 0/1 cs vira 100 ms
        var delayMs = delayCs <= 1 ? 100 : delayCs * 10;
        return new FrameInfo(left, top, delayMs, disposal, frame.PixelWidth, frame.PixelHeight);
    }

    /// <summary>Decodifica o patch do frame e compoe (alpha-over) no canvas.</summary>
    internal static void BlitFrame(byte[] canvas, int canvasW, int canvasH, BitmapFrame frame, FrameInfo info)
    {
        var patch = new byte[info.PatchWidth * info.PatchHeight * 4];
        var converted = frame.Format == PixelFormats.Bgra32
            ? (BitmapSource)frame
            : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        converted.CopyPixels(patch, info.PatchWidth * 4, 0);
        BlitOver(canvas, canvasW, canvasH, patch, info.PatchWidth, info.PatchHeight, info.Left, info.Top);
    }

    /// <summary>Prepara o canvas para o PROXIMO frame conforme o disposal do frame composto.</summary>
    internal static void ApplyDisposal(byte[] canvas, int canvasW, int canvasH, FrameInfo info, byte[]? before)
    {
        switch (info.Disposal)
        {
            case 2: // restaura a area do frame para o fundo (transparente)
                ClearRect(canvas, canvasW, canvasH, info.Left, info.Top, info.PatchWidth, info.PatchHeight);
                break;
            case 3 when before is not null:
                Array.Copy(before, canvas, canvas.Length);
                break;
            // 0/1: mantem o composto
        }
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

    internal static int? GetMetaUInt16(BitmapMetadata? metadata, string query)
        => GetMeta(metadata, query) is ushort value ? value : null;

    internal static int? GetMetaByte(BitmapMetadata? metadata, string query)
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
