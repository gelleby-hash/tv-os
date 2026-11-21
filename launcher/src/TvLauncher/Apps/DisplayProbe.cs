using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace TvLauncher.Apps;

/// <summary>
/// Logs the screens' mode and HDR state for a while after the start, to tell whether a black flash at a boot is the
/// signal to the TV changing (owner, 2026-11-21: the screen flickers to black close to start-up; the page logged
/// <c>hdr off</c> then <c>hdr on</c> 0.1 to 0.2 s apart on 2026-11-15 and 2026-11-20, but the page only sees a change
/// once it has loaded). Every <see cref="CheckEvery"/> it reads each active screen from Windows' display configuration
/// (size, refresh rate, SDR / WCG / HDR and bits per channel) and logs the screens whenever that changes, and it logs
/// every display change Windows sends the window (<c>WM_DISPLAYCHANGE</c>). A change before the launcher started is
/// not seen.
/// </summary>
public sealed class DisplayProbe : IDisposable
{
    public static readonly TimeSpan CheckEvery = TimeSpan.FromMilliseconds(100);
    public const int MaxLines = 40;

    private const int WM_DISPLAYCHANGE = 0x007E;

    private readonly HwndSource? _source;
    private readonly Func<TimeSpan> _sinceStart;
    private readonly Action<string> _log;
    private readonly DispatcherTimer _check;
    private string _last;
    private bool _watching = true;
    private int _lines;

    /// <summary>A screen as the probe logs it. <paramref name="Color"/> is SDR, WCG or HDR, or why it was not read.</summary>
    public sealed record Screen(int Width, int Height, double Hz, string Color, int Bits);

    /// <summary>Call on the UI thread.</summary>
    public DisplayProbe(HwndSource? source, Func<TimeSpan> sinceStart, Action<string> log)
    {
        _source = source;
        _sinceStart = sinceStart;
        _log = log;
        _last = Read();
        _log("display probe: now " + _last);
        _source?.AddHook(OnMessage);
        _check = new DispatcherTimer { Interval = CheckEvery };
        _check.Tick += (_, _) => Check();
        _check.Start();
    }

    /// <summary>The probe's description of the screens: each as "3840x2160 60.00 Hz HDR 10-bit", joined by "; ".</summary>
    public static string Line(IReadOnlyList<Screen> screens) =>
        screens.Count == 0
            ? "no active display"
            : string.Join("; ", screens.Select(s =>
                FormattableString.Invariant($"{s.Width}x{s.Height} {s.Hz:0.00} Hz {s.Color}") + (s.Bits > 0 ? $" {s.Bits}-bit" : "")));

    private void Check()
    {
        if (!_watching)
            return;
        var now = Read();
        if (now == _last)
            return;
        _last = now;
        Write("screens " + now);
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DISPLAYCHANGE && _watching)
        {
            var size = lParam.ToInt64();
            Write($"display change {size & 0xFFFF}x{(size >> 16) & 0xFFFF}, {wParam.ToInt64()} bits per pixel");
            Check();
        }
        return IntPtr.Zero;
    }

    private void Write(string what)
    {
        _lines++;
        _log($"display probe: {_sinceStart().TotalSeconds:0.0} s, {what}");
        if (_lines >= MaxLines)
            Dispose();
    }

    /// <summary>Stops watching, with one line saying when. The owner calls it after the watch time.</summary>
    public void Dispose()
    {
        if (!_watching)
            return;
        _watching = false;
        _check.Stop();
        _source?.RemoveHook(OnMessage);
        _log($"display probe: stopped {_sinceStart().TotalSeconds:0.0} s after the start, {_lines} change(s)");
    }

    /// <summary>The active screens now, as <see cref="Line"/> writes them, or why they could not be read.</summary>
    public static string Read()
    {
        var error = NativeMethods.GetDisplayConfigBufferSizes(NativeMethods.QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount);
        if (error != 0)
            return $"not read (GetDisplayConfigBufferSizes error {error})";
        var paths = new NativeMethods.PATH_INFO[pathCount];
        var modes = new NativeMethods.MODE_INFO[modeCount];
        error = NativeMethods.QueryDisplayConfig(NativeMethods.QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
        if (error != 0)
            return $"not read (QueryDisplayConfig error {error})";

        var screens = new List<Screen>();
        for (var i = 0; i < pathCount; i++)
        {
            var path = paths[i];
            int width = 0, height = 0;
            var index = path.sourceInfo.modeInfoIdx;
            if (index < modeCount && modes[index].infoType == NativeMethods.MODE_INFO_TYPE_SOURCE)
                (width, height) = ((int)modes[index].width, (int)modes[index].height);
            var rate = path.targetInfo.refreshRate;
            var hz = rate.Denominator == 0 ? 0 : (double)rate.Numerator / rate.Denominator;

            var color = new NativeMethods.ADVANCED_COLOR_INFO_2
            {
                header = new NativeMethods.DEVICE_INFO_HEADER
                {
                    type = NativeMethods.GET_ADVANCED_COLOR_INFO_2,
                    size = (uint)Marshal.SizeOf<NativeMethods.ADVANCED_COLOR_INFO_2>(),
                    adapterId = path.targetInfo.adapterId,
                    id = path.targetInfo.id,
                },
            };
            var colorError = NativeMethods.DisplayConfigGetDeviceInfo(ref color);
            screens.Add(colorError == 0
                ? new Screen(width, height, hz, color.activeColorMode switch { 0 => "SDR", 1 => "WCG", 2 => "HDR", var m => "mode " + m }, (int)color.bitsPerColorChannel)
                : new Screen(width, height, hz, $"colour not read (error {colorError})", 0));
        }
        return Line(screens);
    }

    private static class NativeMethods
    {
        public const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
        public const uint MODE_INFO_TYPE_SOURCE = 1;
        public const uint GET_ADVANCED_COLOR_INFO_2 = 15;

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RATIONAL
        {
            public uint Numerator, Denominator;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id, modeInfoIdx, statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id, modeInfoIdx, outputTechnology, rotation, scaling;
            public RATIONAL refreshRate;
            public uint scanLineOrdering;
            public int targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PATH_INFO
        {
            public PATH_SOURCE_INFO sourceInfo;
            public PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        // The union after adapterId is 48 bytes; only a source mode's width and height are read.
        [StructLayout(LayoutKind.Explicit, Size = 64)]
        public struct MODE_INFO
        {
            [FieldOffset(0)] public uint infoType;
            [FieldOffset(4)] public uint id;
            [FieldOffset(8)] public LUID adapterId;
            [FieldOffset(16)] public uint width;
            [FieldOffset(20)] public uint height;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DEVICE_INFO_HEADER
        {
            public uint type, size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ADVANCED_COLOR_INFO_2
        {
            public DEVICE_INFO_HEADER header;
            public uint value, colorEncoding, bitsPerColorChannel, activeColorMode;
        }

        [DllImport("user32.dll")]
        public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        public static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] PATH_INFO[] pathArray,
            ref uint numModeInfoArrayElements, [Out] MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        public static extern int DisplayConfigGetDeviceInfo(ref ADVANCED_COLOR_INFO_2 requestPacket);
    }
}
