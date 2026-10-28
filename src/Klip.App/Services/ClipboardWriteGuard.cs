using System.Collections.Specialized;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Klip.Core.Clipboard;
using Klip.Core.Common;
using Klip.Core.Settings;
using Klip.Core.Storage;
using Klip.Interop;
using Klip.Interop.Clipboard;

namespace Klip.App.Services;

/// <summary>
/// Toda gravacao nossa no clipboard passa por aqui e registra o sequence number
/// para o monitor ignorar o eco.
///
/// pode ser chamado de QUALQUER thread - a chamada real ao
/// clipboard e marshalizada para a <see cref="ClipboardThread"/>. Nenhum
/// chamador abre o clipboard na UI thread. O trabalho caro (ler arquivo,
/// decodificar imagem, montar CF_HTML) fica de fora do Invoke, na thread do
/// chamador, para nao ocupar a thread do clipboard mais que o necessario.
/// </summary>
public sealed class ClipboardWriteGuard(ClipboardThread clipboardThread, SettingsService settings)
{
    /// <summary>
    /// lista fixa dos formatos que o Klip sabe restaurar.
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

    // last image object we published; weak so a big png does not outlive the clipboard
    private WeakReference<VirtualFileDataObject>? _lastImageObject;

    // snapshot wrappers that stand for "our image object was on the clipboard"
    private readonly ConditionalWeakTable<DataObject, VirtualFileDataObject> _imageSnapshots = new();

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
                WriteImageFromPng(File.ReadAllBytes(item.FilePath), item.SourceApp, item.LastCopiedAt.LocalDateTime);
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

    /// <summary>Le o arquivo e grava. O DIB so e montado se algum app pedir.</summary>
    public void WriteImageFromPngFile(string absolutePngPath) =>
        WriteImageFromPng(File.ReadAllBytes(absolutePngPath));

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
    /// Grava bytes PNG + um bitmap ja decodificado (usado depois de uma captura). The
    /// bitmap is only a fallback now: the png bytes are what goes to the clipboard.
    /// </summary>
    public void WriteImageFromPng(byte[] pngBytes, BitmapSource bitmap)
    {
        if (pngBytes.Length > 0)
        {
            WriteImageFromPng(pngBytes);
            return;
        }

        // no png (should not happen): old path, WPF publishes CF_BITMAP
        if (!bitmap.IsFrozen && bitmap.CanFreeze)
            bitmap.Freeze();
        var data = new DataObject();
        data.SetImage(bitmap);
        SetAndRecord(data);
    }

    /// <summary>
    /// Puts a png on the clipboard as our own OLE data object: PNG bytes, a CF_DIB that is
    /// only built when some app asks for it and, setting on, the same png as a virtual
    /// file, so Ctrl+V on an Explorer folder creates it. Nothing is flushed: apps that
    /// only want PNG never pay for the 30+ MB DIB of a 4K shot.
    /// </summary>
    /// <param name="sourceApp">Goes into the {app} token of the file name.</param>
    /// <param name="copiedAt">Time used in the file name; defaults to now.</param>
    public void WriteImageFromPng(byte[] pngBytes, string? sourceApp = null, DateTime? copiedAt = null)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);
        var s = settings.Current;
        var fileName = s.AttachFileToCopiedImages
            ? FileNameTemplate.ToFileName(s.ImageFileNameTemplate, copiedAt ?? DateTime.Now, sourceApp ?? "Klip")
            : null;

        var obj = VirtualFileDataObject.ForPng(pngBytes, fileName, () => PngToDib(pngBytes));
        OnClipboardThread(() =>
        {
            if (!TryPublish(obj))
            {
                // OLE refused three times: plain WPF path so the copy is not lost
                var data = new DataObject();
                data.SetData("PNG", new MemoryStream(pngBytes));
                TrySetDataObject(data);
            }
            RecordSequence();
        });
    }

    /// <summary>OleSetClipboard without flush, same short retry as the WPF path. Clipboard thread.</summary>
    private bool TryPublish(VirtualFileDataObject obj)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (NativeMethods.OleSetClipboard(obj) >= 0)
            {
                _lastImageObject = new WeakReference<VirtualFileDataObject>(obj);
                clipboardThread.TrackOwnedOleObject(obj);
                return true;
            }
            if (attempt < 2)
                Thread.Sleep(20);
        }
        StartupLog.Write("[AVISO] OleSetClipboard falhou, imagem gravada sem arquivo");
        return false;
    }

    /// <summary>
    /// CF_DIB on demand: 32 bpp BI_RGB, bottom-up. Runs on the clipboard thread when an
    /// app (Paint, Word, older stuff) asks; not cached on purpose.
    /// </summary>
    internal static byte[]? PngToDib(byte[] png)
    {
        try
        {
            var decoder = BitmapDecoder.Create(new MemoryStream(png),
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames[0];
            if (frame.Format != PixelFormats.Bgra32)
                frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);

            int w = frame.PixelWidth, h = frame.PixelHeight, stride = w * 4;
            const int header = 40;
            var dib = new byte[header + stride * h];
            var span = dib.AsSpan();
            BitConverter.TryWriteBytes(span, header);           // biSize
            BitConverter.TryWriteBytes(span[4..], w);           // biWidth
            BitConverter.TryWriteBytes(span[8..], h);           // biHeight > 0 = bottom-up
            BitConverter.TryWriteBytes(span[12..], (short)1);   // biPlanes
            BitConverter.TryWriteBytes(span[14..], (short)32);  // biBitCount
            BitConverter.TryWriteBytes(span[20..], stride * h); // biSizeImage (BI_RGB = 0 at 16)

            var pixels = new byte[stride * h];
            frame.CopyPixels(pixels, stride, 0);
            for (var y = 0; y < h; y++)
                Buffer.BlockCopy(pixels, y * stride, dib, header + (h - 1 - y) * stride, stride);
            return dib;
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("PngToDib", ex);
            return null;
        }
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
            return clipboardThread.Invoke<IDataObject?>(() => SnapshotOwnImage() ?? ReadSnapshotFormats());
        }
        catch (TimeoutException)
        {
            StartupLog.Write("[AVISO] Snapshot do clipboard abortado: thread STA ocupada");
            return null;
        }
    }

    /// <summary>
    /// When the clipboard is still our image object, the "snapshot" is the object itself:
    /// copying it into a WPF DataObject would drop the virtual file on restore.
    /// </summary>
    private DataObject? SnapshotOwnImage()
    {
        if (_lastImageObject is not { } weak || !weak.TryGetTarget(out var obj) ||
            !ClipboardThread.IsCurrentClipboard(obj))
            return null;
        var wrapper = new DataObject(obj);
        _imageSnapshots.AddOrUpdate(wrapper, obj);
        return wrapper;
    }

    private static IDataObject? ReadSnapshotFormats()
    {
        try
        {
            // sonda primeiro. IsClipboardFormatAvailable nao abre o
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
                if (snapshot is DataObject wrapper && _imageSnapshots.TryGetValue(wrapper, out var obj))
                {
                    if (!TryPublish(obj))
                        TrySetDataObject(snapshot);
                }
                else
                {
                    TrySetDataObject(snapshot);
                }
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
    /// unico ponto de marshalizacao das gravacoes. O timeout defensivo
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
    /// thread do clipboard, nunca na UI.
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

    // OleSetClipboard bumps the sequence several times in one call; reading it after the
    // call is enough because the monitor only looks at the last value after its debounce
    private void RecordSequence() => _lastOwnSequence = NativeMethods.GetClipboardSequenceNumber();
}
