using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace TvLauncher.Apps;

/// <summary>
/// Keeps the mouse pointer off the pages. The TV box's pointer rests where Windows put it at sign-in, and with the
/// remote's air mouse off nobody moves it: YouTube's TV page drew a hover on the video under it, and the apps opened
/// their hover cards (owner at the TV, 2026-11-15). The screen's bottom-right corner is no answer by itself: Prime
/// Video's rows and Disney+'s row arrows reach it (measured 2026-11-15). So the pointer goes onto a small window of
/// the launcher's own in that corner, above the pages, where no page sees it and the pointer is hidden. A real mouse
/// move takes it off again. Used only with <c>ParkPointer</c> in launcher.json (the mini PC): it moves the pointer.
/// <see cref="Hold"/> also pins the pointer to the spot for a while (the remote's air mouse, see <c>AirMouse</c>).
/// </summary>
public sealed class PointerPark
{
    /// <summary>The spot's side, in device-independent pixels.</summary>
    public const double Size = 4;

    /// <summary>How long a hold lasts after the last call to <see cref="Hold"/>.</summary>
    public static readonly TimeSpan HoldFor = TimeSpan.FromSeconds(2);

    private readonly Window owner;
    private readonly Action<string> log;
    private readonly DispatcherTimer _release;
    private Window? _spot;
    private (int X, int Y, int Width, int Height)? _at;
    private bool _logged;

    public PointerPark(Window owner, Action<string> log)
    {
        this.owner = owner;
        this.log = log;
        _release = new DispatcherTimer { Interval = HoldFor };
        _release.Tick += (_, _) => Release();
    }

    /// <summary>The spot in device pixels: the bottom-right corner of the screen with these bounds.</summary>
    internal static (int X, int Y, int Width, int Height) Spot(int left, int top, int right, int bottom, double scale)
    {
        var size = Math.Max(1, (int)Math.Round(Size * scale));
        return (right - size, bottom - size, size, size);
    }

    /// <summary>Where the pointer goes: the middle of the spot.</summary>
    internal static (int X, int Y) Target(int x, int y, int width, int height) => (x + width / 2, y + height / 2);

    /// <summary>
    /// The spot's own answer to a window message, or null to leave it to Windows. A click never activates the spot:
    /// despite WS_EX_NOACTIVATE, a click on it made it the active window, holding the keyboard so no key reached the
    /// pages (seen on the box with the remote's air mouse on, 2026-11-15; reproduced on the dev PC). The click is
    /// eaten too: <c>AirMouse</c> already turns the remote's clicks into OK and Back.
    /// </summary>
    internal static int? Answer(int message) =>
        message == NativeMethods.WM_MOUSEACTIVATE ? NativeMethods.MA_NOACTIVATEANDEAT : null;

    /// <summary>Shows the spot in the corner of the launcher's screen and puts the pointer on it.</summary>
    public void Park()
    {
        try
        {
            if (!owner.IsVisible)
                return;
            var ownerHandle = new WindowInteropHelper(owner).Handle;
            var monitor = NativeMethods.MonitorFromWindow(ownerHandle, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info))
                return;
            var screen = info.rcMonitor;
            var (x, y, width, height) = Spot(screen.Left, screen.Top, screen.Right, screen.Bottom, VisualTreeHelper.GetDpi(owner).DpiScaleX);
            _at = (x, y, width, height);
            _spot ??= CreateSpot();
            if (!_spot.IsVisible)
                _spot.Show();
            var spot = new WindowInteropHelper(_spot).Handle;
            NativeMethods.SetWindowPos(spot, NativeMethods.HWND_TOPMOST, x, y, width, height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            var (px, py) = Target(x, y, width, height);
            NativeMethods.SetCursorPos(px, py);
            if (!_logged)
            {
                _logged = true;
                log($"pointer: parked in the corner of the screen ({screen.Right - screen.Left}x{screen.Bottom - screen.Top})");
            }
        }
        catch (Exception ex)
        {
            log("pointer: could not park: " + ex.Message);
        }
    }

    /// <summary>
    /// Pins the pointer to the spot until <see cref="HoldFor"/> after the last call (Windows' ClipCursor): every
    /// mouse is held meanwhile, so the remote's air mouse cannot carry the pointer onto a page. Windows can drop a
    /// pin (another window taking the front), so each call pins it again.
    /// </summary>
    public void Hold()
    {
        try
        {
            if (!_release.IsEnabled)
                Park();
            if (_at is not { } at || !owner.IsVisible)
                return;
            var rect = new NativeMethods.RECT { Left = at.X, Top = at.Y, Right = at.X + at.Width, Bottom = at.Y + at.Height };
            NativeMethods.ClipCursor(ref rect);
            _release.Stop();
            _release.Start();
        }
        catch (Exception ex)
        {
            log("pointer: could not hold: " + ex.Message);
        }
    }

    private void Release()
    {
        _release.Stop();
        NativeMethods.ClipCursor(IntPtr.Zero);
    }

    /// <summary>Takes the spot away, as when the desktop button steps the launcher aside.</summary>
    public void Hide()
    {
        if (_release.IsEnabled)
            Release();
        _spot?.Hide();
    }

    private Window CreateSpot()
    {
        var spot = new Window
        {
            Title = "TV launcher pointer spot",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            // Not wholly transparent: a layered window lets the pointer through its transparent pixels.
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Focusable = false,
            Cursor = Cursors.None,
            Width = Size,
            Height = Size,
            Owner = owner,
        };
        // Never the active window, and not in Alt+Tab.
        spot.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(spot).Handle;
            var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE).ToInt64();
            NativeMethods.SetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE,
                new IntPtr(style | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW));
            HwndSource.FromHwnd(handle)?.AddHook((IntPtr _, int message, IntPtr _, IntPtr _, ref bool handled) =>
            {
                if (Answer(message) is not { } answer)
                    return IntPtr.Zero;
                handled = true;
                return new IntPtr(answer);
            });
        };
        return spot;
    }

    private static class NativeMethods
    {
        public const uint MONITOR_DEFAULTTONEAREST = 2;
        public const int GWL_EXSTYLE = -20;
        public const long WS_EX_NOACTIVATE = 0x08000000;
        public const long WS_EX_TOOLWINDOW = 0x00000080;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATEANDEAT = 4;
        public static readonly IntPtr HWND_TOPMOST = new(-1);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor, rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ClipCursor(ref RECT rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ClipCursor(IntPtr none);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    }
}
