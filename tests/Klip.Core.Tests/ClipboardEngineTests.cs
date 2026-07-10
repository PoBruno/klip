using System.Text;
using Klip.Core.Clipboard;
using Klip.Core.Common;
using Klip.Core.Settings;
using Klip.Core.Storage;

namespace Klip.Core.Tests;

public class CfHtmlParserTests
{
    private static byte[] BuildCfHtml(string fragment, string? sourceUrl = null)
    {
        // build a valid CF_HTML payload with byte offsets (like browsers do)
        var pre = "<html><body><!--StartFragment-->";
        var post = "<!--EndFragment--></body></html>";
        var htmlBody = pre + fragment + post;

        var headerTemplate =
            "Version:0.9\r\n" +
            "StartHTML:AAAAAAAAAA\r\n" +
            "EndHTML:BBBBBBBBBB\r\n" +
            "StartFragment:CCCCCCCCCC\r\n" +
            "EndFragment:DDDDDDDDDD\r\n" +
            (sourceUrl is not null ? $"SourceURL:{sourceUrl}\r\n" : "");

        var headerLength = Encoding.UTF8.GetByteCount(headerTemplate);
        var startHtml = headerLength;
        var preBytes = Encoding.UTF8.GetByteCount(pre);
        var fragmentBytes = Encoding.UTF8.GetByteCount(fragment);
        var totalBytes = headerLength + Encoding.UTF8.GetByteCount(htmlBody);

        var header = headerTemplate
            .Replace("AAAAAAAAAA", startHtml.ToString("D10"))
            .Replace("BBBBBBBBBB", totalBytes.ToString("D10"))
            .Replace("CCCCCCCCCC", (startHtml + preBytes).ToString("D10"))
            .Replace("DDDDDDDDDD", (startHtml + preBytes + fragmentBytes).ToString("D10"));

        return Encoding.UTF8.GetBytes(header + htmlBody);
    }

    [Fact]
    public void TryParse_ExtractsFragment()
    {
        var payload = BuildCfHtml("<b>negrito</b>");
        Assert.True(CfHtmlParser.TryParse(payload, out var result));
        Assert.Equal("<b>negrito</b>", result.Fragment);
        Assert.Contains("<html>", result.Html);
    }

    [Fact]
    public void TryParse_FragmentWithUtf8MultiByte_UsesByteOffsets()
    {
        // accents: offsets are in BYTES, nao em chars, catches the classic bug
        var payload = BuildCfHtml("<p>ação e coração</p>");
        Assert.True(CfHtmlParser.TryParse(payload, out var result));
        Assert.Equal("<p>ação e coração</p>", result.Fragment);
    }

    [Fact]
    public void TryParse_ReadsSourceUrl()
    {
        var payload = BuildCfHtml("<i>x</i>", "https://example.com/page");
        Assert.True(CfHtmlParser.TryParse(payload, out var result));
        Assert.Equal("https://example.com/page", result.SourceUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sem header nenhum")]
    [InlineData("StartFragment:99999\r\nEndFragment:5\r\n")]
    public void TryParse_InvalidPayloads_ReturnFalse(string content)
    {
        Assert.False(CfHtmlParser.TryParse(Encoding.UTF8.GetBytes(content), out _));
    }
}

[Collection("AppPaths")]
public class ClipboardIngestServiceTests : IDisposable
{
    private readonly DatabaseFixture _fx = new();
    private readonly string _dataRoot;
    private readonly ClipboardIngestService _ingest;
    private readonly SettingsService _settings;

    public ClipboardIngestServiceTests()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), $"klip-ingest-{Guid.NewGuid():N}");
        AppPaths.Root = _dataRoot; // isolate media in a temp folder
        AppPaths.EnsureCreated();
        _settings = new SettingsService(Path.Combine(_dataRoot, "settings.json"));
        _ingest = new ClipboardIngestService(_fx.Repository, new MediaStore(), _settings);
    }

    [Fact]
    public void Ingest_Text_PersistsWithHashAndMetadata()
    {
        var item = _ingest.Ingest(new ClipboardSnapshot
        {
            Text = "olá mundo",
            SourceApp = "notepad.exe",
            SourceTitle = "Sem título",
        });

        Assert.NotNull(item);
        Assert.Equal(ClipboardItemType.Text, item!.Type);
        Assert.Equal("notepad.exe", item.SourceApp);
        Assert.Equal(1, _fx.Repository.Count());
    }

    [Fact]
    public void Ingest_SameTextTwice_Dedupes()
    {
        _ingest.Ingest(new ClipboardSnapshot { Text = "repetido" });
        _ingest.Ingest(new ClipboardSnapshot { Text = "repetido" });
        Assert.Equal(1, _fx.Repository.Count());
    }

    [Fact]
    public void Ingest_TextWithHtml_KeepsBothRepresentations()
    {
        var item = _ingest.Ingest(new ClipboardSnapshot
        {
            Text = "negrito",
            HtmlFragment = "<b>negrito</b>",
        });

        Assert.Equal(ClipboardItemType.Html, item!.Type);
        Assert.Equal("negrito", item.TextContent);
        Assert.Equal("<b>negrito</b>", item.HtmlContent);
    }

    [Fact]
    public void Ingest_Image_SavesPngToDiskByHash()
    {
        byte[] fakePng = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4, 5];
        var item = _ingest.Ingest(new ClipboardSnapshot { PngBytes = fakePng, ImageWidth = 10, ImageHeight = 20 });

        Assert.Equal(ClipboardItemType.Image, item!.Type);
        Assert.NotNull(item.FilePath);
        var abs = Path.Combine(AppPaths.DataDir, item.FilePath!);
        Assert.True(File.Exists(abs));
        Assert.Equal(fakePng, File.ReadAllBytes(abs));
    }

    [Fact]
    public void Ingest_ExcludedApp_IsFiltered()
    {
        _settings.Update(s => s.ExcludedApps = ["keepass.exe"]);
        var item = _ingest.Ingest(new ClipboardSnapshot { Text = "senha", SourceApp = "KeePass.exe" });
        Assert.Null(item); // match is case-insensitive
        Assert.Equal(0, _fx.Repository.Count());
    }

    [Fact]
    public void Ingest_OverSizeLimit_IsFiltered()
    {
        _settings.Update(s => s.RetentionMaxItemBytes = 10);
        var item = _ingest.Ingest(new ClipboardSnapshot { Text = new string('x', 100) });
        Assert.Null(item);
    }

    [Fact]
    public void Ingest_Files_StoresPathsAsJson()
    {
        var item = _ingest.Ingest(new ClipboardSnapshot { Files = [@"C:\a.txt", @"C:\b.txt"] });
        Assert.Equal(ClipboardItemType.Files, item!.Type);
        Assert.Contains("a.txt", item.FilesJson);
        Assert.Contains(@"C:\a.txt", item.TextContent); // searchable
    }

    public void Dispose()
    {
        _fx.Dispose();
        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch (IOException) { }
    }
}
