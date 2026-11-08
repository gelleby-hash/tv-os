using TvLauncher.Remote;

namespace TvLauncher.Tests;

public sealed class RemoteProtocolTests
{
    [Theory]
    [InlineData("up", RemoteKey.Up)]
    [InlineData("down", RemoteKey.Down)]
    [InlineData("left", RemoteKey.Left)]
    [InlineData("right", RemoteKey.Right)]
    [InlineData("ok", RemoteKey.Ok)]
    [InlineData("back", RemoteKey.Back)]
    [InlineData("home", RemoteKey.Home)]
    [InlineData("playPause", RemoteKey.PlayPause)]
    [InlineData("volumeUp", RemoteKey.VolumeUp)]
    [InlineData("volumeDown", RemoteKey.VolumeDown)]
    public void ParsesEachKey(string name, RemoteKey key)
    {
        var message = RemoteProtocol.Parse($$"""{"v":1,"type":"press","key":"{{name}}"}""");

        Assert.Equal(new PressMessage(key), message);
    }

    [Fact]
    public void ParsesMicStartAndStop()
    {
        Assert.IsType<MicStartMessage>(RemoteProtocol.Parse("""{"v":1,"type":"micStart"}"""));
        Assert.IsType<MicStopMessage>(RemoteProtocol.Parse("""{"v":1,"type":"micStop"}"""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"v":2,"type":"press","key":"up"}""")]
    [InlineData("""{"v":"1","type":"press","key":"up"}""")]
    [InlineData("""{"type":"press","key":"up"}""")]
    [InlineData("""{"v":1,"type":"press","key":"power"}""")]
    [InlineData("""{"v":1,"type":"press","key":"Up"}""")]
    [InlineData("""{"v":1,"type":"press"}""")]
    [InlineData("""{"v":1,"type":"shutdown"}""")]
    [InlineData("""{"v":1,"type":"press","key":"up""")]
    [InlineData("hello")]
    [InlineData("""{"v":1,"type":"\uDC00"}""")]
    [InlineData("""{"\uDC00":0}""")]
    [InlineData("""{"v":1,"type":"press","key":"\uD800"}""")]
    public void RejectsJunk(string json)
    {
        Assert.Null(RemoteProtocol.Parse(json));
    }

    [Fact]
    public void StateAndMicJson()
    {
        Assert.Equal("""{"v":1,"type":"state","app":null,"micAvailable":false}""", RemoteProtocol.State(null, false));
        Assert.Equal("""{"v":1,"type":"state","app":"youtube","micAvailable":true}""", RemoteProtocol.State("youtube", true));
        Assert.Equal("""{"v":1,"type":"mic","listening":false,"reason":"timeout"}""", RemoteProtocol.Mic(false, "timeout"));
        Assert.Equal("""{"v":1,"type":"mic","listening":true,"reason":null}""", RemoteProtocol.Mic(true, null));
    }
}
