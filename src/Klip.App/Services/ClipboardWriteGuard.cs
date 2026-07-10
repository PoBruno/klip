using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Klip.Core.Clipboard;
using Klip.Core.Storage;
using Klip.Interop;

namespace Klip.App.Services;

/// <summary>
/// Every write we make to the clipboard goes through here and records the sequence
/// number so the monitor can ignore the echo. Call on the UI thread (STA).
/// </summary>
public sealed class ClipboardWriteGuard
{
    private uint _lastOwnSequence;

    /// <summary>True when the clipboard update came from one of our own writes.</summary>
    public bool IsOwnWrite(uint sequenceNumber) => sequenceNumber == _lastOwnSequence;

    public void WriteText(string text)
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, text);
        SetAndRecord(data);
    }

    /// <summary>
    /// Paste with full fidelity. Writes text + HTML + RTF together when we have them
    /// so the target app can pick the richest format it supports.
    /// With <paramref name="plainTextOnly"/> it forces plain text only.
    /// </summary>
    public void WriteItem(ClipboardItem item, bool plainTextOnly = false)
    {
        switch (item.Type)
        {
            case ClipboardItemType.Image when item.FilePath is not null:
                // caller (PasteService) already resolved FilePath to absolute
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

    /// <summary>Images go out as PNG + bitmap (DIB) for wider compatibility.</summary>
    public void WriteImageFromPngFile(string absolutePngPath)
    {
        var (bytes, bitmap) = DecodeImageFile(absolutePngPath);
        WriteImageFromPng(bytes, bitmap);
    }

    /// <summary>
    /// Reads the file and decodes the bitmap. This is the heavy part (disk read +
    /// full-size decode), so it can run on a background thread; the actual
    /// clipboard write (SetImage) still has to happen on the UI thread.
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

    /// <summary>Writes PNG bytes + an already decoded bitmap (used after a capture).</summary>
    public void WriteImageFromPng(byte[] pngBytes, BitmapSource bitmap)
    {
        var data = new DataObject();
        data.SetImage(bitmap);                             // CF_BITMAP/CF_DIB through WPF
        data.SetData("PNG", new MemoryStream(pngBytes));   // registered "PNG" format
        SetAndRecord(data);
    }

    public void WriteFiles(IEnumerable<string> paths)
    {
        var collection = new StringCollection();
        foreach (var path in paths)
            collection.Add(path);
        System.Windows.Clipboard.SetFileDropList(collection);
        RecordSequence();
    }

    // ----- snapshot/restore for "paste without clobbering the clipboard" -----

    /// <summary>Grabs the current clipboard content so we can put it back later.</summary>
    public IDataObject? SnapshotCurrent()
    {
        try
        {
            var current = System.Windows.Clipboard.GetDataObject();
            if (current is null)
                return null;
            // copy the formats into our own DataObject, the original is volatile
            var copy = new DataObject();
            foreach (var format in current.GetFormats())
            {
                try
                {
                    var value = current.GetData(format);
                    if (value is not null)
                        copy.SetData(format, value);
                }
                catch (Exception)
                {
                    // can't read this format, just skip it
                }
            }
            return copy;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Puts back a snapshot taken by <see cref="SnapshotCurrent"/>.</summary>
    public void Restore(IDataObject? snapshot)
    {
        if (snapshot is null)
            return;
        try
        {
            TrySetDataObject(snapshot);
            RecordSequence(); // counts as our own write (anti-loop)
        }
        catch (Exception)
        {
            // best effort, don't care if it fails
        }
    }

    private void SetAndRecord(DataObject data)
    {
        TrySetDataObject(data);
        RecordSequence();
    }

    /// <summary>
    /// SetDataObject can throw CLIPBRD_E_CANT_OPEN when another app is holding the
    /// clipboard. A couple of short retries handle that without a long WPF stall.
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
                System.Threading.Thread.Sleep(20);
            }
        }
    }

    private void RecordSequence() => _lastOwnSequence = NativeMethods.GetClipboardSequenceNumber();
}
