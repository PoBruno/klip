using Klip.Core.Snippets;
using Microsoft.Data.Sqlite;

namespace Klip.Core.Storage;

/// <summary>CRUD and search for snippets. Same db as the history, its own table.</summary>
public sealed class SnippetRepository(Database database)
{
    public const int MaxNameLength = 200;
    public const int MaxAliasLength = 64;

    private long _dataVersion;

    /// <summary>Goes up on every write, so the flyout can skip a reload when nothing changed.</summary>
    public long DataVersion => Interlocked.Read(ref _dataVersion);

    /// <summary>Inserts (Id = 0) or updates the snippet. Returns its id.</summary>
    public long Save(Snippet snippet)
    {
        ArgumentNullException.ThrowIfNull(snippet);
        var name = Clip(snippet.Name.Trim(), MaxNameLength);
        var alias = NormalizeAlias(snippet.Alias);
        var now = DateTimeOffset.UtcNow;

        using var conn = database.OpenConnection();
        using var cmd = conn.CreateCommand();
        if (snippet.Id == 0)
        {
            cmd.CommandText = """
                INSERT INTO snippets (name, alias, content, created_at, updated_at)
                VALUES ($name, $alias, $content, $now, $now)
                RETURNING id;
                """;
        }
        else
        {
            cmd.CommandText = """
                UPDATE snippets SET name = $name, alias = $alias, content = $content, updated_at = $now
                WHERE id = $id
                RETURNING id;
                """;
            cmd.Parameters.Add("$id", SqliteType.Integer).Value = snippet.Id;
        }
        cmd.Parameters.Add("$name", SqliteType.Text).Value = name;
        cmd.Parameters.Add("$alias", SqliteType.Text).Value = (object?)alias ?? DBNull.Value;
        cmd.Parameters.Add("$content", SqliteType.Text).Value = snippet.Content ?? "";
        cmd.Parameters.Add("$now", SqliteType.Integer).Value = now.ToUnixTimeMilliseconds();
        var id = cmd.ExecuteScalar();
        if (id is null)
            throw new InvalidOperationException($"snippet {snippet.Id} not found");

        snippet.Id = Convert.ToInt64(id);
        snippet.Name = name;
        snippet.Alias = alias;
        snippet.UpdatedAt = now;
        if (snippet.CreatedAt == default)
            snippet.CreatedAt = now;
        Interlocked.Increment(ref _dataVersion);
        return snippet.Id;
    }

    public Snippet? GetById(long id)
    {
        using var conn = database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM snippets WHERE id = $id;";
        cmd.Parameters.Add("$id", SqliteType.Integer).Value = id;
        var list = ReadAll(cmd);
        return list.Count > 0 ? list[0] : null;
    }

    public void Delete(long id)
    {
        using var conn = database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM snippets WHERE id = $id;";
        cmd.Parameters.Add("$id", SqliteType.Integer).Value = id;
        cmd.ExecuteNonQuery();
        Interlocked.Increment(ref _dataVersion);
    }

    /// <summary>Most recently edited first.</summary>
    public IReadOnlyList<Snippet> GetAll(int limit = 500)
    {
        using var conn = database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM snippets ORDER BY updated_at DESC, id DESC LIMIT $limit;";
        cmd.Parameters.Add("$limit", SqliteType.Integer).Value = limit;
        return ReadAll(cmd);
    }

    /// <summary>
    /// Alias hits first (prefix, case-insensitive: ";s" finds ";sig"), then full text
    /// matches on name, alias and content. An empty query lists everything.
    /// </summary>
    public IReadOnlyList<Snippet> Search(string? query, int limit = 100, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return GetAll(limit);

        var results = new List<Snippet>();
        var seen = new HashSet<long>();
        foreach (var s in FindByAliasPrefix(query, limit))
        {
            if (seen.Add(s.Id))
                results.Add(s);
        }
        cancellationToken.ThrowIfCancellationRequested();

        var fts = ClipboardItemRepository.SanitizeFtsQuery(query);
        if (fts.Length > 0 && results.Count < limit)
        {
            using var conn = database.OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT s.* FROM
                    (SELECT rowid, rank FROM snippets_fts WHERE snippets_fts MATCH $q ORDER BY rank LIMIT $limit) m
                JOIN snippets s ON s.id = m.rowid
                ORDER BY m.rank;
                """;
            cmd.Parameters.Add("$q", SqliteType.Text).Value = fts;
            cmd.Parameters.Add("$limit", SqliteType.Integer).Value = limit;
            using var reg = cancellationToken.Register(cmd.Cancel);
            try
            {
                foreach (var s in ReadAll(cmd))
                {
                    if (results.Count >= limit)
                        break;
                    if (seen.Add(s.Id))
                        results.Add(s);
                }
            }
            catch (SqliteException) when (!cancellationToken.IsCancellationRequested)
            {
                // a query FTS can't parse just means no full text hits
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return results;
    }

    /// <summary>Snippets whose alias starts with the text (trimmed, case-insensitive).</summary>
    public IReadOnlyList<Snippet> FindByAliasPrefix(string text, int limit = 10)
    {
        var prefix = text.Trim();
        if (prefix.Length == 0)
            return [];
        using var conn = database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM snippets
            WHERE alias IS NOT NULL AND alias LIKE $p ESCAPE '\'
            ORDER BY length(alias), updated_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.Add("$p", SqliteType.Text).Value = EscapeLike(prefix) + "%";
        cmd.Parameters.Add("$limit", SqliteType.Integer).Value = limit;
        return ReadAll(cmd);
    }

    public long Count()
    {
        using var conn = database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM snippets;";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>Trimmed, inner whitespace removed, null when empty.</summary>
    public static string? NormalizeAlias(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
            return null;
        var compact = string.Concat(alias.Where(c => !char.IsWhiteSpace(c)));
        return compact.Length == 0 ? null : Clip(compact, MaxAliasLength);
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static List<Snippet> ReadAll(SqliteCommand cmd)
    {
        var list = new List<Snippet>();
        using var r = cmd.ExecuteReader();
        var id = r.GetOrdinal("id");
        var name = r.GetOrdinal("name");
        var alias = r.GetOrdinal("alias");
        var content = r.GetOrdinal("content");
        var created = r.GetOrdinal("created_at");
        var updated = r.GetOrdinal("updated_at");
        while (r.Read())
        {
            list.Add(new Snippet
            {
                Id = r.GetInt64(id),
                Name = r.GetString(name),
                Alias = r.IsDBNull(alias) ? null : r.GetString(alias),
                Content = r.GetString(content),
                CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(created)),
                UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(updated)),
            });
        }
        return list;
    }
}
