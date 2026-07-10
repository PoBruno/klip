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
}

/// <summary>
/// Reads and writes the history items. Writes are serialized by the caller;
/// reads run side by side thanks to WAL.
/// </summary>
public sealed class ClipboardItemRepository(Database database)
{
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
        cmd.Parameters.AddWithValue("$type", TypeToDb(item.Type));
        cmd.Parameters.AddWithValue("$created", item.CreatedAt.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$copied", item.LastCopiedAt.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$app", (object?)item.SourceApp ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$title", (object?)item.SourceTitle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$origin", item.Origin.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$pinned", item.Pinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$favorite", item.Favorite ? 1 : 0);
        cmd.Parameters.AddWithValue("$hash", item.ContentHash);
        cmd.Parameters.AddWithValue("$size", item.ByteSize);
        cmd.Parameters.AddWithValue("$text", (object?)item.TextContent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$html", (object?)item.HtmlContent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rtf", (object?)item.RtfContent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$file", (object?)item.FilePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$thumb", (object?)item.ThumbPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$files", (object?)item.FilesJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ocr", (object?)item.OcrText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$w", (object?)item.Width ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$h", (object?)item.Height ?? DBNull.Value);
        var id = (long)cmd.ExecuteScalar()!;
        item.Id = id;
        return id;
    }

    /// <summary>One query to rule them all, with combined filters.</summary>
    public IReadOnlyList<ClipboardItem> Query(HistoryQuery query)
    {
        using var conn = database.OpenConnection();
        using var cmd = conn.CreateCommand();

        var where = new List<string>();
        var hasSearch = !string.IsNullOrWhiteSpace(query.SearchText);

        if (hasSearch)
        {
            var sanitized = SanitizeFtsQuery(query.SearchText!);
            if (sanitized.Length == 0)
                return [];
            where.Add("items_fts MATCH $q");
            cmd.Parameters.AddWithValue("$q", sanitized);
        }

        if (query.Type is not null)
        {
            // the "Text" tab lumps text+html together (same thing to the user)
            if (query.Type == ClipboardItemType.Text)
            {
                where.Add("i.type IN ('text', 'html')");
            }
            else
            {
                where.Add("i.type = $type");
                cmd.Parameters.AddWithValue("$type", TypeToDb(query.Type.Value));
            }
        }

        if (query.OnlyFavorites)
            where.Add("i.favorite = 1");

        if (query.DateFromMs is not null)
        {
            where.Add("i.last_copied_at >= $from");
            cmd.Parameters.AddWithValue("$from", query.DateFromMs.Value);
        }

        if (query.DateToMs is not null)
        {
            where.Add("i.last_copied_at < $to");
            cmd.Parameters.AddWithValue("$to", query.DateToMs.Value);
        }

        if (!hasSearch && query.BeforeLastCopiedAtMs is not null)
        {
            // keyset paging so na ordem cronologica, ou seja nos itens nao fixados
            where.Add("i.pinned = 0 AND i.last_copied_at < $before");
            cmd.Parameters.AddWithValue("$before", query.BeforeLastCopiedAtMs.Value);
        }

        var whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        cmd.CommandText = hasSearch
            ? $"""
              SELECT i.* FROM items_fts f
              JOIN items i ON i.id = f.rowid
              {whereSql}
              ORDER BY bm25(items_fts), i.last_copied_at DESC
              LIMIT $limit;
              """
            : $"""
              SELECT i.* FROM items i
              {whereSql}
              ORDER BY i.pinned DESC, i.last_copied_at DESC
              LIMIT $limit;
              """;
        cmd.Parameters.AddWithValue("$limit", query.Limit);
        return ReadAll(cmd);
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
        cmd.Parameters.AddWithValue("$ocr", ocrText);
        cmd.Parameters.AddWithValue("$id", id);
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
            read.Parameters.AddWithValue("$id", id);
            oldPath = read.ExecuteScalar() as string;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE items SET
                content_hash = $hash, byte_size = $size, file_path = $file,
                width = $w, height = $h, last_copied_at = $now
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$hash", contentHash);
        cmd.Parameters.AddWithValue("$size", byteSize);
        cmd.Parameters.AddWithValue("$file", filePath);
        cmd.Parameters.AddWithValue("$w", width);
        cmd.Parameters.AddWithValue("$h", height);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$id", id);
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
        cmd.Parameters.AddWithValue("$id", id);
        var list = ReadAll(cmd);
        return list.Count > 0 ? list[0] : null;
    }

    public void Delete(long id)
    {
        using var conn = database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM items WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
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

    /// <summary>Compacta o banco pra recuperar o espa[co que sobrou depois das delecoes.</summary>
    public void Vacuum()
    {
        using var conn = database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "VACUUM;";
        cmd.ExecuteNonQuery();
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
        cmd.Parameters.AddWithValue("$h", contentHash);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Retention: drops the oldest first, never pinned/favorite ones.
    /// Returns the orphan file paths so the caller can wipe them off disk.
    /// </summary>
    public IReadOnlyList<string> ApplyRetention(int maxItems, int maxAgeDays, long maxTotalBytes = 0)
    {
        var orphans = new List<string>();
        using var conn = database.OpenConnection();
        using var tx = conn.BeginTransaction();

        if (maxAgeDays > 0)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-maxAgeDays).ToUnixTimeMilliseconds();
            CollectFiles(conn, tx, $"pinned = 0 AND favorite = 0 AND last_copied_at < {cutoff}", orphans);
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM items WHERE pinned = 0 AND favorite = 0 AND last_copied_at < $cutoff;";
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            cmd.ExecuteNonQuery();
        }

        if (maxItems > 0)
        {
            const string overflow = """
                id IN (SELECT id FROM items WHERE pinned = 0 AND favorite = 0
                       ORDER BY last_copied_at DESC LIMIT -1 OFFSET $max)
                """;
            using (var collect = conn.CreateCommand())
            {
                collect.Transaction = tx;
                collect.CommandText = $"SELECT file_path, thumb_path FROM items WHERE {overflow};";
                collect.Parameters.AddWithValue("$max", maxItems);
                using var reader = collect.ExecuteReader();
                while (reader.Read())
                {
                    if (!reader.IsDBNull(0)) orphans.Add(reader.GetString(0));
                    if (!reader.IsDBNull(1)) orphans.Add(reader.GetString(1));
                }
            }
            using var del = conn.CreateCommand();
            del.Transaction = tx;
            del.CommandText = $"DELETE FROM items WHERE {overflow};";
            del.Parameters.AddWithValue("$max", maxItems);
            del.ExecuteNonQuery();
        }

        if (maxTotalBytes > 0)
        {
            // walk oldest to newest (skipping pinned/favorites), drop until the
            // running total of byte_size fits under the cap
            var toDelete = new List<long>();
            using (var scan = conn.CreateCommand())
            {
                scan.Transaction = tx;
                scan.CommandText = """
                    SELECT id, file_path, thumb_path, byte_size FROM items
                    WHERE pinned = 0 AND favorite = 0
                    ORDER BY last_copied_at DESC;
                    """;
                using var reader = scan.ExecuteReader();
                long running = 0;
                while (reader.Read())
                {
                    running += reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
                    if (running <= maxTotalBytes)
                        continue;
                    toDelete.Add(reader.GetInt64(0));
                    if (!reader.IsDBNull(1)) orphans.Add(reader.GetString(1));
                    if (!reader.IsDBNull(2)) orphans.Add(reader.GetString(2));
                }
            }
            if (toDelete.Count > 0)
            {
                using var del = conn.CreateCommand();
                del.Transaction = tx;
                del.CommandText = $"DELETE FROM items WHERE id IN ({string.Join(',', toDelete)});";
                del.ExecuteNonQuery();
            }
        }

        tx.Commit();
        return orphans;
    }

    private static void CollectFiles(SqliteConnection conn, SqliteTransaction tx, string where, List<string> into)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT file_path, thumb_path FROM items WHERE {where};";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0)) into.Add(reader.GetString(0));
            if (!reader.IsDBNull(1)) into.Add(reader.GetString(1));
        }
    }

    private void SetFlag(long id, string column, bool value)
    {
        using var conn = database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE items SET {column} = $v WHERE id = $id;";
        cmd.Parameters.AddWithValue("$v", value ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private static IReadOnlyList<ClipboardItem> ReadAll(SqliteCommand cmd)
    {
        var result = new List<ClipboardItem>();
        using var reader = cmd.ExecuteReader();
        var o = new Dictionary<string, int>();
        for (var i = 0; i < reader.FieldCount; i++)
            o[reader.GetName(i)] = i;

        while (reader.Read())
        {
            result.Add(new ClipboardItem
            {
                Id = reader.GetInt64(o["id"]),
                Type = TypeFromDb(reader.GetString(o["type"])),
                CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(o["created_at"])),
                LastCopiedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(o["last_copied_at"])),
                SourceApp = reader.IsDBNull(o["source_app"]) ? null : reader.GetString(o["source_app"]),
                SourceTitle = reader.IsDBNull(o["source_title"]) ? null : reader.GetString(o["source_title"]),
                Origin = Enum.Parse<ClipboardItemOrigin>(reader.GetString(o["origin"]), ignoreCase: true),
                Pinned = reader.GetInt64(o["pinned"]) == 1,
                Favorite = reader.GetInt64(o["favorite"]) == 1,
                ContentHash = reader.GetString(o["content_hash"]),
                ByteSize = reader.GetInt64(o["byte_size"]),
                TextContent = reader.IsDBNull(o["text_content"]) ? null : reader.GetString(o["text_content"]),
                HtmlContent = reader.IsDBNull(o["html_content"]) ? null : reader.GetString(o["html_content"]),
                RtfContent = reader.IsDBNull(o["rtf_content"]) ? null : reader.GetString(o["rtf_content"]),
                FilePath = reader.IsDBNull(o["file_path"]) ? null : reader.GetString(o["file_path"]),
                ThumbPath = reader.IsDBNull(o["thumb_path"]) ? null : reader.GetString(o["thumb_path"]),
                FilesJson = reader.IsDBNull(o["files_json"]) ? null : reader.GetString(o["files_json"]),
                OcrText = reader.IsDBNull(o["ocr_text"]) ? null : reader.GetString(o["ocr_text"]),
                Width = reader.IsDBNull(o["width"]) ? null : (int)reader.GetInt64(o["width"]),
                Height = reader.IsDBNull(o["height"]) ? null : (int)reader.GetInt64(o["height"]),
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

    /// <summary>Wraps each user term as a quoted phrase with a prefix (type-ahead).</summary>
    internal static string SanitizeFtsQuery(string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" ", terms.Select(t => $"\"{t.Replace("\"", "\"\"")}\"*"));
    }
}
