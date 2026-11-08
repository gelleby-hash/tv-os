using System.Diagnostics;
using TvLauncher.Weather;

namespace TvLauncher.Tests;

public sealed class WindowsLocationTests
{
    // The three consent values Windows keeps for location: the whole PC, this user, and desktop apps for this user.
    [Theory]
    [InlineData("Allow", "Allow", "Allow", true)]
    [InlineData("Allow", null, null, true)]
    [InlineData("Deny", "Allow", "Allow", false)]
    [InlineData(null, "Allow", "Allow", false)]
    [InlineData("Allow", "Deny", "Allow", false)]
    [InlineData("Allow", "Allow", "Deny", false)]
    public void Setting_AllowsDesktopApps_OnlyWhenNothingDenies(string? machine, string? user, string? desktopApps, bool expected)
    {
        var values = new Dictionary<string, string?> { ["machine"] = machine, ["user"] = user, ["desktopApps"] = desktopApps };

        Assert.Equal(expected, WindowsLocationSetting.AllowsDesktopApps(name => values[name]));
    }

    // Asking Windows while location is off shows its "Let Windows and apps access your location?" prompt and waits on
    // it (run-through 2026-11-02: 11.1 s), so with the setting off the read must not ask at all.
    [Fact]
    public async Task Read_WithTheSettingOff_ReturnsOffAtOnce()
    {
        var watch = Stopwatch.StartNew();

        var reading = await new WindowsLocation(settingOn: () => false).ReadAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new LocationReading(false, null), reading);
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(500), $"took {watch.Elapsed}");
    }
}
