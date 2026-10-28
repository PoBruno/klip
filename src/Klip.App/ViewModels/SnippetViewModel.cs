using Klip.Core.Snippets;

namespace Klip.App.ViewModels;

/// <summary>One row of the snippets tab.</summary>
public sealed class SnippetViewModel(Snippet snippet)
{
    private const int PreviewChars = 160;

    public Snippet Snippet { get; } = snippet;

    public long Id => Snippet.Id;
    public string Name => Snippet.Name;
    public string? Alias => Snippet.Alias;
    public bool HasAlias => !string.IsNullOrEmpty(Snippet.Alias);

    private string? _preview;

    /// <summary>First lines of the content, placeholders left as typed.</summary>
    public string Preview => _preview ??= BuildPreview(Snippet.Content);

    private static string BuildPreview(string content)
    {
        var text = content.Length > PreviewChars * 2 ? content[..(PreviewChars * 2)] : content;
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length > PreviewChars ? text[..PreviewChars] : text;
    }
}
