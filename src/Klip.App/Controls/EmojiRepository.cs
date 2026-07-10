using System.IO;
using System.Text.Json;
using System.Windows;

namespace Klip.App.Controls;

/// <summary>
/// Loads the emoji index (Twemoji images + multi-language keywords) that ships
/// embedded with the app, and answers category listing and name search.
/// Colored emoji comes from Twemoji PNGs since WPF cant render color fonts.
/// </summary>
public sealed class EmojiRepository
{
    public sealed record Emoji(string Code, string Char, string Name, string Keywords);
    public sealed record Category(string Name, string Glyph, IReadOnlyList<Emoji> Emojis);

    private static readonly Lazy<EmojiRepository> _instance = new(Load);
    public static EmojiRepository Instance => _instance.Value;

    private readonly Dictionary<string, Emoji> _byCode;
    public IReadOnlyList<Category> Categories { get; }

    private EmojiRepository(IReadOnlyList<Category> categories, Dictionary<string, Emoji> byCode)
    {
        Categories = categories;
        _byCode = byCode;
    }

    /// <summary>pack URI of the Twemoji PNG for a code, e.g. "1f600".</summary>
    public static string ImageUri(string code) =>
        $"pack://application:,,,/Assets/Emoji/{code}.png";

    /// <summary>
    /// Search by name/keywords across pt, en and es. Empty query returns the
    /// first category (recent-ish default). Matches every whitespace token.
    /// </summary>
    public IReadOnlyList<Emoji> Search(string query, int limit = 200)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Categories.Count > 0 ? Categories[0].Emojis : [];

        var terms = query.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var results = new List<Emoji>();
        foreach (var emoji in _byCode.Values)
        {
            var haystack = emoji.Name + " " + emoji.Keywords;
            if (terms.All(t => haystack.Contains(t, StringComparison.Ordinal)))
            {
                results.Add(emoji);
                if (results.Count >= limit)
                    break;
            }
        }
        return results;
    }

    private static EmojiRepository Load()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/Emoji/emoji-index.json");
            var info = Application.GetResourceStream(uri);
            using var stream = info!.Stream;
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            var byCode = new Dictionary<string, Emoji>();
            foreach (var prop in root.GetProperty("emojis").EnumerateObject())
            {
                var code = prop.Name;
                var e = prop.Value;
                byCode[code] = new Emoji(
                    code,
                    e.GetProperty("char").GetString() ?? "",
                    e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    e.TryGetProperty("keywords", out var k) ? k.GetString() ?? "" : "");
            }

            var categories = new List<Category>();
            foreach (var cat in root.GetProperty("categories").EnumerateArray())
            {
                var emojis = new List<Emoji>();
                foreach (var codeEl in cat.GetProperty("emojis").EnumerateArray())
                {
                    var code = codeEl.GetString();
                    if (code is not null && byCode.TryGetValue(code, out var emoji))
                        emojis.Add(emoji);
                }
                categories.Add(new Category(
                    cat.GetProperty("name").GetString() ?? "",
                    cat.GetProperty("glyph").GetString() ?? "",
                    emojis));
            }
            return new EmojiRepository(categories, byCode);
        }
        catch
        {
            // if the bundle is missing for some reason, degrade to empty
            return new EmojiRepository([], []);
        }
    }
}
