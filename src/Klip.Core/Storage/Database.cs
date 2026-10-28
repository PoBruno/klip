using Microsoft.Data.Sqlite;

namespace Klip.Core.Storage;

/// <summary>
/// Creates and opens the db: WAL, schema, and FTS5 external-content with triggers.
/// </summary>
public sealed class Database : IDisposable
{
    // journal_mode lives in the file header, so it only needs to run once per file
    private const string PersistentPragmas = "PRAGMA journal_mode=WAL;";

    // per connection settings, sent in one round trip on every open.
    // busy_timeout: wait for the writer instead of failing right away.
    // cache_size: negative = KiB, so 16 MiB of page cache.
    // journal_size_limit: keeps the -wal from staying huge after a big import.
    // no foreign_keys here on purpose: the schema has none.
    private const string PerConnectionPragmas =
        "PRAGMA synchronous=NORMAL;" +
        "PRAGMA busy_timeout=5000;" +
        "PRAGMA cache_size=-16000;" +
        "PRAGMA temp_store=MEMORY;" +
        "PRAGMA mmap_size=67108864;" +
        "PRAGMA journal_size_limit=67108864;";

    /// <summary>Latest schema version (PRAGMA user_version).</summary>
    public const int SchemaVersion = 5;

    // free pages above this go back to the OS on the next maintenance pass (~4 MiB)
    private const int FreePagesWorthReclaiming = 1024;

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
        pragma.CommandText = PerConnectionPragmas;
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

        // a brand new file can pick incremental auto_vacuum for free, before any table exists.
        // old files get converted later, off the startup path (see RunMaintenance)
        if (version == 0 && !HasTables(conn))
            Exec(conn, "PRAGMA auto_vacuum=INCREMENTAL;");

        Exec(conn, PersistentPragmas);

        // each block is idempotent and bumps user_version
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
        if (version < 3)
        {
            Exec(conn, SchemaV3);
            SetUserVersion(conn, 3);
            version = 3;
        }
        var migrated = false;
        if (version < 4)
        {
            using var tx = conn.BeginTransaction(deferred: false);
            if (!HasColumn(conn, tx, "items", "preview"))
                Exec(conn, "ALTER TABLE items ADD COLUMN preview TEXT;", tx);
            Exec(conn, SchemaV4, tx);
            Exec(conn, "PRAGMA user_version = 4;", tx);
            tx.Commit();
            version = 4;
            migrated = true;
        }
        if (version < 5)
        {
            using var tx = conn.BeginTransaction(deferred: false);
            Exec(conn, SchemaV5, tx);
            Exec(conn, "PRAGMA user_version = 5;", tx);
            tx.Commit();
            version = 5;
            migrated = true;
        }

        // fresh stats for the planner after the indexes changed; 0x10002 also covers
        // tables that were never analyzed (sqlite.org/pragma.html#pragma_optimize)
        TryExec(conn, migrated ? "PRAGMA optimize=0x10002;" : "PRAGMA optimize;");
    }

    /// <summary>
    /// Cheap periodic maintenance, meant for a background thread: planner stats, giving
    /// free pages back to the OS, and truncating the -wal. Each step has its own try:
    /// a busy database just skips that step until the next pass.
    /// </summary>
    public void RunMaintenance()
    {
        using var conn = OpenConnection();

        TryExec(conn, "PRAGMA optimize;");

        try
        {
            if (ScalarLong(conn, "PRAGMA auto_vacuum;") == 0)
            {
                // old file from before incremental vacuum: convert it once. VACUUM rewrites
                // the file, so this runs here (after retention trimmed it) and not on startup
                Exec(conn, "PRAGMA auto_vacuum=INCREMENTAL;");
                Exec(conn, "VACUUM;");
            }
            else if (ScalarLong(conn, "PRAGMA freelist_count;") > FreePagesWorthReclaiming)
            {
                Exec(conn, "PRAGMA incremental_vacuum(4000);");
            }
        }
        catch (SqliteException)
        {
            // another connection was busy; next pass
        }

        TryExec(conn, "PRAGMA wal_checkpoint(TRUNCATE);");
    }

    /// <summary>
    /// Gives free pages back to the OS without rewriting the whole file (unlike VACUUM).
    /// Fast enough for app exit.
    /// </summary>
    public void ReclaimFreePages()
    {
        using var conn = OpenConnection();
        try
        {
            if (ScalarLong(conn, "PRAGMA auto_vacuum;") == 2)
                Exec(conn, "PRAGMA incremental_vacuum;");
        }
        catch (SqliteException)
        {
        }
    }

    private static bool HasTables(SqliteConnection conn) =>
        ScalarLong(conn, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';") > 0;

    private static bool HasColumn(SqliteConnection conn, SqliteTransaction tx, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $c;";
        cmd.Parameters.AddWithValue("$c", column);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private static long ScalarLong(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    private static int GetUserVersion(SqliteConnection conn) => (int)ScalarLong(conn, "PRAGMA user_version;");

    private static void SetUserVersion(SqliteConnection conn, int version) =>
        Exec(conn, $"PRAGMA user_version = {version};");

    private static void Exec(SqliteConnection conn, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void TryExec(SqliteConnection conn, string sql)
    {
        try
        {
            Exec(conn, sql);
        }
        catch (SqliteException)
        {
            // maintenance never takes the app down
        }
    }

    /// <summary>
    /// Heads up: ClearAllPools() is global, it drops the pool of every SQLite connection in
    /// the process. Fine with a single db that is disposed on shutdown, and it is what frees
    /// the file for the tests that delete it.
    /// </summary>
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
        -- the default history sort is pinned first, then recency
        CREATE INDEX IF NOT EXISTS ix_items_pinned_recency ON items(pinned DESC, last_copied_at DESC);

        CREATE VIRTUAL TABLE IF NOT EXISTS items_fts USING fts5(
            text_content, source_app, source_title, ocr_text,
            content='items', content_rowid='id',
            tokenize='unicode61 remove_diacritics 2',
            prefix='2 3'
        );
        """ + FtsTriggers;

    private const string FtsTriggers = """
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

    // v3: indexes that match the real ORDER BY of the listings
    private const string SchemaV3 = """
        -- lists filter by type/favorite but sort by (pinned DESC, last_copied_at DESC);
        -- without an index on the whole ORDER BY sqlite sorts every matching row in a temp
        -- b-tree before the LIMIT
        CREATE INDEX IF NOT EXISTS ix_items_type_pinned_recency
            ON items(type, pinned DESC, last_copied_at DESC);
        CREATE INDEX IF NOT EXISTS ix_items_fav_pinned_recency
            ON items(pinned DESC, last_copied_at DESC) WHERE favorite = 1;

        -- v1 indexes made redundant by the ones above (and they fooled the planner)
        DROP INDEX IF EXISTS ix_items_type_recency;
        DROP INDEX IF EXISTS ix_items_fav;
        DROP INDEX IF EXISTS ix_items_recency;
        """;

    // v4: preview column for the list, FTS without the never used 2 char prefix index,
    // and indexes for retention so it doesn't walk the whole table every pass
    private const string SchemaV4 = """
        UPDATE items SET preview = substr(text_content, 1, 600)
        WHERE text_content IS NOT NULL AND preview IS NULL;

        DROP TRIGGER IF EXISTS items_ai;
        DROP TRIGGER IF EXISTS items_ad;
        DROP TRIGGER IF EXISTS items_au;
        DROP TABLE IF EXISTS items_fts;
        CREATE VIRTUAL TABLE items_fts USING fts5(
            text_content, source_app, source_title, ocr_text,
            content='items', content_rowid='id',
            tokenize='unicode61 remove_diacritics 2',
            prefix='3'
        );
        INSERT INTO items_fts(items_fts) VALUES('rebuild');
        """ + FtsTriggers + """

        -- evictable rows only (pinned and favorites never go): count, byte sums and the
        -- oldest-first walk all come straight from these, no table scan
        CREATE INDEX IF NOT EXISTS ix_items_evict
            ON items(last_copied_at DESC, byte_size) WHERE pinned = 0 AND favorite = 0;
        CREATE INDEX IF NOT EXISTS ix_items_evict_type
            ON items(type, last_copied_at DESC, byte_size) WHERE pinned = 0 AND favorite = 0;
        """;

    // v5: snippets, text the user saves on purpose. Lives apart from items so retention,
    // "clear all" and the history search never touch it
    private const string SchemaV5 = """
        CREATE TABLE IF NOT EXISTS snippets (
            id          INTEGER PRIMARY KEY,
            name        TEXT NOT NULL,
            alias       TEXT,
            content     TEXT NOT NULL,
            created_at  INTEGER NOT NULL,
            updated_at  INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_snippets_alias ON snippets(alias COLLATE NOCASE) WHERE alias IS NOT NULL;
        CREATE INDEX IF NOT EXISTS ix_snippets_updated ON snippets(updated_at DESC);

        CREATE VIRTUAL TABLE IF NOT EXISTS snippets_fts USING fts5(
            name, alias, content,
            content='snippets', content_rowid='id',
            tokenize='unicode61 remove_diacritics 2',
            prefix='3'
        );
        CREATE TRIGGER IF NOT EXISTS snippets_ai AFTER INSERT ON snippets BEGIN
            INSERT INTO snippets_fts(rowid, name, alias, content) VALUES (new.id, new.name, new.alias, new.content);
        END;
        CREATE TRIGGER IF NOT EXISTS snippets_ad AFTER DELETE ON snippets BEGIN
            INSERT INTO snippets_fts(snippets_fts, rowid, name, alias, content)
            VALUES ('delete', old.id, old.name, old.alias, old.content);
        END;
        CREATE TRIGGER IF NOT EXISTS snippets_au AFTER UPDATE OF name, alias, content ON snippets BEGIN
            INSERT INTO snippets_fts(snippets_fts, rowid, name, alias, content)
            VALUES ('delete', old.id, old.name, old.alias, old.content);
            INSERT INTO snippets_fts(rowid, name, alias, content) VALUES (new.id, new.name, new.alias, new.content);
        END;
        """;
}
