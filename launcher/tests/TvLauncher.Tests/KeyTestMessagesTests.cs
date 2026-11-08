using TvLauncher.Input;

namespace TvLauncher.Tests;

public sealed class KeyTestMessagesTests
{
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_APPCOMMAND = 0x0319;

    [Fact]
    public void KeyDown_ShowsTheRawVirtualKeyCode_AndStopsDefaultHandling()
    {
        // VK_BROWSER_HOME (172) has no WPF Key of its own worth trusting: the code shown is the one in the message.
        var (text, handled) = KeyTestMessages.Describe(WM_KEYDOWN, new IntPtr(0xAC), IntPtr.Zero);

        Assert.NotNull(text);
        Assert.StartsWith("VK 172 (0xAC)", text);
        Assert.True(handled);
    }

    [Fact]
    public void SysKeyDown_ShowsTheCode_ButAltF4StillClosesTheTest()
    {
        var (f10, f10Handled) = KeyTestMessages.Describe(WM_SYSKEYDOWN, new IntPtr(0x79), IntPtr.Zero);
        var (f4, f4Handled) = KeyTestMessages.Describe(WM_SYSKEYDOWN, new IntPtr(0x73), IntPtr.Zero);

        Assert.StartsWith("VK 121 (0x79)", f10);
        Assert.True(f10Handled);
        Assert.StartsWith("VK 115 (0x73)", f4);
        Assert.False(f4Handled);
    }

    [Fact]
    public void AppCommand_ShowsTheCommandFromTheHighWord()
    {
        // APPCOMMAND_BROWSER_HOME (7) from a keyboard device (FAPPCOMMAND_KEY 0x0000) in the high word of lParam.
        var (home, handled) = KeyTestMessages.Describe(WM_APPCOMMAND, IntPtr.Zero, new IntPtr(7 << 16));
        // APPCOMMAND_MEDIA_PLAY_PAUSE (14) from an OEM device (FAPPCOMMAND_OEM 0x1000): the device bits are dropped.
        var (play, _) = KeyTestMessages.Describe(WM_APPCOMMAND, IntPtr.Zero, new IntPtr((0x1000 | 14) << 16));

        Assert.Equal("APPCOMMAND 7", home);
        Assert.True(handled);
        Assert.Equal("APPCOMMAND 14", play);
    }

    [Fact]
    public void OtherMessages_AreLeftAlone()
    {
        var (text, handled) = KeyTestMessages.Describe(WM_KEYUP, new IntPtr(0xAC), IntPtr.Zero);

        Assert.Null(text);
        Assert.False(handled);
    }
}
