using System.Windows;
using System.Windows.Interop;
using Klip.Core.Hotkeys;
using Klip.Interop;

namespace Klip.App.Services;

/// <summary>
/// Global hotkeys via RegisterHotKey on a message-only window.
/// A failed register (1409) is reported back so the UI can handle it.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _handlers = [];
    private int _nextId = 1;

    /// <summary>Win32 error of the last failed <see cref="TryRegister"/> (1409 = taken by another app).</summary>
    public int LastError { get; private set; }

    /// <summary>
    /// How long the WM_HOTKEY being handled waited in the queue before its handler ran,
    /// in ms. Read it at the start of a hotkey handler; anything a blocked ui thread
    /// added before the handler shows up here and nowhere else.
    /// </summary>
    public static int LastQueueMs { get; private set; } = -1;

    /// <summary>
    /// Returns <see cref="LastQueueMs"/> and clears it, so an open that did not come
    /// from a hotkey (tray menu, settings) does not report a stale value. -1 = none.
    /// </summary>
    public static int TakeQueueMs()
    {
        var value = LastQueueMs;
        LastQueueMs = -1;
        return value;
    }

    public HotkeyService()
    {
        // message-only window (HWND_MESSAGE = -3): gets WM_HOTKEY with no UI
        var parameters = new HwndSourceParameters("KlipHotkeyWindow")
        {
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
            ParentWindow = new nint(-3),
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    /// <summary>Registers the gesture; returns false if another app already grabbed it (ERROR_HOTKEY_ALREADY_REGISTERED).</summary>
    public bool TryRegister(HotkeyGesture gesture, Action callback, out int id)
    {
        id = 0;
        if (!TryMapKey(gesture.Key, out var vk))
            return false;

        uint mods = NativeMethods.MOD_NOREPEAT;
        if (gesture.Ctrl) mods |= NativeMethods.MOD_CONTROL;
        if (gesture.Shift) mods |= NativeMethods.MOD_SHIFT;
        if (gesture.Alt) mods |= NativeMethods.MOD_ALT;
        if (gesture.Win) mods |= NativeMethods.MOD_WIN;

        var newId = _nextId;
        if (!NativeMethods.RegisterHotKey(_source.Handle, newId, mods, vk))
        {
            LastError = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            return false;
        }

        _nextId++;
        id = newId;
        _handlers[newId] = callback;
        return true;
    }

    public void Unregister(int id)
    {
        if (_handlers.Remove(id))
            NativeMethods.UnregisterHotKey(_source.Handle, id);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && _handlers.TryGetValue((int)wParam, out var action))
        {
            // both clocks wrap together every ~49 days, the difference stays right
            LastQueueMs = Math.Max(0, Environment.TickCount - NativeMethods.GetMessageTime());
            action();
            handled = true;
        }
        return nint.Zero;
    }

    private static bool TryMapKey(string key, out uint vk)
    {
        vk = 0;
        switch (key)
        {
            case "PRINTSCREEN" or "PRTSC" or "PRTSCN":
                vk = NativeMethods.VK_SNAPSHOT;
                return true;
        }

        if (key.Length == 1 && (char.IsAsciiLetterUpper(key[0]) || char.IsAsciiDigit(key[0])))
        {
            vk = key[0]; // VK of letters/digits == ASCII code
            return true;
        }

        if (key.Length is 2 or 3 && key[0] == 'F' && int.TryParse(key[1..], out var f) && f is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + f - 1); // VK_F1..VK_F24
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        foreach (var id in _handlers.Keys.ToList())
            NativeMethods.UnregisterHotKey(_source.Handle, id);
        _handlers.Clear();
        _source.Dispose();
    }
}
