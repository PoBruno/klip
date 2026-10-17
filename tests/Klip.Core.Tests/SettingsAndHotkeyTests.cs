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

public class EditorPreferencesTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"klip-editor-prefs-{Guid.NewGuid():N}.json");

    [Fact]
    public void SaveAndLoad_EditorZoom()
    {
        using var service = new SettingsService(_path);
        service.Update(s => s.EditorZoom = 1.5);
        service.Flush();

        using var reloaded = new SettingsService(_path);
        Assert.Equal(1.5, reloaded.Current.EditorZoom);
    }

    [Fact]
    public void SaveAndLoad_EditorActiveTool()
    {
        using var service = new SettingsService(_path);
        service.Update(s => s.EditorActiveTool = 3); // Eraser
        service.Flush();

        using var reloaded = new SettingsService(_path);
        Assert.Equal(3, reloaded.Current.EditorActiveTool);
    }

    [Fact]
    public void SaveAndLoad_EditorActiveColor()
    {
        using var service = new SettingsService(_path);
        service.Update(s => s.EditorActiveColor = "#0078D4"); // Blue
        service.Flush();

        using var reloaded = new SettingsService(_path);
        Assert.Equal("#0078D4", reloaded.Current.EditorActiveColor);
    }

    [Fact]
    public void SaveAndLoad_EditorThickness()
    {
        using var service = new SettingsService(_path);
        service.Update(s => s.EditorThickness = 5.5);
        service.Flush();

        using var reloaded = new SettingsService(_path);
        Assert.Equal(5.5, reloaded.Current.EditorThickness);
    }

    [Fact]
    public void SaveAndLoad_AllEditorPreferences()
    {
        using var service = new SettingsService(_path);
        service.Update(s =>
        {
            s.EditorZoom = 2.0;
            s.EditorActiveTool = 7; // Arrow
            s.EditorActiveColor = "#B146C2"; // Purple
            s.EditorThickness = 7.0;
        });
        service.Flush();

        using var reloaded = new SettingsService(_path);
        Assert.Equal(2.0, reloaded.Current.EditorZoom);
        Assert.Equal(7, reloaded.Current.EditorActiveTool);
        Assert.Equal("#B146C2", reloaded.Current.EditorActiveColor);
        Assert.Equal(7.0, reloaded.Current.EditorThickness);
    }

    [Fact]
    public void DefaultValues_EditorPreferences()
    {
        using var service = new SettingsService(_path);
        Assert.Equal(1.0, service.Current.EditorZoom);
        Assert.Equal(1, service.Current.EditorActiveTool); // Pen
        Assert.Equal("#FF4040", service.Current.EditorActiveColor); // Red
        Assert.Equal(3.0, service.Current.EditorThickness);
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
