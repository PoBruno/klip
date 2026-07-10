using System.Runtime.InteropServices;

namespace Klip.Interop;

/// <summary>Synthetic input P/Invoke (SendInput) to paste into the target app.</summary>
public static partial class NativeMethods
{
    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_SCANCODE = 0x0008;

    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_SHIFT = 0x10;
    public const ushort VK_MENU = 0x12;   // Alt
    public const ushort VK_LWIN = 0x5B;
    public const ushort VK_RWIN = 0x5C;
    public const ushort VK_V = 0x56;

    public const uint MAPVK_VK_TO_VSC = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint SendInput(uint cInputs, [In] INPUT[] pInputs, int cbSize);

    [LibraryImport("user32.dll")]
    public static partial uint MapVirtualKeyW(uint uCode, uint uMapType);

    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int vKey);

    // ----- Helpers -----

    /// <summary>True while the given virtual key is currently held down.</summary>
    public static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    private static INPUT KeyInput(ushort vk, bool up)
    {
        var scan = (ushort)MapVirtualKeyW(vk, MAPVK_VK_TO_VSC);
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = scan,
                    // scan codes for apps that actually check them
                    dwFlags = KEYEVENTF_SCANCODE | (up ? KEYEVENTF_KEYUP : 0),
                },
            },
        };
    }

    /// <summary>Releases modifiers that are physically held down before we synthesize input.</summary>
    public static void ReleasePressedModifiers()
    {
        ushort[] modifiers = [VK_CONTROL, VK_SHIFT, VK_MENU, VK_LWIN, VK_RWIN];
        var toRelease = modifiers
            .Where(vk => (GetAsyncKeyState(vk) & 0x8000) != 0)
            .Select(vk => KeyInput(vk, up: true))
            .ToArray();
        if (toRelease.Length > 0)
            SendInput((uint)toRelease.Length, toRelease, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Synthesizes Ctrl+V.</summary>
    public static void SendCtrlV()
    {
        INPUT[] inputs =
        [
            KeyInput(VK_CONTROL, up: false),
            KeyInput(VK_V, up: false),
            KeyInput(VK_V, up: true),
            KeyInput(VK_CONTROL, up: true),
        ];
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    // ----- Synthetic scroll -----

    public const uint MOUSEEVENTF_WHEEL = 0x0800;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetCursorPos(int x, int y);

    /// <summary>Synthetic mouse wheel at the current cursor position (negative notches scroll down).</summary>
    public static void SendMouseWheel(int notches)
    {
        INPUT[] inputs =
        [
            new INPUT
            {
                type = 0, // INPUT_MOUSE
                U = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        dwFlags = MOUSEEVENTF_WHEEL,
                        mouseData = unchecked((uint)(notches * 120)),
                    },
                },
            },
        ];
        SendInput(1, inputs, Marshal.SizeOf<INPUT>());
    }
}
