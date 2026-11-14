namespace Klip.Core.Imaging;

/// <summary>
/// Checks on the alpha byte of a 32 bpp BGRA buffer (4 bytes per pixel, alpha last).
/// </summary>
public static class AlphaChannel
{
    /// <summary>
    /// True when the buffer has at least one pixel and every alpha byte is zero.
    /// Some apps (WeChat 4.x is the known one) put a 32 bpp bitmap on the clipboard with
    /// valid colors and alpha 0 everywhere, which means they never filled the alpha, not
    /// that the picture is invisible. Stops at the first non zero alpha, so a normal
    /// opaque image costs one pixel.
    /// </summary>
    public static bool IsFullyTransparent(ReadOnlySpan<byte> bgra)
    {
        var pixels = bgra.Length / 4;
        if (pixels == 0)
            return false;

        for (var i = 3; i < pixels * 4; i += 4)
        {
            if (bgra[i] != 0)
                return false;
        }
        return true;
    }
}
