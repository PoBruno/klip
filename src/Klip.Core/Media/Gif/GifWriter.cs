namespace Klip.Core.Media.Gif;

/// <summary>
/// Escrita dos blocos GIF89a (RF-F4.08): header, Logical Screen Descriptor,
/// Global Color Table, extensao NETSCAPE2.0, Graphic Control Extension,
/// Image Descriptor e trailer. Layout conforme w3.org/Graphics/GIF/spec-gif89a.
/// </summary>
internal static class GifWriter
{
    public static void WriteHeader(Stream output)
    {
        ReadOnlySpan<byte> signature = "GIF89a"u8;
        output.Write(signature);
    }

    /// <param name="gctSizeExponent">Tamanho da GCT = 2^(exp+1).</param>
    public static void WriteLogicalScreenDescriptor(Stream output, int width, int height, int gctSizeExponent)
    {
        WriteUInt16(output, width);
        WriteUInt16(output, height);
        // GCT presente | color resolution 8 bits | sem sort | tamanho da GCT.
        var packed = 0x80 | (7 << 4) | (gctSizeExponent & 0x07);
        output.WriteByte((byte)packed);
        output.WriteByte(0x00); // background color index
        output.WriteByte(0x00); // pixel aspect ratio (nao especificado)
    }

    /// <summary>
    /// Escreve a Global Color Table: cores RGB da paleta, slot do indice
    /// transparente (preto, valor irrelevante) e padding com zeros ate
    /// <paramref name="tableSize"/> entradas.
    /// </summary>
    public static void WriteGlobalColorTable(Stream output, uint[] palette, int tableSize)
    {
        Span<byte> rgb = stackalloc byte[3];
        foreach (var color in palette)
        {
            rgb[0] = (byte)((color >> 16) & 0xFF);
            rgb[1] = (byte)((color >> 8) & 0xFF);
            rgb[2] = (byte)(color & 0xFF);
            output.Write(rgb);
        }

        rgb.Clear();
        for (var i = palette.Length; i < tableSize; i++)
            output.Write(rgb);
    }

    /// <summary>Extensao NETSCAPE2.0 com loop count 0 = infinito.</summary>
    public static void WriteNetscapeLoop(Stream output)
    {
        ReadOnlySpan<byte> block =
        [
            0x21, 0xFF, 0x0B,
            (byte)'N', (byte)'E', (byte)'T', (byte)'S', (byte)'C', (byte)'A', (byte)'P', (byte)'E',
            (byte)'2', (byte)'.', (byte)'0',
            0x03, 0x01,
            0x00, 0x00, // loop count 0 = forever
            0x00,
        ];
        output.Write(block);
    }

    public static void WriteGraphicControl(Stream output, int delayCentiseconds, bool transparent, byte transparentIndex)
    {
        output.WriteByte(0x21);
        output.WriteByte(0xF9);
        output.WriteByte(0x04);
        // Disposal 1 (Do Not Dispose): base do trio delta + transparencia.
        var packed = (1 << 2) | (transparent ? 1 : 0);
        output.WriteByte((byte)packed);
        WriteUInt16(output, delayCentiseconds);
        output.WriteByte(transparent ? transparentIndex : (byte)0);
        output.WriteByte(0x00);
    }

    public static void WriteImageDescriptor(Stream output, DirtyRect rect)
    {
        output.WriteByte(0x2C);
        WriteUInt16(output, rect.X);
        WriteUInt16(output, rect.Y);
        WriteUInt16(output, rect.Width);
        WriteUInt16(output, rect.Height);
        output.WriteByte(0x00); // sem Local Color Table, sem interlace (so GCT no MVP)
    }

    public static void WriteTrailer(Stream output) => output.WriteByte(0x3B);

    private static void WriteUInt16(Stream output, int value)
    {
        output.WriteByte((byte)(value & 0xFF));
        output.WriteByte((byte)((value >> 8) & 0xFF));
    }
}
