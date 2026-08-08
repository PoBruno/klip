using Klip.Core.Storage;

namespace Klip.Core.Tests;

/// <summary>
/// RF-P2.09: retencao. Cobre os tres limites (quantidade, idade, bytes), a imunidade de
/// fixados/favoritos e o contrato da lista de orfaos devolvida ao chamador.
/// </summary>
public class RetentionTests : IDisposable
{
    private readonly DatabaseFixture _fx = new();

    /// <summary>
    /// Item com tamanho e caminhos de midia controlados. O DatabaseFixture deriva ByteSize do
    /// tamanho do texto, o que nao serve para os testes de teto de bytes.
    /// </summary>
    private static ClipboardItem Item(string hash, long copiedAtMs, long byteSize = 10,
        string? filePath = null, string? thumbPath = null, bool pinned = false, bool favorite = false)
    {
        var item = DatabaseFixture.NewTextItem($"texto {hash}", hash, copiedAtMs, pinned, favorite);
        item.ByteSize = byteSize;
        item.FilePath = filePath;
        item.ThumbPath = thumbPath;
        return item;
    }

    private IReadOnlyList<string> Texts() =>
        _fx.Repository.GetPage(limit: 1000).Select(i => i.TextContent!).ToList();

    [Fact]
    public void MaxItems_KeepsNewest_AndDropsTheOldest()
    {
        for (var i = 0; i < 20; i++)
            _fx.Repository.Upsert(Item($"h{i:D2}", copiedAtMs: 1000 + i));

        _fx.Repository.ApplyRetention(maxItems: 5, maxAgeDays: 0);

        var remaining = _fx.Repository.GetPage(limit: 100);
        Assert.Equal(5, remaining.Count);

        // sobrevivem exatamente os 5 mais recentes por last_copied_at
        var survivors = remaining.Select(i => i.LastCopiedAt.ToUnixTimeMilliseconds()).OrderBy(x => x).ToList();
        Assert.Equal(new long[] { 1015, 1016, 1017, 1018, 1019 }, survivors);
    }

    [Fact]
    public void PinnedAndFavorite_SurviveEveryLimitAtOnce()
    {
        // 10 itens comuns gordos e antigos
        for (var i = 0; i < 10; i++)
            _fx.Repository.Upsert(Item($"h{i}", copiedAtMs: 1000 + i, byteSize: 10_000));

        var old = DateTimeOffset.UtcNow.AddYears(-5).ToUnixTimeMilliseconds();
        _fx.Repository.Upsert(Item("pin", copiedAtMs: old, byteSize: 999_999, pinned: true));
        _fx.Repository.Upsert(Item("fav", copiedAtMs: old, byteSize: 999_999, favorite: true));

        // todos os limites no talo ao mesmo tempo
        _fx.Repository.ApplyRetention(maxItems: 1, maxAgeDays: 1, maxTotalBytes: 1);

        var remaining = _fx.Repository.GetPage(limit: 100);
        Assert.Contains(remaining, i => i.Pinned);
        Assert.Contains(remaining, i => i.Favorite);
        Assert.DoesNotContain(remaining, i => !i.Pinned && !i.Favorite);
    }

    [Fact]
    public void MaxAgeDays_DropsOlderThanCutoff_KeepsRecent()
    {
        var now = DateTimeOffset.UtcNow;
        _fx.Repository.Upsert(Item("recente", now.AddDays(-1).ToUnixTimeMilliseconds()));
        _fx.Repository.Upsert(Item("borda", now.AddDays(-6).ToUnixTimeMilliseconds()));
        _fx.Repository.Upsert(Item("antigo", now.AddDays(-30).ToUnixTimeMilliseconds()));
        _fx.Repository.Upsert(Item("antiquissimo", now.AddDays(-365).ToUnixTimeMilliseconds()));

        _fx.Repository.ApplyRetention(maxItems: 0, maxAgeDays: 7);

        var texts = Texts();
        Assert.Equal(2, texts.Count);
        Assert.Contains("texto recente", texts);
        Assert.Contains("texto borda", texts);
    }

    [Fact]
    public void MaxTotalBytes_KeepsNewestUnderBudget_AndReportsOrphanFiles()
    {
        // 5 itens de 100 bytes; teto 250 deixa passar so os 2 mais recentes (100 + 100)
        for (var i = 0; i < 5; i++)
            _fx.Repository.Upsert(Item($"h{i}", copiedAtMs: 1000 + i, byteSize: 100,
                filePath: $"media/file{i}.png", thumbPath: $"media/thumb{i}.png"));

        var orphans = _fx.Repository.ApplyRetention(maxItems: 0, maxAgeDays: 0, maxTotalBytes: 250);

        var remaining = _fx.Repository.GetPage(limit: 100);
        Assert.Equal(2, remaining.Count);
        Assert.Equal(new long[] { 1003, 1004 },
            remaining.Select(i => i.LastCopiedAt.ToUnixTimeMilliseconds()).OrderBy(x => x));

        // os 3 removidos (indices 0, 1 e 2) entregam arquivo e miniatura
        Assert.Equal(6, orphans.Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.Contains($"media/file{i}.png", orphans);
            Assert.Contains($"media/thumb{i}.png", orphans);
        }
        Assert.DoesNotContain("media/file3.png", orphans);
        Assert.DoesNotContain("media/thumb4.png", orphans);
    }

    [Fact]
    public void MaxTotalBytes_SingleItemOverBudget_IsRemoved()
    {
        _fx.Repository.Upsert(Item("gordo", copiedAtMs: 2000, byteSize: 5_000));

        _fx.Repository.ApplyRetention(maxItems: 0, maxAgeDays: 0, maxTotalBytes: 100);

        Assert.Equal(0, _fx.Repository.Count());
    }

    [Fact]
    public void AllLimitsDisabled_RemovesNothing()
    {
        for (var i = 0; i < 5; i++)
            _fx.Repository.Upsert(Item($"h{i}", copiedAtMs: 1000 + i, byteSize: 1_000_000,
                filePath: $"media/f{i}.png"));

        var orphans = _fx.Repository.ApplyRetention(maxItems: 0, maxAgeDays: 0, maxTotalBytes: 0);

        Assert.Empty(orphans);
        Assert.Equal(5, _fx.Repository.Count());
    }

    [Fact]
    public void Orphans_HaveNoNulls_AndNoDuplicates()
    {
        // dois itens compartilham a mesma miniatura e um terceiro nao tem midia nenhuma
        _fx.Repository.Upsert(Item("a", copiedAtMs: 1000, filePath: "media/a.png", thumbPath: "media/shared.png"));
        _fx.Repository.Upsert(Item("b", copiedAtMs: 1001, filePath: "media/b.png", thumbPath: "media/shared.png"));
        _fx.Repository.Upsert(Item("c", copiedAtMs: 1002));
        _fx.Repository.Upsert(Item("d", copiedAtMs: 1003, filePath: "media/d.png"));

        var orphans = _fx.Repository.ApplyRetention(maxItems: 0, maxAgeDays: 0, maxTotalBytes: 1);

        Assert.Equal(0, _fx.Repository.Count());
        Assert.All(orphans, p => Assert.False(string.IsNullOrEmpty(p)));
        Assert.Equal(orphans.Count, orphans.Distinct().Count());
        Assert.Equal(
            new[] { "media/a.png", "media/b.png", "media/d.png", "media/shared.png" },
            orphans.OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public void Orphans_AcrossPhases_HaveNoDuplicates()
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 6; i++)
            _fx.Repository.Upsert(Item($"h{i}", now.AddDays(-100 + i).ToUnixTimeMilliseconds(),
                byteSize: 100, filePath: $"media/f{i}.png", thumbPath: "media/shared.png"));

        // idade, quantidade e bytes de uma vez so: nenhuma fase pode reportar o mesmo arquivo duas vezes
        var orphans = _fx.Repository.ApplyRetention(maxItems: 2, maxAgeDays: 30, maxTotalBytes: 100);

        Assert.Equal(orphans.Count, orphans.Distinct().Count());
        Assert.Contains("media/shared.png", orphans);
    }

    [Fact]
    public void LargeIdList_IsDeletedInBatches()
    {
        // 601 itens com maxItems=1 => 600 ids no DELETE, ou seja mais de um lote de placeholders
        const int total = 601;
        for (var i = 0; i < total; i++)
            _fx.Repository.Upsert(Item($"h{i:D4}", copiedAtMs: 1_000_000 + i, filePath: $"media/f{i:D4}.png"));

        var orphans = _fx.Repository.ApplyRetention(maxItems: 1, maxAgeDays: 0);

        Assert.Equal(1, _fx.Repository.Count());
        Assert.Equal(total - 1, orphans.Count);
        Assert.Equal(orphans.Count, orphans.Distinct().Count());
        // o sobrevivente e o mais recente
        Assert.Equal(1_000_000 + total - 1, _fx.Repository.GetPage(limit: 1)[0].LastCopiedAt.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void MaxItems_DoesNotCountPinnedOrFavoriteAgainstTheQuota()
    {
        for (var i = 0; i < 5; i++)
            _fx.Repository.Upsert(Item($"h{i}", copiedAtMs: 1000 + i));
        _fx.Repository.Upsert(Item("pin", copiedAtMs: 1, pinned: true));
        _fx.Repository.Upsert(Item("fav", copiedAtMs: 2, favorite: true));

        _fx.Repository.ApplyRetention(maxItems: 3, maxAgeDays: 0);

        // 3 comuns + fixado + favorito
        Assert.Equal(5, _fx.Repository.Count());
    }

    [Fact]
    public void Retention_KeepsFtsIndexConsistent()
    {
        // os triggers de DELETE precisam continuar limpando o items_fts depois do delete em lote
        for (var i = 0; i < 10; i++)
            _fx.Repository.Upsert(Item($"h{i}", copiedAtMs: 1000 + i));

        _fx.Repository.ApplyRetention(maxItems: 2, maxAgeDays: 0);
        _fx.Repository.OptimizeFullTextIndex();

        Assert.True(_fx.Database.QuickCheck());
        Assert.Equal(2, _fx.Repository.Search("texto").Count);
    }

    public void Dispose() => _fx.Dispose();
}
