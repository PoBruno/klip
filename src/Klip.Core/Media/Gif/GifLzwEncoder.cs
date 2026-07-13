namespace Klip.Core.Media.Gif;

/// <summary>
/// Compressao LZW do GIF89a (RF-F4.08). Escreve o bloco de dados de imagem
/// completo: byte "LZW minimum code size", sub-blocos de ate 255 bytes e o
/// terminador 0x00. Codigos LSB-first, crescimento ate 12 bits, clear code
/// ao esgotar a tabela (4096 entradas). A cadencia de crescimento da largura
/// segue o encoder classico do ppmtogif/GIFENCOD (checagem apos emitir o
/// codigo e ANTES de registrar a nova entrada) - e o que os decoders
/// (giflib, browsers) esperam.
/// </summary>
public static class GifLzwEncoder
{
    private const int MaxCodes = 4096;

    /// <summary>
    /// Comprime a sequencia de indices de paleta e escreve o bloco de dados
    /// no stream. <paramref name="minCodeSize"/> deve estar entre 2 e 11 e
    /// todo indice deve caber em minCodeSize bits.
    /// </summary>
    public static void Encode(Stream output, ReadOnlySpan<byte> indices, int minCodeSize)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfLessThan(minCodeSize, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minCodeSize, 11);
        if (indices.IsEmpty)
            throw new ArgumentException("Sequencia de indices vazia.", nameof(indices));

        output.WriteByte((byte)minCodeSize);

        var writer = new BitWriter(output);
        var clearCode = 1 << minCodeSize;
        var eoiCode = clearCode + 1;
        var codeSize = minCodeSize + 1;
        var maxCode = (1 << codeSize) - 1;
        var nextCode = eoiCode + 1;

        // Chave = (codigo do prefixo << 8) | proximo byte; valor = codigo da string.
        var table = new Dictionary<int, int>(MaxCodes);

        writer.WriteCode(clearCode, codeSize);

        int current = indices[0];
        for (var i = 1; i < indices.Length; i++)
        {
            int k = indices[i];
            var key = (current << 8) | k;
            if (table.TryGetValue(key, out var existing))
            {
                current = existing;
                continue;
            }

            writer.WriteCode(current, codeSize);

            // Largura cresce quando o proximo slot livre ja nao cabe - checado
            // antes de registrar a entrada desta iteracao (decoder esta um
            // codigo atras do encoder na construcao da tabela).
            if (nextCode > maxCode && codeSize < 12)
            {
                codeSize++;
                maxCode = (1 << codeSize) - 1;
            }

            if (nextCode < MaxCodes)
            {
                table[key] = nextCode++;
            }
            else
            {
                writer.WriteCode(clearCode, codeSize);
                table.Clear();
                codeSize = minCodeSize + 1;
                maxCode = (1 << codeSize) - 1;
                nextCode = eoiCode + 1;
            }

            current = k;
        }

        writer.WriteCode(current, codeSize);
        if (nextCode > maxCode && codeSize < 12)
            codeSize++;
        writer.WriteCode(eoiCode, codeSize);
        writer.Flush();

        output.WriteByte(0x00); // terminador do bloco de dados
    }

    /// <summary>Empacota codigos LSB-first em sub-blocos de ate 255 bytes.</summary>
    private ref struct BitWriter(Stream output)
    {
        private readonly Stream _output = output;
        private readonly byte[] _block = new byte[255];
        private int _blockLength;
        private int _bitBuffer;
        private int _bitCount;

        public void WriteCode(int code, int codeSize)
        {
            _bitBuffer |= code << _bitCount;
            _bitCount += codeSize;
            while (_bitCount >= 8)
            {
                AppendByte((byte)(_bitBuffer & 0xFF));
                _bitBuffer >>= 8;
                _bitCount -= 8;
            }
        }

        public void Flush()
        {
            if (_bitCount > 0)
            {
                AppendByte((byte)(_bitBuffer & 0xFF));
                _bitBuffer = 0;
                _bitCount = 0;
            }
            FlushBlock();
        }

        private void AppendByte(byte value)
        {
            _block[_blockLength++] = value;
            if (_blockLength == 255)
                FlushBlock();
        }

        private void FlushBlock()
        {
            if (_blockLength == 0)
                return;
            _output.WriteByte((byte)_blockLength);
            _output.Write(_block, 0, _blockLength);
            _blockLength = 0;
        }
    }
}
