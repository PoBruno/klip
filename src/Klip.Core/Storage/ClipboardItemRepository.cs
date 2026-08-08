using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Klip.Core.Storage;

/// <summary>Combined history filters.</summary>
public sealed class HistoryQuery
{
    public ClipboardItemType? Type { get; init; }
    public bool OnlyFavorites { get; init; }
    public string? SearchText { get; init; }
    public long? DateFromMs { get; init; }
    public long? DateToMs { get; init; }
    /// <summary>Keyset for paging; ignored when there's a search.</summary>
    public long? BeforeLastCopiedAtMs { get; init; }
    public int Limit { get; init; } = 100;

    /// <summary>
    /// RF-P2.10: quando true a busca ordena apenas por i.last_copied_at DESC, pulando o
    /// calculo de bm25() (que le as listas de posicao do indice FTS para CADA match antes
    /// do LIMIT). Serve para busca incremental enquanto o usuario digita.
    /// Default false = ordenacao por relevancia, o comportamento historico.
    /// </summary>
    public bool OrderBySearchRecency { get; init; }
}

/// <summary>
/// Reads and writes the history items. Writes are serialized by the caller;
/// reads run side by side thanks to WAL.
/// </summary>
public sealed class ClipboardItemRepository(Database database)
{
    /// <summary>
    /// RF-P2.09: tamanho maximo de cada lote de ids em "DELETE ... WHERE id IN (...)".
    /// Mantem o texto do statement curto e fica muito abaixo do limite de variaveis do SQLite.
    /// </summary>
    private const int DeleteBatchSize = 500;

    /// <summary>Inserts, or if the hash is already there just bumps last_copied_at and moves it to the top.</summary>
    public long Upsert(ClipboardItem item)
    {
        using var conn = database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO items (type, created_at, last_copied_at, source_app, source_title, origin,
                               pinned, favorite, content_hash, byte_size, text_content, html_content,
                               rtf_content, file_path, thumb_path, files_json, ocr_text, width, height)
            VALUES ($type, $created, $copied, $app, $title, $origin,
                    $pinned, $favorite, $hash, $size, $text, $html,
                    $rtf, $file, $thumb, $files, $ocr, $w, $h)
            ON CONFLICT(content_hash) DO UPDATE SET last_copied_at = excluded.last_copied_at
            RETURNING id;
            """;

        // RF-P2.08: parametros declarados com SqliteType explicito em vez de AddWithValue.
        // AddWithValue precisa inferir o tipo do object em runtime a cada chamada (e eram 19
        // por Upsert); aqui o tipo ja vem pronto e o binding vai direto.
        var p = cmd.Parameters;
        p.Add("$type", SqliteType.Text).Value = TypeToDb(item.Type);
        p.Add("$created", SqliteType.Integer).Value = item.CreatedAt.ToUnixTimeMilliseconds();
        p.Add("$copied", SqliteType.Integer).Value = item.LastCopiedAt.ToUnixTimeMilliseconds();
        p.Add("$app", SqliteType.Text).Value = (object?)item.SourceApp ?? DBNull.Value;
        p.Add("$title", SqliteType.Text).Value = (object?)item.SourceTitle ?? DBNull.Value;
        p.Add("$origin", SqliteType.Text).Value = OriginToDb(item.Origin);
        p.Add("$pinned", SqliteType.Integer).Value = item.Pinned ? 1L : 0L;
        p.Add("$favorite", SqliteType.Integer).Value = item.Favorite ? 1L : 0L;
        p.Add("$hash", SqliteType.Text).Value = item.ContentHash;
        p.Add("$size", SqliteType.Integer).Value = item.ByteSize;
        p.Add("$text", SqliteType.Text).Value = (object?)item.TextContent ?? DBNull.Value;
        p.Add("$html", SqliteType.Text).Value = (object?)item.HtmlContent ?? DBNull.Value;
        p.Add("$rtf", SqliteType.Text).Value = (object?)item.RtfContent ?? DBNull.Value;
        p.Add("$file", SqliteType.Text).Value = (object?)item.FilePath ?? DBNull.Value;
        p.Add("$thumb", SqliteType.Text).Value = (object?)item.ThumbPath ?? DBNull.Value;
        p.Add("$files", SqliteType.Text).Value = (object?)item.FilesJson ?? DBNull.Value;
        p.Add("$ocr", SqliteType.Text).Value = (object?)item.OcrText ?? DBNull.Value;
        p.Add("$w", SqliteType.Integer).Value = item.Width is { } w ? w : (object)DBNull.Value;
        p.Add("$h", SqliteType.Integer).Value = item.Height is { } h ? h : (object)DBNull.Value;

        var id = (long)cmd.ExecuteScalar()!;
        item.Id = id;
        return id;
    }

    /// <summary>One query to rule them all, with combined filters.</summary>
    public IReadOnlyList<ClipboardItem> Query(HistoryQuery query)
    {
        using var conn = database.OpenConnection();
        using var cmd = conn.CreateCommand();
        var sql = BuildQuerySql(query, cmd);
        if (sql is null)
            return [];
        cmd.CommandText = sql;
        return ReadAll(cmd);
    }

    /// <summary>
    /// RF-P2.08: diagnostico. Roda EXPLAIN QUERY PLAN em cima do SQL EXATO que Query()
    /// montaria para estes filtros e devolve a coluna "detail" do plano. Existe para os
    /// testes conseguirem provar que nenhuma listagem cai em "USE TEMP B-TREE FOR ORDER BY"
    /// sem precisar duplicar (e deixar apodrecer) o SQL do repositorio.
    /// </summary>
    public IReadOnlyList<string> ExplainQueryPlan(HistoryQuery query)
    {
        using var conn = database.OpenConnection();
        using var cmd = conn.CreateCommand();
        var sql = BuildQuerySql(query, cmd);
        if (sql is null)
            return [];
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;

        var plan = new List<string>();
        using var reader = cmd.ExecuteReader();
        var detail = reader.GetOrdinal("detail");
        while (reader.Read())
            plan.Add(reader.GetString(detail));
        return plan;
    }

    /// <summary>
    /// Monta o SQL da listagem/busca e associa os parametros ao comando.
    /// Devolve null quando a busca ficou vazia depois do saneamento (nada a consultar).
    /// </summary>
    private static string? BuildQuerySql(HistoryQuery query, SqliteCommand cmd)
    {
        var where = new List<string>();
        var hasSearch = !string.IsNullOrWhiteSpace(query.SearchText);

        if (hasSearch)
        {
            var sanitized = SanitizeFtsQuery(query.SearchText!);
            if (sanitized.Length == 0)
                return null;
            where.Add("items_fts MATCH $q");
            cmd.Parameters.Add("$q", SqliteType.Text).Value = sanitized;
        }

        if (query.Type is not null)
        {
            // the "Text" tab lumps text+html together (same thing to the user)
            if (query.Type == ClipboardItemType.Text)
            {
                // RF-P2.08: o "+" e proposital. Com "i.type IN (...)" o SQLite usa
                // ix_items_type_pinned_recency para cada valor do IN, e como ele concatena os
                // dois trechos a saida deixa de estar ordenada - resultado: le TODAS as linhas
                // text+html e joga numa temp B-tree antes do LIMIT. O "+" (no-op documentado do
                // SQLite que desabilita o termo como restricao de indice) faz o planner varrer
                // ix_items_pinned_recency / ix_items_fav_pinned_recency ja na ordem final e
                // apenas filtrar o type linha a linha, parando no LIMIT. Ambos os lados da
                // comparacao sao TEXT, entao a perda de afinidade de coluna nao muda resultado.
                where.Add("+i.type IN ('text', 'html')");
            }
            else
            {
                where.Add("i.type = $type");
                cmd.Parameters.Add("$type", SqliteType.Text).Value = TypeToDb(query.Type.Value);
            }
        }

        if (query.OnlyFavorites)
            where.Add("i.favorite = 1");

        if (query.DateFromMs is not null)
        {
            where.Add("i.last_copied_at >= $from");
            cmd.Parameters.Add("$from", SqliteType.Integer).Value = query.DateFromMs.Value;
        }

        if (query.DateToMs is not null)
        {
            where.Add("i.last_copied_at < $to");
            cmd.Parameters.Add("$to", SqliteType.Integer).Value = query.DateToMs.Value;
        }

        if (!hasSearch && query.BeforeLastCopiedAtMs is not null)
        {
            // keyset paging so na ordem cronologica, ou seja nos itens nao fixados
            where.Add("i.pinned = 0 AND i.last_copied_at < $before");
            cmd.Parameters.Add("$before", SqliteType.Integer).Value = query.BeforeLastCopiedAtMs.Value;
        }

        var whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        cmd.Parameters.Add("$limit", SqliteType.Integer).Value = query.Limit;

        if (!hasSearch)
        {
            // RF-P2.08: este ORDER BY e coberto por ix_items_pinned_recency,
            // ix_items_type_pinned_recency ou ix_items_fav_pinned_recency conforme o filtro.
            return $"""
                SELECT i.* FROM items i
                {whereSql}
                ORDER BY i.pinned DESC, i.last_copied_at DESC
                LIMIT $limit;
                """;
        }

        // RF-P2.10: bm25() pontua TODOS os matches antes do LIMIT, e pontuar exige ler as
        // listas de posicao do indice FTS linha a linha. OrderBySearchRecency troca isso por
        // uma ordenacao simples sobre uma coluna ja carregada.
        var orderBy = query.OrderBySearchRecency
            ? "i.last_copied_at DESC"
            : "bm25(items_fts), i.last_copied_at DESC";

        return $"""
            SELECT i.* FROM items_fts f
            JOIN items i ON i.id = f.rowid
            {whereSql}
            ORDER BY {orderBy}
            LIMIT $limit;
            """;
    }

    /// <summary>Keyset paging, never OFFSET.</summary>
    public IReadOnlyList<ClipboardItem> GetPage(long? beforeLastCopiedAtMs = null, int limit = 100,
        ClipboardItemType? type = null, bool onlyFavorites = false) =>
        Query(new HistoryQuery
        {
            BeforeLastCopiedAtMs = beforeLastCopiedAtMs,
            Limit = limit,
            Type = type,
            OnlyFavorites = onlyFavorites,
        });

    /// <summary>FTS5 search; the query gets cleaned up into a prefix query.</summary>
    public IReadOnlyList<ClipboardItem> Search(string query, int limit = 100) =>
        Query(new HistoryQuery { SearchText = query, Limit = limit });

    /// <summary>
    /// RF-P2.10: sobrecarga com escolha de ordenacao. orderByRecency = true pula bm25()
    /// e ordena so por recencia (busca incremental). A sobrecarga antiga continua valendo
    /// como relevancia, para nao mexer nos chamadores existentes.
    /// </summary>
    public IReadOnlyList<ClipboardItem> Search(string query, int limit, bool orderByRecency) =>
        Query(new HistoryQuery
        {
            SearchText = query,
            Limit = limit,
            OrderBySearchRecency = orderByRecency,
        });

    public void SetPinned(long id, bool pinned) => SetFlag(id, "pinned", pinned);
    public void SetFavorite(long id, bool favorite) => SetFlag(id, "favorite", favorite);

    /// <summary>
    /// Later edits from the editor update the SAME item, so we don't spam the
    /// history with new rows. Returns the old path so the caller can clean it up.
    /// </summary>
    /// <summary>Sets the OCR text of an item; the FTS trigger reindexes it for search.</summary>
    public void UpdateOcrText(long id, string ocrText)
    {
        using var conn = database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE items SET ocr_text = $ocr WHERE id = $id;";
        cmd.Parameters.Add("$ocr", SqliteType.Text).Value = ocrText;
        cmd.Parameters.Add("$id", SqliteType.Integer).Value = id;
        cmd.ExecuteNonQuery();
    }

    public string? UpdateImageContent(long id, string contentHash, long byteSize,
        string filePath, int width, int height)
    {
        using var conn = database.OpenConnection();

        string? oldPath;
        using (var read = conn.CreateCommand())
        {
            read.CommandText = "SELECT file_path FROM items WHERE id = $id;";
            read.Parameters.Add("$id", SqliteType.Integer).Value = id;
            oldPath = read.ExecuteScalar() as string;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE items SET
                content_hash = $hash, byte_size = $size, file_path = $file,
                width = $w, height = $h, last_copied_at = $now
            WHERE id = $id;
            """;
        cmd.Parameters.Add("$hash", SqliteType.Text).Value = contentHash;
        cmd.Parameters.Add("$size", SqliteType.Integer).Value = byteSize;
        cmd.Parameters.Add("$file", SqliteType.Text).Value = filePath;
        cmd.Parameters.Add("$w", SqliteType.Integer).Value = width;
        cmd.Parameters.Add("$h", SqliteType.Integer).Value = height;
        cmd.Parameters.Add("$now", SqliteType.Integer).Value = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        cmd.Parameters.Add("$id", SqliteType.Integer).Value = id;
        try
        {
            cmd.ExecuteNonQuery();
            return oldPath == filePath ? null : oldPath;
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19) // UNIQUE content_hash
        {
            // same content as another existing item, so just leave it alone
            return null;
        }
    }

    public ClipboardItem? GetById(long id)
    {
        using var conn = database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM items WHERE id = $id;";
        cmd.Parameters.Add("$id", SqliteType.Integer).Value = id;
        var list = ReadAll(cmd);
        return list.Count > 0 ? list[0] : null;
    }

    public void Delete(long id)
    {
        using var conn = database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM items WHERE id = $id;";
        cmd.Parameters.Add("$id", SqliteType.Integer).Value = id;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Clears everything but keeps pinned and favorite items.</summary>
    public int ClearAll()
    {
        using var conn = database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM items WHERE pinned = 0 AND favorite = 0;";
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Compacta o banco pra recuperar o espaco que sobrou depois das delecoes.</summary>
    public void Vacuum()
    {
        using var conn = database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "VACUUM;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// RF-P2.10: funde os b-trees incrementais do FTS5 em um so. Depois de muita ingestao o
    /// indice fica fragmentado em varios segmentos e toda busca precisa varrer todos eles.
    /// Q-P.4: o schema declara prefix='2 3', que grava DOIS indices de prefixo extras a cada
    /// insercao (encarece escrita e disco). Tirar isso agora exigiria rebuild completo do
    /// indice, entao fica como questao em aberto no plano - aqui so registramos o custo.
    /// </summary>
    public void OptimizeFullTextIndex()
    {
        try
        {
            using var conn = database.OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO items_fts(items_fts) VALUES('optimize');";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // otimizacao e oportunista: banco ocupado ou indice ausente nao pode derrubar o app
        }
    }

    public long Count()
    {
        using var conn = database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM items;";
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>Every item, for export. Oldest first.</summary>
    public IReadOnlyList<ClipboardItem> GetAllForExport()
    {
        using var conn = database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM items ORDER BY created_at ASC;";
        return ReadAll(cmd);
    }

    /// <summary>True when an item with this hash already exists (import dedupe).</summary>
    public bool ExistsByHash(string contentHash)
    {
        using var conn = database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM items WHERE content_hash = $h LIMIT 1;";
        cmd.Parameters.Add("$h", SqliteType.Text).Value = contentHash;
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Retention: drops the oldest first, never pinned/favorite ones.
    /// Returns the orphan file paths so the caller can wipe them off disk.
    /// RF-P2.09: nenhuma etapa varre a tabela inteira e nenhum valor entra no SQL por
    /// interpolacao de string.
    /// </summary>
    public IReadOnlyList<string> ApplyRetention(int maxItems, int maxAgeDays, long maxTotalBytes = 0)
    {
        var orphans = new List<string>();
        // RF-P2.09: quem consome apaga arquivo por arquivo, entao nada de null nem repetido
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var conn = database.OpenConnection();
        // RF-P2.08: BEGIN IMMEDIATE. Uma transacao DEFERRED abre em modo leitura e so pede o
        // write-lock no primeiro DELETE; se outro escritor entrou nesse intervalo o upgrade
        // falha com SQLITE_BUSY sem chance de retry - classico em WAL.
        using var tx = conn.BeginTransaction(deferred: false);

        if (maxAgeDays > 0)
        {
            // RF-P2.09: DELETE ... RETURNING resolve corte e coleta de arquivos em UM
            // statement. Antes eram dois com o mesmo WHERE (um SELECT dos paths, um DELETE).
            var cutoff = DateTimeOffset.UtcNow.AddDays(-maxAgeDays).ToUnixTimeMilliseconds();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                DELETE FROM items
                WHERE pinned = 0 AND favorite = 0 AND last_copied_at < $cutoff
                RETURNING file_path, thumb_path;
                """;
            cmd.Parameters.Add("$cutoff", SqliteType.Integer).Value = cutoff;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                CollectPaths(reader, fileOrdinal: 0, thumbOrdinal: 1, orphans, seen);
        }

        if (maxItems > 0)
        {
            // RF-P2.09: a subquery ordenada com OFFSET rodava DUAS vezes (uma para coletar os
            // arquivos, outra dentro do DELETE). Agora roda uma vez so e os ids ficam
            // materializados para o delete em lotes.
            var ids = new List<long>();
            using (var collect = conn.CreateCommand())
            {
                collect.Transaction = tx;
                collect.CommandText = """
                    SELECT id, file_path, thumb_path FROM items
                    WHERE pinned = 0 AND favorite = 0
                    ORDER BY last_copied_at DESC
                    LIMIT -1 OFFSET $max;
                    """;
                collect.Parameters.Add("$max", SqliteType.Integer).Value = maxItems;
                using var reader = collect.ExecuteReader();
                while (reader.Read())
                {
                    ids.Add(reader.GetInt64(0));
                    CollectPaths(reader, fileOrdinal: 1, thumbOrdinal: 2, orphans, seen);
                }
            }
            DeleteByIds(conn, tx, ids);
        }

        if (maxTotalBytes > 0)
        {
            // RF-P2.09: o soma-acumulada saiu do C# e virou window function. Antes o SELECT
            // vinha SEM LIMIT e trazia TODAS as linhas nao fixadas para o processo so para
            // somar byte_size; agora o SQLite so devolve as linhas que ja estouraram o teto.
            var ids = new List<long>();
            using (var scan = conn.CreateCommand())
            {
                scan.Transaction = tx;
                scan.CommandText = """
                    WITH ranked AS (
                        SELECT id, file_path, thumb_path,
                               SUM(byte_size) OVER (ORDER BY last_copied_at DESC
                                                    ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS running
                        FROM items WHERE pinned = 0 AND favorite = 0
                    )
                    SELECT id, file_path, thumb_path FROM ranked WHERE running > $maxTotalBytes;
                    """;
                scan.Parameters.Add("$maxTotalBytes", SqliteType.Integer).Value = maxTotalBytes;
                using var reader = scan.ExecuteReader();
                while (reader.Read())
                {
                    ids.Add(reader.GetInt64(0));
                    CollectPaths(reader, fileOrdinal: 1, thumbOrdinal: 2, orphans, seen);
                }
            }
            DeleteByIds(conn, tx, ids);
        }

        tx.Commit();
        return orphans;
    }

    /// <summary>
    /// RF-P2.09: DELETE por lista de ids sem interpolar valor nenhum no SQL. Os placeholders
    /// sao numerados ($id0, $id1, ...) e vao em lotes de DeleteBatchSize.
    /// </summary>
    private static void DeleteByIds(SqliteConnection conn, SqliteTransaction tx, List<long> ids)
    {
        for (var offset = 0; offset < ids.Count; offset += DeleteBatchSize)
        {
            var count = Math.Min(DeleteBatchSize, ids.Count - offset);
            var sql = new StringBuilder("DELETE FROM items WHERE id IN (", 40 + count * 7);
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            for (var i = 0; i < count; i++)
            {
                var name = "$id" + i.ToString(CultureInfo.InvariantCulture);
                if (i > 0)
                    sql.Append(',');
                sql.Append(name);
                cmd.Parameters.Add(name, SqliteType.Integer).Value = ids[offset + i];
            }

            sql.Append(");");
            cmd.CommandText = sql.ToString();
            cmd.ExecuteNonQuery();
        }
    }

    private static void CollectPaths(SqliteDataReader reader, int fileOrdinal, int thumbOrdinal,
        List<string> orphans, HashSet<string> seen)
    {
        AddPath(reader, fileOrdinal, orphans, seen);
        AddPath(reader, thumbOrdinal, orphans, seen);
    }

    private static void AddPath(SqliteDataReader reader, int ordinal, List<string> orphans, HashSet<string> seen)
    {
        if (reader.IsDBNull(ordinal))
            return;
        var path = reader.GetString(ordinal);
        if (path.Length > 0 && seen.Add(path))
            orphans.Add(path);
    }

    private void SetFlag(long id, string column, bool value)
    {
        using var conn = database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE items SET {column} = $v WHERE id = $id;";
        cmd.Parameters.Add("$v", SqliteType.Integer).Value = value ? 1L : 0L;
        cmd.Parameters.Add("$id", SqliteType.Integer).Value = id;
        cmd.ExecuteNonQuery();
    }

    private static IReadOnlyList<ClipboardItem> ReadAll(SqliteCommand cmd)
    {
        var result = new List<ClipboardItem>();
        using var reader = cmd.ExecuteReader();

        // RF-P2.08: ordinais resolvidos UMA vez por query. Antes montava um
        // Dictionary<string,int> a cada chamada e pagava ~21 hashes de string por linha.
        var oId = reader.GetOrdinal("id");
        var oType = reader.GetOrdinal("type");
        var oCreated = reader.GetOrdinal("created_at");
        var oCopied = reader.GetOrdinal("last_copied_at");
        var oApp = reader.GetOrdinal("source_app");
        var oTitle = reader.GetOrdinal("source_title");
        var oOrigin = reader.GetOrdinal("origin");
        var oPinned = reader.GetOrdinal("pinned");
        var oFavorite = reader.GetOrdinal("favorite");
        var oHash = reader.GetOrdinal("content_hash");
        var oSize = reader.GetOrdinal("byte_size");
        var oText = reader.GetOrdinal("text_content");
        var oHtml = reader.GetOrdinal("html_content");
        var oRtf = reader.GetOrdinal("rtf_content");
        var oFile = reader.GetOrdinal("file_path");
        var oThumb = reader.GetOrdinal("thumb_path");
        var oFiles = reader.GetOrdinal("files_json");
        var oOcr = reader.GetOrdinal("ocr_text");
        var oWidth = reader.GetOrdinal("width");
        var oHeight = reader.GetOrdinal("height");

        while (reader.Read())
        {
            result.Add(new ClipboardItem
            {
                Id = reader.GetInt64(oId),
                Type = TypeFromDb(reader.GetString(oType)),
                CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(oCreated)),
                LastCopiedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(oCopied)),
                SourceApp = reader.IsDBNull(oApp) ? null : reader.GetString(oApp),
                SourceTitle = reader.IsDBNull(oTitle) ? null : reader.GetString(oTitle),
                Origin = OriginFromDb(reader.GetString(oOrigin)),
                Pinned = reader.GetInt64(oPinned) == 1,
                Favorite = reader.GetInt64(oFavorite) == 1,
                ContentHash = reader.GetString(oHash),
                ByteSize = reader.GetInt64(oSize),
                TextContent = reader.IsDBNull(oText) ? null : reader.GetString(oText),
                HtmlContent = reader.IsDBNull(oHtml) ? null : reader.GetString(oHtml),
                RtfContent = reader.IsDBNull(oRtf) ? null : reader.GetString(oRtf),
                FilePath = reader.IsDBNull(oFile) ? null : reader.GetString(oFile),
                ThumbPath = reader.IsDBNull(oThumb) ? null : reader.GetString(oThumb),
                FilesJson = reader.IsDBNull(oFiles) ? null : reader.GetString(oFiles),
                OcrText = reader.IsDBNull(oOcr) ? null : reader.GetString(oOcr),
                Width = reader.IsDBNull(oWidth) ? null : (int)reader.GetInt64(oWidth),
                Height = reader.IsDBNull(oHeight) ? null : (int)reader.GetInt64(oHeight),
            });
        }
        return result;
    }

    private static string TypeToDb(ClipboardItemType type) => type switch
    {
        ClipboardItemType.Text => "text",
        ClipboardItemType.Html => "html",
        ClipboardItemType.Image => "image",
        ClipboardItemType.Files => "files",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static ClipboardItemType TypeFromDb(string value) => value switch
    {
        "text" => ClipboardItemType.Text,
        "html" => ClipboardItemType.Html,
        "image" => ClipboardItemType.Image,
        "files" => ClipboardItemType.Files,
        _ => ClipboardItemType.Text,
    };

    /// <summary>
    /// RF-P2.08: mesmo resultado do antigo Origin.ToString().ToLowerInvariant(), sem as duas
    /// alocacoes de string por gravacao.
    /// </summary>
    private static string OriginToDb(ClipboardItemOrigin origin) => origin switch
    {
        ClipboardItemOrigin.Clipboard => "clipboard",
        ClipboardItemOrigin.Capture => "capture",
        ClipboardItemOrigin.Editor => "editor",
        ClipboardItemOrigin.Recording => "recording",
        _ => "clipboard",
    };

    /// <summary>
    /// RF-P2.08: substitui Enum.Parse&lt;ClipboardItemOrigin&gt;(reflexao + alocacao) que rodava
    /// POR LINHA lida. Os valores gravados sao sempre minusculos (ver OriginToDb); qualquer
    /// outra caixa vinda de base antiga/importacao cai no TryParse, que e o caminho raro.
    /// </summary>
    private static ClipboardItemOrigin OriginFromDb(string value) => value switch
    {
        "clipboard" => ClipboardItemOrigin.Clipboard,
        "capture" => ClipboardItemOrigin.Capture,
        "editor" => ClipboardItemOrigin.Editor,
        "recording" => ClipboardItemOrigin.Recording,
        _ => Enum.TryParse<ClipboardItemOrigin>(value, ignoreCase: true, out var parsed)
            ? parsed
            : ClipboardItemOrigin.Clipboard,
    };

    /// <summary>
    /// Wraps each user term as a quoted phrase.
    /// RF-P2.10: o curinga de prefixo so entra em termos com 3+ caracteres. Um "a*" casa com
    /// praticamente todo token do indice, e o FTS5 ainda precisa unir todas essas listas de
    /// documentos antes de descartar o resultado - custo alto para um filtro que nao filtra.
    /// Termos de 1 ou 2 caracteres viram busca exata entre aspas.
    /// </summary>
    internal static string SanitizeFtsQuery(string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" ", terms.Select(t =>
        {
            var quoted = t.Replace("\"", "\"\"");
            return t.Length >= 3 ? $"\"{quoted}\"*" : $"\"{quoted}\"";
        }));
    }
}
