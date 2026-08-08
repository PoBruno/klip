using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Klip.Core.Common;

// RF-P3.06: o contador de gravacoes existe so pra os testes conseguirem provar
// que o debounce coalesce N chamadas de Update numa unica escrita em disco.
[assembly: InternalsVisibleTo("Klip.Core.Tests")]

namespace Klip.Core.Settings;

/// <summary>
/// Persists AppSettings as JSON. RF-P3.06: a mutacao e aplicada em memoria na
/// hora (e o Changed dispara junto, pra UI reagir), mas a gravacao em disco e
/// agendada com debounce - 44 call-sites de Update, incluindo ValueChanged de
/// slider, nao podem custar 3 operacoes de arquivo cada.
/// </summary>
public sealed class SettingsService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // RF-P3.06: arquivo menor e serializacao mais rapida; continua legivel
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Janela de coalescencia das gravacoes (RF-P3.06).</summary>
    internal const int DebounceMilliseconds = 500;

    private readonly string _path;

    /// <summary>Protege Current e a serializacao. Nunca segurado durante IO.</summary>
    private readonly Lock _sync = new();

    /// <summary>Serializa o IO entre o timer, o Flush e o Dispose.</summary>
    private readonly Lock _writeSync = new();

    private readonly Timer _debounce;

    /// <summary>Incrementa a cada mutacao; identifica o que ainda falta gravar.</summary>
    private long _version;

    /// <summary>Ultima versao efetivamente no disco (guardada por _writeSync).</summary>
    private long _writtenVersion;

    private int _writeCount;
    private bool _disposed;

    /// <summary>Quantas vezes o arquivo foi realmente escrito (diagnostico/testes).</summary>
    internal int WriteCount => Volatile.Read(ref _writeCount);

    /// <summary>Ha mutacao aplicada em memoria que ainda nao chegou no disco.</summary>
    internal bool HasPendingWrite
    {
        get
        {
            lock (_sync)
                return _version != Volatile.Read(ref _writtenVersion);
        }
    }

    public AppSettings Current { get; private set; } = new();

    public event Action<AppSettings>? Changed;

    public SettingsService(string? path = null)
    {
        _path = path ?? AppPaths.SettingsFile;

        // criado parado: so passa a contar quando um Update agenda a gravacao
        _debounce = new Timer(
            static state => ((SettingsService)state!).OnDebounceElapsed(),
            this,
            Timeout.Infinite,
            Timeout.Infinite);

        Load();
    }

    public void Load()
    {
        lock (_sync)
        {
            if (File.Exists(_path))
            {
                try
                {
                    Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions) ?? new AppSettings();
                }
                catch (JsonException)
                {
                    // arquivo corrompido: guarda uma copia pra diagnostico e comeca de novo
                    File.Copy(_path, _path + ".corrupt", overwrite: true);
                    Current = new AppSettings();
                }
            }
        }
    }

    /// <summary>
    /// Grava agora, incondicionalmente, e notifica os assinantes.
    /// RF-P3.06: serializa dentro do lock, escreve o arquivo fora dele.
    /// </summary>
    public void Save()
    {
        string json;
        AppSettings snapshot;
        long version;

        lock (_sync)
        {
            snapshot = Current;
            version = ++_version; // forca a gravacao mesmo sem mutacao pendente
            json = JsonSerializer.Serialize(snapshot, JsonOptions);
        }

        WriteIfNewer(json, version);
        Changed?.Invoke(snapshot);
    }

    /// <summary>
    /// RF-P3.06: aplica a mutacao em memoria, avisa a UI na hora e agenda a
    /// gravacao. Nenhuma operacao de arquivo acontece aqui.
    /// </summary>
    public void Update(Action<AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        AppSettings snapshot;
        lock (_sync)
        {
            mutate(Current);
            _version++;
            snapshot = Current;
        }

        ScheduleWrite();
        Changed?.Invoke(snapshot);
    }

    /// <summary>
    /// RF-P3.06: para quem nao pode esperar o debounce (takeover de registro,
    /// por exemplo, onde o backup precisa estar em disco antes da escrita).
    /// </summary>
    public void UpdateAndFlush(Action<AppSettings> mutate)
    {
        Update(mutate);
        Flush();
    }

    /// <summary>
    /// RF-P3.06: grava na hora se houver alteracao pendente. Chamar no OnExit /
    /// OnSessionEnding. Nao segura _sync durante o IO, entao pode ser chamado de
    /// dentro do callback do timer sem risco de deadlock.
    /// </summary>
    public void Flush()
    {
        string json;
        long version;

        lock (_sync)
        {
            version = _version;
            if (version == Volatile.Read(ref _writtenVersion))
                return; // nada pendente

            json = JsonSerializer.Serialize(Current, JsonOptions);
        }

        WriteIfNewer(json, version);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        // para o agendamento antes de gravar; se o callback ja estiver rodando,
        // o controle de versao em WriteIfNewer evita a gravacao duplicada
        _debounce.Dispose();
        Flush();
    }

    private void ScheduleWrite()
    {
        // Change com dueTime reinicia a contagem: N updates seguidos viram uma
        // gravacao so, 500 ms depois do ultimo (RF-P3.06)
        try
        {
            _debounce.Change(DebounceMilliseconds, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // corrida com o Dispose: o Flush do Dispose ja cuida do pendente
        }
    }

    private void OnDebounceElapsed()
    {
        try
        {
            Flush();
        }
        catch (Exception)
        {
            // excecao nao tratada em callback de Timer derruba o processo:
            // perder uma gravacao de settings e sempre melhor que isso. O valor
            // continua em memoria e a proxima janela de debounce tenta de novo.
        }
    }

    private void WriteIfNewer(string json, long version)
    {
        // RF-P3.06: IO fora do lock de mutacao - segurar _sync durante 3
        // operacoes de arquivo bloquearia o proximo Update vindo da UI thread
        lock (_writeSync)
        {
            if (version <= _writtenVersion)
                return; // uma gravacao mais nova ja passou por aqui

            WriteAtomic(json);
            Volatile.Write(ref _writtenVersion, version);
        }
    }

    private void WriteAtomic(string json)
    {
        // durabilidade preservada: temporario + File.Move(overwrite) atomico
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);

        Interlocked.Increment(ref _writeCount);
    }
}
