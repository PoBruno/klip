using System.Buffers.Binary;
using System.Text;
using Klip.Core.Clipboard;

namespace Klip.Core.Tests;

public class PasteAsFileTests
{
    [Fact]
    public void Descriptor_layout_matches_win32()
    {
        var when = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
        var bytes = FileGroupDescriptor.Build([new VirtualFileEntry("Klip.png", 0x1_0000_0010, when)]);

        Assert.Equal(4 + 592, bytes.Length);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(bytes));
        var d = bytes.AsSpan(4);
        Assert.Equal(FileGroupDescriptor.DefaultFlags, BinaryPrimitives.ReadUInt32LittleEndian(d));
        Assert.Equal(0x80u, BinaryPrimitives.ReadUInt32LittleEndian(d[36..]));
        Assert.Equal(when.ToFileTimeUtc(), BinaryPrimitives.ReadInt64LittleEndian(d[56..]));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(d[64..]));
        Assert.Equal(0x10u, BinaryPrimitives.ReadUInt32LittleEndian(d[68..]));
        Assert.Equal("Klip.png", Encoding.Unicode.GetString(d.Slice(72, 16)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(d[(72 + 16)..])); // terminator
    }

    [Fact]
    public void Descriptor_handles_many_files_and_long_names()
    {
        var now = DateTime.UtcNow;
        var bytes = FileGroupDescriptor.Build(
        [
            new VirtualFileEntry(new string('a', 400), 1, now),
            new VirtualFileEntry("b.png", 2, now),
        ]);
        Assert.Equal(4 + 2 * 592, bytes.Length);
        // the last wchar of cFileName stays zero even with a 400 char name
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4 + 590)));
        Assert.Equal("b", Encoding.Unicode.GetString(bytes, 4 + 592 + 72, 2));
    }

    [Theory]
    [InlineData("CabinetWClass", true)]
    [InlineData("Progman", true)]
    [InlineData("WorkerW", true)]
    [InlineData("Notepad", false)]
    [InlineData(null, false)]
    public void Folder_windows(string? cls, bool expected) =>
        Assert.Equal(expected, ExplorerPastePolicy.IsFolderWindowClass(cls));

    [Theory]
    [InlineData("DirectUIHWND", true)]
    [InlineData("SysListView32", true)]
    [InlineData("Edit", false)]
    [InlineData("Microsoft.UI.Content.DesktopChildSiteBridge", false)]
    [InlineData("", false)]
    public void Only_the_item_view_takes_the_paste(string cls, bool expected) =>
        Assert.Equal(expected, ExplorerPastePolicy.IsItemViewFocusClass(cls));

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, false, false, false)]
    public void Candidate(bool image, bool files, bool optedOut, bool expected) =>
        Assert.Equal(expected, ExplorerPastePolicy.IsCandidate(image, files, optedOut));
}
