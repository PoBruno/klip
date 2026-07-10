using Klip.Core.Clipboard;

namespace Klip.Core.Tests;

public class SecretDetectorTests
{
    [Theory]
    [InlineData("sk-abc123DEF456ghi789JKL012mno345")]                    // OpenAI-style
    [InlineData("ghp_1234567890abcdefABCDEF1234567890ab")]              // GitHub PAT
    [InlineData("AKIAIOSFODNN7EXAMPLE")]                                 // AWS access key
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NSJ9.abcDEF123")] // JWT
    [InlineData("password = hunter2secret")]                            // key=value
    [InlineData("Api_Key: aB3xY9zQ1wE7rT5uI0oP2sD4fG6hJ8kL")]           // key=value token
    public void LooksLikeSecret_DetectsSecrets(string text)
    {
        Assert.True(SecretDetector.LooksLikeSecret(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Olá, tudo bem?")]                                      // frase comum
    [InlineData("https://github.com/user/repo")]                        // URL
    [InlineData("Reunião amanhã às 14h na sala 3")]                     // texto com espaços
    [InlineData("SELECT * FROM items WHERE id = 5")]                    // SQL comum
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]                    // só minúsculas (baixa entropia)
    [InlineData("12345678901234567890123456789012")]                   // só dígitos
    public void LooksLikeSecret_IgnoresNormalText(string text)
    {
        Assert.False(SecretDetector.LooksLikeSecret(text));
    }
}

public class CfHtmlBuildTests
{
    [Fact]
    public void BuildCfHtml_RoundTrips_ThroughParser()
    {
        var fragment = "<b>negrito</b> e <i>itálico</i>";
        var cfHtml = CfHtmlParser.BuildCfHtml(fragment);

        // O que geramos deve ser parseável de volta com o mesmo fragmento
        Assert.True(CfHtmlParser.TryParse(System.Text.Encoding.UTF8.GetBytes(cfHtml), out var parsed));
        Assert.Equal(fragment, parsed.Fragment);
    }

    [Fact]
    public void BuildCfHtml_WithUtf8_OffsetsAreByteAccurate()
    {
        var fragment = "<p>ação e coração</p>";
        var cfHtml = CfHtmlParser.BuildCfHtml(fragment);
        Assert.True(CfHtmlParser.TryParse(System.Text.Encoding.UTF8.GetBytes(cfHtml), out var parsed));
        Assert.Equal(fragment, parsed.Fragment);
    }
}
