using System.Buffers;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace Klip.Core.Media.Gif;

/// <summary>
/// Ponte entre o loop de captura CFR e o <see cref="GifFrameBuffer"/>
/// (RF-F4.03/RF-F4.04). Corrige as pausas de GC do caminho antigo (alocacao
/// LOH por frame + dedupe/spill sincronos no handler): o produtor faz apenas a
/// copia do frame (com downscale opcional) para um buffer do
/// <see cref="ArrayPool{T}"/> e enfileira num canal bounded; um worker
/// dedicado calcula o delay real a partir do timestamp CFR (indices de grade
/// pulados viram delay acumulado, preservando a duracao real do GIF) e ingere
/// no buffer com dedupe/spill fora do caminho de captura. Sob pressao o canal
/// descarta o frame mais antigo (DropOldest) devolvendo o buffer ao pool - a
/// duracao segue correta porque o delay vem do timestamp do proximo frame
/// processado.
/// </summary>
public sealed class GifFramePipeline : IAsyncDisposable
{
    private readonly record struct PendingFrame(byte[] Buffer, int Length, int Width, int Height, TimeSpan Timestamp);

    private readonly GifFrameBuffer _buffer;
    private readonly int _firstFrameDelayMs;
    private readonly int _scalePercent;
    private readonly Channel<PendingFrame> _channel;
    private readonly Task _worker;

    private Exception? _error;
    private long _retainedBytes;

    // estado do calculo de delay - so o worker toca
    private bool _hasPrevious;
    private long _previousMs;

    /// <param name="buffer">Destino da retencao (dedupe + spill). O pipeline nao o descarta.</param>
    /// <param name="firstFrameDelayMs">Delay do primeiro frame (passo nominal da grade CFR).</param>
    /// <param name="scalePercent">RF-F4.03: escala aplicada ANTES da retencao (100/75/50).</param>
    /// <param name="capacity">Frames em voo no canal antes do DropOldest.</param>
    public GifFramePipeline(GifFrameBuffer buffer, int firstFrameDelayMs, int scalePercent = 100, int capacity = 8)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(firstFrameDelayMs);
        if (scalePercent is <= 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(scalePercent), "Escala deve estar em (0, 100].");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _buffer = buffer;
        _firstFrameDelayMs = firstFrameDelayMs;
        _scalePercent = scalePercent;
        _channel = Channel.CreateBounded<PendingFrame>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            },
            dropped => ArrayPool<byte>.Shared.Return(dropped.Buffer));
        _worker = Task.Run(RunWorkerAsync);
    }

    /// <summary>Total de bytes de pixels retidos (mesma contagem do teto RF-F4.04).</summary>
    public long RetainedBytes => Interlocked.Read(ref _retainedBytes);

    /// <summary>
    /// Frame retido pelo buffer, com o total acumulado de bytes. Disparado na
    /// THREAD DO WORKER - o consumidor remarshala se precisar tocar em UI.
    /// </summary>
    public event Action<long>? FrameRetained;

    /// <summary>
    /// Caminho quente do produtor (thread do loop CFR): copia o frame - com
    /// downscale, se configurado - para um buffer pooled e enfileira. O span
    /// de origem e valido apenas durante a chamada (o engine reutiliza o
    /// buffer do CpuFrame). Apos <see cref="CompleteAsync"/> vira no-op.
    /// </summary>
    public void Post(ReadOnlySpan<byte> bgra, int width, int height, TimeSpan timestamp)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (bgra.Length != width * height * 4)
            throw new ArgumentException("Buffer BGRA deve ter exatamente width*height*4 bytes.", nameof(bgra));

        int dstW = width, dstH = height;
        if (_scalePercent != 100)
        {
            dstW = Math.Max(1, width * _scalePercent / 100);
            dstH = Math.Max(1, height * _scalePercent / 100);
        }

        var length = dstW * dstH * 4;
        var rented = ArrayPool<byte>.Shared.Rent(length);
        if (_scalePercent == 100)
            bgra.CopyTo(rented);
        else
            DownscaleInto(bgra, width, height, rented.AsSpan(0, length), dstW, dstH);

        if (!_channel.Writer.TryWrite(new PendingFrame(rented, length, dstW, dstH, timestamp)))
            ArrayPool<byte>.Shared.Return(rented); // canal ja completado (parada em curso)
    }

    /// <summary>
    /// Fecha o canal, aguarda o worker drenar os frames pendentes e propaga a
    /// primeira falha de ingestao, se houve. Chamar antes de usar o buffer.
    /// </summary>
    public async Task CompleteAsync()
    {
        _channel.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
        if (_error is not null)
            ExceptionDispatchInfo.Capture(_error).Throw();
    }

    /// <summary>Como <see cref="CompleteAsync"/>, mas engole a falha (rota de erro).</summary>
    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
    }

    private async Task RunWorkerAsync()
    {
        var reader = _channel.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var frame))
            {
                if (_error is null)
                {
                    try
                    {
                        ProcessFrame(frame);
                    }
                    catch (Exception ex)
                    {
                        // guarda a primeira falha e segue drenando (devolver
                        // buffers ao pool); CompleteAsync propaga
                        _error = ex;
                    }
                }
                ArrayPool<byte>.Shared.Return(frame.Buffer);
            }
        }
    }

    private void ProcessFrame(in PendingFrame frame)
    {
        // Delay real a partir do timestamp CFR (RF-F2.05): diferenca em ms
        // inteiros para o frame anterior processado. Ticks pulados pelo engine
        // e frames dropados pelo canal viram delay acumulado no proximo frame,
        // entao a duracao total do GIF reflete o tempo real gravado (CA-F4.2).
        var currentMs = (long)Math.Round(frame.Timestamp.TotalMilliseconds);
        var delayMs = _hasPrevious
            ? (int)Math.Clamp(currentMs - _previousMs, 0, int.MaxValue)
            : _firstFrameDelayMs;
        _hasPrevious = true;
        _previousMs = currentMs;

        if (_buffer.Add(frame.Buffer.AsSpan(0, frame.Length), frame.Width, frame.Height, delayMs))
        {
            var total = Interlocked.Add(ref _retainedBytes, (long)frame.Width * frame.Height * 4);
            FrameRetained?.Invoke(total);
        }
    }

    /// <summary>
    /// Downscale box para um destino fornecido (mesmo filtro do
    /// Klip.Core.Recording.BgraScaler, que so devolve arrays novos - aqui o
    /// destino e pooled para nao alocar por frame).
    /// </summary>
    private static void DownscaleInto(ReadOnlySpan<byte> src, int srcW, int srcH, Span<byte> dst, int dstW, int dstH)
    {
        for (int dy = 0; dy < dstH; dy++)
        {
            int y0 = dy * srcH / dstH;
            int y1 = Math.Max(y0 + 1, (dy + 1) * srcH / dstH);

            for (int dx = 0; dx < dstW; dx++)
            {
                int x0 = dx * srcW / dstW;
                int x1 = Math.Max(x0 + 1, (dx + 1) * srcW / dstW);

                int b = 0, g = 0, r = 0, a = 0;
                int count = (x1 - x0) * (y1 - y0);
                for (int sy = y0; sy < y1; sy++)
                {
                    int row = (sy * srcW + x0) * 4;
                    for (int sx = x0; sx < x1; sx++)
                    {
                        b += src[row];
                        g += src[row + 1];
                        r += src[row + 2];
                        a += src[row + 3];
                        row += 4;
                    }
                }

                int di = (dy * dstW + dx) * 4;
                dst[di] = (byte)(b / count);
                dst[di + 1] = (byte)(g / count);
                dst[di + 2] = (byte)(r / count);
                dst[di + 3] = (byte)(a / count);
            }
        }
    }
}
