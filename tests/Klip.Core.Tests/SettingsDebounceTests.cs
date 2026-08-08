using System.Text.Json;
using Klip.Core.Settings;

namespace Klip.Core.Tests;

/// <summary>
/// RF-P3.06: Update aplica em memoria e notifica na hora, mas a gravacao em
/// disco e coalescida numa janela de debounce. Flush/UpdateAndFlush/Dispose sao
/// as saidas sincronas.
/// </summary>
public sealed class SettingsDebounceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"klip-settings-debounce-{Guid.NewGuid():N}");
    private readonly string _path;

    public SettingsDebounceTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");
    }

    private SettingsService NewService() => new(_path);

    private static string? ReadHotkey(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))?.HotkeyHistory
            : null;

    /// <summary>Espera a gravacao do debounce sem prender o teste num sleep fixo.</summary>
    private static bool WaitForWrite(SettingsService service, int expected, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (service.WriteCount >= expected)
                return true;
            Thread.Sleep(15);
        }

        return service.WriteCount >= expected;
    }

    [Fact]
    public void Update_AppliesToCurrentImmediately()
    {
        using var service = NewService();

        service.Update(s => s.RetentionMaxItems = 4242);

        Assert.Equal(4242, service.Current.RetentionMaxItems);
    }

    [Fact]
    public void Update_RaisesChangedImmediately()
    {
        using var service = NewService();

        var raised = 0;
        AppSettings? payload = null;
        service.Changed += s => { raised++; payload = s; };

        service.Update(s => s.HotkeyHistory = "Win+V");

        Assert.Equal(1, raised);
        Assert.Same(service.Current, payload);
        Assert.Equal("Win+V", payload!.HotkeyHistory);
    }

    [Fact]
    public void Update_DoesNotTouchDiskImmediately()
    {
        using var service = NewService();

        service.Update(s => s.HotkeyHistory = "Win+V");

        Assert.Equal(0, service.WriteCount);
        Assert.False(File.Exists(_path));
        Assert.True(service.HasPendingWrite);
    }

    [Fact]
    public void Update_OverExistingFile_KeepsOldContentUntilFlush()
    {
        using var service = NewService();
        service.UpdateAndFlush(s => s.HotkeyHistory = "Ctrl+Shift+V");

        var before = File.GetLastWriteTimeUtc(_path);
        service.Update(s => s.HotkeyHistory = "Win+V");

        // o arquivo continua com o valor antigo e sem nova gravacao
        Assert.Equal("Ctrl+Shift+V", ReadHotkey(_path));
        Assert.Equal(before, File.GetLastWriteTimeUtc(_path));
        Assert.Equal(1, service.WriteCount);
    }

    [Fact]
    public void Flush_WritesPendingValue()
    {
        using var service = NewService();
        service.Update(s => s.HotkeyHistory = "Win+V");

        service.Flush();

        Assert.Equal(1, service.WriteCount);
        Assert.Equal("Win+V", ReadHotkey(_path));
        Assert.False(service.HasPendingWrite);
    }

    [Fact]
    public void Flush_WithoutPendingChanges_DoesNotWriteAgain()
    {
        using var service = NewService();
        service.Update(s => s.RetentionMaxItems = 7);
        service.Flush();

        service.Flush();
        service.Flush();

        Assert.Equal(1, service.WriteCount);
    }

    [Fact]
    public void HundredUpdates_ProduceASingleWrite()
    {
        using var service = NewService();

        for (var i = 1; i <= 100; i++)
        {
            var value = i;
            service.Update(s => s.RetentionMaxItems = value);
        }

        Assert.Equal(0, service.WriteCount); // nenhuma gravacao durante a rajada

        service.Flush();

        Assert.Equal(1, service.WriteCount);
        Assert.Equal(100, service.Current.RetentionMaxItems);

        var persisted = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path));
        Assert.Equal(100, persisted!.RetentionMaxItems);
    }

    [Fact]
    public void DebounceTimer_WritesOnceAfterQuietPeriod()
    {
        using var service = NewService();

        var changed = 0;
        service.Changed += _ => changed++;

        for (var i = 0; i < 50; i++)
        {
            var value = i;
            service.Update(s => s.RetentionMaxItems = value);
        }

        Assert.True(WaitForWrite(service, 1), "o timer de debounce nao gravou dentro do timeout");

        // deu tempo do debounce rodar: nao pode ter uma segunda gravacao
        Thread.Sleep(SettingsService.DebounceMilliseconds * 2);

        Assert.Equal(1, service.WriteCount);
        Assert.Equal(49, ReadRetention(_path));

        // Changed dispara por Update, nunca pela gravacao do debounce
        Assert.Equal(50, changed);
    }

    [Fact]
    public void UpdateAndFlush_PersistsImmediately()
    {
        using var service = NewService();

        service.UpdateAndFlush(s => s.RegistryBackupTaken = true);

        Assert.Equal(1, service.WriteCount);
        Assert.False(service.HasPendingWrite);

        var persisted = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path));
        Assert.True(persisted!.RegistryBackupTaken);
    }

    [Fact]
    public void Dispose_FlushesPending()
    {
        var service = NewService();
        service.Update(s => s.HotkeyHistory = "Win+V");
        Assert.Equal(0, service.WriteCount);

        service.Dispose();

        Assert.Equal(1, service.WriteCount);
        Assert.Equal("Win+V", ReadHotkey(_path));
    }

    [Fact]
    public void Dispose_Twice_IsSafeAndWritesOnce()
    {
        var service = NewService();
        service.Update(s => s.HotkeyHistory = "Win+V");

        service.Dispose();
        service.Dispose();

        Assert.Equal(1, service.WriteCount);
    }

    [Fact]
    public void NewInstance_SeesFlushedValues()
    {
        using (var service = NewService())
        {
            service.Update(s =>
            {
                s.HotkeyHistory = "Win+V";
                s.RetentionMaxItems = 42;
                s.ExcludedApps = ["keepass.exe"];
            });
            service.Flush();
        }

        using var reloaded = NewService();

        Assert.Equal("Win+V", reloaded.Current.HotkeyHistory);
        Assert.Equal(42, reloaded.Current.RetentionMaxItems);
        Assert.Equal(new[] { "keepass.exe" }, reloaded.Current.ExcludedApps);
        Assert.False(reloaded.HasPendingWrite);
    }

    [Fact]
    public void Save_WritesEvenWithoutPendingMutation()
    {
        using var service = NewService();

        var changed = 0;
        service.Changed += _ => changed++;

        service.Save();
        service.Save();

        Assert.Equal(2, service.WriteCount);
        Assert.Equal(2, changed);
    }

    [Fact]
    public void WrittenJson_IsCompact()
    {
        using var service = NewService();
        service.UpdateAndFlush(s => s.HotkeyHistory = "Win+V");

        // WriteIndented = false (RF-P3.06): sem quebras de linha nem recuo
        var json = File.ReadAllText(_path);
        Assert.DoesNotContain("\n", json);
        Assert.StartsWith("{\"", json);
    }

    [Fact]
    public void ConcurrentUpdatesAndFlushes_ConvergeToLastValue()
    {
        using var service = NewService();

        Parallel.For(0, 200, i =>
        {
            service.Update(s => s.RetentionMaxItems = i);
            if (i % 20 == 0)
                service.Flush();
        });

        service.Flush();

        Assert.False(service.HasPendingWrite);
        Assert.Equal(service.Current.RetentionMaxItems, ReadRetention(_path));
    }

    private static int? ReadRetention(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))?.RetentionMaxItems
            : null;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
