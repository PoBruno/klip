using Klip.Core.Common;

namespace Klip.Core.Tests;

public class FileNameTemplateTests
{
    private static readonly DateTime When = new(2026, 10, 26, 14, 3, 12);

    [Fact]
    public void Default_template_uses_dotted_time()
    {
        Assert.Equal("Klip 2026-10-26 14.03.12", FileNameTemplate.Format(FileNameTemplate.DefaultTemplate, When, null));
    }

    [Fact]
    public void Blank_template_falls_back_to_default()
    {
        Assert.Equal("Klip 2026-10-26 14.03.12.png", FileNameTemplate.ToFileName("  ", When, null));
        Assert.Equal("Klip 2026-10-26 14.03.12.png", FileNameTemplate.ToFileName(null, When, null));
    }

    [Theory]
    [InlineData("{app} {date}", "chrome.exe", "chrome 2026-10-26")]
    [InlineData("{APP}-{Time}", "Paint.EXE", "Paint-14.03.12")]
    [InlineData("shot {app} {date}", null, "shot 2026-10-26")]
    [InlineData("{app}", null, "Klip 2026-10-26 14.03.12")]
    public void App_token(string template, string? app, string expected)
    {
        Assert.Equal(expected, FileNameTemplate.Format(template, When, app));
    }

    [Fact]
    public void Unknown_tokens_and_unbalanced_braces_stay()
    {
        Assert.Equal("a {foo} {date", FileNameTemplate.Format("a {foo} {date", When, null));
    }

    [Theory]
    [InlineData("a:b/c\\d|e?f*g\"h<i>j", "a_b_c_d_e_f_g_h_i_j")]
    [InlineData("name...  ", "name")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.backup", "_nul.backup")]
    [InlineData("tab\there", "tab_here")]
    public void Invalid_characters_and_reserved_names(string template, string expected)
    {
        Assert.Equal(expected, FileNameTemplate.Format(template, When, null));
    }

    [Fact]
    public void Long_names_are_capped()
    {
        var name = FileNameTemplate.Format(new string('x', 500), When, null);
        Assert.True(name.Length <= 120);
    }

    [Fact]
    public void Extension_is_normalized()
    {
        Assert.Equal("x.png", FileNameTemplate.ToFileName("x", When, null, "png"));
        Assert.Equal("x.png", FileNameTemplate.ToFileName("x", When, null, ".png"));
    }

    [Fact]
    public void MakeUnique_adds_suffix()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Klip.png", "Klip (2).png" };
        Assert.Equal("Klip (3).png", FileNameTemplate.MakeUnique("Klip.png", taken.Contains));
        Assert.Equal("other.png", FileNameTemplate.MakeUnique("other.png", taken.Contains));
    }

    [Fact]
    public void AppToken_strips_exe()
    {
        Assert.Equal("explorer", FileNameTemplate.AppToken("explorer.exe"));
        Assert.Equal("", FileNameTemplate.AppToken("  "));
    }
}
