using TvLauncher.Remote;

namespace TvLauncher.Input;

/// <summary>
/// Keeps the remote's air mouse out of the pages. The Rii i25's air mouse comes on at every power-on and the remote
/// cannot be told to stay off (owner, 2026-11-15); waved, it moves the pointer onto the apps, and with it on, OK and
/// Back arrive as mouse clicks. Raw input names the device a mouse report comes from (the Rii's dongle is
/// <c>VID_1997&amp;PID_2433</c>, its air mouse interface <c>MI_01&amp;Col01</c>, recorded 2026-11-15), so a move of
/// the air mouse holds the pointer on the launcher's parked spot, and its left and right clicks are pressed as OK
/// (Enter) and Back. Other mice are left alone, and so is everything while the desktop button has stepped the
/// launcher aside.
/// </summary>
public sealed class AirMouse(string deviceId, IKeyPresser presser, int backVk, Action hold)
{
    private const int VkReturn = 13;
    private const ushort LeftDown = 0x0001, RightDown = 0x0004;

    /// <summary>False while the desktop button has stepped the launcher aside.</summary>
    public bool LauncherInFront { get; set; } = true;

    /// <summary>
    /// Set by the emergency key (the Rii's Search, <c>PointerKeyVk</c>) when a page needs the pointer: the air mouse is
    /// an ordinary mouse until the key is pressed again or Home is.
    /// </summary>
    public bool Free { get; set; }

    /// <summary>Whether a raw input device name (<c>\\?\HID#VID_1997&amp;PID_2433&amp;MI_01...</c>) is the configured device.</summary>
    internal static bool Matches(string? deviceName, string deviceId) =>
        deviceName is not null && deviceName.Contains(deviceId, StringComparison.OrdinalIgnoreCase);

    /// <summary>One raw mouse report: the device's name, its movement and its button flags (RI_MOUSE_*).</summary>
    public void OnReport(string? deviceName, int dx, int dy, ushort buttons)
    {
        if (Free || !LauncherInFront || !Matches(deviceName, deviceId))
            return;
        if (dx != 0 || dy != 0)
            hold();
        if ((buttons & LeftDown) != 0)
            presser.Press(VkReturn);
        if ((buttons & RightDown) != 0)
            presser.Press(backVk);
    }
}
