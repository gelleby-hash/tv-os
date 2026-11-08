using System.Runtime.InteropServices;

namespace TvLauncher.Remote;

/// <summary>Presses a key on this PC, as if on a keyboard.</summary>
public interface IKeyPresser
{
    void Press(int vk);
}

/// <summary>
/// Presses a key with <c>SendInput</c>: one call carrying the key down and the key up, so another input cannot
/// land between them. Windows flags the events as injected, as it does the Rii's own Home, Back and media keys,
/// and the launcher's keyboard hook acts on injected events (see <c>KeyboardHook</c>).
/// </summary>
public sealed class SendInputKeyPresser(Action<string> log) : IKeyPresser
{
    public void Press(int vk)
    {
        var inputs = Inputs(vk);
        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent < inputs.Length)
            log($"remote: SendInput pressed {sent} of {inputs.Length} events for key {vk} (error {Marshal.GetLastWin32Error()})");
    }

    /// <summary>
    /// The key down and key up for <paramref name="vk"/>, with the scan code a keyboard would send. Chromium takes
    /// <c>KeyboardEvent.code</c> from the scan code, so with 0 a page would see an empty code where the Rii's arrows
    /// give <c>ArrowLeft</c> and so on (final branch review, 2026-10-31; not measured in a page). The extended flag
    /// stays as before: with it, the arrows' scan codes are the arrow keys, not the number pad's.
    /// </summary>
    internal static NativeMethods.INPUT[] Inputs(int vk)
    {
        var flags = RemoteKeys.IsExtended(vk) ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0u;
        var scan = (ushort)NativeMethods.MapVirtualKey((uint)vk, NativeMethods.MAPVK_VK_TO_VSC);
        return new[]
        {
            NativeMethods.Key(vk, scan, flags),
            NativeMethods.Key(vk, scan, flags | NativeMethods.KEYEVENTF_KEYUP),
        };
    }

    internal static class NativeMethods
    {
        public const uint INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint MAPVK_VK_TO_VSC = 0;

        public static INPUT Key(int vk, ushort scan, uint flags) => new()
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)vk, wScan = scan, dwFlags = flags } },
        };

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        // The union holds the largest member too (MOUSEINPUT), so INPUT has the size SendInput checks for.
        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk, wScan;
            public uint dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint count, INPUT[] inputs, int size);

        [DllImport("user32.dll")]
        public static extern uint MapVirtualKey(uint code, uint mapType);
    }
}
