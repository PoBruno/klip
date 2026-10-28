using Klip.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Klip.Core.Tests;

/// <summary>Retention in slices plus the image and media byte caps.</summary>
public class RetentionSliceTests : IDisposable
{
    private readonly DatabaseFixture _fx = new();

    private ClipboardItemRepository Repo => _fx.Repository;

    private void AddText(int count, long startMs, string prefix)
    {
        for (var i = 0; i < count; i++)
            Repo.Upsert(DatabaseFixture.NewTextItem($"{prefix} {i}", $"{prefix}{i}", copiedAtMs: startMs + i));
    }

    private void AddImages(int count, long startMs, long byteSize = 1_000)
    {
        for (var i = 0; i < count; i++)
        {
            var item = DatabaseFixture.NewTextItem($"img {i}", $"img{i}", copiedAtMs: startMs + i);
            item.Type = ClipboardItemType.Image;
            item.TextContent = null;
            item.ByteSize = byteSize;
            item.FilePath = $"media/img{i}.png";
            item.ThumbPath = $"thumbs/img{i}.jpg";
            Repo.Upsert(item);
        }
    }

    private long CountWhere(string where)
    {
        using var conn = _fx.Database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM items WHERE " + where;
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void BigDrop_GoesThroughSeveralSlices_AndKeepsTheNewest()
    {
        // 10k -> 1k style cut, scaled down: more than two full slices to delete
        var total = ClipboardItemRepository.RetentionSliceSize * 2 + 300;
        using (var conn = _fx.Database.OpenConnection())
        using (var tx = conn.BeginTransaction())
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO items (type, created_at, last_copied_at, origin, content_hash, byte_size, text_content)
                VALUES ('text', $t, $t, 'clipboard', $h, 10, 'x');
                """;
            var t = cmd.Parameters.Add("$t", SqliteType.Integer);
            var h = cmd.Parameters.Add("$h", SqliteType.Text);
            for (var i = 0; i < total; i++)
            {
                t.Value = 10_000L + i;
                h.Value = "s" + i;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        Repo.Upsert(DatabaseFixture.NewTextItem("fixado velho", "pin", copiedAtMs: 1, pinned: true));

        Repo.ApplyRetention(new RetentionLimits(MaxItems: 100));

        Assert.Equal(101, Repo.Count());
        Assert.Equal(1, CountWhere("pinned = 1"));
        // the survivors are the 100 newest
        Assert.Equal(100, CountWhere($"pinned = 0 AND last_copied_at >= {10_000 + total - 100}"));
    }

    [Fact]
    public void MaxImages_DropsOldImagesOnly_AndReportsTheirFiles()
    {
        AddImages(30, startMs: 1_000);
        AddText(30, startMs: 500, prefix: "txt"); // older than every image, still kept

        var orphans = Repo.ApplyRetention(new RetentionLimits(MaxItems: 1_000, MaxImages: 10));

        Assert.Equal(10, CountWhere("type = 'image'"));
        Assert.Equal(30, CountWhere("type = 'text'"));
        Assert.Equal(40, orphans.Count); // 20 images x (file + thumb)
        Assert.Contains("media/img0.png", orphans);
        Assert.DoesNotContain("media/img29.png", orphans);
        // the newest images survive
        Assert.Equal(10, CountWhere("type = 'image' AND last_copied_at >= 1020"));
    }

    [Fact]
    public void MaxMediaBytes_KeepsNewestImagesUnderTheBudget_AndIgnoresText()
    {
        AddImages(10, startMs: 1_000, byteSize: 100);
        for (var i = 0; i < 5; i++)
        {
            var big = DatabaseFixture.NewTextItem($"texto grande {i}", $"big{i}", copiedAtMs: 100 + i);
            big.ByteSize = 10_000;
            Repo.Upsert(big);
        }

        Repo.ApplyRetention(new RetentionLimits(MaxMediaBytes: 350));

        Assert.Equal(3, CountWhere("type = 'image'"));
        Assert.Equal(3, CountWhere("type = 'image' AND last_copied_at >= 1007"));
        Assert.Equal(5, CountWhere("type = 'text'"));
    }

    [Fact]
    public void PinnedAndFavoriteImages_IgnoreTheImageCaps()
    {
        AddImages(5, startMs: 1_000, byteSize: 1_000);
        using (var conn = _fx.Database.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE items SET pinned = 1 WHERE content_hash = 'img0'; UPDATE items SET favorite = 1 WHERE content_hash = 'img1';";
            cmd.ExecuteNonQuery();
        }

        Repo.ApplyRetention(new RetentionLimits(MaxImages: 1, MaxMediaBytes: 1));

        Assert.Equal(1, CountWhere("content_hash = 'img0'"));
        Assert.Equal(1, CountWhere("content_hash = 'img1'"));
        Assert.Equal(0, CountWhere("type = 'image' AND pinned = 0 AND favorite = 0"));
    }

    [Fact]
    public void UnderEveryLimit_DeletesNothing()
    {
        AddImages(3, startMs: 1_000);
        AddText(3, startMs: 2_000, prefix: "t");

        var orphans = Repo.ApplyRetention(new RetentionLimits(1_000, 0, 1L << 30, 200, 1L << 30));

        Assert.Empty(orphans);
        Assert.Equal(6, Repo.Count());
    }

    public void Dispose() => _fx.Dispose();
}

/// <summary>Schema v4: preview column, FTS rebuilt with a single prefix index, incremental vacuum.</summary>
public class SchemaV4Tests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"klip-v4-{Guid.NewGuid():N}.db");

    private static string ScalarText(Database db, string sql)
    {
        using var conn = db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }

    [Fact]
    public void FreshDatabase_IsIncrementalVacuum_WithTheV4Schema()
    {
        using var db = new Database(_dbPath);
        db.Initialize();

        Assert.Equal(Database.SchemaVersion.ToString(), ScalarText(db, "PRAGMA user_version;"));
        Assert.Equal("2", ScalarText(db, "PRAGMA auto_vacuum;"));
        Assert.Contains("prefix='3'", ScalarText(db, "SELECT sql FROM sqlite_master WHERE name = 'items_fts';"));
        Assert.Equal("1", ScalarText(db, "SELECT COUNT(*) FROM pragma_table_info('items') WHERE name = 'preview';"));
        Assert.Equal("1", ScalarText(db, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'ix_items_evict';"));
        Assert.Equal("1", ScalarText(db, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'ix_items_evict_type';"));
    }

    [Fact]
    public void V3Database_WithData_UpgradesAndKeepsSearchWorking()
    {
        // build a real v3 file: run the current init, then roll it back to the v3 shape
        // (old FTS with prefix '2 3', no preview, no evict indexes, auto_vacuum NONE)
        using (var conn = new SqliteConnection(new Database(_dbPath).ConnectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE items (
                    id INTEGER PRIMARY KEY, type TEXT NOT NULL, created_at INTEGER NOT NULL,
                    last_copied_at INTEGER NOT NULL, source_app TEXT, source_title TEXT,
                    origin TEXT NOT NULL DEFAULT 'clipboard', pinned INTEGER NOT NULL DEFAULT 0,
                    favorite INTEGER NOT NULL DEFAULT 0, content_hash TEXT NOT NULL,
                    byte_size INTEGER NOT NULL, text_content TEXT, html_content TEXT,
                    file_path TEXT, thumb_path TEXT, files_json TEXT, ocr_text TEXT,
                    width INTEGER, height INTEGER, rtf_content TEXT
                );
                CREATE UNIQUE INDEX ix_items_hash ON items(content_hash);
                CREATE INDEX ix_items_pinned_recency ON items(pinned DESC, last_copied_at DESC);
                CREATE VIRTUAL TABLE items_fts USING fts5(
                    text_content, source_app, source_title, ocr_text,
                    content='items', content_rowid='id',
                    tokenize='unicode61 remove_diacritics 2', prefix='2 3');
                CREATE TRIGGER items_ai AFTER INSERT ON items BEGIN
                    INSERT INTO items_fts(rowid, text_content, source_app, source_title, ocr_text)
                    VALUES (new.id, new.text_content, new.source_app, new.source_title, new.ocr_text);
                END;
                INSERT INTO items (type, created_at, last_copied_at, content_hash, byte_size, text_content)
                VALUES ('text', 1, 1, 'a', 10, 'relatorio mensal de vendas'),
                       ('text', 2, 2, 'b', 10, 'ação de cobrança');
                PRAGMA user_version = 3;
                """;
            cmd.ExecuteNonQuery();
        }

        using var db = new Database(_dbPath);
        db.Initialize();
        var repo = new ClipboardItemRepository(db);

        Assert.Equal(Database.SchemaVersion.ToString(), ScalarText(db, "PRAGMA user_version;"));
        Assert.Equal("relatorio mensal de vendas", ScalarText(db, "SELECT preview FROM items WHERE content_hash = 'a';"));
        Assert.Single(repo.QuerySummaries(new HistoryQuery { SearchText = "rel" }));
        Assert.Single(repo.QuerySummaries(new HistoryQuery { SearchText = "acao" })); // diacritics still folded
        // the old file is not converted on startup, only on the maintenance pass
        Assert.Equal("0", ScalarText(db, "PRAGMA auto_vacuum;"));

        db.RunMaintenance();

        Assert.Equal("2", ScalarText(db, "PRAGMA auto_vacuum;"));
        Assert.True(db.QuickCheck());
        Assert.Single(repo.QuerySummaries(new HistoryQuery { SearchText = "vendas" }));

        // and the triggers came back with the new FTS table
        repo.Upsert(DatabaseFixture.NewTextItem("novo depois da migracao", "c"));
        Assert.Single(repo.QuerySummaries(new HistoryQuery { SearchText = "migracao" }));
    }

    [Fact]
    public void ReclaimSpace_GivesFreePagesBack()
    {
        using var db = new Database(_dbPath);
        db.Initialize();
        var repo = new ClipboardItemRepository(db);
        for (var i = 0; i < 300; i++)
            repo.Upsert(DatabaseFixture.NewTextItem(new string('z', 4_000) + i, "r" + i));
        repo.ClearAll();
        using (var conn = db.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }
        Assert.True(long.Parse(ScalarText(db, "PRAGMA freelist_count;")) > 0);

        repo.ReclaimSpace();

        Assert.Equal("0", ScalarText(db, "PRAGMA freelist_count;"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch (IOException) { }
    }
}
