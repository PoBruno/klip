using System.Buffers.Binary;
using System.Text;

namespace Klip.Core.Clipboard;

/// <summary>One file announced through FileGroupDescriptorW.</summary>
public readonly record struct VirtualFileEntry(string Name, long Size, DateTime LastWriteTimeUtc);

/// <summary>
/// Binary layout of FILEGROUPDESCRIPTORW: a count followed by N FILEDESCRIPTORW of 592
/// bytes each. Kept here (no interop) so it can be tested byte by byte.
/// </summary>
public static class FileGroupDescriptor
{
    public const int DescriptorSize = 592;

    // FD_ATTRIBUTES | FD_WRITESTIME | FD_FILESIZE | FD_PROGRESSUI | FD_UNICODE
    public const uint DefaultFlags = 0x4 | 0x20 | 0x40 | 0x4000 | 0x80000000;

    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const int MaxNameChars = 259; // cFileName[260] with the terminator

    public static byte[] Build(IReadOnlyList<VirtualFileEntry> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var buffer = new byte[4 + DescriptorSize * files.Count];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, files.Count);

        for (var i = 0; i < files.Count; i++)
        {
            var d = buffer.AsSpan(4 + i * DescriptorSize, DescriptorSize);
            var f = files[i];
            BinaryPrimitives.WriteUInt32LittleEndian(d, DefaultFlags);
            // clsid(16) sizel(8) pointl(8) stay zero
            BinaryPrimitives.WriteUInt32LittleEndian(d[36..], FILE_ATTRIBUTE_NORMAL);
            BinaryPrimitives.WriteInt64LittleEndian(d[56..], f.LastWriteTimeUtc.ToFileTimeUtc());
            BinaryPrimitives.WriteUInt32LittleEndian(d[64..], (uint)((ulong)f.Size >> 32));
            BinaryPrimitives.WriteUInt32LittleEndian(d[68..], (uint)((ulong)f.Size & 0xFFFFFFFF));

            var name = f.Name.Length > MaxNameChars ? f.Name[..MaxNameChars] : f.Name;
            Encoding.Unicode.GetBytes(name, d[72..]);
        }
        return buffer;
    }
}

/// <summary>
/// Decisions for "Ctrl+V on an Explorer folder creates the png". Pure so the rules can be
/// tested without a desktop.
/// </summary>
public static class ExplorerPastePolicy
{
    /// <summary>Top level windows that are a folder view: Explorer and the desktop.</summary>
    public static bool IsFolderWindowClass(string? className) =>
        className is "CabinetWClass" or "ExploreWClass" || IsDesktopClass(className);

    public static bool IsDesktopClass(string? className) =>
        className is "Progman" or "WorkerW";

    /// <summary>
    /// Focus has to be on the item view itself. Anything else (rename box, address bar,
    /// search box, the XAML islands of the Win11 toolbar) gets the normal Ctrl+V.
    /// </summary>
    public static bool IsItemViewFocusClass(string? className) =>
        className is "DirectUIHWND" or "SysListView32";

    /// <summary>
    /// Image on the clipboard and nothing Explorer could paste on its own. A file format
    /// (real or virtual) means the native paste already works, so we stay out.
    /// </summary>
    public static bool IsCandidate(bool hasImage, bool hasFiles, bool optedOut) =>
        hasImage && !hasFiles && !optedOut;
}
