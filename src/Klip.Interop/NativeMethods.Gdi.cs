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

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateCompatibleBitmap(nint hdc, int cx, int cy);

    [LibraryImport("gdi32.dll")]
    public static partial nint SelectObject(nint hdc, nint h);

    [LibraryImport("gdi32.dll")]
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
    /// Caller must DeleteObject the returned handle.
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
                var old = SelectObject(memDc, bitmap);
                // CAPTUREBLT pra pegar tambem as janelas layered
                BitBlt(memDc, 0, 0, width, height, screenDc, x, y, SRCCOPY | CAPTUREBLT);
                SelectObject(memDc, old);
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
