using Microsoft.Web.WebView2.Core;

namespace TvLauncher;

/// <summary>
/// The WebView2 settings of the home page and the app pages. On the TV everything a keyboard or a right click
/// could open over the screen is off: browser shortcuts (Ctrl+P print, Ctrl+S save, Ctrl+F find, F7 caret
/// browsing), dev tools and the context menu. The dev PC keeps them for debugging.
/// </summary>
internal sealed record PageSettings(bool BrowserAcceleratorKeys, bool DevTools, bool ContextMenus, bool Zoom, bool StatusBar)
{
    public static PageSettings For(bool dev) => new(
        BrowserAcceleratorKeys: dev,
        DevTools: dev,
        ContextMenus: dev,
        Zoom: false,
        StatusBar: false);

    public void ApplyTo(CoreWebView2Settings settings)
    {
        settings.AreBrowserAcceleratorKeysEnabled = BrowserAcceleratorKeys;
        settings.AreDevToolsEnabled = DevTools;
        settings.AreDefaultContextMenusEnabled = ContextMenus;
        settings.IsZoomControlEnabled = Zoom;
        settings.IsStatusBarEnabled = StatusBar;
    }
}
