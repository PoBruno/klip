using Klip.Core.Storage;

namespace Klip.Core.Tests;

public class RepositoryTests : IDisposable
{
    private readonly DatabaseFixture _fx = new();

    [Fact]
    public void Initialize_CreatesValidSchema()
    {
        Assert.True(_fx.Database.QuickCheck());
    }

    [Fact]
    public void Upsert_InsertsAndReadsBack()
    {
        var id = _fx.Repository.Upsert(DatabaseFixture.NewTextItem("olá mundo", "hash-1"));
        var item = _fx.Repository.GetById(id);

        Assert.NotNull(item);
        Assert.Equal("olá mundo", item!.TextContent);
        Assert.Equal(ClipboardItemType.Text, item.Type);
        Assert.Equal("test.exe", item.SourceApp);
    }

    [Fact]
    public void Upsert_SameHash_Dedupes_AndBumpsRecency()
    {
        // same hash won't duplicate, just bumps it to the top
        var id1 = _fx.Repository.Upsert(DatabaseFixture.NewTextItem("abc", "same-hash", copiedAtMs: 1000));
        var id2 = _fx.Repository.Upsert(DatabaseFixture.NewTextItem("abc", "same-hash", copiedAtMs: 2000));

        Assert.Equal(id1, id2);
        Assert.Equal(1, _fx.Repository.Count());
        Assert.Equal(2000, _fx.Repository.GetById(id1)!.LastCopiedAt.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void GetPage_KeysetPagination_OrdersByRecency_PinnedFirst()
    {
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("velho", "h1", copiedAtMs: 1000));
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("novo", "h2", copiedAtMs: 3000));
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("fixado antigo", "h3", copiedAtMs: 500, pinned: true));

        var page = _fx.Repository.GetPage(limit: 10);

        Assert.Equal(3, page.Count);
        Assert.Equal("fixado antigo", page[0].TextContent); // pinned goes first
        Assert.Equal("novo", page[1].TextContent);
        Assert.Equal("velho", page[2].TextContent);
    }

    [Fact]
    public void Search_Fts_FindsByPrefix_AndDiacritics()
    {
        // fts with remove_diacritics, so accents don't matter on search
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("configuração do sistema", "h1"));
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("outro texto qualquer", "h2"));

        Assert.Single(_fx.Repository.Search("configuracao"));
        Assert.Single(_fx.Repository.Search("config"));
        Assert.Empty(_fx.Repository.Search("inexistente"));
    }

    [Fact]
    public void Search_QueryWithFtsOperators_IsSanitized()
    {
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("texto normal", "h1"));
        // must not blow up with an fts5 syntax error
        Assert.Empty(_fx.Repository.Search("\"quote AND (weird"));
    }

    [Fact]
    public void ClearAll_PreservesPinnedAndFavorites()
    {
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("comum", "h1"));
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("fixado", "h2", pinned: true));
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("favorito", "h3", favorite: true));

        var removed = _fx.Repository.ClearAll();

        Assert.Equal(1, removed);
        Assert.Equal(2, _fx.Repository.Count());
    }

    [Fact]
    public void ApplyRetention_MaxItems_RemovesOldest_NeverPinnedOrFavorite()
    {
        // retencao nunca deve remover item fixado ou favorito, mesmo o mais antigo
        for (var i = 0; i < 10; i++)
            _fx.Repository.Upsert(DatabaseFixture.NewTextItem($"item {i}", $"h{i}", copiedAtMs: 1000 + i));
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("fixado", "hp", copiedAtMs: 1, pinned: true));
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("favorito", "hf", copiedAtMs: 2, favorite: true));

        _fx.Repository.ApplyRetention(maxItems: 3, maxAgeDays: 0);

        var remaining = _fx.Repository.GetPage(limit: 100);
        Assert.Equal(5, remaining.Count); // 3 regular + pinned + favorite
        Assert.Contains(remaining, i => i.Pinned);
        Assert.Contains(remaining, i => i.Favorite);
        Assert.Equal("item 9", remaining.First(i => !i.Pinned && !i.Favorite).TextContent);
    }

    [Fact]
    public void ApplyRetention_MaxTotalBytes_DropsOldestUntilItFits()
    {
        // each item is 100 bytes (100-char text). cap at 250 keeps the 2 newest.
        var text = new string('x', 100);
        for (var i = 0; i < 5; i++)
            _fx.Repository.Upsert(DatabaseFixture.NewTextItem(text, $"hb{i}", copiedAtMs: 1000 + i));
        // a pinned old one must survive even if it blows the budget
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem(text, "hbp", copiedAtMs: 1, pinned: true));

        _fx.Repository.ApplyRetention(maxItems: 0, maxAgeDays: 0, maxTotalBytes: 250);

        var remaining = _fx.Repository.GetPage(limit: 100);
        // 2 newest regular (200 bytes) fit under 250, plus the pinned one
        Assert.Equal(3, remaining.Count);
        Assert.Contains(remaining, i => i.Pinned);
    }

    [Fact]
    public void SetPinned_And_Favorite_Roundtrip()
    {
        var id = _fx.Repository.Upsert(DatabaseFixture.NewTextItem("x", "h1"));
        _fx.Repository.SetPinned(id, true);
        _fx.Repository.SetFavorite(id, true);

        var item = _fx.Repository.GetById(id)!;
        Assert.True(item.Pinned);
        Assert.True(item.Favorite);
    }

    [Fact]
    public void Delete_RemovesItem()
    {
        var id = _fx.Repository.Upsert(DatabaseFixture.NewTextItem("x", "h1"));
        _fx.Repository.Delete(id);
        Assert.Null(_fx.Repository.GetById(id));
        Assert.Equal(0, _fx.Repository.Count());
    }

    [Fact]
    public void Upsert_AllColumnsFilled_RoundTripsFieldByField()
    {
        // RF-P2.08: a refatoracao de parametros do Upsert e dos ordinais do ReadAll poderia
        // trocar colunas de lugar sem quebrar nenhum teste antigo; este compara campo a campo
        // com valores distintos em cada coluna.
        var original = new ClipboardItem
        {
            Type = ClipboardItemType.Image,
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_123),
            LastCopiedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_999_456),
            SourceApp = "app-de-origem.exe",
            SourceTitle = "Titulo da janela de origem",
            Origin = ClipboardItemOrigin.Editor,
            Pinned = true,
            Favorite = true,
            ContentHash = "hash-completo",
            ByteSize = 987_654,
            TextContent = "conteudo de texto",
            HtmlContent = "<b>conteudo html</b>",
            RtfContent = @"{\rtf1 conteudo rtf}",
            FilePath = "media/2026/arquivo.png",
            ThumbPath = "media/2026/thumb.png",
            FilesJson = """["C:\\um\\arquivo.txt"]""",
            OcrText = "texto reconhecido por ocr",
            Width = 1920,
            Height = 1080,
        };

        var id = _fx.Repository.Upsert(original);
        var loaded = _fx.Repository.GetById(id)!;

        Assert.Equal(id, loaded.Id);
        Assert.Equal(original.Type, loaded.Type);
        Assert.Equal(original.CreatedAt.ToUnixTimeMilliseconds(), loaded.CreatedAt.ToUnixTimeMilliseconds());
        Assert.Equal(original.LastCopiedAt.ToUnixTimeMilliseconds(), loaded.LastCopiedAt.ToUnixTimeMilliseconds());
        Assert.Equal(original.SourceApp, loaded.SourceApp);
        Assert.Equal(original.SourceTitle, loaded.SourceTitle);
        Assert.Equal(original.Origin, loaded.Origin);
        Assert.Equal(original.Pinned, loaded.Pinned);
        Assert.Equal(original.Favorite, loaded.Favorite);
        Assert.Equal(original.ContentHash, loaded.ContentHash);
        Assert.Equal(original.ByteSize, loaded.ByteSize);
        Assert.Equal(original.TextContent, loaded.TextContent);
        Assert.Equal(original.HtmlContent, loaded.HtmlContent);
        Assert.Equal(original.RtfContent, loaded.RtfContent);
        Assert.Equal(original.FilePath, loaded.FilePath);
        Assert.Equal(original.ThumbPath, loaded.ThumbPath);
        Assert.Equal(original.FilesJson, loaded.FilesJson);
        Assert.Equal(original.OcrText, loaded.OcrText);
        Assert.Equal(original.Width, loaded.Width);
        Assert.Equal(original.Height, loaded.Height);
    }

    [Fact]
    public void Upsert_NullableColumns_StayNull()
    {
        var item = DatabaseFixture.NewTextItem("so texto", "h-nulls");
        item.SourceApp = null;
        item.SourceTitle = null;

        var loaded = _fx.Repository.GetById(_fx.Repository.Upsert(item))!;

        Assert.Null(loaded.SourceApp);
        Assert.Null(loaded.SourceTitle);
        Assert.Null(loaded.HtmlContent);
        Assert.Null(loaded.RtfContent);
        Assert.Null(loaded.FilePath);
        Assert.Null(loaded.ThumbPath);
        Assert.Null(loaded.FilesJson);
        Assert.Null(loaded.OcrText);
        Assert.Null(loaded.Width);
        Assert.Null(loaded.Height);
    }

    [Fact]
    public void Origin_RoundTrips_ForEveryEnumValue()
    {
        // RF-P2.08: o Enum.Parse por linha virou switch sobre string; todos os valores
        // do enum precisam continuar indo e voltando.
        foreach (var origin in Enum.GetValues<ClipboardItemOrigin>())
        {
            var item = DatabaseFixture.NewTextItem($"item {origin}", $"hash-{origin}");
            item.Origin = origin;
            var loaded = _fx.Repository.GetById(_fx.Repository.Upsert(item))!;
            Assert.Equal(origin, loaded.Origin);
        }
    }

    [Fact]
    public void Type_RoundTrips_ForEveryEnumValue()
    {
        foreach (var type in Enum.GetValues<ClipboardItemType>())
        {
            var item = DatabaseFixture.NewTextItem($"item {type}", $"hash-type-{type}");
            item.Type = type;
            var loaded = _fx.Repository.GetById(_fx.Repository.Upsert(item))!;
            Assert.Equal(type, loaded.Type);
        }
    }

    [Fact]
    public void Search_ShortTerms_AreExact_LongTermsArePrefix()
    {
        // RF-P2.10: o curinga de prefixo so vale a partir de 3 caracteres.
        // "c" e "co" viram busca exata e nao casam com o token "configuracao";
        // "con" ganha o "*" e casa.
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("configuracao do sistema", "h1"));

        Assert.Empty(_fx.Repository.Search("c"));
        Assert.Empty(_fx.Repository.Search("co"));
        Assert.Single(_fx.Repository.Search("con"));
        Assert.Single(_fx.Repository.Search("configuracao"));
    }

    [Fact]
    public void Search_ShortTerm_StillMatchesWholeToken()
    {
        // termo curto nao deixa de funcionar: se o token existe inteiro, a busca exata acha
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("um do tres", "h1"));

        Assert.Single(_fx.Repository.Search("do"));
        Assert.Single(_fx.Repository.Search("um"));
    }

    [Fact]
    public void Query_TextTypeFilter_KeepsTextAndHtml_AndDropsTheRest()
    {
        // RF-P2.08: a aba Texto passou a usar "+i.type IN ('text','html')" para nao cair em
        // temp B-tree. O "+" e um no-op de valor, entao a semantica do filtro tem que ser
        // exatamente a mesma de antes - inclusive combinado com busca FTS.
        var text = DatabaseFixture.NewTextItem("relatorio simples", "h-text");
        var html = DatabaseFixture.NewTextItem("relatorio rico", "h-html");
        html.Type = ClipboardItemType.Html;
        var image = DatabaseFixture.NewTextItem("relatorio print", "h-img");
        image.Type = ClipboardItemType.Image;
        var files = DatabaseFixture.NewTextItem("relatorio anexos", "h-files");
        files.Type = ClipboardItemType.Files;
        foreach (var item in new[] { text, html, image, files })
            _fx.Repository.Upsert(item);

        var listed = _fx.Repository.Query(new HistoryQuery { Type = ClipboardItemType.Text });
        Assert.Equal(2, listed.Count);
        Assert.All(listed, i => Assert.True(i.Type is ClipboardItemType.Text or ClipboardItemType.Html));

        var searched = _fx.Repository.Query(new HistoryQuery
        {
            Type = ClipboardItemType.Text,
            SearchText = "relatorio",
        });
        Assert.Equal(2, searched.Count);
        Assert.All(searched, i => Assert.True(i.Type is ClipboardItemType.Text or ClipboardItemType.Html));
    }

    [Fact]
    public void OpenConnection_AppliesBusyTimeout()
    {
        // RF-P2.07: sem busy_timeout qualquer disputa de escrita estoura na hora em vez de esperar
        using var conn = _fx.Database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout;";
        Assert.Equal(5000L, Convert.ToInt64(cmd.ExecuteScalar()));
    }

    [Fact]
    public void OpenConnection_AppliesPerConnectionPragmas()
    {
        using var conn = _fx.Database.OpenConnection();

        Assert.Equal(1L, Pragma(conn, "synchronous"));      // NORMAL
        Assert.Equal(-16000L, Pragma(conn, "cache_size"));  // 16 MiB em KiB negativos
        Assert.Equal(2L, Pragma(conn, "temp_store"));       // MEMORY
        Assert.Equal(67108864L, Pragma(conn, "mmap_size")); // 64 MiB

        static long Pragma(Microsoft.Data.Sqlite.SqliteConnection conn, string name)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA {name};";
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    [Fact]
    public void Schema_HasNoForeignKeys_SoTheForeignKeysPragmaWasPointless()
    {
        // RF-P2.07: justificativa de ter tirado "PRAGMA foreign_keys=ON" do OpenConnection.
        // Nenhuma tabela declara FOREIGN KEY, entao o enforcement nunca teve o que checar.
        using var conn = _fx.Database.OpenConnection();

        using (var tables = conn.CreateCommand())
        {
            tables.CommandText = """
                SELECT COUNT(*) FROM sqlite_master m
                WHERE m.type = 'table'
                  AND EXISTS (SELECT 1 FROM pragma_foreign_key_list(m.name));
                """;
            Assert.Equal(0L, (long)tables.ExecuteScalar()!);
        }

        using (var check = conn.CreateCommand())
        {
            check.CommandText = "PRAGMA foreign_key_check;";
            using var reader = check.ExecuteReader();
            Assert.False(reader.Read());
        }
    }

    [Fact]
    public void Initialize_LeavesDatabaseInWalMode()
    {
        // RF-P2.07: journal_mode e persistente no arquivo, gravado uma vez no Initialize()
        using var conn = _fx.Database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", (string)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void RunMaintenance_DoesNotThrow_AndKeepsDatabaseValid()
    {
        // RF-P2.07: manutencao e oportunista e nunca pode derrubar o app
        for (var i = 0; i < 20; i++)
            _fx.Repository.Upsert(DatabaseFixture.NewTextItem($"item {i}", $"h{i}"));
        _fx.Repository.ApplyRetention(maxItems: 5, maxAgeDays: 0);

        _fx.Database.RunMaintenance();
        _fx.Database.RunMaintenance(); // idempotente

        Assert.True(_fx.Database.QuickCheck());
        Assert.Equal(5, _fx.Repository.Count());
    }

    [Fact]
    public void OptimizeFullTextIndex_KeepsSearchWorking()
    {
        // RF-P2.10
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("relatorio anual", "h1"));
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("relatorio mensal", "h2"));

        _fx.Repository.OptimizeFullTextIndex();

        Assert.Equal(2, _fx.Repository.Search("relatorio").Count);
        Assert.True(_fx.Database.QuickCheck());
    }

    public void Dispose() => _fx.Dispose();
}
