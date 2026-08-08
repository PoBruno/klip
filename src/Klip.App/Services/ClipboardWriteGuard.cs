using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Klip.Core.Clipboard;
using Klip.Core.Storage;
using Klip.Interop;

namespace Klip.App.Services;

/// <summary>
/// Toda gravacao nossa no clipboard passa por aqui e registra o sequence number
/// para o monitor ignorar o eco (RF-03.09).
///
/// RF-P2.01 / ADR-P.05: pode ser chamado de QUALQUER thread - a chamada real ao
/// clipboard e marshalizada para a <see cref="ClipboardThread"/>. Nenhum
/// chamador abre o clipboard na UI thread. O trabalho caro (ler arquivo,
/// decodificar imagem, montar CF_HTML) fica de fora do Invoke, na thread do
/// chamador, para nao ocupar a thread do clipboard mais que o necessario.
/// </summary>
public sealed class ClipboardWriteGuard(ClipboardThread clipboardThread)
{
    /// <summary>
    /// RF-P2.01: lista fixa dos formatos que o Klip sabe restaurar.
    ///
    /// MUDANCA DE COMPORTAMENTO: antes o snapshot enumerava GetFormats() e
    /// chamava GetData em CADA formato. Copiando do Excel/Word isso sao dezenas
    /// de round-trips COM ao app de origem (Excel publica ~25 formatos
    /// proprietarios) so para restaurar coisas que o Klip nunca usa. Agora o
    /// "colar sem sujar o clipboard" restaura apenas texto, HTML, RTF, imagem e
    /// lista de arquivos; formatos proprietarios do app de origem se perdem.
    /// </summary>
    private static readonly (uint Id, string Name)[] SnapshotFormats =
    [
        (NativeMethods.CF_UNICODETEXT, DataFormats.UnicodeText),
        (NativeMethods.RegisterClipboardFormat(DataFormats.Html), DataFormats.Html),
        (NativeMethods.RegisterClipboardFormat(DataFormats.Rtf), DataFormats.Rtf),
        (NativeMethods.RegisterClipboardFormat("PNG"), "PNG"),
        (NativeMethods.CF_DIB, DataFormats.Bitmap),
        (NativeMethods.CF_HDROP, DataFormats.FileDrop),
    ];

    /// <summary>So a thread do clipboard le e escreve isto.</summary>
    private uint _lastOwnSequence;

    /// <summary>True quando a mudanca do clipboard veio de uma gravacao nossa.</summary>
    public bool IsOwnWrite(uint sequenceNumber) => sequenceNumber == _lastOwnSequence;

    public void WriteText(string text)
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, text);
        SetAndRecord(data);
    }

    /// <summary>
    /// Colagem com fidelidade. Grava texto + HTML + RTF juntos quando existem,
    /// para o app de destino escolher o formato mais rico que entende.
    /// Com <paramref name="plainTextOnly"/> forca texto puro.
    /// </summary>
    public void WriteItem(ClipboardItem item, bool plainTextOnly = false)
    {
        switch (item.Type)
        {
            case ClipboardItemType.Image when item.FilePath is not null:
                // o chamador (PasteService) ja resolveu FilePath para absoluto
                WriteImageFromPngFile(item.FilePath);
                return;

            case ClipboardItemType.Files when item.FilesJson is not null:
                var files = System.Text.Json.JsonSerializer.Deserialize<List<string>>(item.FilesJson) ?? [];
                var existing = files.Where(File.Exists).ToList();
                // se os arquivos sumiram do disco, cola o texto do caminho como fallback
                if (existing.Count > 0)
                    WriteFiles(existing);
                else if (item.TextContent is not null)
                    WriteText(item.TextContent);
                return;

            default:
                var text = item.TextContent ?? "";
                var data = new DataObject();
                data.SetData(DataFormats.UnicodeText, text);
                if (!plainTextOnly)
                {
                    if (!string.IsNullOrEmpty(item.HtmlContent))
                        data.SetData(DataFormats.Html, CfHtmlParser.BuildCfHtml(item.HtmlContent));
                    if (!string.IsNullOrEmpty(item.RtfContent))
                        data.SetData(DataFormats.Rtf, item.RtfContent);
                }
                SetAndRecord(data);
                return;
        }
    }

    /// <summary>Le o arquivo, decodifica e grava. O decode NAO ocupa a thread do clipboard.</summary>
    public void WriteImageFromPngFile(string absolutePngPath)
    {
        var (bytes, bitmap) = DecodeImageFile(absolutePngPath);
        WriteImageFromPng(bytes, bitmap);
    }

    /// <summary>
    /// Le o arquivo e decodifica o bitmap. E a parte pesada (disco + decode em
    /// tamanho cheio), entao roda na thread do chamador; so a gravacao em si vai
    /// para a thread do clipboard. O bitmap sai congelado, atravessa threads.
    /// </summary>
    public static (byte[] bytes, BitmapSource bitmap) DecodeImageFile(string absolutePngPath)
    {
        var bytes = File.ReadAllBytes(absolutePngPath);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(bytes);
        bitmap.EndInit();
        bitmap.Freeze();
        return (bytes, bitmap);
    }

    /// <summary>
    /// Grava bytes PNG + um bitmap ja decodificado (usado depois de uma captura).
    ///
    /// RF-P2.05 (parcial): NAO oferecemos CF_DIB nem CF_DIBV5 explicitamente. O
    /// SetImage do WPF publica CF_BITMAP e o proprio Windows sintetiza
    /// CF_DIB &lt;-&gt; CF_BITMAP &lt;-&gt; CF_DIBV5 sob demanda, entao anunciar
    /// as variantes so faria o OleFlushClipboard materializar a mesma imagem
    /// varias vezes (um screenshot 4K da ~33 MB por copia de DIB). Conferido:
    /// SetImage + SetData("PNG") ja e o conjunto minimo, nao ha redundancia
    /// para remover aqui.
    ///
    /// O copy: true (que dispara OleFlushClipboard e materializa tudo na hora)
    /// continua por ora - trocar por delayed rendering exige implementar
    /// IDataObject com TYMED sob demanda e fica no backlog.
    /// </summary>
    public void WriteImageFromPng(byte[] pngBytes, BitmapSource bitmap)
    {
        // RF-P2.01: congela AQUI, na thread que criou o bitmap - um BitmapSource
        // vivo e um DispatcherObject e estouraria ao ser tocado na thread do
        // clipboard. Todos os chamadores ja entregam congelado; isto e a rede.
        if (!bitmap.IsFrozen && bitmap.CanFreeze)
            bitmap.Freeze();

        var data = new DataObject();
        data.SetImage(bitmap);                             // CF_BITMAP via WPF
        data.SetData("PNG", new MemoryStream(pngBytes));   // formato registrado "PNG"
        SetAndRecord(data);
    }

    public void WriteFiles(IEnumerable<string> paths)
    {
        var collection = new StringCollection();
        foreach (var path in paths)
            collection.Add(path);

        OnClipboardThread(() =>
        {
            System.Windows.Clipboard.SetFileDropList(collection);
            RecordSequence();
        });
    }

    // ----- snapshot/restore para "colar sem sujar o clipboard" -----

    /// <summary>Guarda o conteudo atual do clipboard para devolver depois.</summary>
    public IDataObject? SnapshotCurrent()
    {
        try
        {
            return clipboardThread.Invoke<IDataObject?>(ReadSnapshotFormats);
        }
        catch (TimeoutException)
        {
            StartupLog.Write("[AVISO] Snapshot do clipboard abortado: thread STA ocupada");
            return null;
        }
    }

    private static IDataObject? ReadSnapshotFormats()
    {
        try
        {
            // RF-P2.01: sonda primeiro. IsClipboardFormatAvailable nao abre o
            // clipboard e nao fala com o processo de origem; se nada da lista
            // fixa estiver la, nem chegamos a pedir o IDataObject.
            Span<bool> wanted = stackalloc bool[SnapshotFormats.Length];
            var any = false;
            for (var i = 0; i < SnapshotFormats.Length; i++)
            {
                var id = SnapshotFormats[i].Id;
                wanted[i] = id != 0 && NativeMethods.IsClipboardFormatAvailable(id);
                any |= wanted[i];
            }
            if (!any)
                return null;

            var current = System.Windows.Clipboard.GetDataObject();
            if (current is null)
                return null;

            // copia os formatos para um DataObject nosso, o original e volatil
            var copy = new DataObject();
            for (var i = 0; i < SnapshotFormats.Length; i++)
            {
                if (!wanted[i])
                    continue;
                try
                {
                    var value = current.GetData(SnapshotFormats[i].Name);
                    if (value is null)
                        continue;
                    // congela para o objeto poder atravessar threads sem afinidade
                    if (value is BitmapSource { IsFrozen: false, CanFreeze: true } bitmap)
                        bitmap.Freeze();
                    copy.SetData(SnapshotFormats[i].Name, value);
                }
                catch (Exception)
                {
                    // formato ilegivel, pula
                }
            }
            return copy;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Devolve um snapshot tirado por <see cref="SnapshotCurrent"/>.</summary>
    public void Restore(IDataObject? snapshot)
    {
        if (snapshot is null)
            return;
        try
        {
            OnClipboardThread(() =>
            {
                TrySetDataObject(snapshot);
                RecordSequence(); // conta como gravacao nossa (anti-loop)
            });
        }
        catch (Exception)
        {
            // best effort, tudo bem falhar
        }
    }

    private void SetAndRecord(DataObject data) => OnClipboardThread(() =>
    {
        TrySetDataObject(data);
        RecordSequence();
    });

    /// <summary>
    /// RF-P2.01: unico ponto de marshalizacao das gravacoes. O timeout defensivo
    /// do <see cref="ClipboardThread"/> vira log em vez de excecao - chamadores
    /// como o editor e a captura rodam na UI thread e sempre trataram gravacao
    /// no clipboard como best effort; deixar um TimeoutException subir ali
    /// derrubaria o app por causa de um clipboard ocupado.
    /// </summary>
    private void OnClipboardThread(Action action)
    {
        try
        {
            clipboardThread.Invoke(action);
        }
        catch (TimeoutException)
        {
            StartupLog.Write("[AVISO] Gravacao no clipboard abortada: thread STA ocupada");
        }
    }

    /// <summary>
    /// SetDataObject pode estourar CLIPBRD_E_CANT_OPEN quando outro app esta com
    /// o clipboard. Duas repeticoes curtas resolvem - e agora os Sleep caem na
    /// thread do clipboard, nunca na UI (RF-P2.01).
    /// </summary>
    private static void TrySetDataObject(object data)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetDataObject(data, copy: true);
                return;
            }
            catch (Exception) when (attempt < 2)
            {
                Thread.Sleep(20);
            }
        }
    }

    private void RecordSequence() => _lastOwnSequence = NativeMethods.GetClipboardSequenceNumber();
}
