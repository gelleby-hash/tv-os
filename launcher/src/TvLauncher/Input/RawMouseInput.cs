using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace TvLauncher.Input;

/// <summary>
/// Raw input from every mouse, in the background too, delivered to a window's message hook: each report with the
/// name of the device that sent it (<see cref="AirMouse"/> needs to tell the remote's air mouse from other mice).
/// Only reads: the pointer still moves and clicks still land as usual.
/// </summary>
public sealed class RawMouseInput : IDisposable
{
    private readonly HwndSource _source;
    private readonly Action<string?, int, int, ushort> _report;
    private readonly Dictionary<IntPtr, string?> _names = new();

    /// <param name="report">Called with the device's name, its movement and its RI_MOUSE_* button flags.</param>
    public RawMouseInput(HwndSource source, Action<string?, int, int, ushort> report)
    {
        _source = source;
        _report = report;
        var device = new NativeMethods.RAWINPUTDEVICE
        {
            usUsagePage = 1, usUsage = 2, dwFlags = NativeMethods.RIDEV_INPUTSINK, hwndTarget = source.Handle,
        };
        if (!NativeMethods.RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICE>()))
            throw new InvalidOperationException($"RegisterRawInputDevices failed (error {Marshal.GetLastWin32Error()}).");
        source.AddHook(Hook);
    }

    public void Dispose() => _source.RemoveHook(Hook);

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_INPUT)
            Read(lParam);
        return IntPtr.Zero;
    }

    private void Read(IntPtr input)
    {
        uint size = 0;
        var header = (uint)Marshal.SizeOf<NativeMethods.RAWINPUTHEADER>();
        NativeMethods.GetRawInputData(input, NativeMethods.RID_INPUT, IntPtr.Zero, ref size, header);
        if (size == 0)
            return;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (NativeMethods.GetRawInputData(input, NativeMethods.RID_INPUT, buffer, ref size, header) != size)
                return;
            var h = Marshal.PtrToStructure<NativeMethods.RAWINPUTHEADER>(buffer);
            if (h.dwType != NativeMethods.RIM_TYPEMOUSE)
                return;
            // RAWMOUSE: usFlags, then usButtonFlags and usButtonData at 4, ulRawButtons at 8, lLastX at 12, lLastY at 16.
            var body = buffer + (int)header;
            var buttons = (ushort)Marshal.ReadInt16(body, 4);
            var dx = Marshal.ReadInt32(body, 12);
            var dy = Marshal.ReadInt32(body, 16);
            _report(Name(h.hDevice), dx, dy, buttons);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // A report made by SendInput has no device; a real one's name is read once per device.
    private string? Name(IntPtr device)
    {
        if (device == IntPtr.Zero)
            return null;
        if (_names.TryGetValue(device, out var name))
            return name;
        uint chars = 0;
        NativeMethods.GetRawInputDeviceInfo(device, NativeMethods.RIDI_DEVICENAME, null, ref chars);
        var text = new StringBuilder((int)chars + 1);
        name = NativeMethods.GetRawInputDeviceInfo(device, NativeMethods.RIDI_DEVICENAME, text, ref chars) > 0 ? text.ToString() : null;
        _names[device] = name;
        return name;
    }

    private static class NativeMethods
    {
        public const int WM_INPUT = 0x00FF;
        public const uint RIDEV_INPUTSINK = 0x00000100, RID_INPUT = 0x10000003, RIDI_DEVICENAME = 0x20000007, RIM_TYPEMOUSE = 0;

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTDEVICE
        {
            public ushort usUsagePage, usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTHEADER
        {
            public uint dwType, dwSize;
            public IntPtr hDevice, wParam;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

        [DllImport("user32.dll")]
        public static extern uint GetRawInputData(IntPtr input, uint command, IntPtr data, ref uint size, uint headerSize);

        [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", CharSet = CharSet.Unicode)]
        public static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, StringBuilder? data, ref uint size);
    }
}
