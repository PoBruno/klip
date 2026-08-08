using Klip.Core.Hotkeys;
using Klip.Core.Settings;

namespace Klip.Core.Tests;

public class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Shift+V", true, true, false, false, "V")]
    [InlineData("Win+V", false, false, false, true, "V")]
    [InlineData("Win+Shift+S", false, true, false, true, "S")]
    [InlineData("ctrl+alt+F12", true, false, true, false, "F12")]
    [InlineData("PrintScreen", false, false, false, false, "PRINTSCREEN")]
    public void TryParse_ValidGestures(string text, bool ctrl, bool shift, bool alt, bool win, string key)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var g));
        Assert.Equal(new HotkeyGesture(ctrl, shift, alt, win, key), g);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Ctrl+Shift")]     // sem tecla principal
    [InlineData("Ctrl+A+B")]       // duas teclas principais
    public void TryParse_InvalidGestures(string? text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out _));
    }

    [Fact]
    public void ToString_Roundtrip()
    {
        Assert.True(HotkeyGesture.TryParse("ctrl+shift+v", out var g));
        Assert.Equal("Ctrl+Shift+V", g.ToString());
    }
}

public class SettingsServiceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"klip-settings-{Guid.NewGuid():N}.json");

    [Fact]
    public void SaveAndLoad_Roundtrip()
    {
        using var service = new SettingsService(_path);
        service.Update(s =>
        {
            s.HotkeyHistory = "Win+V";
            s.RetentionMaxItems = 42;
        });
        service.Flush(); // RF-P3.06: Update so agenda; o disco espera o debounce

        using var reloaded = new SettingsService(_path);
        Assert.Equal("Win+V", reloaded.Current.HotkeyHistory);
        Assert.Equal(42, reloaded.Current.RetentionMaxItems);
    }

    [Fact]
    public void Load_CorruptFile_FallsBackToDefaults()
    {
        File.WriteAllText(_path, "{ isso não é json válido ");
        using var service = new SettingsService(_path);
        Assert.Equal("Ctrl+Shift+V", service.Current.HotkeyHistory);
        Assert.True(File.Exists(_path + ".corrupt"));
    }

    [Fact]
    public void Load_MissingFile_UsesDefaultsWithoutCreatingIt()
    {
        using var service = new SettingsService(_path);

        Assert.Equal("Ctrl+Shift+V", service.Current.HotkeyHistory);
        Assert.Equal("Ctrl+Shift+S", service.Current.HotkeyCapture);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void UpdateAndFlush_PersistsWithoutWaitingForDebounce()
    {
        // RF-P3.06: caminho usado pelo takeover de registro, onde o backup
        // precisa estar em disco antes de mexer nas chaves
        using var service = new SettingsService(_path);
        service.UpdateAndFlush(s =>
        {
            s.RegistryBackupTaken = true;
            s.RegistryBackupDisabledHotkeys = "Win+V";
        });

        using var reloaded = new SettingsService(_path);
        Assert.True(reloaded.Current.RegistryBackupTaken);
        Assert.Equal("Win+V", reloaded.Current.RegistryBackupDisabledHotkeys);
    }

    [Fact]
    public void Dispose_PersistsPendingUpdate()
    {
        var service = new SettingsService(_path);
        service.Update(s => s.RetentionMaxAgeDays = 30);
        service.Dispose();

        using var reloaded = new SettingsService(_path);
        Assert.Equal(30, reloaded.Current.RetentionMaxAgeDays);
    }

    public void Dispose()
    {
        File.Delete(_path);
        if (File.Exists(_path + ".corrupt"))
            File.Delete(_path + ".corrupt");
        if (File.Exists(_path + ".tmp"))
            File.Delete(_path + ".tmp");
    }
}
