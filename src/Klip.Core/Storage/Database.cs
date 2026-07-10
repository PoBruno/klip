using Microsoft.Data.Sqlite;

namespace Klip.Core.Storage;

/// <summary>
/// Creates and opens the db: WAL, schema, and FTS5 external-content with triggers.
/// </summary>
public sealed class Database : IDisposable
{
    public string ConnectionString { get; }

    public Database(string databaseFile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databaseFile)!);
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
    }

    public SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    /// <summary>Integrity check at startup.</summary>
    public bool QuickCheck()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA quick_check;";
        return (string?)cmd.ExecuteScalar() == "ok";
    }

    public void Initialize()
    {
        using var conn = OpenConnection();

        var version = GetUserVersion(conn);

        // migracoes incrementais e idempotentes: cada bloco sobe o user_version
        if (version < 1)
        {
            Exec(conn, SchemaV1);
            SetUserVersion(conn, 1);
            version = 1;
        }
        if (version < 2)
        {
            // v2: paste keeping formatting (RTF)
            Exec(conn, "ALTER TABLE items ADD COLUMN rtf_content TEXT;");
            SetUserVersion(conn, 2);
            version = 2;
        }
    }

    private static int GetUserVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void SetUserVersion(SqliteConnection conn, int version)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA user_version = {version};";
        cmd.ExecuteNonQuery();
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => SqliteConnection.ClearAllPools();

    // base schema v1
    private const string SchemaV1 = """
        CREATE TABLE IF NOT EXISTS items (
            id              INTEGER PRIMARY KEY,
            type            TEXT NOT NULL,
            created_at      INTEGER NOT NULL,
            last_copied_at  INTEGER NOT NULL,
            source_app      TEXT,
            source_title    TEXT,
            origin          TEXT NOT NULL DEFAULT 'clipboard',
            pinned          INTEGER NOT NULL DEFAULT 0,
            favorite        INTEGER NOT NULL DEFAULT 0,
            content_hash    TEXT NOT NULL,
            byte_size       INTEGER NOT NULL,
            text_content    TEXT,
            html_content    TEXT,
            file_path       TEXT,
            thumb_path      TEXT,
            files_json      TEXT,
            ocr_text        TEXT,
            width           INTEGER,
            height          INTEGER
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ix_items_hash ON items(content_hash);
        CREATE INDEX IF NOT EXISTS ix_items_recency ON items(last_copied_at DESC);
        CREATE INDEX IF NOT EXISTS ix_items_type_recency ON items(type, last_copied_at DESC);
        CREATE INDEX IF NOT EXISTS ix_items_fav ON items(favorite) WHERE favorite = 1;

        CREATE VIRTUAL TABLE IF NOT EXISTS items_fts USING fts5(
            text_content, source_app, source_title, ocr_text,
            content='items', content_rowid='id',
            tokenize='unicode61 remove_diacritics 2',
            prefix='2 3'
        );

        CREATE TRIGGER IF NOT EXISTS items_ai AFTER INSERT ON items BEGIN
            INSERT INTO items_fts(rowid, text_content, source_app, source_title, ocr_text)
            VALUES (new.id, new.text_content, new.source_app, new.source_title, new.ocr_text);
        END;
        CREATE TRIGGER IF NOT EXISTS items_ad AFTER DELETE ON items BEGIN
            INSERT INTO items_fts(items_fts, rowid, text_content, source_app, source_title, ocr_text)
            VALUES ('delete', old.id, old.text_content, old.source_app, old.source_title, old.ocr_text);
        END;
        CREATE TRIGGER IF NOT EXISTS items_au AFTER UPDATE OF text_content, source_app, source_title, ocr_text ON items BEGIN
            INSERT INTO items_fts(items_fts, rowid, text_content, source_app, source_title, ocr_text)
            VALUES ('delete', old.id, old.text_content, old.source_app, old.source_title, old.ocr_text);
            INSERT INTO items_fts(rowid, text_content, source_app, source_title, ocr_text)
            VALUES (new.id, new.text_content, new.source_app, new.source_title, new.ocr_text);
        END;
        """;
}
