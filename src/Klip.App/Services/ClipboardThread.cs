using System.Windows.Interop;
using System.Windows.Threading;

namespace Klip.App.Services;

/// <summary>
/// ADR-P.05: thread STA dedicada dona de todo acesso ao clipboard. A UI thread
/// nunca abre o clipboard - abrir o clipboard e um lock global do sistema e o
/// caminho do WPF pode bloquear ~1 s por retry interno.
///
/// Contexto: System.Windows.Clipboard tem retry proprio (OleRetryCount = 10 x
/// OleRetryDelay = 100 ms de Thread.Sleep). Somado ao retry externo que o
/// monitor tinha, o pior caso passava de 6 s de UI thread parada - e como o app
/// instala hooks globais de input na UI thread, isso congelava o input do
/// Windows inteiro. Toda leitura e toda gravacao passam a viver aqui.
///
/// Serializar tudo numa thread so tem um efeito colateral util: gravacao e
/// leitura entram na MESMA fila do dispatcher, entao o anti-loop por sequence
/// number (RF-P2.01) deixa de ter corrida entre as duas.
/// </summary>
public sealed class ClipboardThread : IDisposable
{
    /// <summary>Teto para a thread subir e criar a janela message-only.</summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Timeout defensivo dos Invoke sincronos. A thread do clipboard pode ficar
    /// presa quando outro processo segura o clipboard (o proprio WPF dorme ate
    /// ~1 s por tentativa). Sem teto, um Invoke vindo da UI thread deixaria de
    /// ser uma marshalizacao e viraria exatamente o congelamento que este tipo
    /// existe para eliminar - melhor abortar e perder a operacao.
    /// </summary>
    private static readonly TimeSpan InvokeTimeout = TimeSpan.FromSeconds(10);

    private const int ShutdownJoinMs = 2000;

    private readonly Thread _thread;
    private HwndSource? _messageWindow;
    private volatile bool _disposed;

    /// <summary>Dispatcher da thread STA. Todo acesso ao clipboard roda nele.</summary>
    public Dispatcher Dispatcher { get; }

    /// <summary>HWND message-only desta thread, para AddClipboardFormatListener.</summary>
    public nint MessageWindowHandle { get; }

    public ClipboardThread()
    {
        // nao e descartado no caminho de timeout de proposito: a thread ainda
        // pode chamar Set() depois, e sinalizar um evento ja descartado
        // derrubaria o processo por excecao nao tratada em thread de fundo
        var ready = new ManualResetEventSlim(false);
        Dispatcher? dispatcher = null;
        nint handle = 0;
        Exception? failure = null;

        _thread = new Thread(() =>
        {
            try
            {
                // Dispatcher.CurrentDispatcher CRIA o dispatcher desta thread
                dispatcher = Dispatcher.CurrentDispatcher;

                // janela message-only propria: ancora o pump desta thread e da um
                // HWND estavel de diagnostico. HwndSource exige um dispatcher na
                // thread, por isso ela so pode nascer aqui dentro.
                _messageWindow = new HwndSource(new HwndSourceParameters("KlipClipboardSta")
                {
                    WindowStyle = 0,
                    ExtendedWindowStyle = 0,
                    ParentWindow = new nint(-3), // HWND_MESSAGE
                });
                handle = _messageWindow.Handle;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                ready.Set();
            }

            if (failure is not null)
                return;

            // bombeia mensagens ate InvokeShutdown(); e o Run que permite
            // WM_CLIPBOARDUPDATE, DispatcherTimer e Invoke funcionarem aqui
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "Klip.ClipboardSta",
        };

        // OLE/clipboard exige STA; sem isso GetDataObject falha de imediato
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!ready.Wait(StartupTimeout))
        {
            throw new InvalidOperationException(
                $"ClipboardThread nao ficou pronta em {StartupTimeout.TotalSeconds:0}s " +
                "(a thread STA do clipboard nao criou a janela message-only).");
        }

        ready.Dispose();

        if (failure is not null)
        {
            throw new InvalidOperationException(
                "Falha ao inicializar a thread STA do clipboard.", failure);
        }

        Dispatcher = dispatcher!;
        MessageWindowHandle = handle;
    }

    /// <summary>True quando o chamador JA esta na thread do clipboard.</summary>
    public bool CheckAccess() => Dispatcher.CheckAccess();

    /// <summary>
    /// Executa na thread do clipboard e espera terminar. Reentrancia vinda da
    /// propria thread roda inline - um Invoke aninhado no mesmo dispatcher
    /// travaria ate o timeout. Depois do Dispose vira no-op: no encerramento
    /// ainda chegam restauracoes de clipboard atrasadas do PasteService, e
    /// estourar nelas mataria o processo por excecao em thread de fundo.
    /// </summary>
    public void Invoke(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
            return;
        }
        if (_disposed)
            return;
        Dispatcher.Invoke(action, DispatcherPriority.Send, CancellationToken.None, InvokeTimeout);
    }

    public T Invoke<T>(Func<T> func)
    {
        if (Dispatcher.CheckAccess())
            return func();
        if (_disposed)
            return default!;
        return Dispatcher.Invoke(func, DispatcherPriority.Send, CancellationToken.None, InvokeTimeout);
    }

    /// <summary>Enfileira sem esperar. Preferir quando o resultado nao importa.</summary>
    public void BeginInvoke(Action action)
    {
        if (_disposed)
            return;
        Dispatcher.BeginInvoke(action, DispatcherPriority.Send);
    }

    public Task<T> InvokeAsync<T>(Func<T> func) => Dispatcher.InvokeAsync(func).Task;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            // a janela pertence a thread STA: destruir de fora seria cross-thread
            var window = _messageWindow;
            _messageWindow = null;
            if (window is not null)
                Dispatcher.Invoke(window.Dispose, DispatcherPriority.Send, CancellationToken.None, InvokeTimeout);
        }
        catch (Exception)
        {
            // shutdown e best effort: se a thread ja morreu, segue o InvokeShutdown
        }

        Dispatcher.InvokeShutdown();
        _thread.Join(ShutdownJoinMs);
    }
}
