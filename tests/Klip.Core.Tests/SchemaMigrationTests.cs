using Klip.Core.Storage;

namespace Klip.Core.Tests;

public class SchemaMigrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"klip-mig-{Guid.NewGuid():N}.db");

    [Fact]
    public void Initialize_SetsUserVersion_AndAddsRtfColumn()
    {
        using var db = new Database(_dbPath);
        db.Initialize();

        using var conn = db.OpenConnection();

        // user_version bumped to the current schema (>= 3 depois da migracao de indices)
        using (var v = conn.CreateCommand())
        {
            v.CommandText = "PRAGMA user_version;";
            Assert.True(Convert.ToInt32(v.ExecuteScalar()) >= 3);
        }

        // rtf_content column exists after the v2 migration
        using (var cols = conn.CreateCommand())
        {
            cols.CommandText = "SELECT COUNT(*) FROM pragma_table_info('items') WHERE name = 'rtf_content';";
            Assert.Equal(1L, (long)cols.ExecuteScalar()!);
        }
    }

    [Fact]
    public void Initialize_IsIdempotent()
    {
        using var db = new Database(_dbPath);
        db.Initialize();
        db.Initialize(); // running twice must not throw or re-add the column
        db.Initialize(); // a v3 tambem precisa aguentar reexecucao (CREATE/DROP INDEX IF EXISTS)
        Assert.True(db.QuickCheck());

        // e o user_version tem que refletir a ultima migracao aplicada
        using var conn = db.OpenConnection();
        using var v = conn.CreateCommand();
        v.CommandText = "PRAGMA user_version;";
        Assert.True(Convert.ToInt32(v.ExecuteScalar()) >= 3);
    }

    [Fact]
    public void MigrationV3_CreatesOrderCoveringIndexes_AndDropsTheRedundantOnes()
    {
        // RF-P2.08
        using var db = new Database(_dbPath);
        db.Initialize();
        db.Initialize(); // idempotente: rodar de novo nao pode recriar os indices removidos

        var indexes = IndexNames(db);

        Assert.Contains("ix_items_type_pinned_recency", indexes);
        Assert.Contains("ix_items_fav_pinned_recency", indexes);
        // continuam valendo: unicidade do hash e a ordenacao padrao
        Assert.Contains("ix_items_hash", indexes);
        Assert.Contains("ix_items_pinned_recency", indexes);

        // redundantes com os novos indices compostos/parciais
        Assert.DoesNotContain("ix_items_type_recency", indexes);
        Assert.DoesNotContain("ix_items_fav", indexes);
        Assert.DoesNotContain("ix_items_recency", indexes);
    }

    [Fact]
    public void MigrationV3_UpgradesAnExistingV2Database()
    {
        // simula base ja em producao: cria na v2 (com os indices antigos) e so depois migra
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(new Database(_dbPath).ConnectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE items (
                    id INTEGER PRIMARY KEY, type TEXT NOT NULL, created_at INTEGER NOT NULL,
                    last_copied_at INTEGER NOT NULL, source_app TEXT, source_title TEXT,
                    origin TEXT NOT NULL DEFAULT 'clipboard', pinned INTEGER NOT NULL DEFAULT 0,
                    favorite INTEGER NOT NULL DEFAULT 0, content_hash TEXT NOT NULL,
                    byte_size INTEGER NOT NULL, text_content TEXT, html_content TEXT,
                    file_path TEXT, thumb_path TEXT, files_json TEXT, ocr_text TEXT,
                    width INTEGER, height INTEGER, rtf_content TEXT
                );
                CREATE INDEX ix_items_type_recency ON items(type, last_copied_at DESC);
                CREATE INDEX ix_items_recency ON items(last_copied_at DESC);
                CREATE INDEX ix_items_fav ON items(favorite) WHERE favorite = 1;
                PRAGMA user_version = 2;
                """;
            cmd.ExecuteNonQuery();
        }

        using var db = new Database(_dbPath);
        db.Initialize();

        var indexes = IndexNames(db);
        Assert.Contains("ix_items_type_pinned_recency", indexes);
        Assert.Contains("ix_items_fav_pinned_recency", indexes);
        Assert.DoesNotContain("ix_items_type_recency", indexes);
        Assert.DoesNotContain("ix_items_fav", indexes);
        Assert.DoesNotContain("ix_items_recency", indexes);
        Assert.True(db.QuickCheck());
    }

    private static List<string> IndexNames(Database db)
    {
        using var conn = db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index';";
        var names = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    [Fact]
    public void RtfContent_RoundTrips()
    {
        using var db = new Database(_dbPath);
        db.Initialize();
        var repo = new ClipboardItemRepository(db);
        var item = DatabaseFixture.NewTextItem("texto", "h1");
        item.HtmlContent = "<b>texto</b>";
        item.RtfContent = @"{\rtf1 texto}";
        var id = repo.Upsert(item);

        var loaded = repo.GetById(id)!;
        Assert.Equal(@"{\rtf1 texto}", loaded.RtfContent);
        Assert.Equal("<b>texto</b>", loaded.HtmlContent);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch (IOException) { }
    }
}
