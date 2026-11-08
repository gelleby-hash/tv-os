using TvLauncher.Input;

namespace TvLauncher.Tests;

public sealed class HomeKeyFilterTests
{
    private const int VkHome = 0x24;
    private const int VkEnd = 0x23;
    private const int VkBrowserHome = 0xAC;

    [Fact]
    public void ShouldTrigger_OnlyConfiguredKeyDown()
    {
        var filter = new HomeKeyFilter([VkHome]);

        Assert.False(filter.ShouldTrigger(VkEnd, keyDown: true, timestampMs: 1_000));
        Assert.False(filter.ShouldTrigger(VkHome, keyDown: false, timestampMs: 2_000));
        Assert.True(filter.ShouldTrigger(VkHome, keyDown: true, timestampMs: 3_000));
    }

    [Fact]
    public void ShouldTrigger_AnyKeyInTheList_OneDebounceAcrossThem()
    {
        var filter = new HomeKeyFilter([VkHome, VkBrowserHome]);

        Assert.True(filter.ShouldTrigger(VkBrowserHome, keyDown: true, timestampMs: 1_000));
        // Both keys sent together by one button: the second press is the same press.
        Assert.False(filter.ShouldTrigger(VkHome, keyDown: true, timestampMs: 1_050));
        Assert.True(filter.ShouldTrigger(VkHome, keyDown: true, timestampMs: 1_300));
        Assert.False(filter.ShouldTrigger(VkEnd, keyDown: true, timestampMs: 5_000));
    }

    [Fact]
    public void ShouldTrigger_DebouncesWithin300ms()
    {
        var filter = new HomeKeyFilter([VkHome]);

        Assert.True(filter.ShouldTrigger(VkHome, keyDown: true, timestampMs: 10_000));
        Assert.False(filter.ShouldTrigger(VkHome, keyDown: true, timestampMs: 10_150));
        Assert.False(filter.ShouldTrigger(VkHome, keyDown: true, timestampMs: 10_299));
        Assert.True(filter.ShouldTrigger(VkHome, keyDown: true, timestampMs: 10_300));
        Assert.False(filter.ShouldTrigger(VkHome, keyDown: true, timestampMs: 10_400));
    }
}
