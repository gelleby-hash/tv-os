using System.Runtime.InteropServices;

namespace TvLauncher.Apps;

/// <summary>Brings a window to the front through Win32.</summary>
public sealed class Win32WindowSystem
{
    private readonly Action<string>? _log;

    /// <param name="log">Receives a line when bringing a window to the front fails, so it shows in the launcher log.</param>
    public Win32WindowSystem(Action<string>? log = null) => _log = log;

    /// <summary>Restores the window if minimised and brings it to the foreground.</summary>
    public void Activate(IntPtr hwnd)
    {
        if (TryActivate(hwnd) is { } problem)
            _log?.Invoke(problem);
    }

    /// <summary>As <see cref="Activate"/>, but returns the problem (null when both calls worked) instead of logging it.</summary>
    public string? TryActivate(IntPtr hwnd)
    {
        if (NativeMethods.IsIconic(hwnd))
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);

        // Windows only lets the process that sent the last input take the foreground. An empty mouse
        // input makes this process that sender (the PowerToys fix), without the menu side effects a
        // synthetic Alt press has in Edge and other apps.
        var input = new NativeMethods.INPUT { type = NativeMethods.INPUT_MOUSE };
        var sent = NativeMethods.SendInput(1, [input], Marshal.SizeOf<NativeMethods.INPUT>());
        var sendError = sent == 0 ? Marshal.GetLastWin32Error() : 0;
        var foreground = NativeMethods.SetForegroundWindow(hwnd);
        var foregroundError = foreground ? 0 : Marshal.GetLastWin32Error();
        return ActivateProblem(hwnd, sent, sendError, foreground, foregroundError);
    }

    /// <summary>True when the window is the one Windows has in the foreground.</summary>
    public bool InFront(IntPtr hwnd) => NativeMethods.GetForegroundWindow() == hwnd;

    /// <summary>For the log: which window is in front and which window of its thread has the keyboard.</summary>
    public string Keyboard(IntPtr ours)
    {
        var front = NativeMethods.GetForegroundWindow();
        string? program = null, frontClass = null, focusClass = null;
        if (front != IntPtr.Zero)
        {
            var thread = NativeMethods.GetWindowThreadProcessId(front, out var pid);
            try { program = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
            catch (Exception) { program = "pid " + pid; }
            frontClass = ClassName(front);
            var info = new NativeMethods.GUITHREADINFO { cbSize = Marshal.SizeOf<NativeMethods.GUITHREADINFO>() };
            if (NativeMethods.GetGUIThreadInfo(thread, ref info) && info.hwndFocus != IntPtr.Zero)
                focusClass = ClassName(info.hwndFocus);
        }
        return KeyboardLine(front != IntPtr.Zero && front == ours, program, frontClass, focusClass);
    }

    internal static string KeyboardLine(bool launcherInFront, string? program, string? frontClass, string? focusClass) =>
        (launcherInFront ? "launcher in front" : program is null ? "in front: nothing" : $"in front: {program} {frontClass}")
        + $", keyboard on {focusClass ?? "(none)"}";

    private static string ClassName(IntPtr hwnd)
    {
        var name = new System.Text.StringBuilder(256);
        return NativeMethods.GetClassName(hwnd, name, name.Capacity) > 0 ? name.ToString() : "?";
    }

    /// <summary>True when no window of the calling thread has the keyboard focus. Call it on the UI thread.</summary>
    public bool KeyboardFocusLost() => NativeMethods.GetFocus() == IntPtr.Zero;

    /// <summary>The log line for a failed Activate, or null when both calls worked.</summary>
    internal static string? ActivateProblem(IntPtr hwnd, uint sent, int sendError, bool foreground, int foregroundError)
    {
        var problems = new List<string>();
        if (sent == 0)
            problems.Add($"SendInput sent nothing (error {sendError})");
        if (!foreground)
            problems.Add($"SetForegroundWindow refused (error {foregroundError})");
        return problems.Count == 0 ? null : $"Activate 0x{hwnd:X}: {string.Join("; ", problems)}";
    }

    private static class NativeMethods
    {
        public const int SW_RESTORE = 9;
        public const uint INPUT_MOUSE = 0;

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        // MOUSEINPUT is the largest member of the native union, so this has the native size.
        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public MOUSEINPUT mi;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hwnd, int cmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetFocus();

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int left, top, right, bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct GUITHREADINFO
        {
            public int cbSize, flags;
            public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
            public RECT rcCaret;
        }

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);

        [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int size);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint count, INPUT[] inputs, int size);
    }
}
