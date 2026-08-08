using System.Buffers;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Klip.Core.Clipboard;
using Klip.Interop;

namespace Klip.App.Services;

/// <summary>
/// Observa o clipboard com AddClipboardFormatListener + WM_CLIPBOARDUPDATE.
///
/// RF-P2.01 / ADR-P.05: a janela message-only, o timer de debounce e TODA a
/// leitura vivem na <see cref="ClipboardThread"/> (STA dedicada). A UI thread
/// nunca abre o clipboard - antes ela pagava o retry proprio deste servico
/// (6 x Thread.Sleep(15)) somado ao do WPF (10 x 100 ms dentro de
/// Clipboard.GetDataObject), o que dava mais de 6 s de UI parada no pior caso.
/// Como os hooks globais de input tambem moram na UI thread, aquilo congelava
/// o input do Windows inteiro.
///
/// A ingestao continua fora daqui, em Task.Run.
/// </summary>
public sealed class ClipboardMonitorService : IDisposable
{
    // ----- formatos de opt-out -----

    /// <summary>Convencao do proprio Windows para "nao guarde isto no historico".</summary>
    private const string ExcludeMonitorFormat = "ExcludeClipboardContentFromMonitorProcessing";

    /// <summary>DWORD 0 = nao pode entrar no historico.</summary>
    private const string CanIncludeHistoryFormat = "CanIncludeInClipboardHistory";

    /// <summary>
    /// RF-P2.01: "Clipboard Viewer Ignore" NAO e documentado pela Microsoft, mas
    /// e a convencao de facto dos gerenciadores de senha (KeePass, 1Password,
    /// Bitwarden) para pedir que gerenciadores de clipboard ignorem a copia.
    /// Respeitar e obrigatorio: sem isso o Klip guardaria senhas em claro no
    /// historico de quem usa esses apps. O formato so precisa ESTAR presente,
    /// o conteudo dele nao importa - por isso a sonda basta e nem lemos o dado.
    /// </summary>
    private const string ViewerIgnoreFormat = "Clipboard Viewer Ignore";

    private const string PngFormat = "PNG";

    // ids de formato registrado sao estaveis por sessao: resolve uma vez so
    private static readonly uint IdHtml = NativeMethods.RegisterClipboardFormat(DataFormats.Html);
    private static readonly uint IdRtf = NativeMethods.RegisterClipboardFormat(DataFormats.Rtf);
    private static readonly uint IdPng = NativeMethods.RegisterClipboardFormat(PngFormat);
    private static readonly uint IdExcludeMonitor = NativeMethods.RegisterClipboardFormat(ExcludeMonitorFormat);
    private static readonly uint IdCanIncludeHistory = NativeMethods.RegisterClipboardFormat(CanIncludeHistoryFormat);
    private static readonly uint IdViewerIgnore = NativeMethods.RegisterClipboardFormat(ViewerIgnoreFormat);

    private readonly ClipboardIngestService _ingest;
    private readonly ClipboardWriteGuard _writeGuard;
    private readonly ClipboardThread _clipboardThread;

    /// <summary>Cache de nome de exe; so tocado pela thread do clipboard.</summary>
    private readonly SourceAppCache _sourceApps = new();

    private HwndSource? _source;
    private DispatcherTimer? _debounce;
    private nint _listenerHandle;
    private uint _lastProcessedSequence;
    private volatile bool _paused;

    /// <summary>Pausa a captura pelo tray ou pelas configuracoes.</summary>
    public bool IsPaused
    {
        get => _paused;
        set => _paused = value;
    }

    public ClipboardMonitorService(
        ClipboardIngestService ingest,
        ClipboardWriteGuard writeGuard,
        ClipboardThread clipboardThread)
    {
        _ingest = ingest;
        _writeGuard = writeGuard;
        _clipboardThread = clipboardThread;

        // RF-P2.01: janela, hook e timer nascem NA thread do clipboard. Um
        // DispatcherTimer pertence ao dispatcher da thread que o cria, entao
        // criar aqui fora faria o Tick disparar na UI thread de novo.
        clipboardThread.Invoke(() =>
        {
            _debounce = new DispatcherTimer(DispatcherPriority.Send, clipboardThread.Dispatcher)
            {
                // apps disparam varios WM_CLIPBOARDUPDATE por copia (limpa e
                // escreve): a janela curta funde tudo em uma leitura so
                Interval = TimeSpan.FromMilliseconds(150),
            };
            _debounce.Tick += (_, _) =>
            {
                _debounce!.Stop();
                ProcessClipboard();
            };

            _source = new HwndSource(new HwndSourceParameters("KlipClipboardListener")
            {
                WindowStyle = 0,
                ExtendedWindowStyle = 0,
                ParentWindow = new nint(-3), // HWND_MESSAGE
            });
            _source.AddHook(WndProc);
            _listenerHandle = _source.Handle;

            if (!NativeMethods.AddClipboardFormatListener(_listenerHandle))
                StartupLog.Write("[ERRO] AddClipboardFormatListener falhou");
        });
    }

    /// <summary>Roda na thread do clipboard (a janela nasceu la).</summary>
    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_CLIPBOARDUPDATE)
        {
            handled = true;
            if (!_paused && _debounce is { } debounce)
            {
                debounce.Stop();
                debounce.Start(); // reinicia a janela de coalescencia
            }
        }
        return nint.Zero;
    }

    private void ProcessClipboard()
    {
        var sequence = NativeMethods.GetClipboardSequenceNumber();
        if (sequence == _lastProcessedSequence)
            return; // mesmo estado, eco duplicado
        _lastProcessedSequence = sequence;

        // anti-loop: ignora as nossas proprias gravacoes (RF-03.09). Como
        // gravacao e leitura agora rodam na MESMA thread, o registro do
        // sequence number sempre acontece antes deste teste.
        if (_writeGuard.IsOwnWrite(sequence))
            return;

        try
        {
            var snapshot = ReadSnapshotWithRetry();
            if (snapshot is null || snapshot.IsEmpty)
                return;

            // persistencia fora da thread do clipboard
            _ = Task.Run(() =>
            {
                try
                {
                    var item = _ingest.Ingest(snapshot);
                    if (item is not null)
                        StartupLog.WriteVerbose($"Ingest: {item.Type} {item.ByteSize}B de {item.SourceApp ?? "?"}");
                }
                catch (Exception ex)
                {
                    StartupLog.WriteException("Ingest", ex);
                }
            });
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("ClipboardUpdate", ex);
        }
    }

    /// <summary>
    /// RF-P2.01: o retry externo caiu de 6 tentativas para 2.
    ///
    /// O erro real e CLIPBRD_E_CANT_OPEN durante a janela em que o app de origem
    /// ainda segura o clipboard escrevendo os proprios formatos - alguns
    /// milissegundos. Uma segunda tentativa cobre isso; o WPF ja tem retry
    /// interno por cima (OleRetryCount = 10 x OleRetryDelay = 100 ms), entao
    /// insistir mais aqui so prolonga a disputa por um lock GLOBAL do sistema.
    /// Perder uma copia e o comportamento previsto pela propria documentacao do
    /// listener (ele pode perder mudancas rapidas) e e preferivel a segurar o
    /// clipboard de todo mundo. O proximo WM_CLIPBOARDUPDATE recupera o fluxo.
    ///
    /// Os 20 ms de espera custam zero para a UI: isto roda na thread STA propria.
    /// </summary>
    private ClipboardSnapshot? ReadSnapshotWithRetry()
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return ReadSnapshot();
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                if (attempt == 0)
                    Thread.Sleep(20);
            }
        }

        StartupLog.Write($"[AVISO] Clipboard ocupado por {DescribeClipboardHolder()}; item perdido");
        return null;
    }

    /// <summary>Diagnostico de quem estava com o clipboard aberto.</summary>
    private string DescribeClipboardHolder()
    {
        var hwnd = NativeMethods.GetOpenClipboardWindow();
        if (hwnd == nint.Zero)
            return "processo desconhecido";
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return _sourceApps.Resolve(pid) ?? $"pid {pid}";
    }

    /// <summary>
    /// Le o clipboard. Sempre na thread do clipboard (STA).
    ///
    /// RF-P2.01: a sondagem com IsClipboardFormatAvailable acontece ANTES de
    /// qualquer GetDataObject. Ela le a lista de formatos mantida pelo win32,
    /// sem abrir o clipboard e sem round-trip COM ao processo de origem, entao
    /// so pedimos GetData do que realmente existe. Antes eram ate 16 idas e
    /// voltas por copia (GetDataObject + GetDataPresent/GetData de 7 formatos +
    /// 2 opt-outs); agora sao 1 + o numero de formatos presentes - e zero
    /// quando a copia nao tem nada que interesse.
    /// </summary>
    private ClipboardSnapshot? ReadSnapshot()
    {
        // gerenciadores de senha pedem para ficar de fora do historico
        if (IsAvailable(IdViewerIgnore) || IsAvailable(IdExcludeMonitor))
            return null;

        var hasText = NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_UNICODETEXT);
        var hasPng = IsAvailable(IdPng);
        var hasBitmap = !hasPng && (
            NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_DIB) ||
            NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_DIBV5) ||
            NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_BITMAP));
        var hasFiles = NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_HDROP);

        // ClipboardSnapshot.IsEmpty so olha texto/imagem/arquivos: sem nenhum
        // deles a ingestao descartaria o item, entao nem vale abrir o clipboard
        if (!hasText && !hasPng && !hasBitmap && !hasFiles)
            return null;

        var hasHtml = IsAvailable(IdHtml);
        var hasRtf = IsAvailable(IdRtf);
        var checkHistoryOptOut = IsAvailable(IdCanIncludeHistory);

        var data = System.Windows.Clipboard.GetDataObject();
        if (data is null)
            return null;

        if (checkHistoryOptOut && ReadDwordFormat(data, CanIncludeHistoryFormat) == 0)
            return null;

        var (sourceApp, sourceTitle) = ResolveSource();

        string? text = null;
        string? htmlFragment = null;
        string? rtf = null;
        byte[]? pngBytes = null;
        int? width = null, height = null;
        IReadOnlyList<string>? files = null;

        if (hasText)
            text = data.GetData(DataFormats.UnicodeText) as string;

        if (hasHtml && data.GetData(DataFormats.Html) is string rawHtml)
            htmlFragment = ParseHtmlFragment(rawHtml);

        // mantem o RTF para a formatacao sobreviver ao colar em Word/WordPad/Outlook
        if (hasRtf && data.GetData(DataFormats.Rtf) is string rawRtf)
            rtf = rawRtf;

        if (hasPng && data.GetData(PngFormat) is MemoryStream pngStream)
        {
            pngBytes = pngStream.ToArray();
            var (w, h) = TryReadPngSize(pngBytes);
            width = w;
            height = h;
        }
        else if (hasBitmap && data.GetData(DataFormats.Bitmap, autoConvert: true) is BitmapSource image)
        {
            // CF_DIB como fallback: decodifica e reencoda em PNG. Continua caro,
            // mas agora e a thread do clipboard que paga, nao a UI.
            image.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            pngBytes = ms.ToArray();
            width = image.PixelWidth;
            height = image.PixelHeight;
        }

        if (hasFiles && data.GetData(DataFormats.FileDrop) is string[] drop)
            files = drop;

        return new ClipboardSnapshot
        {
            Text = text,
            HtmlFragment = htmlFragment,
            Rtf = rtf,
            PngBytes = pngBytes,
            ImageWidth = width,
            ImageHeight = height,
            Files = files,
            SourceApp = sourceApp,
            SourceTitle = sourceTitle,
        };
    }

    /// <summary>
    /// RF-P2.01: o WPF entrega o payload CF_HTML como string, mas o parser
    /// trabalha em bytes (os offsets do formato sao posicoes em BYTES). Antes
    /// isso era um Encoding.UTF8.GetBytes do payload inteiro - uma copia nova
    /// no heap por copia, e paginas coladas do navegador passam de centenas de
    /// KB. Agora a transcodificacao vai para um buffer do ArrayPool.
    /// </summary>
    private static string? ParseHtmlFragment(string rawHtml)
    {
        if (rawHtml.Length == 0)
            return null;

        var buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(rawHtml.Length));
        try
        {
            var written = Encoding.UTF8.GetBytes(rawHtml, buffer);
            return CfHtmlParser.TryParse(buffer.AsSpan(0, written), out var parsed)
                ? parsed.Fragment
                : null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsAvailable(uint formatId) =>
        formatId != 0 && NativeMethods.IsClipboardFormatAvailable(formatId);

    private static int? ReadDwordFormat(IDataObject data, string format)
    {
        try
        {
            if (data.GetData(format) is MemoryStream ms && ms.Length >= 4)
            {
                Span<byte> buf = stackalloc byte[4];
                ms.Position = 0;
                ms.ReadExactly(buf);
                return BitConverter.ToInt32(buf);
            }
        }
        catch (Exception)
        {
            // formato ilegivel, trata como se nao existisse
        }
        return null;
    }

    /// <summary>App/janela de origem a partir do dono do clipboard, com fallback no foreground.</summary>
    private (string? app, string? title) ResolveSource()
    {
        try
        {
            var owner = NativeMethods.GetClipboardOwner();
            var foreground = NativeMethods.GetForegroundWindow();
            var hwnd = owner != nint.Zero ? owner : foreground;
            if (hwnd == nint.Zero)
                return (null, null);

            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            var app = _sourceApps.Resolve(pid);

            // titulo: a janela em foreground e mais fiel que o owner, que costuma vir oculto
            var title = NativeMethods.GetWindowTextSafe(foreground);
            return (app, title.Length > 0 ? title : null);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    private static (int?, int?) TryReadPngSize(byte[] png)
    {
        // IHDR: width/height sao big-endian nos offsets 16..23
        if (png.Length < 24)
            return (null, null);
        var w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        var h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        return (w > 0 ? w : null, h > 0 ? h : null);
    }

    public void Dispose()
    {
        try
        {
            // RF-P2.01: o listener PRECISA sair na mesma thread que o registrou -
            // a janela message-only pertence a thread do clipboard
            _clipboardThread.Invoke(() =>
            {
                _debounce?.Stop();
                _debounce = null;

                if (_listenerHandle != nint.Zero)
                {
                    NativeMethods.RemoveClipboardFormatListener(_listenerHandle);
                    _listenerHandle = nint.Zero;
                }

                if (_source is { } source)
                {
                    source.RemoveHook(WndProc);
                    source.Dispose();
                    _source = null;
                }
            });
        }
        catch (Exception ex)
        {
            // encerramento e best effort; o handle morre com a thread de qualquer jeito
            StartupLog.WriteException("ClipboardMonitorDispose", ex);
        }
    }

    /// <summary>
    /// RF-P2.02: nome do exe de origem sem Process.GetProcessById.
    ///
    /// O caminho antigo criava um objeto Process por copia: abre handle com
    /// direitos amplos (PROCESS_QUERY_INFORMATION | PROCESS_VM_READ) e monta
    /// ProcessModule. Aqui o handle e aberto com PROCESS_QUERY_LIMITED_INFORMATION,
    /// o unico direito que atravessa niveis de integridade, e o caminho sai de
    /// QueryFullProcessImageNameW.
    ///
    /// A chave do cache inclui o tempo de criacao porque o Windows recicla PIDs
    /// agressivamente - so o pid daria nome errado depois de um restart do app
    /// de origem. Falha (processo elevado/protegido nega o handle) e silenciosa:
    /// o item entra sem origem, que e o comportamento correto.
    ///
    /// Sem lock: so a thread do clipboard chama isto.
    /// </summary>
    private sealed class SourceAppCache
    {
        private const int MaxEntries = 256;

        private readonly Dictionary<(uint Pid, long CreatedAt), string> _entries = [];
        private readonly Queue<(uint Pid, long CreatedAt)> _insertionOrder = new();

        public string? Resolve(uint pid)
        {
            if (pid == 0)
                return null;

            var handle = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == nint.Zero)
                return null;

            try
            {
                if (!NativeMethods.GetProcessTimes(handle, out var createdAt, out _, out _, out _))
                    return null;

                var key = (pid, createdAt);
                if (_entries.TryGetValue(key, out var cached))
                    return cached;

                var path = NativeMethods.QueryProcessImagePathSafe(handle);
                if (string.IsNullOrEmpty(path))
                    return null;

                var name = Path.GetFileName(path);
                if (name.Length == 0)
                    return null;

                Add(key, name);
                return name;
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }

        /// <summary>Descarte FIFO: o teto existe so para o cache nao virar um vazamento.</summary>
        private void Add((uint Pid, long CreatedAt) key, string name)
        {
            _entries[key] = name;
            _insertionOrder.Enqueue(key);

            while (_insertionOrder.Count > MaxEntries)
                _entries.Remove(_insertionOrder.Dequeue());
        }
    }
}
