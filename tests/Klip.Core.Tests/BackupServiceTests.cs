using Klip.Core.Common;
using Klip.Core.Storage;

namespace Klip.Core.Tests;

[Collection("AppPaths")]
public class BackupServiceTests : IDisposable
{
    private readonly string _root;
    private readonly DatabaseFixture _fx = new();
    private readonly BackupService _backup;

    public BackupServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"klip-backup-{Guid.NewGuid():N}");
        AppPaths.Root = _root;
        AppPaths.EnsureCreated();
        _backup = new BackupService(_fx.Repository, new MediaStore());
    }

    [Fact]
    public void Export_ThenImport_IntoEmptyDb_RestoresItems()
    {
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("primeiro", "h1", copiedAtMs: 1000));
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("segundo", "h2", copiedAtMs: 2000, favorite: true));

        var zip = Path.Combine(_root, "backup.zip");
        var exp = _backup.Export(zip);
        Assert.Equal(2, exp.Items);
        Assert.True(File.Exists(zip));

        // import into a fresh db
        var db2Path = Path.Combine(_root, "other.db");
        using var db2 = new Database(db2Path);
        db2.Initialize();
        var repo2 = new ClipboardItemRepository(db2);
        var backup2 = new BackupService(repo2, new MediaStore());

        var imp = backup2.Import(zip);

        Assert.Equal(2, imp.Imported);
        Assert.Equal(0, imp.SkippedDuplicates);
        Assert.Equal(2, repo2.Count());
        Assert.Contains(repo2.GetAllForExport(), i => i.TextContent == "segundo" && i.Favorite);
    }

    [Fact]
    public void Import_Twice_SkipsDuplicatesByHash()
    {
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("único", "h1"));
        var zip = Path.Combine(_root, "backup.zip");
        _backup.Export(zip);

        // import back into the mesmo banco: everything is a duplicate
        var imp = _backup.Import(zip);
        Assert.Equal(0, imp.Imported);
        Assert.Equal(1, imp.SkippedDuplicates);
        Assert.Equal(1, _fx.Repository.Count());
    }

    [Fact]
    public void Import_InvalidZip_Throws()
    {
        Directory.CreateDirectory(_root); var bad = Path.Combine(_root, "bad.zip");
        System.IO.Compression.ZipFile.Open(bad, System.IO.Compression.ZipArchiveMode.Create).Dispose();
        Assert.Throws<InvalidDataException>(() => _backup.Import(bad));
    }

    public void Dispose()
    {
        _fx.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
