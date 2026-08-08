using System.Globalization;
using System.Text;
using Klip.Core.Clipboard;

namespace Klip.Core.Tests;

/// <summary>
/// RF-P2.03: cobertura do parse de header CF_HTML sobre span. O nome carrega o
/// sufixo Span porque a suite legada em ClipboardEngineTests.cs ja ocupa o tipo
/// <c>CfHtmlParserTests</c> no mesmo namespace.
/// </summary>
public class CfHtmlParserSpanTests
{
    private const string Pre = "<html><body><!--StartFragment-->";
    private const string Post = "<!--EndFragment--></body></html>";

    /// <summary>
    /// Monta um payload CF_HTML com os offsets calculados em BYTES de verdade -
    /// e o unico jeito de flagrar o bug classico de contar caracteres.
    /// Os placeholders tem largura fixa (6 chars -> 10 digitos), entao o tamanho
    /// do header nao muda entre a medicao e a versao final.
    /// </summary>
    private static byte[] BuildPayload(
        string fragment,
        string newline = "\r\n",
        string? sourceUrl = null,
        bool withHtmlContext = true,
        string? extraHeaderLine = null,
        bool extraLineFirst = false)
    {
        var lines = new List<string> { "Version:0.9" };

        if (extraHeaderLine is not null && extraLineFirst)
            lines.Add(extraHeaderLine);

        lines.Add(withHtmlContext ? "StartHTML:__SH__" : "StartHTML:-1");
        lines.Add(withHtmlContext ? "EndHTML:__EH__" : "EndHTML:-1");
        lines.Add("StartFragment:__SF__");
        lines.Add("EndFragment:__EF__");

        if (sourceUrl is not null)
            lines.Add($"SourceURL:{sourceUrl}");
        if (extraHeaderLine is not null && !extraLineFirst)
            lines.Add(extraHeaderLine);

        var template = string.Join(newline, lines) + newline;

        var sized = template
            .Replace("__SH__", "0000000000")
            .Replace("__EH__", "0000000000")
            .Replace("__SF__", "0000000000")
            .Replace("__EF__", "0000000000");

        var headerBytes = Encoding.UTF8.GetByteCount(sized);
        var startHtml = headerBytes;
        var startFragment = startHtml + Encoding.UTF8.GetByteCount(Pre);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = endFragment + Encoding.UTF8.GetByteCount(Post);

        var header = template
            .Replace("__SH__", D10(startHtml))
            .Replace("__EH__", D10(endHtml))
            .Replace("__SF__", D10(startFragment))
            .Replace("__EF__", D10(endFragment));

        return Encoding.UTF8.GetBytes(header + Pre + fragment + Post);
    }

    private static string D10(int value) => value.ToString("D10", CultureInfo.InvariantCulture);

    private static string Pad(int value, int digits) =>
        value.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');

    // ----- quebras de linha -----

    [Fact]
    public void TryParse_CrLfHeader_ExtractsFragment()
    {
        Assert.True(CfHtmlParser.TryParse(BuildPayload("<b>negrito</b>"), out var result));
        Assert.Equal("<b>negrito</b>", result.Fragment);
        Assert.StartsWith("<html><body>", result.Html);
        Assert.EndsWith("</body></html>", result.Html);
    }

    [Fact]
    public void TryParse_LfOnlyHeader_ExtractsFragment()
    {
        Assert.True(CfHtmlParser.TryParse(BuildPayload("<b>lf</b>", newline: "\n"), out var result));
        Assert.Equal("<b>lf</b>", result.Fragment);
    }

    [Fact]
    public void TryParse_CrOnlyHeader_ExtractsFragment()
    {
        Assert.True(CfHtmlParser.TryParse(BuildPayload("<b>cr</b>", newline: "\r"), out var result));
        Assert.Equal("<b>cr</b>", result.Fragment);
    }

    // ----- formato dos offsets -----

    [Theory]
    [InlineData(4)]   // "0051"
    [InlineData(10)]  // "0000000063" - o padding padrao do formato
    [InlineData(20)]  // zeros a esquerda em quantidade arbitraria
    public void TryParse_OffsetsWithLeadingZeros_ParsedAsDecimal(int digits)
    {
        const string fragment = "fragmento";
        var zeros = new string('0', digits);

        // o header e ASCII puro e o padding tem largura fixa, entao medir com
        // zeros e escrever o valor real dao exatamente o mesmo tamanho
        var template = $"Version:0.9\r\nStartFragment:{zeros}\r\nEndFragment:{zeros}\r\n";
        var start = template.Length;
        var end = start + fragment.Length;

        var header = "Version:0.9\r\n" +
                     $"StartFragment:{Pad(start, digits)}\r\n" +
                     $"EndFragment:{Pad(end, digits)}\r\n";
        Assert.Equal(template.Length, header.Length);

        Assert.True(CfHtmlParser.TryParse(Encoding.UTF8.GetBytes(header + fragment), out var result));
        Assert.Equal(fragment, result.Fragment);
    }

    [Fact]
    public void TryParse_NoHtmlContext_FallsBackToFragment()
    {
        // StartHTML/EndHTML em -1: sem contexto, Html vira o proprio fragmento
        Assert.True(CfHtmlParser.TryParse(BuildPayload("<i>sem contexto</i>", withHtmlContext: false), out var result));
        Assert.Equal("<i>sem contexto</i>", result.Fragment);
        Assert.Equal(result.Fragment, result.Html);
    }

    // ----- SourceURL -----

    [Fact]
    public void TryParse_WithSourceUrl_KeepsColonsInValue()
    {
        var payload = BuildPayload("<i>x</i>", sourceUrl: "https://example.com/page?a=1:2");

        Assert.True(CfHtmlParser.TryParse(payload, out var result));
        Assert.Equal("https://example.com/page?a=1:2", result.SourceUrl);
    }

    [Fact]
    public void TryParse_WithoutSourceUrl_ReturnsNull()
    {
        Assert.True(CfHtmlParser.TryParse(BuildPayload("<i>x</i>"), out var result));
        Assert.Null(result.SourceUrl);
    }

    // ----- fim do header -----

    [Fact]
    public void TryParse_UnknownKeyAfterOffsets_EndsHeaderWithoutThrowing()
    {
        var payload = BuildPayload("<i>x</i>", extraHeaderLine: "X-Custom:qualquer coisa");

        Assert.True(CfHtmlParser.TryParse(payload, out var result));
        Assert.Equal("<i>x</i>", result.Fragment);
    }

    [Fact]
    public void TryParse_UnknownKeyBeforeOffsets_ReturnsFalse()
    {
        // chave desconhecida encerra o header: os offsets que vem depois dela
        // nunca sao lidos, entao o parse falha - mas sem lancar
        var payload = BuildPayload("<i>x</i>", extraHeaderLine: "X-Custom:para aqui", extraLineFirst: true);

        Assert.False(CfHtmlParser.TryParse(payload, out var result));
        Assert.Equal("", result.Fragment);
    }

    [Fact]
    public void TryParse_KnownSelectionKeys_DoNotEndHeader()
    {
        var payload = BuildPayload("<i>sel</i>", extraHeaderLine: "StartSelection:0000000000", extraLineFirst: true);

        Assert.True(CfHtmlParser.TryParse(payload, out var result));
        Assert.Equal("<i>sel</i>", result.Fragment);
    }

    [Fact]
    public void TryParse_BodyWithColon_DoesNotConfuseHeaderScan()
    {
        // a primeira linha do corpo tem ':' (href) mas a chave e desconhecida
        Assert.True(CfHtmlParser.TryParse(BuildPayload("<a href=\"https://x.dev\">link</a>"), out var result));
        Assert.Equal("<a href=\"https://x.dev\">link</a>", result.Fragment);
    }

    [Fact]
    public void TryParse_HeaderBeyondProbeLimit_ReturnsFalse()
    {
        // a sondagem para em 512 bytes: um header maior que isso ANTES dos
        // offsets nao e lido (navegadores reais poem SourceURL depois deles)
        var padding = new string('u', 600);
        var payload = BuildPayload("<i>x</i>", extraHeaderLine: $"SourceURL:https://x.dev/{padding}", extraLineFirst: true);

        Assert.True(payload.Length > 512);
        Assert.False(CfHtmlParser.TryParse(payload, out _));
    }

    // ----- payloads invalidos -----

    [Fact]
    public void TryParse_EmptyPayload_ReturnsFalse()
    {
        Assert.False(CfHtmlParser.TryParse(Array.Empty<byte>(), out var result));
        Assert.Equal("", result.Fragment);
    }

    [Fact]
    public void TryParse_HeaderWithoutBody_ReturnsFalse()
    {
        // offsets apontam pra um corpo que nao existe
        var payload = BuildPayload("<b>corpo</b>");
        var headerOnly = payload.AsSpan(0, payload.Length - Encoding.UTF8.GetByteCount(Pre + "<b>corpo</b>" + Post)).ToArray();

        Assert.False(CfHtmlParser.TryParse(headerOnly, out _));
    }

    [Fact]
    public void TryParse_TruncatedPayload_ReturnsFalse()
    {
        // corta o sufixo inteiro mais um pedaco do fragmento: EndFragment passa
        // a apontar pra fora do buffer
        var payload = BuildPayload("<b>fragmento inteiro</b>");
        var truncated = payload.AsSpan(0, payload.Length - Post.Length - 5).ToArray();

        Assert.False(CfHtmlParser.TryParse(truncated, out _));
    }

    [Fact]
    public void TryParse_EndFragmentBeyondPayload_ReturnsFalse()
    {
        var payload = Encoding.UTF8.GetBytes("StartFragment:0000000005\r\nEndFragment:9999999\r\ncorpo");
        Assert.False(CfHtmlParser.TryParse(payload, out _));
    }

    [Fact]
    public void TryParse_EndFragmentBeforeStartFragment_ReturnsFalse()
    {
        var payload = Encoding.UTF8.GetBytes("StartFragment:0000000040\r\nEndFragment:0000000010\r\n" + new string('x', 60));
        Assert.False(CfHtmlParser.TryParse(payload, out _));
    }

    [Fact]
    public void TryParse_MissingFragmentOffsets_ReturnsFalse()
    {
        Assert.False(CfHtmlParser.TryParse(Encoding.UTF8.GetBytes("sem header nenhum"), out _));
    }

    // ----- UTF-8 multibyte: o teste que importa -----
    // os pares substitutos vao como escape \U (foguete U+1F680, bandeira
    // U+1F1E7 U+1F1F7) pra nao colocar emoji literal no codigo

    [Theory]
    [InlineData("<p>ação e coração</p>")]
    [InlineData("<p>emoji \U0001F680 no meio \U0001F1E7\U0001F1F7</p>")]
    [InlineData("<p>ação — \U0001F680 — coração ÿ ß 日本語</p>")]
    public void TryParse_Utf8MultiByte_UsesByteOffsetsNotCharOffsets(string fragment)
    {
        var payload = BuildPayload(fragment);

        // sanidade: o payload realmente tem mais bytes que chars, senao o teste
        // nao estaria exercitando nada
        Assert.True(Encoding.UTF8.GetByteCount(fragment) > fragment.Length);

        Assert.True(CfHtmlParser.TryParse(payload, out var result));
        Assert.Equal(fragment, result.Fragment);
        Assert.Contains(fragment, result.Html);
    }

    [Fact]
    public void TryParse_Utf8MultiByte_WithLfHeaderAndSourceUrl()
    {
        const string fragment = "<b>São Paulo \U0001F1E7\U0001F1F7</b>";
        var payload = BuildPayload(fragment, newline: "\n", sourceUrl: "https://exemplo.com.br/ação");

        Assert.True(CfHtmlParser.TryParse(payload, out var result));
        Assert.Equal(fragment, result.Fragment);
        Assert.Equal("https://exemplo.com.br/ação", result.SourceUrl);
    }

    // ----- sobrecarga span -----

    [Fact]
    public void TryParse_SpanOverload_MatchesArrayOverload()
    {
        var payload = BuildPayload("<b>span</b>", sourceUrl: "https://x.dev");

        Assert.True(CfHtmlParser.TryParse(payload, out var fromArray));
        Assert.True(CfHtmlParser.TryParse(payload.AsSpan(), out var fromSpan));

        Assert.Equal(fromArray, fromSpan);
    }

    [Fact]
    public void TryParse_SpanOverload_AcceptsSliceOfLargerBuffer()
    {
        var payload = BuildPayload("<b>fatia</b>");
        var buffer = new byte[payload.Length + 64];
        payload.CopyTo(buffer, 0);

        Assert.True(CfHtmlParser.TryParse(buffer.AsSpan(0, payload.Length), out var result));
        Assert.Equal("<b>fatia</b>", result.Fragment);
    }

    // ----- round-trip com BuildCfHtml -----

    [Theory]
    [InlineData("<b>negrito</b>")]
    [InlineData("<p>ação e coração</p>")]
    [InlineData("<p>\U0001F680 emoji \U0001F1E7\U0001F1F7 e 日本語</p>")]
    [InlineData("")]
    [InlineData("x")]
    public void BuildCfHtml_RoundTrips(string fragment)
    {
        var built = CfHtmlParser.BuildCfHtml(fragment);
        var payload = Encoding.UTF8.GetBytes(built);

        if (fragment.Length == 0)
        {
            // fragmento vazio nao carrega informacao: StartFragment == EndFragment
            Assert.False(CfHtmlParser.TryParse(payload, out _));
            return;
        }

        Assert.True(CfHtmlParser.TryParse(payload, out var result));
        Assert.Equal(fragment, result.Fragment);
        Assert.Contains(fragment, result.Html);
    }

    [Fact]
    public void BuildCfHtml_HeaderIsFixedWidthAndSelfConsistent()
    {
        // 17 chars mas 21 bytes em UTF-8: os offsets tem que sair em bytes
        const string fragment = "<p>coração \U0001F680</p>";
        var payload = Encoding.UTF8.GetBytes(CfHtmlParser.BuildCfHtml(fragment));

        var header = Encoding.ASCII.GetString(payload, 0, 105);
        Assert.Equal(
            "Version:0.9\r\n" +
            "StartHTML:0000000105\r\n" +
            "EndHTML:0000000190\r\n" +
            "StartFragment:0000000137\r\n" +
            "EndFragment:0000000158\r\n",
            header);

        // EndHTML precisa bater com o tamanho real do payload em bytes
        Assert.Equal(190, payload.Length);
        Assert.Equal(fragment, Encoding.UTF8.GetString(payload, 137, 158 - 137));
    }

    [Fact]
    public void BuildCfHtml_NullFragment_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CfHtmlParser.BuildCfHtml(null!));
    }
}
