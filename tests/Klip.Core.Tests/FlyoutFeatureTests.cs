using System.Globalization;
using Klip.Core.Clipboard;
using Klip.Core.Snippets;
using Klip.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Klip.Core.Tests;

public class SnippetExpanderTests
{
    private static readonly DateTime Now = new(2026, 10, 27, 14, 5, 9);
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

    [Fact]
    public void Date_UsesTheRegionalShortDate()
    {
        Assert.Equal("Hoje: 27/10/2026", SnippetExpander.Expand("Hoje: {date}", Now, culture: PtBr));
        Assert.Equal("Today: 10/27/2026", SnippetExpander.Expand("Today: {date}", Now, culture: EnUs));
    }

    [Fact]
    public void Time_And_DateTime()
    {
        Assert.Equal("14:05", SnippetExpander.Expand("{time}", Now, culture: PtBr));
        Assert.Equal("27/10/2026 14:05", SnippetExpander.Expand("{datetime}", Now, culture: PtBr));
    }

    [Fact]
    public void Placeholders_AreCaseInsensitive()
    {
        Assert.Equal("27/10/2026", SnippetExpander.Expand("{DATE}", Now, culture: PtBr));
    }

    [Fact]
    public void Clipboard_IsReadOnce_AndOnlyWhenUsed()
    {
        var reads = 0;
        string? Read()
        {
            reads++;
            return "abc";
        }

        Assert.Equal("abc-abc", SnippetExpander.Expand("{clipboard}-{clipboard}", Now, Read, PtBr));
        Assert.Equal(1, reads);

        Assert.Equal("no placeholder", SnippetExpander.Expand("no placeholder", Now, Read, PtBr));
        Assert.Equal(1, reads);
    }

    [Fact]
    public void Clipboard_NullOrThrowing_ExpandsToEmpty()
    {
        Assert.Equal("[]", SnippetExpander.Expand("[{clipboard}]", Now, () => null, PtBr));
        Assert.Equal("[]", SnippetExpander.Expand("[{clipboard}]", Now, () => throw new InvalidOperationException(), PtBr));
    }

    [Fact]
    public void UnknownPlaceholders_AndCodeBraces_StayAsTyped()
    {
        const string code = "if (x) { return {foo}; }";
        Assert.Equal(code, SnippetExpander.Expand(code, Now, culture: PtBr));
        Assert.Equal("{cursor}", SnippetExpander.Expand("{cursor}", Now, culture: PtBr));
        Assert.Equal("open { only", SnippetExpander.Expand("open { only", Now, culture: PtBr));
    }

    [Fact]
    public void DoubledBraces_AreLiteral()
    {
        Assert.Equal("{date} = 27/10/2026", SnippetExpander.Expand("{{date}} = {date}", Now, culture: PtBr));
    }

    [Fact]
    public void Empty_IsEmpty()
    {
        Assert.Equal("", SnippetExpander.Expand(null, Now));
        Assert.Equal("", SnippetExpander.Expand("", Now));
        Assert.False(SnippetExpander.HasPlaceholders("plain"));
        Assert.True(SnippetExpander.UsesClipboard("x {Clipboard}"));
    }
}

public sealed class SnippetRepositoryTests : IDisposable
{
    private readonly DatabaseFixture _fx = new();
    private readonly SnippetRepository _repo;

    public SnippetRepositoryTests() => _repo = new SnippetRepository(_fx.Database);

    public void Dispose() => _fx.Dispose();

    private long Add(string name, string? alias, string content) =>
        _repo.Save(new Snippet { Name = name, Alias = alias, Content = content });

    [Fact]
    public void Save_Inserts_ThenUpdates()
    {
        var s = new Snippet { Name = "  Assinatura  ", Alias = " ;sig ", Content = "Abraços,\nBruno" };
        var id = _repo.Save(s);
        Assert.True(id > 0);
        Assert.Equal("Assinatura", s.Name);
        Assert.Equal(";sig", s.Alias);

        var loaded = _repo.GetById(id)!;
        Assert.Equal("Abraços,\nBruno", loaded.Content);

        loaded.Content = "Até mais";
        _repo.Save(loaded);
        Assert.Equal("Até mais", _repo.GetById(id)!.Content);
        Assert.Equal(1, _repo.Count());
    }

    [Fact]
    public void Update_OfAMissingSnippet_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => _repo.Save(new Snippet { Id = 999, Name = "x", Content = "y" }));
    }

    [Fact]
    public void Alias_IsNormalized()
    {
        Assert.Null(SnippetRepository.NormalizeAlias("   "));
        Assert.Equal(";addr", SnippetRepository.NormalizeAlias(" ; addr "));
    }

    [Fact]
    public void Search_AliasPrefixComesFirst_ThenFullText()
    {
        var addr = Add("Endereço", ";addr", "Rua das Flores, 100");
        var sig = Add("Assinatura", ";sig", "Atenciosamente");
        Add("Outro", null, "texto sobre assinatura digital");

        var bySigAlias = _repo.Search(";s");
        Assert.Equal(sig, bySigAlias[0].Id);

        var byWord = _repo.Search("assinatura");
        Assert.Equal(2, byWord.Count);

        var byContent = _repo.Search("flores");
        Assert.Equal(addr, Assert.Single(byContent).Id);

        // diacritics folded like the history search
        Assert.Equal(addr, Assert.Single(_repo.Search("endereco")).Id);
    }

    [Fact]
    public void Search_EmptyListsAll_NewestFirst()
    {
        Add("a", null, "1");
        var b = Add("b", null, "2");
        var all = _repo.Search("");
        Assert.Equal(2, all.Count);
        Assert.Equal(b, all[0].Id);
    }

    [Fact]
    public void AliasPrefix_EscapesLikeWildcards()
    {
        Add("pct", "50%off", "x");
        Add("other", "50abc", "y");
        Assert.Single(_repo.FindByAliasPrefix("50%"));
        Assert.Empty(_repo.FindByAliasPrefix("50_")); // "_" is literal, not "any char"
        Assert.Empty(_repo.FindByAliasPrefix("   "));
    }

    [Fact]
    public void Delete_RemovesFromTableAndSearch()
    {
        var id = Add("tmp", ";tmp", "temporario");
        _repo.Delete(id);
        Assert.Null(_repo.GetById(id));
        Assert.Empty(_repo.Search("temporario"));
        Assert.Empty(_repo.Search(";tmp"));
    }

    [Fact]
    public void Snippets_SurviveHistoryClearAll()
    {
        Add("keep", null, "kept");
        _fx.Repository.Upsert(DatabaseFixture.NewTextItem("history", "h1"));
        _fx.Repository.ClearAll();
        Assert.Equal(1, _repo.Count());
    }

    [Fact]
    public void Writes_BumpDataVersion()
    {
        var v = _repo.DataVersion;
        var id = Add("a", null, "b");
        Assert.True(_repo.DataVersion > v);
        v = _repo.DataVersion;
        _repo.Delete(id);
        Assert.True(_repo.DataVersion > v);
    }
}

public sealed class SchemaV5Tests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"klip-v5-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(f); } catch (IOException) { }
        }
    }

    [Fact]
    public void V4Database_GetsTheSnippetTables_AndKeepsItems()
    {
        // a v4 file: current schema, snippets dropped, version rolled back
        using (var db = new Database(_dbPath))
        {
            db.Initialize();
            new ClipboardItemRepository(db).Upsert(DatabaseFixture.NewTextItem("relatorio", "a"));
            using var conn = db.OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DROP TRIGGER snippets_ai; DROP TRIGGER snippets_ad; DROP TRIGGER snippets_au;
                DROP TABLE snippets_fts; DROP TABLE snippets;
                PRAGMA user_version = 4;
                """;
            cmd.ExecuteNonQuery();
        }

        using var upgraded = new Database(_dbPath);
        upgraded.Initialize();
        upgraded.Initialize(); // idempotent

        using (var conn = upgraded.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version;";
            Assert.Equal((long)Database.SchemaVersion, Convert.ToInt64(cmd.ExecuteScalar()));
        }

        var snippets = new SnippetRepository(upgraded);
        snippets.Save(new Snippet { Name = "n", Alias = ";n", Content = "conteudo" });
        Assert.Single(snippets.Search("conteudo"));
        Assert.Single(new ClipboardItemRepository(upgraded).QuerySummaries(new HistoryQuery { SearchText = "relatorio" }));
    }
}

public class PasteJoinTests
{
    private static ClipboardItem Item(ClipboardItemType type) => new() { Type = type, ContentHash = Guid.NewGuid().ToString() };

    [Fact]
    public void Join_PutsOneLineBreakBetweenParts()
    {
        Assert.Equal("a\r\nb\r\nc", PasteJoin.Join(["a", "b", "c"]));
        Assert.Equal("a\nb", PasteJoin.Join(["a\n", "b"]));
        Assert.Equal("a\r\nb", PasteJoin.Join(["a", null, "", "b"]));
        Assert.Equal("", PasteJoin.Join([]));
    }

    [Fact]
    public void CanJoinAsText_OnlyForTextAndHtml()
    {
        Assert.True(PasteJoin.CanJoinAsText([Item(ClipboardItemType.Text), Item(ClipboardItemType.Html)]));
        Assert.False(PasteJoin.CanJoinAsText([Item(ClipboardItemType.Text), Item(ClipboardItemType.Image)]));
        Assert.False(PasteJoin.CanJoinAsText([]));
    }

    [Fact]
    public void AddCollected_SkipsRepeats_AndDropsTheOldest()
    {
        var list = new List<int>();
        Assert.True(PasteJoin.AddCollected(list, 1, (a, b) => a == b, max: 3));
        Assert.False(PasteJoin.AddCollected(list, 1, (a, b) => a == b, max: 3));
        PasteJoin.AddCollected(list, 2, (a, b) => a == b, max: 3);
        PasteJoin.AddCollected(list, 3, (a, b) => a == b, max: 3);
        PasteJoin.AddCollected(list, 4, (a, b) => a == b, max: 3);
        Assert.Equal([2, 3, 4], list);
    }

    [Fact]
    public void PasteQueue_CustomCap_GoesPastFive()
    {
        var q = new PasteQueue<int>();
        q.Begin(20, max: 50);
        Assert.Equal(20, q.Target);
        q.Begin(9);
        Assert.Equal(5, q.Target);
    }
}

public class HistoryPauseTests
{
    private sealed class FakeTime : TimeProvider
    {
        private readonly List<FakeTimer> _timers = [];
        public DateTimeOffset Now { get; private set; } = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        public int LiveTimers => _timers.Count(t => !t.Disposed);

        public override DateTimeOffset GetUtcNow() => Now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var t = new FakeTimer(callback, state, Now + dueTime);
            _timers.Add(t);
            return t;
        }

        public void Advance(TimeSpan by)
        {
            Now += by;
            foreach (var t in _timers.ToList())
            {
                if (!t.Disposed && !t.Fired && t.Due <= Now)
                {
                    t.Fired = true;
                    t.Callback(t.State);
                }
            }
        }

        private sealed class FakeTimer(TimerCallback callback, object? state, DateTimeOffset due) : ITimer
        {
            public TimerCallback Callback { get; } = callback;
            public object? State { get; } = state;
            public DateTimeOffset Due { get; } = due;
            public bool Disposed { get; private set; }
            public bool Fired { get; set; }
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync()
            {
                Disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }

    [Fact]
    public void AtRest_ThereIsNoTimer()
    {
        var time = new FakeTime();
        using var p = new HistoryPause(time);
        Assert.False(p.HasTimer);
        p.Pause(null); // until resumed: nothing to wait for
        Assert.True(p.IsPaused);
        Assert.False(p.HasTimer);
        p.Resume();
        Assert.False(p.IsPaused);
        Assert.Equal(0, time.LiveTimers);
    }

    [Fact]
    public void TimedPause_EndsOnItsOwn_AndTheTimerGoesAway()
    {
        var time = new FakeTime();
        using var p = new HistoryPause(time);
        var changes = 0;
        p.Changed += () => changes++;

        p.Pause(TimeSpan.FromMinutes(15));
        Assert.True(p.HasTimer);
        Assert.Equal(time.Now.AddMinutes(15), p.PausedUntil);

        time.Advance(TimeSpan.FromMinutes(14));
        Assert.True(p.IsPaused);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(p.IsPaused);
        Assert.False(p.HasTimer);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void IgnoreNext_IsConsumedOnce()
    {
        var time = new FakeTime();
        using var p = new HistoryPause(time);
        p.IgnoreNext();
        Assert.True(p.IsIgnoringNext);
        Assert.True(p.TryConsumeIgnoreNext());
        Assert.False(p.TryConsumeIgnoreNext());
        Assert.False(p.HasTimer);
    }

    [Fact]
    public void IgnoreNext_Expires()
    {
        var time = new FakeTime();
        using var p = new HistoryPause(time);
        p.IgnoreNext();
        time.Advance(HistoryPause.IgnoreNextWindow);
        Assert.False(p.IsIgnoringNext);
        Assert.False(p.TryConsumeIgnoreNext());
        Assert.Equal(0, time.LiveTimers);
    }

    [Fact]
    public void BothDeadlines_ShareOneTimer_ArmedForTheEarliest()
    {
        var time = new FakeTime();
        using var p = new HistoryPause(time);
        p.Pause(TimeSpan.FromHours(1));
        p.IgnoreNext(); // 5 min, earlier
        Assert.Equal(1, time.LiveTimers);

        time.Advance(HistoryPause.IgnoreNextWindow);
        Assert.False(p.IsIgnoringNext);
        Assert.True(p.IsPaused);
        Assert.Equal(1, time.LiveTimers); // re-armed for the pause

        time.Advance(TimeSpan.FromHours(1));
        Assert.False(p.IsPaused);
        Assert.Equal(0, time.LiveTimers);
    }

    [Fact]
    public void ResumeEarly_DropsTheTimer()
    {
        var time = new FakeTime();
        using var p = new HistoryPause(time);
        p.Pause(TimeSpan.FromMinutes(15));
        p.Resume();
        Assert.Equal(0, time.LiveTimers);
        Assert.Null(p.PausedUntil);
    }
}

public class PreviewFormatTests
{
    [Fact]
    public void Clip_ShortTextIsUntouched()
    {
        Assert.Equal("abc", PreviewFormat.Clip("abc", 10, out var cut));
        Assert.False(cut);
        Assert.Equal("", PreviewFormat.Clip(null, 10, out cut));
        Assert.False(cut);
    }

    [Fact]
    public void Clip_LongTextIsCutAndFlagged()
    {
        var text = new string('x', 50);
        Assert.Equal(new string('x', 20), PreviewFormat.Clip(text, 20, out var cut));
        Assert.True(cut);
    }

    [Fact]
    public void Clip_NeverSplitsASurrogatePair()
    {
        // the emoji takes chars 4 and 5, a cut at 5 would leave a lone high surrogate
        var text = "abcd" + "\U0001F600" + "efgh";
        var clipped = PreviewFormat.Clip(text, 5, out var cut);
        Assert.True(cut);
        Assert.Equal("abcd", clipped);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(3L * 1024 * 1024, "3 MB")]
    public void HumanSize_UsesTheRightUnit(long bytes, string expected)
    {
        Assert.Equal(expected, PreviewFormat.HumanSize(bytes, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(0x30, 0)]
    [InlineData(0x31, 1)]
    [InlineData(0x39, 9)]
    [InlineData(0x61, 1)]
    [InlineData(0x69, 9)]
    [InlineData(0x41, 0)]
    public void ShortcutNumber_MapsDigitKeys(int vk, int expected)
    {
        Assert.Equal(expected, PreviewFormat.ShortcutNumber(vk));
    }
}
