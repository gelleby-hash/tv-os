using TvLauncher.Input;

namespace TvLauncher.Tests;

public sealed class KeyboardHookTests
{
    private const int VkBrowserHome = 0xAC;
    private const int VkBrowserBack = 0xA6;
    private const int VkBrowserSearch = 170;
    private const int VkApps = 93;
    private const int VkHome = 0x24;

    private static readonly int[] HomeKeys = [VkBrowserHome];
    private static readonly int[] ExtraKeys = [VkBrowserSearch, VkApps];

    private static bool Swallow(int vkCode, bool appInFront = false, bool launcherInFront = true, int[]? homeKeys = null) =>
        KeyboardHook.ShouldSwallow(homeKeys ?? HomeKeys, VkBrowserBack, ExtraKeys, appInFront, launcherInFront, vkCode);

    [Fact]
    public void ShouldSwallow_ConfiguredHomeKey_Always()
    {
        // The decision does not depend on down, up, repeat or debounce: every event of the key is swallowed.
        // It does not depend on an app being in front, and not on Windows flagging the event as injected: the
        // remote's Home and Back are consumer keys, which Windows flags as injected even though they are real.
        foreach (var appInFront in new[] { false, true })
        {
            Assert.True(Swallow(VkBrowserHome, appInFront));
            Assert.True(Swallow(VkHome, appInFront, homeKeys: [VkHome]));
        }
    }

    [Fact]
    public void ShouldSwallow_EveryKeyInTheHomeList()
    {
        int[] homeKeys = [VkBrowserHome, VkHome];

        Assert.True(Swallow(VkBrowserHome, homeKeys: homeKeys));
        Assert.True(Swallow(VkHome, homeKeys: homeKeys));
        Assert.False(Swallow(0x41, homeKeys: homeKeys));
    }

    [Fact]
    public void ShouldSwallow_BackOnlyWhileAppInFront()
    {
        // On the home screen Back reaches the page, which closes an overlay with it.
        Assert.False(Swallow(VkBrowserBack, appInFront: false));
        Assert.True(Swallow(VkBrowserBack, appInFront: true));
    }

    [Fact]
    public void ShouldSwallow_ExtraKeys_WhileLauncherInFront()
    {
        // The remote's Search (170) and 3-lines (93) buttons would open Start and a context menu; Windows flags
        // them as injected, which is not asked. On the home screen and inside an app alike.
        foreach (var appInFront in new[] { false, true })
        {
            Assert.True(Swallow(VkBrowserSearch, appInFront));
            Assert.True(Swallow(VkApps, appInFront));
        }
    }

    [Fact]
    public void ShouldSwallow_ExtraKeys_PassOnDesktop()
    {
        // The desktop button stepped the launcher aside: the desktop and VLC keep their keys.
        Assert.False(Swallow(VkBrowserSearch, launcherInFront: false));
        Assert.False(Swallow(VkApps, launcherInFront: false));
    }

    [Fact]
    public void ShouldSwallow_OtherKeys_PassThrough()
    {
        foreach (var appInFront in new[] { false, true })
        {
            Assert.False(Swallow(VkHome, appInFront));
            Assert.False(Swallow(0x41, appInFront));
        }
    }

    [Fact]
    public void TakeReport_KeyDownsUntilNoneLeft()
    {
        var left = 2;

        Assert.True(KeyboardHook.TakeReport(ref left, keyDown: true));
        Assert.True(KeyboardHook.TakeReport(ref left, keyDown: true));
        Assert.False(KeyboardHook.TakeReport(ref left, keyDown: true));
        Assert.Equal(0, left);
    }

    [Fact]
    public void TakeReport_KeyUpIsNotReportedAndCostsNothing()
    {
        var left = 1;

        Assert.False(KeyboardHook.TakeReport(ref left, keyDown: false));
        Assert.Equal(1, left);
    }
}
