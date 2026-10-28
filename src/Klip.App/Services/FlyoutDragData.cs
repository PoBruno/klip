using System.Collections.Specialized;
using System.IO;
using System.Text;
using System.Windows;
using Klip.Core.Common;
using Klip.Core.Settings;
using Klip.Core.Storage;
using Klip.Interop;
using Klip.Interop.Clipboard;

namespace Klip.App.Services;

/// <summary>
/// Builds what a history card carries when it is dragged out of the flyout: an image
/// becomes a PNG file (the same virtual file object the clipboard uses), files stay
/// files, text goes as text plus a .txt for targets that only take files.
/// </summary>
public static class FlyoutDragData
{
    // a drag of more than this is never what the user wants, and the bytes sit in memory
    private const int MaxTextChars = 2_000_000;

    /// <param name="full">The complete row (not a list summary).</param>
    /// <param name="imagePath">Absolute path of the PNG when the item is an image.</param>
    /// <param name="isSecret">Secrets are not draggable.</param>
    public static IDataObject? Create(ClipboardItem full, string? imagePath, bool isSecret, AppSettings settings)
    {
        if (isSecret)
            return null;

        var local = full.LastCopiedAt.ToLocalTime().DateTime;

        if (full.Type == ClipboardItemType.Image && imagePath is not null && File.Exists(imagePath))
        {
            var png = File.ReadAllBytes(imagePath);
            var name = FileNameTemplate.ToFileName(settings.ImageFileNameTemplate, local, full.SourceApp ?? "Klip");
            var obj = VirtualFileDataObject.ForPng(png, name, () => ClipboardWriteGuard.PngToDib(png));
            return new DataObject(obj);
        }

        if (full.Type == ClipboardItemType.Files && full.FilesJson is not null)
        {
            var paths = ReadFiles(full.FilesJson);
            if (paths.Count == 0)
                return null;
            var data = new DataObject();
            var list = new StringCollection();
            list.AddRange(paths.ToArray());
            data.SetFileDropList(list);
            return data;
        }

        var text = full.TextContent;
        if (string.IsNullOrEmpty(text))
            return null;
        if (text.Length > MaxTextChars)
            text = text[..MaxTextChars];

        var textObj = new VirtualFileDataObject();
        textObj.SetData(NativeMethods.CF_UNICODETEXT, Encoding.Unicode.GetBytes(text + "\0"));
        textObj.AddFile(FileNameTemplate.ToFileName(settings.ImageFileNameTemplate, local, full.SourceApp ?? "Klip", ".txt"),
            Encoding.UTF8.GetBytes(text));
        return new DataObject(textObj);
    }

    private static List<string> ReadFiles(string json)
    {
        try
        {
            var all = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? [];
            return all.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }
}
