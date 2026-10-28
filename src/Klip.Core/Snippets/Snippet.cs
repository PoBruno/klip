namespace Klip.Core.Snippets;

/// <summary>A saved piece of text the user pastes on purpose (snippets table).</summary>
public sealed class Snippet
{
    public long Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Short handle typed in the flyout search, like ";sig". Optional.</summary>
    public string? Alias { get; set; }

    /// <summary>Raw text, placeholders still unresolved.</summary>
    public string Content { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
