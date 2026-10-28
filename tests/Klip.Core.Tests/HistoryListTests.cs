using System.Diagnostics;
using Klip.Core.Settings;
using Klip.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Klip.Core.Tests;

/// <summary>
/// The list side of the history: slim rows, pinned items loaded apart from the recents,
/// keyset paging with ties, search cost and the change counter the flyout relies on.
/// </summary>
public class HistoryListTests : IDisposable
{
    private readonly DatabaseFixture _fx = new();

    private ClipboardItemRepository Repo => _fx.Repository;

    // pages through the unpinned items the same way the flyout does
    private List<ClipboardItem> PageAllUnpinned(int pageSize, ClipboardItemType? type = null)
    {
        var all = new List<ClipboardItem>();
        ClipboardItem? last = null;
        while (true)
        {
            var page = Repo.QuerySummaries(new HistoryQuery
            {
                Pinned = false,
                Type = type,
                BeforeLastCopiedAtMs = last?.LastCopiedAt.ToUnixTimeMilliseconds(),
                BeforeId = last?.Id,
                Limit = pageSize,
            });
            all.AddRange(page);
            if (page.Count < pageSize)
                return all;
            last = page[^1];
        }
    }

    [Fact]
    public void FortyPinned_AllShowUp_AndRecentsStillPage()
    {
        for (var i = 0; i < 40; i++)
            Repo.Upsert(DatabaseFixture.NewTextItem($"fixado {i}", $"p{i}", copiedAtMs: 5_000 + i, pinned: true));
        for (var i = 0; i < 150; i++)
            Repo.Upsert(DatabaseFixture.NewTextItem($"recente {i}", $"r{i}", copiedAtMs: 1_000 + i));

        var pinned = Repo.QuerySummaries(new HistoryQuery { Pinned = true, Limit = 500 });
        Assert.Equal(40, pinned.Count);
        Assert.All(pinned, p => Assert.True(p.Pinned));

        // the first recents page is NOT eaten by the pinned ones
        var first = Repo.QuerySummaries(new HistoryQuery { Pinned = false, Limit = 30 });
        Assert.Equal(30, first.Count);
        Assert.All(first, p => Assert.False(p.Pinned));

        var recents = PageAllUnpinned(30);
        Assert.Equal(150, recents.Count);
        Assert.Equal(150, recents.Select(r => r.Id).Distinct().Count());
        var stamps = recents.Select(r => r.LastCopiedAt.ToUnixTimeMilliseconds()).ToList();
        Assert.Equal(stamps.OrderByDescending(x => x), stamps);
    }

    [Fact]
    public void Keyset_WithTiesOnTheSameMillisecond_SkipsNothing()
    {
        // a batch import or a fast multi copy lands many rows on the same millisecond
        for (var i = 0; i < 25; i++)
            Repo.Upsert(DatabaseFixture.NewTextItem($"empate {i}", $"t{i}", copiedAtMs: 9_000));
        for (var i = 0; i < 7; i++)
            Repo.Upsert(DatabaseFixture.NewTextItem($"antes {i}", $"a{i}", copiedAtMs: 8_000 + i));

        var all = PageAllUnpinned(pageSize: 10);

        Assert.Equal(32, all.Count);
        Assert.Equal(32, all.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public void Keyset_WithTies_WorksPerTab()
    {
        for (var i = 0; i < 12; i++)
        {
            var item = DatabaseFixture.NewTextItem($"img {i}", $"i{i}", copiedAtMs: 7_000);
            item.Type = ClipboardItemType.Image;
            Repo.Upsert(item);
        }
        Repo.Upsert(DatabaseFixture.NewTextItem("texto", "x", copiedAtMs: 7_000));

        var images = PageAllUnpinned(pageSize: 5, type: ClipboardItemType.Image);
        Assert.Equal(12, images.Count);
        Assert.Equal(12, images.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public void Summaries_CarryOnlyThePreview_AndLoadFullBringsTheRest()
    {
        var big = new string('x', 200_000);
        var item = DatabaseFixture.NewTextItem(big, "big");
        item.Type = ClipboardItemType.Html;
        item.HtmlContent = "<p>" + big + "</p>";
        item.RtfContent = @"{\rtf1 big}";
        Repo.Upsert(item);

        var row = Assert.Single(Repo.QuerySummaries(new HistoryQuery { Limit = 10 }));
        Assert.True(row.IsSummary);
        Assert.Null(row.TextContent);
        Assert.Null(row.HtmlContent);
        Assert.Null(row.RtfContent);
        Assert.NotNull(row.Preview);
        Assert.Equal(ClipboardItem.PreviewLength, row.Preview!.Length);

        var full = Repo.LoadFull(row);
        Assert.False(full.IsSummary);
        Assert.Equal(big, full.TextContent);
        Assert.Equal(@"{\rtf1 big}", full.RtfContent);
    }

    [Fact]
    public void Preview_DoesNotSplitASurrogatePair()
    {
        var text = new string('a', ClipboardItem.PreviewLength - 1) + "\U0001F600tail";
        var preview = ClipboardItem.BuildPreview(text)!;
        Assert.Equal(ClipboardItem.PreviewLength - 1, preview.Length);
        Assert.False(char.IsHighSurrogate(preview[^1]));
    }

    [Fact]
    public void Search_Summaries_FindByRank_ByRecency_AndWithFilters()
    {
        Repo.Upsert(DatabaseFixture.NewTextItem("pagamento boleto", "s1", copiedAtMs: 1_000));
        Repo.Upsert(DatabaseFixture.NewTextItem("pagamento pix pagamento", "s2", copiedAtMs: 2_000));
        var fav = DatabaseFixture.NewTextItem("pagamento favorito", "s3", copiedAtMs: 3_000, favorite: true);
        Repo.Upsert(fav);
        Repo.Upsert(DatabaseFixture.NewTextItem("outra coisa", "s4", copiedAtMs: 4_000));

        var byRank = Repo.QuerySummaries(new HistoryQuery { SearchText = "pag", Limit = 10 });
        Assert.Equal(3, byRank.Count);
        Assert.All(byRank, r => Assert.True(r.IsSummary));

        var byRecency = Repo.QuerySummaries(new HistoryQuery { SearchText = "pag", Limit = 10, OrderBySearchRecency = true });
        Assert.Equal(new long[] { 3_000, 2_000, 1_000 }, byRecency.Select(r => r.LastCopiedAt.ToUnixTimeMilliseconds()));

        var onlyFav = Repo.QuerySummaries(new HistoryQuery { SearchText = "pag", OnlyFavorites = true, Limit = 10 });
        Assert.Equal(fav.Id, Assert.Single(onlyFav).Id);

    }

    [Fact]
    public void QuerySummaries_HonorsCancellation()
    {
        Repo.Upsert(DatabaseFixture.NewTextItem("algo", "c1"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            Repo.QuerySummaries(new HistoryQuery { SearchText = "algo" }, cts.Token));
    }

    [Fact]
    public void DataVersion_MovesOnWrites_AndStaysOnReads()
    {
        var v0 = Repo.DataVersion;
        var id = Repo.Upsert(DatabaseFixture.NewTextItem("a", "dv1"));
        var v1 = Repo.DataVersion;
        Assert.True(v1 > v0);

        Repo.QuerySummaries(new HistoryQuery());
        Repo.GetById(id);
        Repo.Count();
        Assert.Equal(v1, Repo.DataVersion);

        Repo.SetPinned(id, true);
        var v2 = Repo.DataVersion;
        Assert.True(v2 > v1);

        Repo.Delete(id);
        Assert.True(Repo.DataVersion > v2);

        // retention with nothing to drop is not a change
        var v3 = Repo.DataVersion;
        Repo.ApplyRetention(new RetentionLimits(MaxItems: 100));
        Assert.Equal(v3, Repo.DataVersion);
    }

    [Fact]
    public void Search_On10kItems_IsFast_AndBeatsTheOldFullRowQuery()
    {
        // realistic-ish rows: short texts that all share a common prefix (worst case for
        // a prefix search), and a fat html payload like the ones the old query dragged around
        var html = "<div>" + new string('h', 6_000) + "</div>";
        using (var conn = _fx.Database.OpenConnection())
        using (var tx = conn.BeginTransaction())
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO items (type, created_at, last_copied_at, origin, content_hash, byte_size,
                                   text_content, html_content, preview)
                VALUES ('html', $t, $t, 'clipboard', $h, 6000, $text, $html, $text);
                """;
            var pT = cmd.Parameters.Add("$t", SqliteType.Integer);
            var pH = cmd.Parameters.Add("$h", SqliteType.Text);
            var pText = cmd.Parameters.Add("$text", SqliteType.Text);
            cmd.Parameters.Add("$html", SqliteType.Text).Value = html;
            for (var i = 0; i < 10_000; i++)
            {
                pT.Value = 1_000_000L + i;
                pH.Value = "bulk" + i;
                pText.Value = $"pagamento numero {i} referente ao pedido {i % 97}";
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        var query = new HistoryQuery { SearchText = "pag", Limit = 30 };
        Repo.QuerySummaries(query); // warm the cache and the plan

        var newMs = MedianMs(() => Repo.QuerySummaries(query));
        var oldMs = MedianMs(() => OldStyleSearch("\"pag\"*"));

        // relative: the slim search has to win clearly over SELECT i.* + bm25()
        Assert.True(newMs < oldMs, $"slim search {newMs:F2} ms vs old {oldMs:F2} ms");
        // absolute, with room for a slow CI box (target on a dev machine is < 10 ms)
        Assert.True(newMs < 50, $"slim search took {newMs:F2} ms on 10k rows");
    }

    private void OldStyleSearch(string fts)
    {
        using var conn = _fx.Database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT i.* FROM items_fts f JOIN items i ON i.id = f.rowid
            WHERE items_fts MATCH $q ORDER BY bm25(items_fts), i.last_copied_at DESC LIMIT 30;
            """;
        cmd.Parameters.AddWithValue("$q", fts);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            for (var c = 0; c < reader.FieldCount; c++)
                _ = reader.GetValue(c);
        }
    }

    private static double MedianMs(Action action)
    {
        var samples = new List<double>();
        for (var i = 0; i < 7; i++)
        {
            var sw = Stopwatch.StartNew();
            action();
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        return samples[samples.Count / 2];
    }

    public void Dispose() => _fx.Dispose();
}

/// <summary>Old settings files moving to the new history defaults.</summary>
public class SettingsMigrationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"klip-settings-{Guid.NewGuid():N}.json");

    private SettingsService LoadFrom(string json)
    {
        File.WriteAllText(_path, json);
        return new SettingsService(_path);
    }

    [Fact]
    public void OldDefault10k_BecomesThe1kDefault()
    {
        using var s = LoadFrom("""{"RetentionMaxItems":10000,"Theme":"dark"}""");
        Assert.Equal(1_000, s.Current.RetentionMaxItems);
        Assert.Equal("dark", s.Current.Theme);
        Assert.Equal(SettingsService.CurrentSettingsVersion, s.Current.SettingsVersion);
    }

    [Fact]
    public void ValuePickedByTheUser_IsKept()
    {
        using var s = LoadFrom("""{"RetentionMaxItems":5000,"RetentionMaxAgeDays":4}""");
        Assert.Equal(5_000, s.Current.RetentionMaxItems);
        Assert.Equal(4, s.Current.RetentionMaxAgeDays);
    }

    [Fact]
    public void AlreadyMigratedFile_With10k_IsKept()
    {
        // the user went back to 10k on purpose after the migration
        using var s = LoadFrom("""{"RetentionMaxItems":10000,"SettingsVersion":1}""");
        Assert.Equal(10_000, s.Current.RetentionMaxItems);
    }

    [Fact]
    public void MigratedValue_SurvivesASaveAndReload()
    {
        using (var s = LoadFrom("""{"RetentionMaxItems":10000}"""))
            s.UpdateAndFlush(x => x.ShowEmojiTab = false);

        using var reloaded = new SettingsService(_path);
        Assert.Equal(1_000, reloaded.Current.RetentionMaxItems);
        Assert.Equal(1, reloaded.Current.SettingsVersion);
        Assert.False(reloaded.Current.ShowEmojiTab);
    }

    [Fact]
    public void LoadingDoesNotWriteTheFile()
    {
        using var s = LoadFrom("""{"RetentionMaxItems":10000}""");
        Assert.False(s.HasPendingWrite);
        Assert.Contains("10000", File.ReadAllText(_path));
    }

    [Fact]
    public void FreshInstall_GetsTheNewDefaults()
    {
        var s = new AppSettings();
        SettingsService.Migrate(s);
        Assert.Equal(1_000, s.RetentionMaxItems);
        Assert.Equal(200, s.RetentionMaxImages);
        Assert.Equal(1L << 30, s.RetentionMaxMediaBytes);
    }

    public void Dispose()
    {
        foreach (var f in new[] { _path, _path + ".tmp", _path + ".corrupt" })
            try { File.Delete(f); } catch (IOException) { }
    }
}
