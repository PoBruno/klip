namespace Klip.Core.Media.Gif;

/// <summary>
/// Buffer de retencao de frames da gravacao GIF (RF-F4.06, D-F4.2): dedupe
/// vetorizado com o frame anterior na ingestao (frame identico acumula delay
/// e e descartado), retencao em RAM ate o teto configurado e spill para
/// arquivos raw BGRA em disco acima disso (RF-F4.04). Nao e thread-safe:
/// a ingestao vem de uma unica thread (o worker do GifFramePipeline).
/// </summary>
public sealed class GifFrameBuffer : IDisposable
{
    /// <summary>Delay maximo acumulavel por frame: 65535 cs = 655350 ms.</summary>
    private const long MaxDelayMs = 655_350;

    private sealed class Entry
    {
        public byte[]? Ram;
        public string? SpillPath;
        public int DelayMs;
    }

    private readonly long _maxRamBytes;
    private readonly string _spillDirectory;
    private readonly string _spillPrefix = Guid.NewGuid().ToString("N");
    private readonly List<Entry> _entries = [];

    // Referencia ao ultimo frame para o dedupe: aponta para o array retido em
    // RAM ou para o scratch reutilizado quando a entrada foi para o spill.
    private byte[]? _lastBgra;

    // Buffer de comparacao reutilizado entre frames spillados: evita um
    // ToArray() (alocacao LOH) por frame no caminho de ingestao (bug de GC do
    // pipeline GIF, RF-F4.04).
    private byte[]? _compareScratch;

    private int _width;
    private int _height;
    private long _ramBytes;
    private bool _disposed;

    public GifFrameBuffer(long maxRamBytes, string spillDirectory)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxRamBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(spillDirectory);
        _maxRamBytes = maxRamBytes;
        _spillDirectory = spillDirectory;
    }

    /// <summary>Quantidade de frames retidos (apos dedupe).</summary>
    public int Count => _entries.Count;

    /// <summary>Bytes de pixels retidos em RAM (spill nao conta).</summary>
    public long EstimatedBytes => _ramBytes;

    /// <summary>
    /// Ingere um frame BGRA32 top-down. Retorna false se o frame e identico
    /// ao anterior (dedupe RF-F4.06): o delay e acumulado no frame retido e
    /// nada e armazenado - a duracao total fica preservada (CA-F4.2).
    /// </summary>
    public bool Add(ReadOnlySpan<byte> bgra, int width, int height, int delayMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegative(delayMs);
        if (bgra.Length != width * height * 4)
            throw new ArgumentException("Buffer BGRA deve ter exatamente width*height*4 bytes.", nameof(bgra));

        if (_entries.Count == 0)
        {
            _width = width;
            _height = height;
        }
        else if (width != _width || height != _height)
        {
            throw new ArgumentException("Todos os frames devem ter as mesmas dimensoes.", nameof(bgra));
        }

        // Dedupe exato: SequenceEqual e vetorizado (SIMD) pelo runtime.
        if (_lastBgra is not null && bgra.SequenceEqual(_lastBgra))
        {
            var last = _entries[^1];
            last.DelayMs = (int)Math.Min((long)last.DelayMs + delayMs, MaxDelayMs);
            return false;
        }

        var entry = new Entry { DelayMs = delayMs };

        if (_ramBytes + bgra.Length <= _maxRamBytes)
        {
            // O frame retido em RAM precisa de armazenamento proprio; o dedupe
            // do proximo frame compara direto com ele (sem copia extra).
            var copy = bgra.ToArray();
            entry.Ram = copy;
            _ramBytes += copy.Length;
            _lastBgra = copy;
        }
        else
        {
            entry.SpillPath = SpillToDisk(bgra);
            // Copia do ultimo frame fica sempre em RAM para o dedupe do
            // proximo, mas num scratch REUTILIZADO (nada de ToArray por frame).
            _compareScratch ??= new byte[bgra.Length];
            bgra.CopyTo(_compareScratch);
            _lastBgra = _compareScratch;
        }

        _entries.Add(entry);
        return true;
    }

    /// <summary>
    /// Materializa a lista de frames para o encoder. Entradas em RAM
    /// compartilham o array retido; entradas em spill viram fontes lazy que
    /// leem o arquivo a cada acesso a <see cref="GifFrameSource.Bgra"/>
    /// (invalido apos <see cref="Dispose"/>).
    /// </summary>
    public IReadOnlyList<GifFrameSource> Snapshot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var frames = new List<GifFrameSource>(_entries.Count);
        foreach (var entry in _entries)
        {
            if (entry.Ram is not null)
            {
                frames.Add(new GifFrameSource(entry.Ram, _width, _height, entry.DelayMs));
            }
            else
            {
                var path = entry.SpillPath!;
                frames.Add(GifFrameSource.FromLoader(() => File.ReadAllBytes(path), _width, _height, entry.DelayMs));
            }
        }
        return frames;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        foreach (var entry in _entries)
        {
            if (entry.SpillPath is null)
                continue;
            try
            {
                File.Delete(entry.SpillPath);
            }
            catch (IOException)
            {
                // Melhor esforco: arquivo em uso nao pode bloquear o dispose.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        _entries.Clear();
        _lastBgra = null;
        _compareScratch = null;
        _ramBytes = 0;
    }

    private string SpillToDisk(ReadOnlySpan<byte> frame)
    {
        Directory.CreateDirectory(_spillDirectory);
        var path = Path.Combine(_spillDirectory, $"klip-gif-{_spillPrefix}-{_entries.Count:D6}.bgra");
        // FileStream bufferizado escrevendo direto do span (sem copia
        // intermediaria); SequentialScan documenta o padrao de acesso do
        // Snapshot. O spill roda no worker do pipeline, fora da captura.
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 81920, FileOptions.SequentialScan);
        stream.Write(frame);
        return path;
    }
}
