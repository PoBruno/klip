namespace Klip.Core.Hotkeys;

/// <summary>
/// Which configured hotkeys only work because a registry change took them away from
/// Windows, and which of those changes is gone now. A Windows update can put
/// PrintScreen back on the snipping tool or drop a letter from DisabledHotkeys; our
/// RegisterHotKey keeps returning success, but the key opens the system tool instead.
/// </summary>
public static class TakeoverCheck
{
    public static IReadOnlyList<string> Lost(
        IEnumerable<HotkeyGesture> configured,
        bool winVFreed,
        bool winShiftSFreed,
        bool printScreenFreed)
    {
        var lost = new List<string>();
        foreach (var g in configured)
        {
            string? name = null;
            if (IsWinV(g) && !winVFreed)
                name = "Win+V";
            else if (IsWinShiftS(g) && !winShiftSFreed)
                name = "Win+Shift+S";
            else if (IsPlainPrintScreen(g) && !printScreenFreed)
                name = "PrintScreen";

            if (name is not null && !lost.Contains(name))
                lost.Add(name);
        }
        return lost;
    }

    private static bool IsWinV(HotkeyGesture g) =>
        g is { Win: true, Ctrl: false, Shift: false, Alt: false, Key: "V" };

    private static bool IsWinShiftS(HotkeyGesture g) =>
        g is { Win: true, Shift: true, Ctrl: false, Alt: false, Key: "S" };

    // only the bare key is bound to the snipping tool; Ctrl+PrintScreen and friends are ours anyway
    private static bool IsPlainPrintScreen(HotkeyGesture g) =>
        g is { Win: false, Ctrl: false, Shift: false, Alt: false, Key: "PRINTSCREEN" or "PRTSC" or "PRTSCN" };
}
