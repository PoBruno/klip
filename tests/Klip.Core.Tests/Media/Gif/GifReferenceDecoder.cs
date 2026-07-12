namespace Klip.Core.Tests.Media.Gif;

/// <summary>
/// Mini-decoder GIF de referencia para os testes de round-trip (RF-F4.11):
/// parser de blocos GIF89a, des-LZW e reconstrucao do canvas honrando
/// disposal 1 + indice transparente. Independente do encoder de producao
/// (implementado a partir da spec w3.org/Graphics/GIF/spec-gif89a).
/// </summary>
internal static class GifReferenceDecoder
{
    internal sealed class DecodedGif
    {
        public int Width { get; init; }
        public int Height { get; init; }
        public uint[] GlobalPalette { get; init; } = [];
        public int? LoopCount { get; set; }
        public List<DecodedFrame> Frames { get; } = [];
    }

    internal sealed class DecodedFrame
    {
        /// <summary>Canvas composto (RGB888 por pixel) apos desenhar este frame.</summary>
        public uint[] Canvas { get; init; } = [];
        public int DelayCs { get; init; }
        public int X { get; init; }
        public int Y { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public bool HasTransparency { get; init; }
    }

    public static DecodedGif Decode(byte[] gif)
    {
        var pos = 0;

        var signature = System.Text.Encoding.ASCII.GetString(gif, 0, 6);
        Assert.Equal("GIF89a", signature);
        pos += 6;

        var width = ReadUInt16(gif, ref pos);
        var height = ReadUInt16(gif, ref pos);
        var packed = gif[pos++];
        pos++; // background color index
        pos++; // pixel aspect ratio

        var hasGct = (packed & 0x80) != 0;
        Assert.True(hasGct, "Encoder deve sempre emitir Global Color Table.");
        var gctSize = 1 << ((packed & 0x07) + 1);
        var palette = ReadColorTable(gif, ref pos, gctSize);

        var result = new DecodedGif { Width = width, Height = height, GlobalPalette = palette };
        var canvas = new uint[width * height];

        var pendingDelayCs = 0;
        var pendingTransparent = false;
        byte pendingTransparentIndex = 0;
        var pendingDisposal = 0;

        while (true)
        {
            var block = gif[pos++];
            if (block == 0x3B)
                break; // trailer

            if (block == 0x21)
            {
                var label = gif[pos++];
                if (label == 0xF9)
                {
                    var size = gif[pos++];
                    Assert.Equal(4, size);
                    var gcePacked = gif[pos++];
                    pendingDisposal = (gcePacked >> 2) & 0x07;
                    pendingTransparent = (gcePacked & 0x01) != 0;
                    pendingDelayCs = ReadUInt16(gif, ref pos);
                    pendingTransparentIndex = gif[pos++];
                    Assert.Equal(0, gif[pos++]); // terminador
                }
                else if (label == 0xFF)
                {
                    var appData = ReadSubBlocks(gif, ref pos);
                    if (appData.Length >= 14 &&
                        System.Text.Encoding.ASCII.GetString(appData, 0, 11) == "NETSCAPE2.0" &&
                        appData[11] == 0x01)
                    {
                        result.LoopCount = appData[12] | (appData[13] << 8);
                    }
                }
                else
                {
                    ReadSubBlocks(gif, ref pos); // comentario/plain text: ignora
                }
                continue;
            }

            Assert.Equal(0x2C, block); // image descriptor
            var left = ReadUInt16(gif, ref pos);
            var top = ReadUInt16(gif, ref pos);
            var imgWidth = ReadUInt16(gif, ref pos);
            var imgHeight = ReadUInt16(gif, ref pos);
            var imgPacked = gif[pos++];
            Assert.Equal(0, imgPacked & 0x40); // interlace nao suportado no MVP
            var activePalette = palette;
            if ((imgPacked & 0x80) != 0)
            {
                var lctSize = 1 << ((imgPacked & 0x07) + 1);
                activePalette = ReadColorTable(gif, ref pos, lctSize);
            }

            var minCodeSize = gif[pos++];
            var data = ReadSubBlocks(gif, ref pos);
            var indices = LzwDecode(minCodeSize, data, imgWidth * imgHeight);

            // Composicao: disposal 1 (Do Not Dispose) mantem o canvas; pixels
            // transparentes deixam o conteudo anterior aparecer.
            Assert.True(pendingDisposal is 0 or 1, $"Disposal inesperado: {pendingDisposal}");
            for (var y = 0; y < imgHeight; y++)
            {
                for (var x = 0; x < imgWidth; x++)
                {
                    var index = indices[y * imgWidth + x];
                    if (pendingTransparent && index == pendingTransparentIndex)
                        continue;
                    canvas[(top + y) * width + (left + x)] = activePalette[index];
                }
            }

            result.Frames.Add(new DecodedFrame
            {
                Canvas = (uint[])canvas.Clone(),
                DelayCs = pendingDelayCs,
                X = left,
                Y = top,
                Width = imgWidth,
                Height = imgHeight,
                HasTransparency = pendingTransparent,
            });

            pendingDelayCs = 0;
            pendingTransparent = false;
            pendingTransparentIndex = 0;
            pendingDisposal = 0;
        }

        return result;
    }

    /// <summary>
    /// Decodifica um bloco de dados de imagem completo (byte min code size +
    /// sub-blocos + terminador), como emitido por GifLzwEncoder.Encode.
    /// </summary>
    public static byte[] DecodeLzwBlock(byte[] block, int expectedCount)
    {
        var pos = 0;
        var minCodeSize = block[pos++];
        var data = ReadSubBlocks(block, ref pos);
        Assert.Equal(block.Length, pos); // nada sobrando apos o terminador
        return LzwDecode(minCodeSize, data, expectedCount);
    }

    private static byte[] LzwDecode(int minCodeSize, byte[] data, int expectedCount)
    {
        var clearCode = 1 << minCodeSize;
        var eoiCode = clearCode + 1;
        var codeSize = minCodeSize + 1;

        var table = new List<byte[]>(4096);
        void Reset()
        {
            table.Clear();
            for (var i = 0; i < clearCode; i++)
                table.Add([(byte)i]);
            table.Add([]); // clear
            table.Add([]); // eoi
            codeSize = minCodeSize + 1;
        }

        Reset();
        var output = new List<byte>(expectedCount);
        var bitPos = 0;
        var previous = -1;
        var sawEoi = false;

        while (!sawEoi)
        {
            Assert.True(bitPos + codeSize <= data.Length * 8, "Dados LZW terminaram sem EOI.");
            var code = ReadCode(data, ref bitPos, codeSize);

            if (code == clearCode)
            {
                Reset();
                previous = -1;
                continue;
            }

            if (code == eoiCode)
            {
                sawEoi = true;
                continue;
            }

            byte[] entry;
            if (code < table.Count)
            {
                entry = table[code];
            }
            else if (code == table.Count && previous >= 0)
            {
                // Caso KwKwK: string anterior + primeiro byte dela.
                var prev = table[previous];
                entry = new byte[prev.Length + 1];
                prev.CopyTo(entry, 0);
                entry[^1] = prev[0];
            }
            else
            {
                throw new InvalidDataException($"Codigo LZW invalido: {code} (tabela com {table.Count}).");
            }

            output.AddRange(entry);

            if (previous >= 0 && table.Count < 4096)
            {
                var prev = table[previous];
                var added = new byte[prev.Length + 1];
                prev.CopyTo(added, 0);
                added[^1] = entry[0];
                table.Add(added);
                if (table.Count == 1 << codeSize && codeSize < 12)
                    codeSize++;
            }

            previous = code;
        }

        Assert.Equal(expectedCount, output.Count);
        return [.. output];
    }

    private static int ReadCode(byte[] data, ref int bitPos, int codeSize)
    {
        var result = 0;
        for (var i = 0; i < codeSize; i++)
        {
            var p = bitPos + i;
            if (((data[p >> 3] >> (p & 7)) & 1) != 0)
                result |= 1 << i;
        }
        bitPos += codeSize;
        return result;
    }

    private static uint[] ReadColorTable(byte[] gif, ref int pos, int size)
    {
        var palette = new uint[size];
        for (var i = 0; i < size; i++)
        {
            palette[i] = ((uint)gif[pos] << 16) | ((uint)gif[pos + 1] << 8) | gif[pos + 2];
            pos += 3;
        }
        return palette;
    }

    private static byte[] ReadSubBlocks(byte[] gif, ref int pos)
    {
        var buffer = new List<byte>();
        while (true)
        {
            int length = gif[pos++];
            if (length == 0)
                break;
            for (var i = 0; i < length; i++)
                buffer.Add(gif[pos++]);
        }
        return [.. buffer];
    }

    private static int ReadUInt16(byte[] gif, ref int pos)
    {
        var value = gif[pos] | (gif[pos + 1] << 8);
        pos += 2;
        return value;
    }
}
