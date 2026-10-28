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

    /// <summary>
    /// Keyset for paging over unpinned items; ignored when there's a search. Pair it with
    /// <see cref="BeforeId"/> so items copied in the same millisecond don't get skipped.
    /// </summary>
    public long? BeforeLastCopiedAtMs { get; init; }

    /// <summary>Id of the last item of the previous page (tie breaker for the keyset).</summary>
    public long? BeforeId { get; init; }

    /// <summary>null = both, true = only pinned, false = only unpinned. Ignored on search.</summary>
    public bool? Pinned { get; init; }

    public int Limit { get; init; } = 100;

    /// <summary>
    /// Search only: newest matches first instead of best matches first. Cheap either way,
    /// the ordering happens inside the FTS index before the join.
    /// </summary>
    public bool OrderBySearchRecency { get; init; }
}

/// <summary>Limits applied by <see cref="ClipboardItemRepository.ApplyRetention(RetentionLimits)"/>; 0 = no limit.</summary>
public sealed record RetentionLimits(
    int MaxItems = 0,
    int MaxAgeDays = 0,
    long MaxTotalBytes = 0,
    int MaxImages = 0,
    long MaxMediaBytes = 0);

/// <summary>
/// Reads and writes the history items. Writes are serialized by the caller;
/// reads run side by side thanks to WAL.
/// </summary>
public sealed class ClipboardItemRepository(Database database)
{
    /// <summary>Rows deleted per transaction by retention, so the write lock is never held for long.</summary>
    internal const int RetentionSliceSize = 500;

    // columns the history list needs; the heavy ones (text, html, rtf, ocr) stay in the db
    private const string SummaryColumns =
        "i.id, i.type, i.created_at, i.last_copied_at, i.source_app, i.origin, i.pinned, i.favorite, " +
        "i.content_hash, i.byte_size, i.preview, i.file_path, i.thumb_path, i.files_json, i.width, i.height";

    private long _dataVersion;

    /// <summary>
    /// Goes up on every write. The flyout compares it with what it loaded last time and
    /// skips the query when nothing changed. (PRAGMA data_version is per connection and
    /// the pool hands out different ones, so it can't do this job.)
    /// </summary>
    public long DataVersion => Interlocked.Read(ref _dataVersion);

    private void Touch() => Interlocked.Increment(ref _dataVersion);

    /// <summary>Inserts, or if the hash is already there just bumps last_copied_at and moves it to the top.</summary>
    public long Upsert(ClipboardItem item)
    {
        item.Preview ??= ClipboardItem.BuildPreview(item.TextContent);

        using var conn = database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO items (type, created_at, last_copied_at, source_app, source_title, origin,
                               pinned, favorite, content_hash, byte_size, text_content, html_content,
                               rtf_content, file_path, thumb_path, files_json, ocr_text, width, height, preview)
            VALUES ($type, $created, $copied, $app, $title, $origin,
                    $pinned, $favorite, $hash, $size, $text, $html,
                    $rtf, $file, $thumb, $files, $ocr, $w, $h, $preview)
            ON CONFLICT(content_hash) DO UPDATE SET last_copied_at = excluded.last_copied_at
            RETURNING id;
            """;

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
        p.Add("$preview", SqliteType.Text).Value = (object?)item.Preview ?? DBNull.Value;

        var id = (long)cmd.ExecuteScalar()!;
        item.Id = id;
        Touch();
        return id;
    }

    /// <summary>Full rows (all content loaded). Fine for small pages, tests and tools.</summary>
    public IReadOnlyList<ClipboardItem> Query(HistoryQuery query)
    {
        using var conn = database.OpenConnection();
        using var cmd = conn.CreateCommand();
        var sql = BuildQuerySql(query, cmd, "i.*");
        if (sql is null)
            return [];
        cmd.CommandText = sql;
        return ReadAll(cmd);
    }

    /// <summary>
    /// What the history list uses: same filters as <see cref="Query"/> but only the columns
    /// a card shows. Items come back with <see cref="ClipboardItem.IsSummary"/> set.
    /// </summary>
    public IReadOnlyList<ClipboardItem> QuerySummaries(HistoryQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var conn = database.OpenConnection();
        using var cmd = conn.CreateCommand();
        var sql = BuildQuerySql(query, cmd, SummaryColumns);
        if (sql is null)
            return [];
        cmd.CommandText = sql;
        using var registration = cancellationToken.Register(static c => ((SqliteCommand)c!).Cancel(), cmd);
        try
        {
            return ReadSummaries(cmd, cancellationToken);
        }
        catch (SqliteException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    /// <summary>
    /// Runs EXPLAIN QUERY PLAN over the exact SQL Query() would build, so tests can check
    /// that no listing falls back to sorting in a temp b-tree.
    /// </summary>
    public IReadOnlyList<string> ExplainQueryPlan(HistoryQuery query)
    {
        using var conn = database.OpenConnection();
        using var cmd = conn.CreateCommand();
        var sql = BuildQuerySql(query, cmd, SummaryColumns);
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
    /// Builds the list/search SQL and binds the parameters. Returns null when the search
    /// is empty after cleanup (nothing to look for).
    /// </summary>
    private static string? BuildQuerySql(HistoryQuery query, SqliteCommand cmd, string columns)
    {
        var where = new List<string>();
        var hasSearch = !string.IsNullOrWhiteSpace(query.SearchText);

        if (query.Type is not null)
        {
            // the "Text" tab lumps text+html together (same thing to the user)
            if (query.Type == ClipboardItemType.Text)
            {
                // the "+" is on purpose: with a plain IN sqlite walks the type index once per
                // value and has to sort the merged output. "+" turns the term into a plain
                // filter, so the scan stays on the (pinned, recency) index, already sorted,
                // and stops at the LIMIT
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

        cmd.Parameters.Add("$limit", SqliteType.Integer).Value = query.Limit;

        if (hasSearch)
            return BuildSearchSql(query, cmd, columns, where);

        if (query.Pinned is { } pinned)
            where.Add(pinned ? "i.pinned = 1" : "i.pinned = 0");

        if (query.BeforeLastCopiedAtMs is not null)
        {
            // keyset only walks the unpinned items (pinned ones are loaded apart).
            // ties on the same millisecond are broken by id, in the same direction the
            // index stores them (rowid ascending inside the key)
            where.Add("i.pinned = 0");
            cmd.Parameters.Add("$before", SqliteType.Integer).Value = query.BeforeLastCopiedAtMs.Value;
            if (query.BeforeId is { } beforeId)
            {
                where.Add("i.last_copied_at <= $before AND (i.last_copied_at < $before OR i.id > $beforeId)");
                cmd.Parameters.Add("$beforeId", SqliteType.Integer).Value = beforeId;
            }
            else
            {
                where.Add("i.last_copied_at < $before");
            }
        }

        var whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

        // covered by ix_items_pinned_recency, ix_items_type_pinned_recency or
        // ix_items_fav_pinned_recency depending on the filter; id ASC is the order
        // the index already keeps ties in, so no extra sort
        return $"""
            SELECT {columns} FROM items i
            {whereSql}
            ORDER BY i.pinned DESC, i.last_copied_at DESC, i.id
            LIMIT $limit;
            """;
    }

    private static string? BuildSearchSql(HistoryQuery query, SqliteCommand cmd, string columns, List<string> where)
    {
        var sanitized = SanitizeFtsQuery(query.SearchText!);
        if (sanitized.Length == 0)
            return null;
        cmd.Parameters.Add("$q", SqliteType.Text).Value = sanitized;

        // rank and rowid are resolved inside the FTS index, so the sort only carries two
        // integers per match; the item rows are read for the winners only. Never bm25() in
        // the outer query: that drags every matching row (html, rtf...) into the sorter
        var innerOrder = query.OrderBySearchRecency ? "rowid DESC" : "rank";
        var outerOrder = query.OrderBySearchRecency ? "i.last_copied_at DESC, i.id DESC" : "m.rank, i.last_copied_at DESC";

        if (where.Count == 0)
        {
            return $"""
                SELECT {columns} FROM
                    (SELECT rowid, rank FROM items_fts WHERE items_fts MATCH $q ORDER BY {innerOrder} LIMIT $limit) m
                JOIN items i ON i.id = m.rowid
                ORDER BY {outerOrder};
                """;
        }

        // with tab/date filters the LIMIT can't go inside (it would cut matches the filter
        // keeps), so filter on the join; still only slim columns
        var whereSql = string.Join(" AND ", where);
        return $"""
            SELECT {columns} FROM
                (SELECT rowid, rank FROM items_fts WHERE items_fts MATCH $q) m
            JOIN items i ON i.id = m.rowid
            WHERE {whereSql}
            ORDER BY {outerOrder}
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

    /// <summary>Search with the newest matches first instead of the best ones.</summary>
    public IReadOnlyList<ClipboardItem> Search(string query, int limit, bool orderByRecency) =>
        Query(new HistoryQuery
        {
            SearchText = query,
            Limit = limit,
            OrderBySearchRecency = orderByRecency,
        });

    public void SetPinned(long id, bool pinned) => SetFlag(id, "pinned", pinned);
    public void SetFavorite(long id, bool favorite) => SetFlag(id, "favorite", favorite);

    /// <summary>Sets the OCR text of an item; the FTS trigger reindexes it for search.</summary>
    public void UpdateOcrText(long id, string ocrText)
    {
        using var conn = database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE items SET ocr_text = $ocr WHERE id = $id;";
        cmd.Parameters.Add("$ocr", SqliteType.Text).Value = ocrText;
        cmd.Parameters.Add("$id", SqliteType.Integer).Value = id;
        cmd.ExecuteNonQuery();
        Touch();
    }

    /// <summary>
    /// Later edits from the editor update the SAME item, so we don't spam the
    /// history with new rows. Returns the old path so the caller can clean it up.
    /// </summary>
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
            Touch();
            return oldPath == filePath ? null : oldPath;
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19) // UNIQUE content_hash
        {
            // same content as another existing item, so just leave it alone
            return null;
        }
    }

    /// <summary>Full row, with every content column. ~0.1 ms, call it right before using the content.</summary>
    public ClipboardItem? GetById(long id)
    {
        using var conn = database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM items WHERE id = $id;";
        cmd.Parameters.Add("$id", SqliteType.Integer).Value = id;
        var list = ReadAll(cmd);
        return list.Count > 0 ? list[0] : null;
    }

    /// <summary>Returns the item itself when it is already complete, else loads the full row.</summary>
    public ClipboardItem LoadFull(ClipboardItem item) =>
        item.IsSummary ? GetById(item.Id) ?? item : item;

    public void Delete(long id)
    {
        using var conn = database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM items WHERE id = $id;";
        cmd.Parameters.Add("$id", SqliteType.Integer).Value = id;
        cmd.ExecuteNonQuery();
        Touch();
    }

    /// <summary>Clears everything but keeps pinned and favorite items.</summary>
    public int ClearAll()
    {
        using var conn = database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM items WHERE pinned = 0 AND favorite = 0;";
        var n = cmd.ExecuteNonQuery();
        Touch();
        return n;
    }

    /// <summary>Full VACUUM: rewrites the whole file. Manual button only.</summary>
    public void Vacuum()
    {
        using var conn = database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "VACUUM;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Gives free pages back without rewriting the file (incremental vacuum). Safe on exit.</summary>
    public void ReclaimSpace() => database.ReclaimFreePages();

    // last full FTS optimize; merges run in between
    private long _lastFtsOptimizeTicks;
    private static readonly TimeSpan FtsOptimizeInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// FTS index upkeep for a background thread. Usually a bounded incremental merge
    /// (a few hundred pages, short lock); the full optimize, which rewrites the whole
    /// index, runs at most once a day.
    /// </summary>
    public void OptimizeFullTextIndex()
    {
        try
        {
            using var conn = database.OpenConnection();
            using var cmd = conn.CreateCommand();
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastFtsOptimizeTicks);
            if (last == 0 || now - last >= (long)FtsOptimizeInterval.TotalMilliseconds)
            {
                // first pass of the process only merges too: right after startup is the
                // worst moment to hold the lock for a full rewrite
                if (last != 0)
                {
                    cmd.CommandText = "INSERT INTO items_fts(items_fts) VALUES('optimize');";
                    cmd.ExecuteNonQuery();
                    Interlocked.Exchange(ref _lastFtsOptimizeTicks, now);
                    return;
                }
                Interlocked.Exchange(ref _lastFtsOptimizeTicks, now);
            }
            cmd.CommandText = "INSERT INTO items_fts(items_fts, rank) VALUES('merge', 500);";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // opportunistic: a busy db or a missing index can't take the app down
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
    /// </summary>
    public IReadOnlyList<string> ApplyRetention(int maxItems, int maxAgeDays, long maxTotalBytes = 0) =>
        ApplyRetention(new RetentionLimits(maxItems, maxAgeDays, maxTotalBytes));

    /// <summary>
    /// Retention in slices: each slice deletes at most <see cref="RetentionSliceSize"/> rows in
    /// its own short transaction, so ingest never waits long for the write lock, even on the
    /// first pass after the limit dropped from 10k to 1k. Every check starts with a cheap
    /// count/sum over the partial indexes and does nothing when under the limit.
    /// </summary>
    public IReadOnlyList<string> ApplyRetention(RetentionLimits limits)
    {
        var orphans = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deleted = 0;

        using var conn = database.OpenConnection();

        if (limits.MaxAgeDays > 0)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-limits.MaxAgeDays).ToUnixTimeMilliseconds();
            deleted += DeleteInSlices(conn, orphans, seen, """
                DELETE FROM items WHERE id IN (
                    SELECT id FROM items
                    WHERE pinned = 0 AND favorite = 0 AND last_copied_at < $cutoff
                    LIMIT $slice)
                RETURNING file_path, thumb_path;
                """, cmd => cmd.Parameters.Add("$cutoff", SqliteType.Integer).Value = cutoff);
        }

        if (limits.MaxItems > 0 &&
            CountEvictable(conn, type: null) > limits.MaxItems)
        {
            deleted += DeleteInSlices(conn, orphans, seen, """
                DELETE FROM items WHERE id IN (
                    SELECT id FROM items
                    WHERE pinned = 0 AND favorite = 0
                    ORDER BY last_copied_at DESC
                    LIMIT $slice OFFSET $max)
                RETURNING file_path, thumb_path;
                """, cmd => cmd.Parameters.Add("$max", SqliteType.Integer).Value = limits.MaxItems);
        }

        if (limits.MaxImages > 0 &&
            CountEvictable(conn, type: "image") > limits.MaxImages)
        {
            deleted += DeleteInSlices(conn, orphans, seen, """
                DELETE FROM items WHERE id IN (
                    SELECT id FROM items
                    WHERE type = 'image' AND pinned = 0 AND favorite = 0
                    ORDER BY last_copied_at DESC
                    LIMIT $slice OFFSET $max)
                RETURNING file_path, thumb_path;
                """, cmd => cmd.Parameters.Add("$max", SqliteType.Integer).Value = limits.MaxImages);
        }

        if (limits.MaxMediaBytes > 0)
            deleted += TrimToByteBudget(conn, "image", limits.MaxMediaBytes, orphans, seen);

        if (limits.MaxTotalBytes > 0)
            deleted += TrimToByteBudget(conn, null, limits.MaxTotalBytes, orphans, seen);

        if (deleted > 0)
            Touch();
        return orphans;
    }

    private static long CountEvictable(SqliteConnection conn, string? type)
    {
        using var cmd = conn.CreateCommand();
        if (type is null)
        {
            cmd.CommandText = "SELECT COUNT(*) FROM items WHERE pinned = 0 AND favorite = 0;";
        }
        else
        {
            cmd.CommandText = "SELECT COUNT(*) FROM items WHERE type = $t AND pinned = 0 AND favorite = 0;";
            cmd.Parameters.Add("$t", SqliteType.Text).Value = type;
        }
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// Runs a "DELETE ... LIMIT $slice ... RETURNING" statement over and over, one
    /// transaction per slice, until a slice comes back short.
    /// </summary>
    private static int DeleteInSlices(SqliteConnection conn, List<string> orphans, HashSet<string> seen,
        string sql, Action<SqliteCommand> bind)
    {
        var total = 0;
        while (true)
        {
            var count = 0;
            // IMMEDIATE: a deferred tx would start as a reader and could fail the upgrade
            // to writer with SQLITE_BUSY and no retry, the classic WAL trap
            using (var tx = conn.BeginTransaction(deferred: false))
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.Parameters.Add("$slice", SqliteType.Integer).Value = RetentionSliceSize;
                bind(cmd);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        count++;
                        CollectPaths(reader, fileOrdinal: 0, thumbOrdinal: 1, orphans, seen);
                    }
                }
                tx.Commit();
            }
            total += count;
            if (count < RetentionSliceSize)
                return total;
        }
    }

    /// <summary>
    /// Drops the oldest evictable rows (optionally of one type) until the sum of byte_size
    /// fits the budget. The sum and the running total come from the partial indexes alone.
    /// </summary>
    private static int TrimToByteBudget(SqliteConnection conn, string? type, long budget,
        List<string> orphans, HashSet<string> seen)
    {
        var typeFilter = type is null ? "" : "type = $t AND ";

        using (var sum = conn.CreateCommand())
        {
            sum.CommandText = $"SELECT total(byte_size) FROM items WHERE {typeFilter}pinned = 0 AND favorite = 0;";
            if (type is not null)
                sum.Parameters.Add("$t", SqliteType.Text).Value = type;
            if (Convert.ToDouble(sum.ExecuteScalar(), CultureInfo.InvariantCulture) <= budget)
                return 0;
        }

        // ids over the budget, newest kept first
        var ids = new List<long>();
        using (var scan = conn.CreateCommand())
        {
            scan.CommandText = $"""
                SELECT id FROM (
                    SELECT id, SUM(byte_size) OVER (ORDER BY last_copied_at DESC, id
                                                    ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS running
                    FROM items WHERE {typeFilter}pinned = 0 AND favorite = 0)
                WHERE running > $budget;
                """;
            scan.Parameters.Add("$budget", SqliteType.Integer).Value = budget;
            if (type is not null)
                scan.Parameters.Add("$t", SqliteType.Text).Value = type;
            using var reader = scan.ExecuteReader();
            while (reader.Read())
                ids.Add(reader.GetInt64(0));
        }

        var total = 0;
        for (var offset = 0; offset < ids.Count; offset += RetentionSliceSize)
        {
            var count = Math.Min(RetentionSliceSize, ids.Count - offset);
            using var tx = conn.BeginTransaction(deferred: false);
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            var sql = new StringBuilder("DELETE FROM items WHERE pinned = 0 AND favorite = 0 AND id IN (", 80 + count * 7);
            for (var i = 0; i < count; i++)
            {
                var name = "$id" + i.ToString(CultureInfo.InvariantCulture);
                if (i > 0)
                    sql.Append(',');
                sql.Append(name);
                cmd.Parameters.Add(name, SqliteType.Integer).Value = ids[offset + i];
            }
            // pinned/favorite checked again: someone may have pinned it since the scan
            sql.Append(") RETURNING file_path, thumb_path;");
            cmd.CommandText = sql.ToString();
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    total++;
                    CollectPaths(reader, fileOrdinal: 0, thumbOrdinal: 1, orphans, seen);
                }
            }
            tx.Commit();
        }
        return total;
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
        Touch();
    }

    private static IReadOnlyList<ClipboardItem> ReadAll(SqliteCommand cmd)
    {
        var result = new List<ClipboardItem>();
        using var reader = cmd.ExecuteReader();

        // ordinals resolved once per query, not per row
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
        var oPreview = reader.GetOrdinal("preview");

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
                Preview = reader.IsDBNull(oPreview) ? null : reader.GetString(oPreview),
            });
        }
        return result;
    }

    // same order as SummaryColumns
    private static IReadOnlyList<ClipboardItem> ReadSummaries(SqliteCommand cmd, CancellationToken cancellationToken)
    {
        var result = new List<ClipboardItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(new ClipboardItem
            {
                IsSummary = true,
                Id = reader.GetInt64(0),
                Type = TypeFromDb(reader.GetString(1)),
                CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                LastCopiedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                SourceApp = reader.IsDBNull(4) ? null : reader.GetString(4),
                Origin = OriginFromDb(reader.GetString(5)),
                Pinned = reader.GetInt64(6) == 1,
                Favorite = reader.GetInt64(7) == 1,
                ContentHash = reader.GetString(8),
                ByteSize = reader.GetInt64(9),
                Preview = reader.IsDBNull(10) ? null : reader.GetString(10),
                FilePath = reader.IsDBNull(11) ? null : reader.GetString(11),
                ThumbPath = reader.IsDBNull(12) ? null : reader.GetString(12),
                FilesJson = reader.IsDBNull(13) ? null : reader.GetString(13),
                Width = reader.IsDBNull(14) ? null : (int)reader.GetInt64(14),
                Height = reader.IsDBNull(15) ? null : (int)reader.GetInt64(15),
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

    // same as Origin.ToString().ToLowerInvariant() without two allocations per write
    private static string OriginToDb(ClipboardItemOrigin origin) => origin switch
    {
        ClipboardItemOrigin.Clipboard => "clipboard",
        ClipboardItemOrigin.Capture => "capture",
        ClipboardItemOrigin.Editor => "editor",
        ClipboardItemOrigin.Recording => "recording",
        _ => "clipboard",
    };

    // values are always written lowercase; TryParse is only for old/imported data
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
    /// Wraps each user term as a quoted phrase. Only terms with 3+ chars get the prefix
    /// wildcard: "a*" matches nearly every token and FTS5 still has to merge all those
    /// doclists before the filter does anything. 1 and 2 char terms are exact.
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
