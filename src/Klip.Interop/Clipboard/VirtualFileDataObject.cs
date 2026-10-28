using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Klip.Core.Clipboard;
using IDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Klip.Interop.Clipboard;

/// <summary>
/// COM data object that carries one or more files that do not exist on disk
/// (FileGroupDescriptorW + FileContents), next to ordinary formats such as PNG and
/// CF_DIB. Explorer, the desktop and file dialogs paste/drop it as real files; apps that
/// want an image keep reading PNG/DIB.
/// <para>
/// Works for both the clipboard (<see cref="NativeMethods.OleSetClipboard"/>, without
/// flushing, so the bytes are only handed out when someone asks) and drag and drop
/// (wrap it in a WPF <c>DataObject</c> and call <c>DragDrop.DoDragDrop</c>).
/// </para>
/// <para>
/// The calls arrive on the thread that published the object, so that thread must be STA
/// and keep pumping messages. Formats are rendered on demand: a lazy format (DIB of a 4K
/// shot is ~33 MB) only exists while somebody is reading it.
/// </para>
/// </summary>
[ComVisible(true)]
public sealed class VirtualFileDataObject : IDataObject
{
    public static readonly uint CfFileGroupDescriptorW = NativeMethods.RegisterClipboardFormat("FileGroupDescriptorW");
    public static readonly uint CfFileContents = NativeMethods.RegisterClipboardFormat("FileContents");
    public static readonly uint CfPng = NativeMethods.RegisterClipboardFormat("PNG");

    /// <summary>Marker so the monitor can tell our own object apart even if the sequence slips.</summary>
    public static readonly uint CfKlipMarker = NativeMethods.RegisterClipboardFormat("Klip.VirtualFile");

    private static readonly uint CfPerformedDropEffect = NativeMethods.RegisterClipboardFormat("Performed DropEffect");
    private static readonly uint CfPasteSucceeded = NativeMethods.RegisterClipboardFormat("Paste Succeeded");

    private readonly List<(VirtualFileEntry Entry, byte[] Contents)> _files = [];
    private readonly List<uint> _order = [];
    private readonly Dictionary<uint, Func<byte[]?>> _formats = [];

    // whatever the drop target / shell stores back on us (DropDescription, drag image...)
    private readonly Dictionary<uint, byte[]> _setByTarget = [];

    /// <summary>
    /// Raised when the shell reports a finished paste/drop ("Performed DropEffect").
    /// Runs on the publishing thread.
    /// </summary>
    public event Action? Transferred;

    public bool HasFiles => _files.Count > 0;

    /// <summary>
    /// Clipboard/drag object for a PNG: PNG bytes, optional lazy CF_DIB and, when
    /// <paramref name="fileName"/> is given, the same bytes as a virtual file.
    /// </summary>
    public static VirtualFileDataObject ForPng(byte[] png, string? fileName, Func<byte[]?>? dib = null)
    {
        ArgumentNullException.ThrowIfNull(png);
        var obj = new VirtualFileDataObject();
        // PNG first: apps take the first format they understand, and PNG keeps alpha
        obj.SetData(CfPng, png);
        if (dib is not null)
            obj.SetLazyData(NativeMethods.CF_DIB, dib);
        if (!string.IsNullOrEmpty(fileName))
            obj.AddFile(fileName, png);
        obj.SetData(CfKlipMarker, [1, 0, 0, 0]);
        return obj;
    }

    /// <summary>Adds a virtual file. The descriptor and FileContents are announced once.</summary>
    public void AddFile(string name, byte[] contents, DateTime? lastWriteUtc = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(contents);
        _files.Add((new VirtualFileEntry(name, contents.LongLength, lastWriteUtc ?? DateTime.UtcNow), contents));
        if (_files.Count == 1)
        {
            Announce(CfFileGroupDescriptorW, () => FileGroupDescriptor.Build(_files.ConvertAll(f => f.Entry)));
            Announce(CfFileContents, static () => null); // served per lindex in GetData
        }
    }

    public void SetData(uint format, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Announce(format, () => data);
    }

    public void SetData(string format, byte[] data) => SetData(NativeMethods.RegisterClipboardFormat(format), data);

    /// <summary>Format rendered only when a consumer asks for it (not cached).</summary>
    public void SetLazyData(uint format, Func<byte[]?> render)
    {
        ArgumentNullException.ThrowIfNull(render);
        Announce(format, render);
    }

    private void Announce(uint format, Func<byte[]?> render)
    {
        if (!_formats.ContainsKey(format))
            _order.Add(format);
        _formats[format] = render;
    }

    // ----- IDataObject -----

    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        medium = default;
        var cf = (uint)(ushort)format.cfFormat;

        if (cf == CfFileContents && _files.Count > 0)
        {
            // lindex -1 shows up from older callers that only expect one file
            var index = format.lindex < 0 ? 0 : format.lindex;
            if (index >= _files.Count)
                throw new COMException(null, NativeMethods.DV_E_LINDEX);
            var contents = _files[index].Contents;

            if ((format.tymed & TYMED.TYMED_ISTREAM) != 0)
            {
                medium = StreamMedium(contents);
                return;
            }
            if ((format.tymed & TYMED.TYMED_HGLOBAL) != 0)
            {
                medium = HGlobalMedium(contents);
                return;
            }
            throw new COMException(null, NativeMethods.DV_E_TYMED);
        }

        byte[]? data = null;
        if (_formats.TryGetValue(cf, out var render))
            data = render();
        else if (_setByTarget.TryGetValue(cf, out var stored))
            data = stored;

        if (data is null)
            throw new COMException(null, NativeMethods.DV_E_FORMATETC);
        if ((format.tymed & TYMED.TYMED_HGLOBAL) == 0)
            throw new COMException(null, NativeMethods.DV_E_TYMED);
        medium = HGlobalMedium(data);
    }

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) =>
        throw new COMException(null, NativeMethods.DV_E_FORMATETC);

    public int QueryGetData(ref FORMATETC format)
    {
        var cf = (uint)(ushort)format.cfFormat;
        if (format.dwAspect != DVASPECT.DVASPECT_CONTENT)
            return NativeMethods.DV_E_FORMATETC;
        if (cf == CfFileContents && _files.Count > 0)
            return (format.tymed & (TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL)) != 0 ? NativeMethods.S_OK : NativeMethods.DV_E_TYMED;
        if (!_formats.ContainsKey(cf) && !_setByTarget.ContainsKey(cf))
            return NativeMethods.DV_E_FORMATETC;
        return (format.tymed & TYMED.TYMED_HGLOBAL) != 0 ? NativeMethods.S_OK : NativeMethods.DV_E_TYMED;
    }

    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
    {
        formatOut = formatIn;
        formatOut.ptd = 0;
        return NativeMethods.DATA_S_SAMEFORMATETC;
    }

    public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release)
    {
        var cf = (uint)(ushort)formatIn.cfFormat;
        try
        {
            if (medium.tymed == TYMED.TYMED_HGLOBAL && medium.unionmember != 0 &&
                NativeMethods.HGlobalToBytes(medium.unionmember) is { } bytes)
            {
                _setByTarget[cf] = bytes;
                if (cf == CfPerformedDropEffect || cf == CfPasteSucceeded)
                    Transferred?.Invoke();
            }
        }
        finally
        {
            if (release)
                NativeMethods.ReleaseStgMedium(ref medium);
        }
    }

    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
    {
        if (direction != DATADIR.DATADIR_GET)
            throw new COMException(null, NativeMethods.E_NOTIMPL);

        var list = new FORMATETC[_order.Count];
        for (var i = 0; i < _order.Count; i++)
        {
            var cf = _order[i];
            var isContents = cf == CfFileContents;
            list[i] = new FORMATETC
            {
                cfFormat = unchecked((short)(ushort)cf),
                dwAspect = DVASPECT.DVASPECT_CONTENT,
                lindex = isContents ? 0 : -1,
                ptd = 0,
                tymed = isContents ? TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL : TYMED.TYMED_HGLOBAL,
            };
        }
        return new FormatEnumerator(list, 0);
    }

    public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection)
    {
        connection = 0;
        return NativeMethods.OLE_E_ADVISENOTSUPPORTED;
    }

    public void DUnadvise(int connection) =>
        throw new COMException(null, NativeMethods.OLE_E_ADVISENOTSUPPORTED);

    public int EnumDAdvise(out IEnumSTATDATA? enumAdvise)
    {
        enumAdvise = null;
        return NativeMethods.OLE_E_ADVISENOTSUPPORTED;
    }

    // ----- helpers -----

    private static STGMEDIUM HGlobalMedium(byte[] data)
    {
        var h = NativeMethods.BytesToHGlobal(data);
        if (h == 0)
            throw new OutOfMemoryException();
        return new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = h };
    }

    private static STGMEDIUM StreamMedium(byte[] data)
    {
        var h = NativeMethods.BytesToHGlobal(data);
        if (h == 0)
            throw new OutOfMemoryException();
        var hr = NativeMethods.CreateStreamOnHGlobal(h, true, out var stream);
        if (hr < 0)
        {
            NativeMethods.GlobalFree(h);
            throw new COMException(null, hr);
        }
        try
        {
            // GlobalSize can round up; the stream must end exactly at the last byte
            stream.SetSize(data.LongLength);
            return new STGMEDIUM
            {
                tymed = TYMED.TYMED_ISTREAM,
                unionmember = Marshal.GetComInterfaceForObject(stream, typeof(IStream)),
            };
        }
        finally
        {
            Marshal.ReleaseComObject(stream);
        }
    }

    [ComVisible(true)]
    private sealed class FormatEnumerator(FORMATETC[] items, int position) : IEnumFORMATETC
    {
        private int _position = position;

        public int Next(int celt, FORMATETC[] rgelt, int[]? pceltFetched)
        {
            var n = 0;
            while (n < celt && _position < items.Length)
                rgelt[n++] = items[_position++];
            if (pceltFetched is { Length: > 0 })
                pceltFetched[0] = n;
            return n == celt ? NativeMethods.S_OK : NativeMethods.S_FALSE;
        }

        public int Skip(int celt)
        {
            _position = Math.Min(items.Length, _position + celt);
            return _position < items.Length ? NativeMethods.S_OK : NativeMethods.S_FALSE;
        }

        public int Reset()
        {
            _position = 0;
            return NativeMethods.S_OK;
        }

        public void Clone(out IEnumFORMATETC newEnum) => newEnum = new FormatEnumerator(items, _position);
    }
}
