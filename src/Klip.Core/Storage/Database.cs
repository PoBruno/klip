using Microsoft.Data.Sqlite;

namespace Klip.Core.Storage;

/// <summary>
/// Creates and opens the db: WAL, schema, and FTS5 external-content with triggers.
/// </summary>
public sealed class Database : IDisposable
{
    /// <summary>
    /// RF-P2.07: PRAGMAs que vivem no arquivo do banco. Rodam UMA vez, no Initialize().
    /// journal_mode=WAL e gravado no header do arquivo e sobrevive ao fechamento da
    /// conexao, entao repetir a cada OpenConnection() so gastava I/O a toa.
    /// </summary>
    private const string PersistentPragmas = "PRAGMA journal_mode=WAL;";

    /// <summary>
    /// RF-P2.07: PRAGMAs que valem apenas para a conexao atual, entao precisam rodar em
    /// todo OpenConnection(). Vao juntos em um unico ExecuteNonQuery para pagar um
    /// round-trip so.
    /// - busy_timeout=5000: sem ele qualquer SQLITE_BUSY estoura na hora em vez de
    ///   esperar o escritor terminar (o caso comum em WAL com leitura concorrente).
    /// - cache_size=-16000: valor negativo = KiB, ou seja 16 MiB de cache de paginas.
    /// - mmap_size: 64 MiB mapeados, corta copias de buffer nas leituras.
    /// NAO ha PRAGMA foreign_keys aqui de proposito: o schema (ver SchemaV1) nao declara
    /// nenhuma FOREIGN KEY, entao o enforcement nunca teve o que checar. Alem disso o
    /// e_sqlite3 embutido pelo SQLitePCLRaw ja e compilado com foreign_keys ligado por
    /// padrao, ou seja o "PRAGMA foreign_keys=ON" que existia aqui era literalmente um
    /// statement a mais por conexao para nao mudar nada (RF-P2.07).
    /// </summary>
    private const string PerConnectionPragmas =
        "PRAGMA synchronous=NORMAL;" +
        "PRAGMA busy_timeout=5000;" +
        "PRAGMA cache_size=-16000;" +
        "PRAGMA temp_store=MEMORY;" +
        "PRAGMA mmap_size=67108864;";

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
        // RF-P2.07: so os por-conexao; journal_mode ficou no Initialize()
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

        // RF-P2.07: PRAGMA persistente, uma vez so por arquivo
        Exec(conn, PersistentPragmas);

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
        if (version < 3)
        {
            // v3: indices de cobertura do ORDER BY (RF-P2.08)
            Exec(conn, SchemaV3);
            SetUserVersion(conn, 3);
            version = 3;
        }
    }

    /// <summary>
    /// RF-P2.07: manutencao periodica barata. "PRAGMA optimize" reavalia as estatisticas
    /// (ANALYZE incremental) das tabelas que mudaram muito, e o checkpoint TRUNCATE devolve
    /// o -wal ao tamanho zero em vez de deixar o arquivo crescer indefinidamente.
    /// Cada passo tem seu proprio try/catch: manutencao nunca pode derrubar o app - se o
    /// banco estiver ocupado por outro leitor, o checkpoint simplesmente nao acontece agora.
    /// </summary>
    public void RunMaintenance()
    {
        using var conn = OpenConnection();

        try
        {
            Exec(conn, "PRAGMA optimize;");
        }
        catch (SqliteException)
        {
            // sem estatisticas atualizadas nesta rodada; segue o jogo
        }

        try
        {
            Exec(conn, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        catch (SqliteException)
        {
            // leitor ativo segurando o WAL; o proximo ciclo tenta de novo
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

    /// <summary>
    /// ATENCAO (efeito colateral global): ClearAllPools() e estatico e derruba o pool de
    /// TODAS as conexoes SQLite do processo, nao so as deste banco. Hoje o app tem um unico
    /// arquivo de banco e o Dispose acontece no shutdown, entao e aceitavel - e alem disso e
    /// o que garante que o arquivo fica liberado (importante nos testes, que apagam o .db no
    /// fim do fixture). Se um dia existir um segundo Database vivo no mesmo processo, isso
    /// precisa virar um pool por instancia.
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

    // v3: indices alinhados ao ORDER BY real das listagens (RF-P2.08)
    private const string SchemaV3 = """
        -- RF-P2.08: as queries filtram por type/favorite mas ordenam por
        -- (pinned DESC, last_copied_at DESC). Sem um indice que cubra o ORDER BY inteiro
        -- o SQLite monta uma temp B-tree so pra ordenar - custo O(n log n) sobre TODAS as
        -- linhas que passam no filtro, antes mesmo de aplicar o LIMIT.
        CREATE INDEX IF NOT EXISTS ix_items_type_pinned_recency
            ON items(type, pinned DESC, last_copied_at DESC);
        CREATE INDEX IF NOT EXISTS ix_items_fav_pinned_recency
            ON items(pinned DESC, last_copied_at DESC) WHERE favorite = 1;

        -- indices da v1 que ficaram redundantes e ainda por cima atrapalhavam o planner
        -- (ele os escolhia pelo filtro e depois ordenava em temp B-tree). Cada um deles era
        -- tambem um b-tree a mais para manter em todo INSERT/UPDATE.
        -- ix_items_type_recency(type, last_copied_at): prefixo de ix_items_type_pinned_recency.
        DROP INDEX IF EXISTS ix_items_type_recency;
        -- ix_items_fav(favorite) WHERE favorite=1: mesmo predicado parcial de
        -- ix_items_fav_pinned_recency, que ainda resolve a ordenacao.
        DROP INDEX IF EXISTS ix_items_fav;
        -- ix_items_recency(last_copied_at): nenhuma query ordena so por recencia sem tambem
        -- restringir/ordenar por pinned, e ix_items_pinned_recency cobre todas elas.
        DROP INDEX IF EXISTS ix_items_recency;
        """;
}
