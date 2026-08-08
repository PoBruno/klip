namespace Klip.Core.Input;

/// <summary>
/// RF-P1.01: fila SPSC (um produtor, um consumidor) lock-free entre a thread do
/// hook de baixo nivel e a thread do worker. O callback do hook precisa devolver
/// dentro do LowLevelHooksTimeout, entao o caminho de escrita nao pode bloquear
/// nem alocar: o array e pre-alocado e o evento e escrito no slot por referencia.
///
/// RF-P1.02: quando cheia, a fila DESCARTA e contabiliza em <see cref="DroppedCount"/>
/// em vez de bloquear o produtor - perder input e melhor do que o Windows
/// desinstalar o hook por estouro de tempo.
/// </summary>
public sealed class InputEventRing
{
    private readonly InputEvent[] _buffer;
    private readonly int _mask;
    private readonly AutoResetEvent _signal = new(false);

    // Indices monotonicos (nao mascarados). Podem estourar int; toda comparacao
    // usa subtracao (w - r), que continua correta no wrap em aritmetica unchecked.
    private int _writeIndex;
    private int _readIndex;

    private long _droppedCount;
    private long _enqueuedCount;

    /// <param name="capacity">Potencia de 2 e maior ou igual a 2.</param>
    public InputEventRing(int capacity)
    {
        if (capacity < 2 || (capacity & (capacity - 1)) != 0)
            throw new ArgumentException("Capacidade deve ser potencia de 2 e maior ou igual a 2.", nameof(capacity));

        _buffer = new InputEvent[capacity];
        _mask = capacity - 1;
    }

    public int Capacity => _buffer.Length;

    /// <summary>Contagem aproximada; segura para ler concorrentemente com produtor e consumidor.</summary>
    public int Count
    {
        get
        {
            int w = Volatile.Read(ref _writeIndex);
            int r = Volatile.Read(ref _readIndex);
            int n = unchecked(w - r);
            if (n < 0) return 0;                       // leitura rasgada entre os dois indices
            return n > _buffer.Length ? _buffer.Length : n;
        }
    }

    /// <summary>Eventos descartados por buffer cheio desde a criacao (nao zera no <see cref="Reset"/>).</summary>
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    /// <summary>Total de eventos aceitos desde a criacao (nao zera no <see cref="Reset"/>).</summary>
    public long EnqueuedCount => Interlocked.Read(ref _enqueuedCount);

    /// <summary>Produtor (thread do hook). Retorna false e conta em DroppedCount quando cheio. NUNCA bloqueia, NUNCA aloca.</summary>
    public bool TryEnqueue(uint message, int a, int b, uint time)
    {
        int w = Volatile.Read(ref _writeIndex);
        int r = Volatile.Read(ref _readIndex);

        if (unchecked(w - r) >= _buffer.Length)
        {
            Interlocked.Increment(ref _droppedCount);
            return false;
        }

        // Escrita in-place: nenhuma struct temporaria, nenhuma copia extra.
        ref InputEvent slot = ref _buffer[w & _mask];
        slot.Message = message;
        slot.A = a;
        slot.B = b;
        slot.Time = time;

        // Publica o item so depois dos campos estarem escritos.
        Volatile.Write(ref _writeIndex, unchecked(w + 1));
        Interlocked.Increment(ref _enqueuedCount);

        // RF-P1.01: uma syscall por evento mataria o orcamento do callback. Sinaliza
        // apenas na transicao vazio -> nao-vazio. A releitura do indice de leitura
        // acontece DEPOIS da publicacao: se o consumidor ainda esta no indice
        // anterior, ele esta dormindo ou prestes a dormir, e o Set nao se perde.
        if (Volatile.Read(ref _readIndex) == w)
            _signal.Set();

        return true;
    }

    /// <summary>Consumidor (thread do worker). Retorna false quando vazio.</summary>
    public bool TryDequeue(out InputEvent e)
    {
        int r = Volatile.Read(ref _readIndex);
        int w = Volatile.Read(ref _writeIndex);

        if (unchecked(w - r) <= 0)
        {
            e = default;
            return false;
        }

        e = _buffer[r & _mask];
        Volatile.Write(ref _readIndex, unchecked(r + 1));
        return true;
    }

    /// <summary>Bloqueia ate haver trabalho ou o timeout expirar. Retorna true se foi sinalizado.</summary>
    public bool WaitForWork(int timeoutMilliseconds) => _signal.WaitOne(timeoutMilliseconds);

    /// <summary>Acorda o consumidor mesmo sem itens (usado no shutdown).</summary>
    public void Signal() => _signal.Set();

    /// <summary>Esvazia a fila. So use quando nao ha produtor ativo. Contadores acumulados sao preservados.</summary>
    public void Reset()
    {
        Volatile.Write(ref _readIndex, 0);
        Volatile.Write(ref _writeIndex, 0);
        _signal.Reset(); // descarta um Set pendente para nao gerar acorde falso apos o re-arme
    }
}
