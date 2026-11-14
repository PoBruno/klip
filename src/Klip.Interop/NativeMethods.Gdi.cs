using System.Runtime.InteropServices;

namespace Klip.Interop;

/// <summary>GDI P/Invoke for screen capture via BitBlt.</summary>
public static partial class NativeMethods
{
    public const uint SRCCOPY = 0x00CC0020;
    public const uint CAPTUREBLT = 0x40000000;

    [LibraryImport("user32.dll")]
    public static partial nint GetDC(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(nint hWnd, nint hDC);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    public static partial nint CreateCompatibleBitmap(nint hdc, int cx, int cy);

    [LibraryImport("gdi32.dll")]
    public static partial nint SelectObject(nint hdc, nint h);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BitBlt(nint hdc, int x, int y, int cx, int cy,
        nint hdcSrc, int x1, int y1, uint rop);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint ho);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint hdc);

    /// <summary>
    /// Captures a screen region (physical virtual-screen coords) into an HBITMAP.
    /// Caller must DeleteObject the returned handle. Throws when GDI fails (out of GDI
    /// memory, secure desktop, display going away) instead of handing back a black
    /// bitmap that the overlay would show as a frozen black screen.
    /// </summary>
    public static nint CaptureScreenRegionToHBitmap(int x, int y, int width, int height)
    {
        var screenDc = GetDC(nint.Zero);
        try
        {
            var memDc = CreateCompatibleDC(screenDc);
            try
            {
                var bitmap = CreateCompatibleBitmap(screenDc, width, height);
                if (bitmap == nint.Zero)
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(),
                        $"CreateCompatibleBitmap {width}x{height} falhou");
                var old = SelectObject(memDc, bitmap);
                // CAPTUREBLT pra pegar tambem as janelas layered
                var copied = BitBlt(memDc, 0, 0, width, height, screenDc, x, y, SRCCOPY | CAPTUREBLT);
                var error = Marshal.GetLastPInvokeError();
                SelectObject(memDc, old);
                if (!copied)
                {
                    DeleteObject(bitmap);
                    throw new System.ComponentModel.Win32Exception(error,
                        $"BitBlt {width}x{height} em {x},{y} falhou");
                }
                return bitmap;
            }
            finally
            {
                DeleteDC(memDc);
            }
        }
        finally
        {
            ReleaseDC(nint.Zero, screenDc);
        }
    }
}
