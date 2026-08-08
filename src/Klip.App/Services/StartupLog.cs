using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using Klip.Core.Common;
using Klip.Interop;

namespace Klip.App.Services;

/// <summary>
/// Log de startup/erro de um app de tray sem console. Nao substitui logging
/// estruturado, e so o suficiente para diagnostico em campo.
/// <para>
/// RF-P3.05: a gravacao e ASSINCRONA. A versao anterior fazia
/// <c>File.AppendAllText</c> dentro de um lock a cada linha - abrir, escrever e
/// fechar o arquivo, na thread do chamador, com 70+ call-sites (um deles por item
/// de clipboard ingerido). Aqui o produtor so enfileira; uma unica thread
/// <c>Klip.LogWriter</c> mantem o <see cref="StreamWriter"/> aberto com
/// <c>AutoFlush = false</c> e grava por lote.
/// </para>
/// <para>
/// Contrato: <see cref="Write"/> NUNCA bloqueia e NUNCA lanca. Falha de disco
/// (cheio, sem permissao, pasta removida) e engolida - log nunca derruba o app.
/// </para>
/// </summary>
public static class StartupLog
{
    /// <summary>Rotaciona ao passar de 2 MB, mantendo 1 arquivo anterior (teto de 4 MB no disco).</summary>
    private const long MaxFileBytes = 2L * 1024 * 1024;

    /// <summary>Backlog maximo NA FILA. Acima disso a linha e DESCARTADA e contada em <see cref="DroppedLines"/>.</summary>
    private const int MaxQueuedLines = 4096;

    /// <summary>Flush por tempo: o writer acorda mesmo sem trabalho para esvaziar o buffer.</summary>
    private const int FlushIntervalMilliseconds = 1000;

    /// <summary>
    /// Teto de linhas por lote. O tamanho do arquivo so e conferido no flush, entao
    /// uma rajada drenada de uma vez so passaria MUITO dos 2 MB antes de rotacionar.
    /// Com 512 linhas o excesso fica na casa das dezenas de KB e ainda assim 20.000
    /// chamadas a Write custam ~40 flushes, nao 20.000.
    /// </summary>
    private const int MaxBatchLines = 512;

    /// <summary>Teto de espera de <see cref="Flush"/> e <see cref="Shutdown"/>.</summary>
    private const int DrainTimeoutMilliseconds = 2000;

    /// <summary>Fatia de espera de <see cref="Flush"/>: transforma o pulso perdido em poll.</summary>
    private const int FlushPollMilliseconds = 25;

    // ConcurrentQueue como backing store: Add e lock-free do lado do produtor e a
    // colecao e ilimitada, entao Add nunca bloqueia o chamador.
    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>());

    // Pulsado pelo writer depois de cada flush; usado por Flush() para nao dormir
    // o intervalo inteiro esperando o lote pendente chegar ao disco.
    private static readonly ManualResetEventSlim FlushedPulse = new(false);

    private static readonly object StartSync = new();

    private static Thread? _writerThread;
    private static int _writerStarted;

    private static long _enqueued;
    private static long _persisted;
    private static long _dropped;

    private static int _shutdown;
    private static int _verbose;

    public static string LogFile => Path.Combine(AppPaths.Root, "startup.log");

    /// <summary>Linhas descartadas por backlog (writer preso em disco lento/antivirus).</summary>
    public static long DroppedLines => Interlocked.Read(ref _dropped);

    /// <summary>
    /// RF-P3.05: chave do log de alta frequencia (um registro por item de clipboard
    /// ingerido, por exemplo). Default false - ligar so para diagnostico em campo.
    /// </summary>
    public static bool VerboseEnabled
    {
        get => Volatile.Read(ref _verbose) != 0;
        set => Volatile.Write(ref _verbose, value ? 1 : 0);
    }

    public static void Write(string message)
    {
        try
        {
            // Timestamp montado no PRODUTOR: a fila pode atrasar, mas o horario da
            // linha continua sendo o do evento. InvariantCulture porque ':' e '/'
            // sao placeholders de separador em format string customizada e mudariam
            // com a cultura da maquina.
            string line = string.Create(
                CultureInfo.InvariantCulture,
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}");

            if (Volatile.Read(ref _shutdown) != 0)
            {
                // Depois do Shutdown a thread nao existe mais, mas o app ainda
                // registra as ultimas linhas do teardown: volta ao append sincrono,
                // que e o custo certo para um punhado de linhas no fim do processo.
                AppendSynchronously(line);
                return;
            }

            // Backlog grande = writer preso (disco cheio, antivirus, handle perdido).
            // Log nunca pode virar consumo ilimitado de memoria: descarta e conta
            // (RF-P3.05). A medida e a PROFUNDIDADE DA FILA, nao "linhas ainda nao
            // persistidas": linhas ja entregues ao StreamWriter aguardando o flush do
            // lote nao ocupam a fila e nao podem provocar descarte numa rajada normal.
            if (Queue.Count > MaxQueuedLines)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }

            EnsureWriterStarted();

            Queue.Add(line);
            Interlocked.Increment(ref _enqueued);
        }
        catch (Exception)
        {
            // Contrato "nunca lanca": corrida com Shutdown (InvalidOperationException
            // de CompleteAdding), OOM da fila, o que for.
        }
    }

    /// <summary>
    /// Registra um erro. Diferente de <see cref="Write"/>, ESPERA a linha chegar ao
    /// disco (teto de 2 s): uma excecao nao tratada costuma ser a ultima coisa que
    /// acontece antes do processo morrer, e uma linha que ficou na fila nao serve
    /// para diagnostico nenhum.
    /// </summary>
    public static void WriteException(string context, Exception ex)
    {
        Write($"[ERRO] {context}: {ex}");
        Flush();
    }

    /// <summary>Grava so quando <see cref="VerboseEnabled"/> esta ligado.</summary>
    public static void WriteVerbose(string message)
    {
        if (Volatile.Read(ref _verbose) == 0)
            return;

        Write(message);
    }

    /// <summary>
    /// Grava o que estiver pendente e espera, com teto de 2 s. Chamar no
    /// <c>OnExit</c>/<c>OnSessionEnding</c>. Nunca lanca.
    /// </summary>
    public static void Flush()
    {
        try
        {
            if (Volatile.Read(ref _writerStarted) == 0 || Volatile.Read(ref _shutdown) != 0)
                return;

            long target = Volatile.Read(ref _enqueued);
            long deadline = Environment.TickCount64 + DrainTimeoutMilliseconds;

            while (Volatile.Read(ref _persisted) < target)
            {
                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                    return; // teto duro: o log nunca segura o encerramento do app

                // Reset antes de reconferir o contador: se o pulso chegou no meio,
                // a reconferencia abaixo ja sai do laco. Duas chamadas concorrentes
                // de Flush podem roubar o pulso uma da outra, por isso a espera e
                // fatiada - o pior caso vira poll de 25 ms, nunca deadlock.
                FlushedPulse.Reset();
                if (Volatile.Read(ref _persisted) >= target)
                    return;

                FlushedPulse.Wait((int)Math.Min(remaining, FlushPollMilliseconds));
            }
        }
        catch (Exception)
        {
            // Idem Write: diagnostico nunca e fonte de falha.
        }
    }

    /// <summary>
    /// Flush + encerra a thread de gravacao (teto de 2 s). Depois disso
    /// <see cref="Write"/> volta ao append sincrono. Idempotente.
    /// </summary>
    public static void Shutdown()
    {
        try
        {
            Flush();

            if (Interlocked.Exchange(ref _shutdown, 1) != 0)
                return;

            Thread? writer;
            lock (StartSync)
            {
                writer = _writerThread;
                _writerThread = null;
            }

            if (writer is null)
                return; // nada foi escrito nesta sessao

            Queue.CompleteAdding();

            // O writer drena o resto, da flush e fecha o arquivo. A thread e
            // IsBackground, entao mesmo estourando o teto o processo sai.
            writer.Join(DrainTimeoutMilliseconds);
        }
        catch (Exception)
        {
        }
    }

    // ================= Thread de gravacao =================

    private static void EnsureWriterStarted()
    {
        if (Volatile.Read(ref _writerStarted) != 0)
            return;

        lock (StartSync)
        {
            // Inicializacao preguicosa: a thread so sobe no primeiro Write.
            if (_writerThread is not null || Volatile.Read(ref _shutdown) != 0)
                return;

            var thread = new Thread(WriterMain)
            {
                Name = "Klip.LogWriter",
                IsBackground = true,
            };

            _writerThread = thread;
            Volatile.Write(ref _writerStarted, 1);
            thread.Start();
        }
    }

    /// <summary>
    /// ADR-P.08: gravacao de log e manutencao pura - prioridade de CPU, IO e memoria
    /// baixas + E-cores, para nunca competir com a UI nem com o caminho de input.
    /// <para>
    /// O aviso oficial sobre inversao de prioridade em background mode se aplica a
    /// recursos COMPARTILHADOS. Aqui o unico compartilhado com a UI e o semaforo
    /// interno da fila, segurado por um punhado de instrucoes; o recurso realmente
    /// lento (o handle do arquivo) e exclusivo desta thread.
    /// </para>
    /// </summary>
    private static void WriterMain() => PowerEfficiency.RunAsBackgroundIo(WriterPump);

    private static void WriterPump()
    {
        using var sink = new LogSink();

        try
        {
            while (true)
            {
                bool taken;
                string? line;
                try
                {
                    // Acorda a cada 1 s mesmo sem trabalho: e o flush por tempo do
                    // lote que ficou no buffer (AutoFlush = false).
                    taken = Queue.TryTake(out line, FlushIntervalMilliseconds);
                }
                catch (Exception)
                {
                    break; // fila completada/descartada
                }

                long batch = 0;
                if (taken && line is not null)
                {
                    sink.Append(line);
                    batch++;

                    // Drena o lote antes de tocar no disco: 20.000 chamadas a Write
                    // viram algumas dezenas de flushes, nao 20.000. O teto por lote
                    // mantem a conferencia de tamanho (rotacao) frequente o bastante.
                    while (batch < MaxBatchLines && Queue.TryTake(out string? extra) && extra is not null)
                    {
                        sink.Append(extra);
                        batch++;
                    }
                }

                sink.FlushAndRotate();

                if (batch > 0)
                    Interlocked.Add(ref _persisted, batch);
                FlushedPulse.Set();

                if (Queue.IsCompleted)
                    break;
            }
        }
        catch (Exception)
        {
            // Nada pode escapar: esta thread nao tem handler acima dela.
        }
        finally
        {
            // Acorda quem estiver esperando em Flush() em vez de deixa-lo no teto.
            FlushedPulse.Set();
        }
    }

    /// <summary>
    /// Append sincrono de emergencia, usado apenas DEPOIS de <see cref="Shutdown"/>,
    /// quando a thread de gravacao ja morreu e ainda ha teardown para registrar.
    /// </summary>
    private static void AppendSynchronously(string line)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.AppendAllText(LogFile, line + Environment.NewLine);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Arquivo de log com o handle mantido aberto. Vive SO na thread
    /// <c>Klip.LogWriter</c>: nao ha lock aqui de proposito.
    /// </summary>
    private sealed class LogSink : IDisposable
    {
        private FileStream? _stream;
        private StreamWriter? _writer;
        private bool _pending;

        public void Append(string line)
        {
            try
            {
                if (_writer is null && !TryOpen())
                    return;

                _writer!.WriteLine(line);
                _pending = true;
            }
            catch (Exception)
            {
                // Disco cheio, permissao revogada, pasta removida: solta o handle e
                // tenta reabrir na proxima linha. Log e best effort.
                Close();
            }
        }

        /// <summary>Esvazia o buffer e rotaciona se o arquivo passou de 2 MB.</summary>
        public void FlushAndRotate()
        {
            if (!_pending || _writer is null || _stream is null)
                return;

            try
            {
                _writer.Flush();
                _pending = false;

                // Tamanho lido do proprio stream, uma syscall por LOTE (nao por
                // linha): estimar por contagem de chars erraria com UTF-8 multibyte.
                if (_stream.Length >= MaxFileBytes)
                    Rotate();
            }
            catch (Exception)
            {
                Close();
            }
        }

        public void Dispose() => Close();

        private bool TryOpen()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Root);

                // FileShare.ReadWrite: o usuario pode abrir o log no bloco de notas
                // durante um diagnostico em campo sem quebrar a gravacao.
                _stream = new FileStream(
                    LogFile,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    bufferSize: 4096);

                // UTF-8 sem BOM: mesmo formato que File.AppendAllText produzia, os
                // logs antigos continuam legiveis no mesmo arquivo.
                _writer = new StreamWriter(_stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = false,
                };
                return true;
            }
            catch (Exception)
            {
                Close();
                return false;
            }
        }

        /// <summary>
        /// Fecha, renomeia para <c>&lt;nome&gt;.1</c> (sobrescrevendo o anterior) e
        /// reabre. So um arquivo anterior e mantido.
        /// </summary>
        private void Rotate()
        {
            Close();

            try
            {
                File.Move(LogFile, LogFile + ".1", overwrite: true);
            }
            catch (Exception)
            {
                // Nao deu para renomear (arquivo aberto por outro processo com share
                // restrito): trunca no lugar, que ao menos respeita o teto de disco.
                try
                {
                    File.WriteAllText(LogFile, string.Empty);
                }
                catch (Exception)
                {
                }
            }

            TryOpen();
        }

        private void Close()
        {
            try
            {
                _writer?.Flush();
            }
            catch (Exception)
            {
            }

            try
            {
                // Dispose do StreamWriter ja fecha o FileStream subjacente.
                _writer?.Dispose();
                _stream?.Dispose();
            }
            catch (Exception)
            {
            }

            _writer = null;
            _stream = null;
            _pending = false;
        }
    }
}
