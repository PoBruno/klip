using Klip.Core.Storage;

namespace Klip.Core.Tests;

public class RepositoryTests : IDisposable
{
    private readonly DatabaseFixture _fx = new();

    [Fact]
    public void Initialize_CreatesValidSchema()
    {
        Assert.True(_fx.Database.QuickCheck());
    }

    [Fact]
    public void Upsert_InsertsAndReadsBack()
    {
        var id = _fx.Repository.Upsert(DatabaseFixture.NewTextItem("olá mundo", "hash-1"));
        var item = _fx.Repository.GetById(id);

        Assert.NotNull(item);
        Assert.Equal("olá mundo", item!.TextContent);
        Assert.Equal(ClipboardItemType.Text, item.Type);
        Assert.Equal("test.exe", item.SourceApp);
    }

    [Fact]
    public void Upsert_SameHash_Dedupes_AndBumpsRecency()
    {
        // same hash won't duplicate, just bumps it to the top
        var id1 = _fx.Repository.Upsert(DatabaseFixture.NewTextItem("abc", "same-hash", copiedAtMs: 1000));
        var id2 = _fx.Repository.Upsert(DatabaseFixture.NewTextItem("abc", "same-hash", copiedAtMs: 2000));

        Assert.Equal(id1, id2);
        Assert.Equal(1, _fx.Repository.Count());
        Assert.Equal(2000, _fx.Repository.GetById(id1)!.LastCopiedAt.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void GetPage_KeysetPagination_OrdersByRecency_PinnedFirst()
    {
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("velho", "h1", copiedAtMs: 1000));
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("novo", "h2", copiedAtMs: 3000));
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("fixado antigo", "h3", copiedAtMs: 500, pinned: true));

        var page = _fx.Repository.GetPage(limit: 10);

        Assert.Equal(3, page.Count);
        Assert.Equal("fixado antigo", page[0].TextContent); // pinned goes first
        Assert.Equal("novo", page[1].TextContent);
        Assert.Equal("velho", page[2].TextContent);
    }

    [Fact]
    public void Search_Fts_FindsByPrefix_AndDiacritics()
    {
        // fts with remove_diacritics, so accents don't matter on search
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("configuração do sistema", "h1"));
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("outro texto qualquer", "h2"));

        Assert.Single(_fx.Repository.Search("configuracao"));
        Assert.Single(_fx.Repository.Search("config"));
        Assert.Empty(_fx.Repository.Search("inexistente"));
    }

    [Fact]
    public void Search_QueryWithFtsOperators_IsSanitized()
    {
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("texto normal", "h1"));
        // must not blow up with an fts5 syntax error
        Assert.Empty(_fx.Repository.Search("\"quote AND (weird"));
    }

    [Fact]
    public void ClearAll_PreservesPinnedAndFavorites()
    {
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("comum", "h1"));
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("fixado", "h2", pinned: true));
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("favorito", "h3", favorite: true));

        var removed = _fx.Repository.ClearAll();

        Assert.Equal(1, removed);
        Assert.Equal(2, _fx.Repository.Count());
    }

    [Fact]
    public void ApplyRetention_MaxItems_RemovesOldest_NeverPinnedOrFavorite()
    {
        // retencao nunca deve remover item fixado ou favorito, mesmo o mais antigo
        for (var i = 0; i < 10; i++)
            _fx.Repository.Upsert(DatabaseFixture.NewTextItem($"item {i}", $"h{i}", copiedAtMs: 1000 + i));
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("fixado", "hp", copiedAtMs: 1, pinned: true));
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("favorito", "hf", copiedAtMs: 2, favorite: true));

        _fx.Repository.ApplyRetention(maxItems: 3, maxAgeDays: 0);

        var remaining = _fx.Repository.GetPage(limit: 100);
        Assert.Equal(5, remaining.Count); // 3 regular + pinned + favorite
        Assert.Contains(remaining, i => i.Pinned);
        Assert.Contains(remaining, i => i.Favorite);
        Assert.Equal("item 9", remaining.First(i => !i.Pinned && !i.Favorite).TextContent);
    }

    [Fact]
    public void ApplyRetention_MaxTotalBytes_DropsOldestUntilItFits()
    {
        // each item is 100 bytes (100-char text). cap at 250 keeps the 2 newest.
        var text = new string('x', 100);
        for (var i = 0; i < 5; i++)
            _fx.Repository.Upsert(DatabaseFixture.NewTextItem(text, $"hb{i}", copiedAtMs: 1000 + i));
        // a pinned old one must survive even if it blows the budget
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem(text, "hbp", copiedAtMs: 1, pinned: true));

        _fx.Repository.ApplyRetention(maxItems: 0, maxAgeDays: 0, maxTotalBytes: 250);

        var remaining = _fx.Repository.GetPage(limit: 100);
        // 2 newest regular (200 bytes) fit under 250, plus the pinned one
        Assert.Equal(3, remaining.Count);
        Assert.Contains(remaining, i => i.Pinned);
    }

    [Fact]
    public void SetPinned_And_Favorite_Roundtrip()
    {
        var id = _fx.Repository.Upsert(DatabaseFixture.NewTextItem("x", "h1"));
        _fx.Repository.SetPinned(id, true);
        _fx.Repository.SetFavorite(id, true);

        var item = _fx.Repository.GetById(id)!;
        Assert.True(item.Pinned);
        Assert.True(item.Favorite);
    }

    [Fact]
    public void Delete_RemovesItem()
    {
        var id = _fx.Repository.Upsert(DatabaseFixture.NewTextItem("x", "h1"));
        _fx.Repository.Delete(id);
        Assert.Null(_fx.Repository.GetById(id));
        Assert.Equal(0, _fx.Repository.Count());
    }

    public void Dispose() => _fx.Dispose();
}
