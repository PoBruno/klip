using System.Buffers.Text;
using System.Text;

namespace Klip.Core.Clipboard;

/// <summary>
/// Parser for the Windows "HTML Format" (CF_HTML) clipboard format.
/// Payload is UTF-8 with an ASCII header of offsets:
/// Version / StartHTML / EndHTML / StartFragment / EndFragment [/ SourceURL].
/// RF-P2.03: o header e lido direto sobre ReadOnlySpan&lt;byte&gt;, sem
/// materializar nenhuma string intermediaria - so o fragmento, o HTML e a
/// SourceURL de saida alocam.
/// </summary>
public static class CfHtmlParser
{
    public sealed record CfHtmlResult(string Html, string Fragment, string? SourceUrl);

    /// <summary>
    /// RF-P2.03: o header CF_HTML nunca passa de algumas centenas de bytes.
    /// Sondar alem disso so varreria o corpo do HTML a toa.
    /// </summary>
    private const int MaxHeaderProbeBytes = 512;

    private const int OffsetDigits = 10;
    private const string VersionLine = "Version:0.9\r\n";
    private const string Crlf = "\r\n";
    private const string KeyStartHtml = "StartHTML:";
    private const string KeyEndHtml = "EndHTML:";
    private const string KeyStartFragment = "StartFragment:";
    private const string KeyEndFragment = "EndFragment:";
    private const string FragmentPrefix = "<html><body><!--StartFragment-->";
    private const string FragmentSuffix = "<!--EndFragment--></body></html>";

    /// <summary>
    /// O header gerado por <see cref="BuildCfHtml"/> tem largura fixa e e ASCII
    /// puro, entao o tamanho em bytes e igual ao tamanho em chars e da pra
    /// saber quanto ele ocupa sem formatar nada antes (RF-P2.03).
    /// </summary>
    private static readonly int HeaderLength =
        VersionLine.Length +
        KeyStartHtml.Length + OffsetDigits + Crlf.Length +
        KeyEndHtml.Length + OffsetDigits + Crlf.Length +
        KeyStartFragment.Length + OffsetDigits + Crlf.Length +
        KeyEndFragment.Length + OffsetDigits + Crlf.Length;

    private static readonly CfHtmlResult EmptyResult = new("", "", null);

    /// <summary>
    /// Sobrecarga historica. Encaminha para a versao span - o cast explicito e
    /// obrigatorio, senao a resolucao de sobrecarga escolheria este mesmo metodo.
    /// </summary>
    public static bool TryParse(byte[] payload, out CfHtmlResult result) =>
        TryParse((ReadOnlySpan<byte>)payload, out result);

    /// <summary>
    /// RF-P2.03: parse sem alocar o header. Offsets do CF_HTML sao posicoes em
    /// BYTES a partir do inicio do dado, nunca em caracteres.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out CfHtmlResult result)
    {
        result = EmptyResult;
        if (payload.IsEmpty)
            return false;

        var header = ReadHeader(payload);

        // RF-P2.03: offsets ausentes, fora dos limites do payload ou invertidos
        // retornam false em vez de estourar uma excecao de range
        if (header.StartFragment < 0 || header.EndFragment < 0)
            return false;
        if (header.StartFragment > payload.Length || header.EndFragment > payload.Length)
            return false;
        if (header.StartFragment >= header.EndFragment)
            return false;

        var fragment = Encoding.UTF8.GetString(payload[header.StartFragment..header.EndFragment]);

        // StartHTML/EndHTML podem vir -1 quando nao ha contexto: cai no fragmento
        var html = header.StartHtml >= 0 &&
                   header.EndHtml > header.StartHtml &&
                   header.EndHtml <= payload.Length
            ? Encoding.UTF8.GetString(payload[header.StartHtml..header.EndHtml])
            : fragment;

        var sourceUrl = header.HasSourceUrl ? Encoding.UTF8.GetString(header.SourceUrl) : null;

        result = new CfHtmlResult(html, fragment, sourceUrl);
        return true;
    }

    /// <summary>
    /// Monta um payload CF_HTML valido (com o header de offsets) a partir de um
    /// fragmento HTML, pra recolar mantendo a formatacao.
    /// RF-P2.03: header de largura fixa + <c>string.Create</c>, uma unica alocacao.
    /// </summary>
    public static string BuildCfHtml(string fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);

        // prefixo e sufixo sao ASCII puro: bytes == chars
        var startHtml = HeaderLength;
        var startFragment = startHtml + FragmentPrefix.Length;
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = endFragment + FragmentSuffix.Length;

        var totalChars = HeaderLength + FragmentPrefix.Length + fragment.Length + FragmentSuffix.Length;

        return string.Create(
            totalChars,
            (fragment, startHtml, endHtml, startFragment, endFragment),
            static (dest, state) =>
            {
                var pos = 0;
                Append(dest, ref pos, VersionLine);
                AppendOffset(dest, ref pos, KeyStartHtml, state.startHtml);
                AppendOffset(dest, ref pos, KeyEndHtml, state.endHtml);
                AppendOffset(dest, ref pos, KeyStartFragment, state.startFragment);
                AppendOffset(dest, ref pos, KeyEndFragment, state.endFragment);
                Append(dest, ref pos, FragmentPrefix);
                Append(dest, ref pos, state.fragment);
                Append(dest, ref pos, FragmentSuffix);
            });
    }

    private static void Append(Span<char> dest, ref int pos, ReadOnlySpan<char> text)
    {
        text.CopyTo(dest[pos..]);
        pos += text.Length;
    }

    /// <summary>
    /// Escreve "Chave:" seguido do offset com 10 digitos (padding de zeros, o
    /// padrao de fato do formato). Preenche as 10 posicoes sempre, porque o
    /// buffer do <c>string.Create</c> vem com conteudo indefinido.
    /// </summary>
    private static void AppendOffset(Span<char> dest, ref int pos, ReadOnlySpan<char> key, int value)
    {
        Append(dest, ref pos, key);

        var digits = dest.Slice(pos, OffsetDigits);
        for (var i = OffsetDigits - 1; i >= 0; i--)
        {
            digits[i] = (char)('0' + value % 10);
            value /= 10;
        }

        pos += OffsetDigits;
        Append(dest, ref pos, Crlf);
    }

    /// <summary>Offsets lidos do header; SourceUrl aponta pra dentro do payload.</summary>
    private ref struct HeaderOffsets
    {
        public int StartHtml;
        public int EndHtml;
        public int StartFragment;
        public int EndFragment;
        public bool HasSourceUrl;
        public ReadOnlySpan<byte> SourceUrl;
    }

    private static HeaderOffsets ReadHeader(ReadOnlySpan<byte> payload)
    {
        var header = new HeaderOffsets
        {
            StartHtml = -1,
            EndHtml = -1,
            StartFragment = -1,
            EndFragment = -1,
        };

        var truncated = payload.Length > MaxHeaderProbeBytes;
        var probe = truncated ? payload[..MaxHeaderProbeBytes] : payload;

        var pos = 0;
        while (pos < probe.Length)
        {
            var rest = probe[pos..];

            // quebras de linha do header podem ser CRLF, LF ou CR sozinho
            var breakAt = rest.IndexOfAny((byte)'\r', (byte)'\n');

            ReadOnlySpan<byte> line;
            if (breakAt < 0)
            {
                // sem terminador: se a sondagem cortou o buffer, o valor pode
                // estar pela metade e nao da pra confiar nele
                if (truncated)
                    break;

                line = rest;
                pos = probe.Length;
            }
            else
            {
                line = rest[..breakAt];
                pos += breakAt + 1;

                // CRLF conta como um terminador so
                if (rest[breakAt] == (byte)'\r' && pos < probe.Length && probe[pos] == (byte)'\n')
                    pos++;
            }

            if (!TryReadEntry(line, ref header))
                break;
        }

        return header;
    }

    /// <summary>
    /// Le uma linha do header. Retorna false quando a linha nao tem ':' ou traz
    /// uma chave desconhecida - qualquer um dos dois marca o fim do header.
    /// </summary>
    private static bool TryReadEntry(ReadOnlySpan<byte> line, ref HeaderOffsets header)
    {
        var colon = line.IndexOf((byte)':');
        if (colon < 0)
            return false;

        var key = TrimAscii(line[..colon]);
        var value = TrimAscii(line[(colon + 1)..]);

        if (Ascii.EqualsIgnoreCase(key, "StartFragment"u8))
            header.StartFragment = ReadOffset(value);
        else if (Ascii.EqualsIgnoreCase(key, "EndFragment"u8))
            header.EndFragment = ReadOffset(value);
        else if (Ascii.EqualsIgnoreCase(key, "StartHTML"u8))
            header.StartHtml = ReadOffset(value);
        else if (Ascii.EqualsIgnoreCase(key, "EndHTML"u8))
            header.EndHtml = ReadOffset(value);
        else if (Ascii.EqualsIgnoreCase(key, "SourceURL"u8))
        {
            // o valor pode conter ':' (https://...): so o primeiro separa a chave
            header.HasSourceUrl = true;
            header.SourceUrl = value;
        }
        else if (!Ascii.EqualsIgnoreCase(key, "Version"u8) &&
                 !Ascii.EqualsIgnoreCase(key, "StartSelection"u8) &&
                 !Ascii.EqualsIgnoreCase(key, "EndSelection"u8))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Utf8Parser aceita zeros a esquerda em qualquer quantidade (0000000121) e
    /// o -1 usado quando nao ha contexto HTML. Valor invalido vira -1 (ausente).
    /// </summary>
    private static int ReadOffset(ReadOnlySpan<byte> value) =>
        Utf8Parser.TryParse(value, out int parsed, out var consumed) && consumed == value.Length
            ? parsed
            : -1;

    private static ReadOnlySpan<byte> TrimAscii(ReadOnlySpan<byte> span)
    {
        var start = 0;
        var end = span.Length;

        while (start < end && IsAsciiSpace(span[start]))
            start++;
        while (end > start && IsAsciiSpace(span[end - 1]))
            end--;

        return span[start..end];
    }

    private static bool IsAsciiSpace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or (byte)'\v';
}
