using System.Text;

namespace Klip.Core.Clipboard;

/// <summary>
/// Parser for the Windows "HTML Format" (CF_HTML) clipboard format.
/// Payload is UTF-8 with an ASCII header of offsets:
/// Version / StartHTML / EndHTML / StartFragment / EndFragment [/ SourceURL].
/// </summary>
public static class CfHtmlParser
{
    public sealed record CfHtmlResult(string Html, string Fragment, string? SourceUrl);

    public static bool TryParse(byte[] payload, out CfHtmlResult result)
    {
        result = new CfHtmlResult("", "", null);
        if (payload.Length == 0)
            return false;

        // header is ASCII; offsets are BYTE positions into the payload
        var headerText = Encoding.ASCII.GetString(payload, 0, Math.Min(payload.Length, 1024));

        var startFragment = ReadOffset(headerText, "StartFragment:");
        var endFragment = ReadOffset(headerText, "EndFragment:");
        var startHtml = ReadOffset(headerText, "StartHTML:");
        var endHtml = ReadOffset(headerText, "EndHTML:");
        var sourceUrl = ReadValue(headerText, "SourceURL:");

        if (startFragment < 0 || endFragment < 0 || endFragment > payload.Length || startFragment >= endFragment)
            return false;

        var fragment = Encoding.UTF8.GetString(payload, startFragment, endFragment - startFragment);

        string html;
        if (startHtml >= 0 && endHtml > startHtml && endHtml <= payload.Length)
            html = Encoding.UTF8.GetString(payload, startHtml, endHtml - startHtml);
        else
            html = fragment;

        result = new CfHtmlResult(html, fragment, sourceUrl);
        return true;
    }

    private static int ReadOffset(string header, string key)
    {
        var value = ReadValue(header, key);
        return value is not null && int.TryParse(value, out var offset) ? offset : -1;
    }

    private static string? ReadValue(string header, string key)
    {
        var index = header.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return null;
        var start = index + key.Length;
        var end = header.IndexOfAny(['\r', '\n'], start);
        if (end < 0)
            end = header.Length;
        return header[start..end].Trim();
    }

    /// <summary>
    /// Monta um payload CF_HTML valido (com o header de offsets) a partir de um
    /// fragmento HTML, pra recolar mantendo a formatacao.
    /// </summary>
    public static string BuildCfHtml(string fragment)
    {
        const string headerTemplate =
            "Version:0.9\r\n" +
            "StartHTML:{0:D10}\r\n" +
            "EndHTML:{1:D10}\r\n" +
            "StartFragment:{2:D10}\r\n" +
            "EndFragment:{3:D10}\r\n";
        const string pre = "<html><body><!--StartFragment-->";
        const string post = "<!--EndFragment--></body></html>";

        // placeholder run just to measure the header size (offsets are fixed width)
        var headerLen = System.Text.Encoding.UTF8.GetByteCount(
            string.Format(headerTemplate, 0, 0, 0, 0));
        var startHtml = headerLen;
        var startFragment = startHtml + System.Text.Encoding.UTF8.GetByteCount(pre);
        var endFragment = startFragment + System.Text.Encoding.UTF8.GetByteCount(fragment);
        var endHtml = endFragment + System.Text.Encoding.UTF8.GetByteCount(post);

        var header = string.Format(headerTemplate, startHtml, endHtml, startFragment, endFragment);
        return header + pre + fragment + post;
    }
}
