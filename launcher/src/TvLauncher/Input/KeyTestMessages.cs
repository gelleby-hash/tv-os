using System.Windows.Input;

namespace TvLauncher.Input;

/// <summary>
/// What the --keytest screen shows for a window message. The code comes straight from the message: the
/// virtual-key code of WM_KEYDOWN and WM_SYSKEYDOWN, and the command of WM_APPCOMMAND (which some remotes
/// send instead of a key), so what is shown is what HomeKeyVk must be set to. The WPF key name is a label only.
/// </summary>
internal static class KeyTestMessages
{
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_APPCOMMAND = 0x0319;
    private const int VK_F4 = 0x73;
    private const int FAPPCOMMAND_MASK = 0xF000;

    /// <returns>
    /// The text to show (null for any other message) and whether to stop Windows' default handling.
    /// Every key and command is stopped, so a browser Home or media key starts nothing, except Alt+F4,
    /// which closes the key test.
    /// </returns>
    public static (string? Text, bool Handled) Describe(int msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_KEYDOWN:
            case WM_SYSKEYDOWN:
                var vk = (int)(wParam.ToInt64() & 0xFF);
                var text = $"VK {vk} (0x{vk:X2})\n{KeyInterop.KeyFromVirtualKey(vk)}";
                return (text, !(msg == WM_SYSKEYDOWN && vk == VK_F4));
            case WM_APPCOMMAND:
                // GET_APPCOMMAND_LPARAM: the high word without the device bits.
                var command = (int)((lParam.ToInt64() >> 16) & 0xFFFF) & ~FAPPCOMMAND_MASK;
                return ($"APPCOMMAND {command}", true);
            default:
                return (null, false);
        }
    }
}
