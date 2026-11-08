using System.IO;
using TvLauncher.Config;
using TvLauncher.Remote;

namespace TvLauncher.Tests;

public sealed class RemoteKeysTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tvlauncher-tests-" + Guid.NewGuid().ToString("N"));

    public RemoteKeysTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // Builds a config the way launcher.json does, so the keys under test come from the real loader.
    private LauncherConfig Config(string homeKeyVk, int backKeyVk)
    {
        var path = Path.Combine(_dir, "launcher.json");
        File.WriteAllText(path, $$"""
            {
              "Apps": [
                { "Id": "youtube", "Name": "YouTube", "Url": "https://www.youtube.com/tv", "UserAgent": "Cobalt/27",
                  "Back": "Page", "DirectPauseFallback": true, "Icon": "icons/youtube.svg" }
              ],
              "HomeKeyVk": {{homeKeyVk}},
              "BackKeyVk": {{backKeyVk}},
              "Location": { "Latitude": 10, "Longitude": 20 },
              "Locale": "en-GB",
              "Unit": "C",
              "Clock": "24",
              "BackgroundRotateSeconds": 60,
              "SpotlightFolders": [ "%LOCALAPPDATA%/Spotlight" ],
              "LogPath": "%LOCALAPPDATA%/TvLauncher/launcher.log"
            }
            """);
        return LauncherConfig.Load(path);
    }

    [Theory]
    [InlineData(RemoteKey.Up, 38)]
    [InlineData(RemoteKey.Down, 40)]
    [InlineData(RemoteKey.Left, 37)]
    [InlineData(RemoteKey.Right, 39)]
    [InlineData(RemoteKey.Ok, 13)]
    [InlineData(RemoteKey.Back, 166)]
    [InlineData(RemoteKey.Home, 172)]
    [InlineData(RemoteKey.PlayPause, 179)]
    [InlineData(RemoteKey.VolumeUp, 175)]
    [InlineData(RemoteKey.VolumeDown, 174)]
    public void MapsEveryKey(RemoteKey key, int expected)
    {
        // The mini PC's own setting: the remote's Home plus the keyboard's F12. The first entry is the Rii's.
        var config = Config("[172, 123]", 166);

        Assert.Equal(expected, RemoteKeys.VirtualKey(key, config));
    }

    [Fact]
    public void HomeFollowsConfig()
    {
        var config = Config("36", 8);

        Assert.Equal(36, RemoteKeys.VirtualKey(RemoteKey.Home, config));
        Assert.Equal(8, RemoteKeys.VirtualKey(RemoteKey.Back, config));
    }

    [Fact]
    public void EveryKeyIsMapped()
    {
        var config = Config("172", 166);

        foreach (var key in Enum.GetValues<RemoteKey>())
            Assert.True(RemoteKeys.VirtualKey(key, config) > 0, key.ToString());
    }

    [Theory]
    [InlineData(37, true)]
    [InlineData(38, true)]
    [InlineData(39, true)]
    [InlineData(40, true)]
    [InlineData(166, true)]
    [InlineData(172, true)]
    [InlineData(174, true)]
    [InlineData(175, true)]
    [InlineData(179, true)]
    [InlineData(183, true)]
    [InlineData(13, false)]
    [InlineData(165, false)]
    [InlineData(184, false)]
    public void ExtendedKeys(int vk, bool expected) => Assert.Equal(expected, RemoteKeys.IsExtended(vk));

    // Builds the two events only; SendInput is never called, so no key is pressed on the PC running the tests.
    [Theory]
    [InlineData(37, 0x0001u)]
    [InlineData(13, 0u)]
    public void InputsCarryTheScanCodeAndTheFlags(int vk, uint extended)
    {
        var inputs = SendInputKeyPresser.Inputs(vk);

        Assert.Equal(2, inputs.Length);
        var down = inputs[0].u.ki;
        var up = inputs[1].u.ki;
        Assert.All(inputs, i => Assert.Equal(1u, i.type));
        Assert.Equal((ushort)vk, down.wVk);
        Assert.Equal((ushort)vk, up.wVk);
        Assert.NotEqual(0, down.wScan);
        Assert.Equal(down.wScan, up.wScan);
        Assert.Equal(extended, down.dwFlags);
        Assert.Equal(extended | 0x0002u, up.dwFlags);
    }
}
