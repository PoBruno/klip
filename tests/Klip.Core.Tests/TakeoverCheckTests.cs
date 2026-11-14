using Klip.Core.Hotkeys;

namespace Klip.Core.Tests;

public class TakeoverCheckTests
{
    private static HotkeyGesture G(string text)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var g));
        return g;
    }

    [Fact]
    public void Lost_AllFreed_Empty()
    {
        var lost = TakeoverCheck.Lost([G("Win+V"), G("PrintScreen"), G("Win+Shift+S")], true, true, true);
        Assert.Empty(lost);
    }

    [Fact]
    public void Lost_PrintScreenBackOnSnipping_Reported()
    {
        var lost = TakeoverCheck.Lost([G("Win+V"), G("PrintScreen")], true, true, false);
        Assert.Equal(["PrintScreen"], lost);
    }

    [Fact]
    public void Lost_WinVAndWinShiftS_ReportedOnce()
    {
        var lost = TakeoverCheck.Lost([G("Win+V"), G("Win+Shift+S"), G("Win+V")], false, false, true);
        Assert.Equal(["Win+V", "Win+Shift+S"], lost);
    }

    [Fact]
    public void Lost_GesturesThatDoNotNeedTakeover_Ignored()
    {
        var lost = TakeoverCheck.Lost(
            [G("Ctrl+Shift+V"), G("Ctrl+PrintScreen"), G("Win+Alt+V")], false, false, false);
        Assert.Empty(lost);
    }
}
