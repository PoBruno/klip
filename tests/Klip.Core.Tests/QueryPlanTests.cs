using Klip.Core.Storage;

namespace Klip.Core.Tests;

/// <summary>
/// RF-P2.08: guarda-costas dos indices. Cada caso roda EXPLAIN QUERY PLAN sobre o SQL exato
/// que ClipboardItemRepository.Query() monta (via ExplainQueryPlan, que reaproveita o mesmo
/// construtor de SQL - assim o teste nao pode divergir do repositorio) e exige que o plano NAO
/// contenha "USE TEMP B-TREE FOR ORDER BY".
///
/// Esse texto no plano significa que o SQLite desistiu de usar indice para o ORDER BY e vai
/// materializar e ordenar TODAS as linhas que passam no filtro antes de aplicar o LIMIT - o
/// custo cresce com o tamanho do historico, nao com o tamanho da pagina.
/// </summary>
public class QueryPlanTests : IDisposable
{
    private const string TempBTree = "USE TEMP B-TREE FOR ORDER BY";

    private readonly DatabaseFixture _fx = new();

    public QueryPlanTests()
    {
        // volume e variedade suficientes para o planner ter o que escolher
        for (var i = 0; i < 200; i++)
        {
            var item = DatabaseFixture.NewTextItem($"item {i}", $"h{i:D4}", copiedAtMs: 1_000_000 + i,
                pinned: i % 40 == 0, favorite: i % 25 == 0);
            item.Type = (i % 10) switch
            {
                0 => ClipboardItemType.Image,
                3 => ClipboardItemType.Html,
                7 => ClipboardItemType.Files,
                _ => ClipboardItemType.Text,
            };
            _fx.Repository.Upsert(item);
        }
    }

    private string Plan(HistoryQuery query) =>
        string.Join(Environment.NewLine, _fx.Repository.ExplainQueryPlan(query));

    private void AssertNoSort(string label, HistoryQuery query)
    {
        var plan = Plan(query);
        Assert.False(plan.Contains(TempBTree, StringComparison.Ordinal),
            $"'{label}' caiu em ordenacao por temp B-tree. Plano:{Environment.NewLine}{plan}");
        // e tambem nao pode virar varredura da tabela: o ORDER BY precisa vir de um indice
        Assert.Contains("INDEX", plan, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultListing_UsesIndexOrder()
    {
        AssertNoSort("listagem padrao", new HistoryQuery());
    }

    [Theory]
    [InlineData(ClipboardItemType.Text)]
    [InlineData(ClipboardItemType.Html)]
    [InlineData(ClipboardItemType.Image)]
    [InlineData(ClipboardItemType.Files)]
    public void TypeFilter_UsesIndexOrder(ClipboardItemType type)
    {
        // Text tambem cobre o caso especial "text + html" da aba Texto
        AssertNoSort($"filtro por tipo {type}", new HistoryQuery { Type = type });
    }

    [Fact]
    public void OnlyFavorites_UsesIndexOrder()
    {
        AssertNoSort("apenas favoritos", new HistoryQuery { OnlyFavorites = true });
    }

    [Fact]
    public void OnlyFavoritesPlusType_UsesIndexOrder()
    {
        AssertNoSort("favoritos + tipo",
            new HistoryQuery { OnlyFavorites = true, Type = ClipboardItemType.Image });
        AssertNoSort("favoritos + aba Texto",
            new HistoryQuery { OnlyFavorites = true, Type = ClipboardItemType.Text });
    }

    [Fact]
    public void DateFilter_UsesIndexOrder()
    {
        AssertNoSort("data inicial", new HistoryQuery { DateFromMs = 1_000_050 });
        AssertNoSort("data final", new HistoryQuery { DateToMs = 1_000_150 });
        AssertNoSort("intervalo de datas",
            new HistoryQuery { DateFromMs = 1_000_050, DateToMs = 1_000_150 });
        AssertNoSort("intervalo de datas + tipo",
            new HistoryQuery { DateFromMs = 1_000_050, DateToMs = 1_000_150, Type = ClipboardItemType.Text });
    }

    [Fact]
    public void KeysetPaging_UsesIndexOrder()
    {
        AssertNoSort("paginacao keyset", new HistoryQuery { BeforeLastCopiedAtMs = 1_000_100 });
        AssertNoSort("paginacao keyset + tipo",
            new HistoryQuery { BeforeLastCopiedAtMs = 1_000_100, Type = ClipboardItemType.Text });
        AssertNoSort("paginacao keyset + favoritos",
            new HistoryQuery { BeforeLastCopiedAtMs = 1_000_100, OnlyFavorites = true });
    }

    [Fact]
    public void FullTextSearch_DrivesFromTheFtsIndex()
    {
        // A busca e o unico caso em que a temp B-tree e inevitavel: o loop externo e o
        // items_fts (MATCH), que entrega os matches na ordem interna do indice FTS, entao
        // qualquer ORDER BY nosso exige ordenacao. O que se cobra aqui e que o FTS seja o
        // driver e que o items entre por rowid, sem varrer a tabela.
        var plan = Plan(new HistoryQuery { SearchText = "item" });

        Assert.Contains("VIRTUAL TABLE INDEX", plan, StringComparison.Ordinal);
        Assert.Contains("SEARCH i USING INTEGER PRIMARY KEY", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("SCAN i", plan, StringComparison.Ordinal);
    }

    [Fact]
    public void FullTextSearch_RecencyOrder_SkipsBm25()
    {
        // RF-P2.10: com OrderBySearchRecency o bm25() sai do ORDER BY, entao o SQLite nao
        // precisa pontuar cada match (leitura das listas de posicao do indice FTS).
        var byRelevance = Plan(new HistoryQuery { SearchText = "item" });
        var byRecency = Plan(new HistoryQuery { SearchText = "item", OrderBySearchRecency = true });

        Assert.Contains("VIRTUAL TABLE INDEX", byRelevance, StringComparison.Ordinal);
        Assert.Contains("VIRTUAL TABLE INDEX", byRecency, StringComparison.Ordinal);

        // as duas variantes devolvem os mesmos itens, so muda a ordem
        var a = _fx.Repository.Search("item", 500).Select(i => i.Id).OrderBy(x => x);
        var b = _fx.Repository.Search("item", 500, orderByRecency: true).Select(i => i.Id).OrderBy(x => x);
        Assert.Equal(a, b);
    }

    [Fact]
    public void RecencySearch_IsOrderedByLastCopiedAtDescending()
    {
        var results = _fx.Repository.Search("item", 20, orderByRecency: true);

        Assert.NotEmpty(results);
        var stamps = results.Select(i => i.LastCopiedAt.ToUnixTimeMilliseconds()).ToList();
        Assert.Equal(stamps.OrderByDescending(x => x), stamps);
    }

    [Fact]
    public void ListingPlans_StaySortFree_AfterAnalyze()
    {
        // RunMaintenance roda "PRAGMA optimize", que faz ANALYZE incremental e grava
        // sqlite_stat1. Com estatisticas o planner muda de ideia sobre varios caminhos, entao
        // o conjunto de indices precisa aguentar os dois cenarios: base nova (sem stats) e
        // base rodada (com stats).
        _fx.Database.RunMaintenance();

        AssertNoSort("listagem padrao", new HistoryQuery());
        AssertNoSort("aba Texto", new HistoryQuery { Type = ClipboardItemType.Text });
        AssertNoSort("tipo imagem", new HistoryQuery { Type = ClipboardItemType.Image });
        AssertNoSort("apenas favoritos", new HistoryQuery { OnlyFavorites = true });
        AssertNoSort("favoritos + aba Texto",
            new HistoryQuery { OnlyFavorites = true, Type = ClipboardItemType.Text });
        AssertNoSort("intervalo de datas",
            new HistoryQuery { DateFromMs = 1_000_050, DateToMs = 1_000_150 });
        AssertNoSort("paginacao keyset", new HistoryQuery { BeforeLastCopiedAtMs = 1_000_100 });
    }

    public void Dispose() => _fx.Dispose();
}
