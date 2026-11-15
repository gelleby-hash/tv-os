using TvLauncher.Config;

namespace TvLauncher.Remote;

/// <summary>
/// Maps the phone remote's keys to the Windows virtual-key codes the Rii i25 sends (measured 2026-10-26), so the
/// launcher's own keyboard hook and the apps treat a phone press exactly like a press on the Rii.
/// </summary>
public static class RemoteKeys
{
    private const int VkReturn = 13;
    private const int VkLeft = 37, VkUp = 38, VkRight = 39, VkDown = 40;
    private const int VkVolumeMute = 173, VkVolumeDown = 174, VkVolumeUp = 175;
    private const int VkMediaNextTrack = 176, VkMediaPrevTrack = 177, VkMediaPlayPause = 179;

    /// <summary>
    /// Home, Back and the menu key come from the config, as the hook reads them: Home is the first entry of
    /// <c>HomeKeyVk</c> (the Rii's own; the mini PC lists the keyboard's F12 after it), Back is <c>BackKeyVk</c>,
    /// the menu key <c>MenuKeyVk</c>.
    /// </summary>
    public static int VirtualKey(RemoteKey key, LauncherConfig config) => key switch
    {
        RemoteKey.Up => VkUp,
        RemoteKey.Down => VkDown,
        RemoteKey.Left => VkLeft,
        RemoteKey.Right => VkRight,
        RemoteKey.Ok => VkReturn,
        RemoteKey.Back => config.BackKeyVk,
        RemoteKey.Home => config.HomeKeyVks[0],
        RemoteKey.PlayPause => VkMediaPlayPause,
        RemoteKey.VolumeUp => VkVolumeUp,
        RemoteKey.VolumeDown => VkVolumeDown,
        RemoteKey.Menu => config.MenuKeyVk,
        // The Rii's forward and backward buttons are next and previous track, and YouTube jumps to the next and the
        // previous video with them (owner, 2026-11-15; recorded the same day).
        RemoteKey.NextTrack => VkMediaNextTrack,
        RemoteKey.PreviousTrack => VkMediaPrevTrack,
        RemoteKey.Mute => VkVolumeMute,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Not a key the remote can press."),
    };

    /// <summary>
    /// The extended-key flag: the arrows (37 to 40), the menu key (93) and the browser and media keys (166 to 183,
    /// which holds Back, Home, volume and play/pause) are extended keys on a keyboard, and a press sent without the
    /// flag can be read as the numpad key of the same scan code.
    /// </summary>
    internal static bool IsExtended(int vk) => vk is >= 37 and <= 40 || vk == 93 || vk is >= 166 and <= 183;
}
