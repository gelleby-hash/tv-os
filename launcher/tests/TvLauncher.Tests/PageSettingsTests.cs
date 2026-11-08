namespace TvLauncher.Tests;

public sealed class PageSettingsTests
{
    [Fact]
    public void For_TheTv_TurnsOffBrowserKeysDevToolsAndMenus()
    {
        // Ctrl+P, Ctrl+S, Ctrl+F or F7 from the remote's keyboard must not open a dialog over the home screen.
        var tv = PageSettings.For(dev: false);

        Assert.False(tv.BrowserAcceleratorKeys);
        Assert.False(tv.DevTools);
        Assert.False(tv.ContextMenus);
        Assert.False(tv.Zoom);
        Assert.False(tv.StatusBar);
    }

    [Fact]
    public void For_TheDevPc_KeepsBrowserKeysDevToolsAndMenus()
    {
        var dev = PageSettings.For(dev: true);

        Assert.True(dev.BrowserAcceleratorKeys);
        Assert.True(dev.DevTools);
        Assert.True(dev.ContextMenus);
        Assert.False(dev.Zoom);
        Assert.False(dev.StatusBar);
    }
}
