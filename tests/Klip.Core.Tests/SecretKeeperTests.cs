using Klip.Core.Clipboard;
using Klip.Core.Common;
using Klip.Core.Settings;
using Klip.Core.Storage;

namespace Klip.Core.Tests;

public class SecretScheduleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TakeDue_ReturnsOnlyExpired()
    {
        var s = new SecretSchedule();
        s.Set(1, T0.AddSeconds(30));
        s.Set(2, T0.AddSeconds(60));

        Assert.Empty(s.TakeDue(T0.AddSeconds(10)));
        Assert.Equal([1L], s.TakeDue(T0.AddSeconds(30)));
        Assert.Equal(T0.AddSeconds(60), s.NextDeadline());
        Assert.True(s.Contains(2));
        Assert.False(s.Contains(1));
    }

    [Fact]
    public void Set_SameIdAgain_PushesDeadline()
    {
        var s = new SecretSchedule();
        s.Set(1, T0.AddSeconds(30));
        s.Set(1, T0.AddSeconds(90));

        Assert.Empty(s.TakeDue(T0.AddSeconds(60)));
        Assert.Equal(1, s.Count);
    }

    [Fact]
    public void NextDeadline_EmptyIsNull()
    {
        var s = new SecretSchedule();
        Assert.Null(s.NextDeadline());
        Assert.Empty(s.TakeAll());
    }
}

[Collection("AppPaths")]
public class SecretKeeperIngestTests : IDisposable
{
    private const string Secret = "ghp_1234567890abcdefABCDEF1234567890ab";

    private readonly DatabaseFixture _fx = new();
    private readonly string _dataRoot;
    private readonly SettingsService _settings;
    private readonly string _pendingFile;
    private readonly ClipboardIngestService _ingest;

    public SecretKeeperIngestTests()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), $"klip-secret-{Guid.NewGuid():N}");
        AppPaths.Root = _dataRoot;
        AppPaths.EnsureCreated();
        _settings = new SettingsService(Path.Combine(_dataRoot, "settings.json"));
        _pendingFile = Path.Combine(_dataRoot, "secrets-pending.txt");
        _ingest = new ClipboardIngestService(_fx.Repository, new MediaStore(), _settings,
            secretPendingFile: _pendingFile);
    }

    [Fact]
    public void KeepZero_SkipsSecretLikeBefore()
    {
        _settings.UpdateAndFlush(s => { s.SkipSecrets = true; s.SecretKeepSeconds = 0; });
        Assert.Null(_ingest.Ingest(new ClipboardSnapshot { Text = Secret }));
        Assert.Equal(0, _fx.Repository.Count());
    }

    [Fact]
    public void KeepSeconds_KeepsItemMarkedAndPurgeWipesIt()
    {
        _settings.UpdateAndFlush(s => { s.SkipSecrets = true; s.SecretKeepSeconds = 60; });
        var item = _ingest.Ingest(new ClipboardSnapshot { Text = Secret });

        Assert.NotNull(item);
        Assert.True(_ingest.IsSecretItem(item!.Id));
        Assert.True(File.Exists(_pendingFile));
        var stored = _fx.Repository.GetById(item.Id)!;
        Assert.Equal(Secret, stored.TextContent); // pasting still works
        Assert.StartsWith("ghp_", stored.Preview);
        Assert.DoesNotContain("1234567890", stored.Preview);

        Assert.Equal(1, _ingest.Secrets.PurgeAll());
        Assert.Equal(0, _fx.Repository.Count());
        Assert.False(_ingest.IsSecretItem(item.Id));
        Assert.False(File.Exists(_pendingFile));
    }

    [Fact]
    public void Purge_KeepsItemPinnedMeanwhile()
    {
        _settings.UpdateAndFlush(s => { s.SkipSecrets = true; s.SecretKeepSeconds = 30; });
        var item = _ingest.Ingest(new ClipboardSnapshot { Text = Secret })!;
        _fx.Repository.SetPinned(item.Id, true);

        _ingest.Secrets.PurgeAll();
        Assert.Equal(1, _fx.Repository.Count());
    }

    [Fact]
    public void Leftovers_FromPreviousRun_AreWiped()
    {
        _settings.UpdateAndFlush(s => { s.SkipSecrets = true; s.SecretKeepSeconds = 120; });
        var item = _ingest.Ingest(new ClipboardSnapshot { Text = Secret })!;

        // simulates a crash: a new service on the same files, no purge on exit
        var next = new ClipboardIngestService(_fx.Repository, new MediaStore(), _settings,
            secretPendingFile: _pendingFile);
        Assert.Equal(1, next.PurgeSecretLeftovers());
        Assert.Null(_fx.Repository.GetById(item.Id));
        _ingest.Secrets.Dispose();
    }

    [Fact]
    public void NormalText_IsNotSecret()
    {
        _settings.UpdateAndFlush(s => { s.SkipSecrets = true; s.SecretKeepSeconds = 30; });
        var item = _ingest.Ingest(new ClipboardSnapshot { Text = "Reunião amanhã às 14h" })!;
        Assert.False(_ingest.IsSecretItem(item.Id));
    }

    public void Dispose()
    {
        _ingest.Secrets.Dispose();
        _fx.Dispose();
        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
