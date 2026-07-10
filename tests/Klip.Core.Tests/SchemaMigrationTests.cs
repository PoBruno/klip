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

        // user_version bumped to the current schema (>= 2)
        using (var v = conn.CreateCommand())
        {
            v.CommandText = "PRAGMA user_version;";
            Assert.True(Convert.ToInt32(v.ExecuteScalar()) >= 2);
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
        Assert.True(db.QuickCheck());
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
