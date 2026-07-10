using Klip.Core.Storage;

namespace Klip.Core.Tests;

public class HistoryQueryTests : IDisposable
{
    private readonly DatabaseFixture _fx = new();

    private static long Ms(int daysAgo) =>
        DateTimeOffset.UtcNow.AddDays(-daysAgo).ToUnixTimeMilliseconds();

    [Fact]
    public void Query_DateRange_FiltersByLastCopiedAt()
    {
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("hoje", "h1", copiedAtMs: Ms(0)));
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("semana passada", "h2", copiedAtMs: Ms(10)));

        var results = _fx.Repository.Query(new HistoryQuery { DateFromMs = Ms(7) });

        Assert.Single(results);
        Assert.Equal("hoje", results[0].TextContent);
    }

    [Fact]
    public void Query_SearchPlusDateFilter_Composes()
    {
        // busca AND data
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("relatorio anual", "h1", copiedAtMs: Ms(0)));
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("relatorio velho", "h2", copiedAtMs: Ms(30)));
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("outra coisa", "h3", copiedAtMs: Ms(0)));

        var results = _fx.Repository.Query(new HistoryQuery
        {
            SearchText = "relatorio",
            DateFromMs = Ms(7),
        });

        Assert.Single(results);
        Assert.Equal("relatorio anual", results[0].TextContent);
    }

    [Fact]
    public void Query_TypeText_IncludesHtmlItems()
    {
        // Aba "Texto" agrega text + html
        var textItem = DatabaseFixture.NewTextItem("puro", "h1");
        var htmlItem = DatabaseFixture.NewTextItem("rico", "h2");
        htmlItem.Type = ClipboardItemType.Html;
        htmlItem.HtmlContent = "<b>rico</b>";
        _fx.Repository.Upsert(textItem);
        _fx.Repository.Upsert(htmlItem);

        var results = _fx.Repository.Query(new HistoryQuery { Type = ClipboardItemType.Text });

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void Query_FavoritesPlusType_Composes()
    {
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("fav texto", "h1", favorite: true));
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("comum", "h2"));

        var results = _fx.Repository.Query(new HistoryQuery
        {
            OnlyFavorites = true,
            Type = ClipboardItemType.Text,
        });

        Assert.Single(results);
        Assert.True(results[0].Favorite);
    }

    [Fact]
    public void Query_KeysetPagination_SkipsPinnedOnNextPages()
    {
        for (var i = 0; i < 5; i++)
            _fx.Repository.Upsert(DatabaseFixture.NewTextItem($"item {i}", $"h{i}", copiedAtMs: 1000 + i));
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("fixado", "hp", copiedAtMs: 1, pinned: true));

        var page1 = _fx.Repository.Query(new HistoryQuery { Limit = 3 });
        Assert.Equal("fixado", page1[0].TextContent); // fixado sempre no topo

        var lastUnpinnedMs = page1.Last(i => !i.Pinned).LastCopiedAt.ToUnixTimeMilliseconds();
        var page2 = _fx.Repository.Query(new HistoryQuery { Limit = 3, BeforeLastCopiedAtMs = lastUnpinnedMs });

        Assert.DoesNotContain(page2, i => i.Pinned); // não duplica o fixado
        Assert.All(page2, i => Assert.True(i.LastCopiedAt.ToUnixTimeMilliseconds() < lastUnpinnedMs));
    }

    public void Dispose() => _fx.Dispose();
}
