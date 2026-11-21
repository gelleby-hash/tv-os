using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace TvLauncher.Apps;

/// <summary>
/// Logs each change of the foreground window for a while after the start, to find what takes the front from the
/// launcher during a boot (mini PC log, 2026-11-21: up to 7 asks, and Creative's Volume Panel holding the keyboard
/// after the asking had stopped). Each line names the window's program and class, whether it is visible, its size
/// and whether it is topmost, so a window that covers the screen shows. Windows' foreground event catches a window
/// that holds the front for a moment; it did not report the launcher's own window coming to the front (seen on the
/// mini PC and reproduced on the dev PC, 2026-11-21, cause unknown), so a check every <see cref="CheckEvery"/> adds
/// what the event missed. A window is logged only when it differs from the last one logged.
/// </summary>
public sealed class ForegroundProbe : IDisposable
{
    public static readonly TimeSpan WatchFor = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan CheckEvery = TimeSpan.FromMilliseconds(100);
    public const int MaxLines = 40;

    private readonly IntPtr _ours;
    private readonly Func<TimeSpan> _sinceStart;
    private readonly Action<string> _log;
    private readonly NativeMethods.WinEventProc _callback;
    private readonly DispatcherTimer _check;
    private IntPtr _hook;
    private IntPtr _last;
    private bool _watching = true;
    private int _lines;

    /// <summary>Call on the UI thread: Windows sends the events to the thread that set the hook.</summary>
    public ForegroundProbe(IntPtr ours, Func<TimeSpan> sinceStart, Action<string> log)
    {
        _ours = ours;
        _sinceStart = sinceStart;
        _log = log;
        _callback = (_, _, hwnd, _, _, _, _) => Seen(hwnd);
        _hook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _callback, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
        _last = NativeMethods.GetForegroundWindow();
        _log("front probe: now " + Win32WindowSystem.Describe(_last, _ours)
            + (_hook == IntPtr.Zero ? "; no foreground event (SetWinEventHook failed), checking only" : ""));
        _check = new DispatcherTimer { Interval = CheckEvery };
        _check.Tick += (_, _) => Seen(NativeMethods.GetForegroundWindow());
        _check.Start();
    }

    private void Seen(IntPtr hwnd)
    {
        if (!_watching || hwnd == _last)
            return;
        _last = hwnd;
        _lines++;
        _log($"front probe: {_sinceStart().TotalSeconds:0.0} s, " + Win32WindowSystem.Describe(hwnd, _ours));
        if (_lines >= MaxLines)
            Dispose();
    }

    /// <summary>Stops watching, with one line saying when. The owner calls it <see cref="WatchFor"/> after the start.</summary>
    public void Dispose()
    {
        if (!_watching)
            return;
        _watching = false;
        _check.Stop();
        if (_hook != IntPtr.Zero)
            NativeMethods.UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
        _log($"front probe: stopped {_sinceStart().TotalSeconds:0.0} s after the start, {_lines} change(s)");
    }

    private static class NativeMethods
    {
        public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        public const uint WINEVENT_OUTOFCONTEXT = 0x0000;

        public delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [DllImport("user32.dll")]
        public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback,
            uint processId, uint threadId, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
    }
}
