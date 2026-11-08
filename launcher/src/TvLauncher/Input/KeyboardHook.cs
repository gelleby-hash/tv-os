using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TvLauncher.Input;

/// <param name="timestamp">Stopwatch.GetTimestamp() at the moment the hook saw the press.</param>
public sealed class HomePressedEventArgs(long timestamp) : EventArgs
{
    public long Timestamp { get; } = timestamp;
}

/// <summary>
/// System-wide low-level keyboard hook (WH_KEYBOARD_LL) for the remote's Home and Back keys. Every event of a
/// Home key (down, up, repeats and debounced presses) is swallowed, so the app in front never sees it: in an
/// app VK_BROWSER_HOME would navigate away from the service. A press the <see cref="HomeKeyFilter"/> accepts
/// raises <see cref="HomePressed"/>. The Back key is swallowed and raises <see cref="BackPressed"/> only while
/// <see cref="AppInFront"/>; on the home screen it passes to the page. The extra keys (the remote's Search and
/// menu buttons) are swallowed while <see cref="LauncherInFront"/>, and pass when the desktop button stepped the
/// launcher aside. Events flagged as injected count as real: Windows flags the Rii i25's Home and Back so, and
/// the launcher never sends these keys itself.
/// The hook lives on its own message-loop thread: Windows calls a low-level hook on the thread that set it,
/// holds up every keystroke on the PC while it runs, and silently drops a hook that keeps timing out, so it
/// must not share a thread with UI work that can block. The callback only filters and posts the event.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private readonly IReadOnlyList<int> _homeVks;
    private readonly int _backVk;
    private readonly IReadOnlyList<int> _swallowVks;
    private readonly HomeKeyFilter _homeFilter;
    private readonly HomeKeyFilter _backFilter;
    private readonly SynchronizationContext? _context;
    // Held in a field so the garbage collector cannot free the delegate while Windows still calls it.
    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private readonly Thread _thread;
    private uint _threadId;
    private bool _disposed;

    /// <param name="homeVks">Virtual-key codes of the Home keys; a press of any of them goes home.</param>
    /// <param name="backVk">Virtual-key code of the Back key.</param>
    /// <param name="swallowVks">Keys kept from Windows, and from the app, while the launcher is in front.</param>
    public KeyboardHook(IReadOnlyList<int> homeVks, int backVk, IReadOnlyList<int> swallowVks)
    {
        _swallowVks = swallowVks;
        _homeVks = homeVks;
        _backVk = backVk;
        _homeFilter = new HomeKeyFilter(homeVks);
        _backFilter = new HomeKeyFilter([backVk]);
        _context = SynchronizationContext.Current;
        _proc = HookCallback;

        Exception? failure = null;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = NativeMethods.GetCurrentThreadId();
            // Creates this thread's message queue so Dispose can post WM_QUIT to it.
            NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, NativeMethods.PM_NOREMOVE);
            var hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero)
                failure = new Win32Exception(Marshal.GetLastWin32Error(), "Installing the keyboard hook failed.");
            ready.Set();
            if (hook == IntPtr.Zero)
                return;

            // Windows runs the hook callback from inside GetMessage; the loop ends on WM_QUIT.
            while (NativeMethods.GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
            {
            }
            NativeMethods.UnhookWindowsHookEx(hook);
        })
        {
            IsBackground = true,
            Name = "Remote key hook",
        };
        _thread.Start();
        ready.Wait();
        if (failure is not null)
            throw failure;
    }

    /// <summary>
    /// Raised on the creating thread's synchronization context, or the thread pool if it has none. The
    /// arguments carry the time of the press, taken on the hook thread before the event is posted.
    /// </summary>
    public event EventHandler<HomePressedEventArgs>? HomePressed;

    /// <summary>Raised like <see cref="HomePressed"/>, for a press of the Back key while <see cref="AppInFront"/>.</summary>
    public event EventHandler? BackPressed;

    /// <summary>True while an app is in front, set by the window. Read on the hook thread, so volatile.</summary>
    public volatile bool AppInFront;

    /// <summary>False while the desktop button has stepped the launcher aside; set by the window. Starts true.</summary>
    public volatile bool LauncherInFront = true;

    /// <summary>
    /// Raised like <see cref="HomePressed"/> for each of the first key-downs the hook sees (<see cref="ReportKeys"/> of
    /// them), with the key's code and whether Windows flagged it injected. After a boot a viewer found the remote dead
    /// until the mouse moved (owner, 2026-11-08), and the log could not say whether the keys reached Windows.
    /// </summary>
    public event Action<int, bool>? KeyReported;

    // How many key-downs are still to be reported; taken on the hook thread.
    private int _reportKeys;

    /// <summary>Reports the next this many key-downs through <see cref="KeyReported"/>.</summary>
    public void ReportKeys(int count) => Interlocked.Exchange(ref _reportKeys, count);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join();
    }

    private IntPtr HookCallback(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(data);
            var msg = (int)message;
            var keyDown = msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
            var vkCode = (int)info.vkCode;
            var appInFront = AppInFront;
            var launcherInFront = LauncherInFront;
            // TickCount64 rather than info.time, which is 32-bit and wraps after 49.7 days of uptime.
            var now = Environment.TickCount64;
            if (TakeReport(ref _reportKeys, keyDown))
                RaiseKeyReported(vkCode, (info.flags & NativeMethods.LLKHF_INJECTED) != 0);
            if (_homeFilter.ShouldTrigger(vkCode, keyDown, now))
                RaiseHomePressed(new HomePressedEventArgs(Stopwatch.GetTimestamp()));
            else if (appInFront && _backFilter.ShouldTrigger(vkCode, keyDown, now))
                RaiseBackPressed();
            if (ShouldSwallow(_homeVks, _backVk, _swallowVks, appInFront, launcherInFront, vkCode))
                return 1;
        }
        return NativeMethods.CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    /// <summary>
    /// Every event of a Home key is kept from the app in front, of the Back key while an app is in front, and of
    /// the extra keys while the launcher is in front.
    /// Whether Windows flagged the event as injected is not asked (see the class summary).
    /// </summary>
    internal static bool ShouldSwallow(
        IReadOnlyList<int> homeVks, int backVk, IReadOnlyList<int> swallowVks, bool appInFront, bool launcherInFront, int vkCode) =>
        homeVks.Contains(vkCode)
        || (appInFront && vkCode == backVk)
        || (launcherInFront && swallowVks.Contains(vkCode));

    /// <summary>True, and one fewer left, for a key-down while any are left to report.</summary>
    internal static bool TakeReport(ref int left, bool keyDown)
    {
        if (!keyDown)
            return false;
        while (true)
        {
            var current = Volatile.Read(ref left);
            if (current <= 0)
                return false;
            if (Interlocked.CompareExchange(ref left, current - 1, current) == current)
                return true;
        }
    }

    private void RaiseKeyReported(int vkCode, bool injected)
    {
        if (_context is not null)
            _context.Post(_ => KeyReported?.Invoke(vkCode, injected), null);
        else
            ThreadPool.QueueUserWorkItem(_ => KeyReported?.Invoke(vkCode, injected));
    }

    private void RaiseHomePressed(HomePressedEventArgs args)
    {
        if (_context is not null)
            _context.Post(_ => HomePressed?.Invoke(this, args), null);
        else
            ThreadPool.QueueUserWorkItem(_ => HomePressed?.Invoke(this, args));
    }

    private void RaiseBackPressed()
    {
        if (_context is not null)
            _context.Post(_ => BackPressed?.Invoke(this, EventArgs.Empty), null);
        else
            ThreadPool.QueueUserWorkItem(_ => BackPressed?.Invoke(this, EventArgs.Empty));
    }

    private static class NativeMethods
    {
        public const int WH_KEYBOARD_LL = 13;
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const uint LLKHF_INJECTED = 0x10;
        public const uint WM_QUIT = 0x0012;
        public const uint PM_NOREMOVE = 0;

        public delegate IntPtr LowLevelKeyboardProc(int code, IntPtr message, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode, scanCode, flags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam, lParam;
            public uint time;
            public int ptX, ptY;
            public uint lPrivate;
        }

        [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int hookId, LowLevelKeyboardProc proc, IntPtr module, uint threadId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

        [DllImport("user32.dll", EntryPoint = "GetMessageW")]
        public static extern int GetMessage(out MSG msg, IntPtr hwnd, uint filterMin, uint filterMax);

        [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint filterMin, uint filterMax, uint remove);

        [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string? moduleName);
    }
}
