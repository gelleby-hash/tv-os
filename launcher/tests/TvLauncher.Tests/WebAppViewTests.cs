using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using TvLauncher.Apps;

namespace TvLauncher.Tests;

public sealed class WebAppViewTests
{
    [Fact]
    public void KeyEvent_SpaceDown_TypesASpace()
    {
        var down = Parse(WebAppView.KeyEvent(AppKey.Space, down: true));

        Assert.Equal("keyDown", down.GetProperty("type").GetString());
        Assert.Equal(" ", down.GetProperty("key").GetString());
        Assert.Equal("Space", down.GetProperty("code").GetString());
        Assert.Equal(" ", down.GetProperty("text").GetString());
        Assert.Equal(32, down.GetProperty("windowsVirtualKeyCode").GetInt32());
        Assert.Equal(32, down.GetProperty("nativeVirtualKeyCode").GetInt32());
    }

    [Fact]
    public void KeyEvent_SpaceUp_HasNoText()
    {
        var up = Parse(WebAppView.KeyEvent(AppKey.Space, down: false));

        Assert.Equal("keyUp", up.GetProperty("type").GetString());
        Assert.Equal(" ", up.GetProperty("key").GetString());
        Assert.Equal("Space", up.GetProperty("code").GetString());
        Assert.False(up.TryGetProperty("text", out _));
        Assert.Equal(32, up.GetProperty("windowsVirtualKeyCode").GetInt32());
    }

    [Theory]
    [InlineData(true, "keyDown")]
    [InlineData(false, "keyUp")]
    public void KeyEvent_Escape(bool down, string type)
    {
        var e = Parse(WebAppView.KeyEvent(AppKey.Escape, down));

        Assert.Equal(type, e.GetProperty("type").GetString());
        Assert.Equal("Escape", e.GetProperty("key").GetString());
        Assert.Equal("Escape", e.GetProperty("code").GetString());
        Assert.False(e.TryGetProperty("text", out _));
        Assert.Equal(27, e.GetProperty("windowsVirtualKeyCode").GetInt32());
        Assert.Equal(27, e.GetProperty("nativeVirtualKeyCode").GetInt32());
    }

    // The spike's key that opens YouTube's voice search (2026-10-31): a raw key
    // down, no key name or text.
    [Theory]
    [InlineData(true, "rawKeyDown")]
    [InlineData(false, "keyUp")]
    public void KeyEvent_Microphone(bool down, string type)
    {
        var e = Parse(WebAppView.KeyEvent(AppKey.Microphone, down));

        Assert.Equal(type, e.GetProperty("type").GetString());
        Assert.Equal(12290, e.GetProperty("windowsVirtualKeyCode").GetInt32());
        Assert.Equal(12290, e.GetProperty("nativeVirtualKeyCode").GetInt32());
        Assert.False(e.TryGetProperty("key", out _));
        Assert.False(e.TryGetProperty("code", out _));
        Assert.False(e.TryGetProperty("text", out _));
    }

    [Fact]
    public void PhoneMicAudioMessage_CarriesThePcmInBase64()
    {
        var m = Parse(WebAppView.PhoneMicAudioMessage(new byte[] { 0x01, 0x80, 0xFF, 0x7F }));

        Assert.Equal("audio", m.GetProperty("tvMic").GetString());
        Assert.Equal("AYD/fw==", m.GetProperty("pcm").GetString());
    }

    [Fact]
    public void PhoneMicEndMessage()
    {
        var m = Parse(WebAppView.PhoneMicEndMessage);

        Assert.Equal("end", m.GetProperty("tvMic").GetString());
    }

    [Theory]
    [InlineData("""{"tvMic":"stopped"}""", true)]
    [InlineData("""{"tvMic":"end"}""", false)]
    [InlineData("""{"tvMic":1}""", false)]
    [InlineData("""{"other":"stopped"}""", false)]
    [InlineData("\"stopped\"", false)]
    [InlineData("[]", false)]
    [InlineData("not json", false)]
    [InlineData("""{"tvMic":"\uDC00"}""", false)]
    public void IsPhoneMicStopped(string json, bool expected)
    {
        Assert.Equal(expected, WebAppView.IsPhoneMicStopped(json));
    }

    // PageScripts.ExitWhenEmpty posts this when the app empties its page; window.close() is refused for a page the
    // browser did not open ("Scripts may close only the windows that were opened by them", 2026-11-02).
    [Theory]
    [InlineData("""{"tvExit":"empty"}""", true)]
    [InlineData("""{"tvExit":"other"}""", false)]
    [InlineData("""{"tvExit":true}""", false)]
    [InlineData("""{"tvMic":"stopped"}""", false)]
    [InlineData(""""tvExit"""", false)]
    [InlineData("not json", false)]
    public void IsExitRequested(string json, bool expected)
    {
        Assert.Equal(expected, WebAppView.IsExitRequested(json));
    }

    [Fact]
    public void BrowserArguments_NotDev_NoDebuggingPort()
    {
        const string disney = "--enable-spatial-navigation --autoplay-policy=no-user-gesture-required";

        Assert.Equal(disney, WebAppView.BrowserArguments(disney, dev: false, devPort: 9301));
        Assert.Equal("", WebAppView.BrowserArguments("", dev: false, devPort: 9300));
    }

    [Fact]
    public void BrowserArguments_Dev_AddsThePort()
    {
        Assert.Equal(
            "--enable-spatial-navigation --remote-debugging-port=9301",
            WebAppView.BrowserArguments("--enable-spatial-navigation", dev: true, devPort: 9301));
        Assert.Equal("--remote-debugging-port=9300", WebAppView.BrowserArguments("", dev: true, devPort: 9300));
    }

    [Fact]
    public void UserDataFolder_PerApp()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.Equal(local + @"\TvLauncher\WebView2-disney", WebAppView.UserDataFolder("disney"));
    }

    [Fact]
    public void LoadFailure_NoServerResponse_Shows()
    {
        Assert.True(WebAppView.ShowsLoadFailure(false, CoreWebView2WebErrorStatus.ConnectionAborted, 0));
        Assert.True(WebAppView.ShowsLoadFailure(false, CoreWebView2WebErrorStatus.HostNameNotResolved, 0));
    }

    [Fact]
    public void LoadFailure_ServerAnswered_DoesNotShow()
    {
        Assert.False(WebAppView.ShowsLoadFailure(false, CoreWebView2WebErrorStatus.ConnectionAborted, 200));
        Assert.False(WebAppView.ShowsLoadFailure(false, CoreWebView2WebErrorStatus.Unknown, 503));
    }

    [Fact]
    public void LoadFailure_Cancelled_DoesNotShow()
    {
        Assert.False(WebAppView.ShowsLoadFailure(false, CoreWebView2WebErrorStatus.OperationCanceled, 0));
    }

    [Fact]
    public void LoadFailure_Success_DoesNotShow()
    {
        Assert.False(WebAppView.ShowsLoadFailure(true, CoreWebView2WebErrorStatus.Unknown, 0));
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;
}
