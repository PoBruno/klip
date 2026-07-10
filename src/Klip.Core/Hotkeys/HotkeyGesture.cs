namespace Klip.Core.Hotkeys;

/// <summary>
/// A global hotkey gesture like "Ctrl+Shift+V" (pure parsing, testable).
/// Mapping to Win32 VK/MOD happens over in the App/Interop.
/// </summary>
public sealed record HotkeyGesture(bool Ctrl, bool Shift, bool Alt, bool Win, string Key)
{
    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = new HotkeyGesture(false, false, false, false, "");
        if (string.IsNullOrWhiteSpace(text))
            return false;

        bool ctrl = false, shift = false, alt = false, win = false;
        string key = "";

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL": ctrl = true; break;
                case "SHIFT": shift = true; break;
                case "ALT": alt = true; break;
                case "WIN" or "WINDOWS" or "SUPER": win = true; break;
                default:
                    if (key.Length > 0)
                        return false; // two non-modifier keys, no good
                    key = raw.ToUpperInvariant();
                    break;
            }
        }

        if (key.Length == 0)
            return false;

        gesture = new HotkeyGesture(ctrl, shift, alt, win, key);
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Ctrl) parts.Add("Ctrl");
        if (Shift) parts.Add("Shift");
        if (Alt) parts.Add("Alt");
        if (Win) parts.Add("Win");
        parts.Add(Key);
        return string.Join("+", parts);
    }
}
