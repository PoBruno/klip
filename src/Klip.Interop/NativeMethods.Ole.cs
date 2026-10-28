using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Klip.Interop;

/// <summary>
/// OLE clipboard and HGLOBAL helpers. The OLE calls take COM interfaces, so they use
/// DllImport (LibraryImport does not marshal ComImport interfaces).
/// </summary>
public static partial class NativeMethods
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int DV_E_FORMATETC = unchecked((int)0x80040064);
    public const int DV_E_TYMED = unchecked((int)0x80040069);
    public const int DV_E_LINDEX = unchecked((int)0x80040068);
    public const int OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003);
    public const int DATA_S_SAMEFORMATETC = 0x00040130;
    public const int E_NOTIMPL = unchecked((int)0x80004001);

    public const uint GMEM_MOVEABLE = 0x0002;

    [LibraryImport("ole32.dll")]
    public static partial int OleInitialize(nint pvReserved);

    /// <summary>Publishes the object as the clipboard WITHOUT flushing it (data stays lazy).</summary>
    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int OleSetClipboard([MarshalAs(UnmanagedType.Interface)] IDataObject? pDataObj);

    /// <summary>Renders every format and lets go of the object. Use on shutdown.</summary>
    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int OleFlushClipboard();

    /// <summary>S_OK when this object is still the one on the clipboard.</summary>
    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int OleIsCurrentClipboard([MarshalAs(UnmanagedType.Interface)] IDataObject pDataObj);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int CreateStreamOnHGlobal(nint hGlobal, [MarshalAs(UnmanagedType.Bool)] bool fDeleteOnRelease, out IStream ppstm);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern void ReleaseStgMedium(ref STGMEDIUM pmedium);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint GlobalLock(nint hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(nint hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nuint GlobalSize(nint hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint GlobalFree(nint hMem);

    /// <summary>Copies bytes into a new movable HGLOBAL. Zero on failure.</summary>
    public static nint BytesToHGlobal(ReadOnlySpan<byte> data)
    {
        var h = GlobalAlloc(GMEM_MOVEABLE, (nuint)Math.Max(1, data.Length));
        if (h == 0)
            return 0;
        var p = GlobalLock(h);
        if (p == 0)
        {
            GlobalFree(h);
            return 0;
        }
        unsafe
        {
            data.CopyTo(new Span<byte>((void*)p, data.Length));
        }
        GlobalUnlock(h);
        return h;
    }

    /// <summary>Copies the content of an HGLOBAL. Null if it cannot be locked.</summary>
    public static byte[]? HGlobalToBytes(nint h)
    {
        if (h == 0)
            return null;
        var size = (int)GlobalSize(h);
        var p = GlobalLock(h);
        if (p == 0)
            return null;
        try
        {
            var bytes = new byte[size];
            Marshal.Copy(p, bytes, 0, size);
            return bytes;
        }
        finally
        {
            GlobalUnlock(h);
        }
    }
}
