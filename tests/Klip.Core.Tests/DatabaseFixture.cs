using Klip.Core.Storage;

namespace Klip.Core.Tests;

public sealed class DatabaseFixture : IDisposable
{
    public string DbPath { get; }
    public Database Database { get; }
    public ClipboardItemRepository Repository { get; }

    public DatabaseFixture()
    {
        DbPath = Path.Combine(Path.GetTempPath(), $"klip-test-{Guid.NewGuid():N}.db");
        Database = new Database(DbPath);
        Database.Initialize();
        Repository = new ClipboardItemRepository(Database);
    }

    public static ClipboardItem NewTextItem(string text, string hash, long copiedAtMs = 0,
        bool pinned = false, bool favorite = false)
    {
        var ts = copiedAtMs == 0 ? DateTimeOffset.UtcNow : DateTimeOffset.FromUnixTimeMilliseconds(copiedAtMs);
        return new ClipboardItem
        {
            Type = ClipboardItemType.Text,
            CreatedAt = ts,
            LastCopiedAt = ts,
            ContentHash = hash,
            ByteSize = text.Length,
            TextContent = text,
            SourceApp = "test.exe",
            SourceTitle = "Janela de teste",
            Pinned = pinned,
            Favorite = favorite,
        };
    }

    public void Dispose()
    {
        Database.Dispose();
        try
        {
            File.Delete(DbPath);
            File.Delete(DbPath + "-wal");
            File.Delete(DbPath + "-shm");
        }
        catch (IOException)
        {
            // Melhor esforço em CI
        }
    }
}
