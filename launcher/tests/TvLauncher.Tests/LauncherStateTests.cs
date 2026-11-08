using System.IO;
using TvLauncher.Config;
using TvLauncher.Weather;

namespace TvLauncher.Tests;

public sealed class LauncherStateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tvlauncher-tests-" + Guid.NewGuid().ToString("N"));

    public LauncherStateTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void State_Missing_BoostOn()
    {
        var logged = new List<string>();

        var state = LauncherState.Load(Path.Combine(_dir, "state.json"), logged.Add);

        Assert.True(state.BoostOn);
        Assert.Single(logged);
    }

    [Fact]
    public void State_Corrupt_BoostOn()
    {
        var path = Path.Combine(_dir, "state.json");
        File.WriteAllText(path, "{ not json");
        var logged = new List<string>();

        var state = LauncherState.Load(path, logged.Add);

        Assert.True(state.BoostOn);
        Assert.Single(logged);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void State_SaveThenLoad_RoundTrips(bool boostOn)
    {
        var path = Path.Combine(_dir, "state.json");

        Assert.True(new LauncherState(boostOn).Save(path));

        Assert.Equal(boostOn, LauncherState.Load(path).BoostOn);
        Assert.Equal(new[] { "state.json" }, Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void State_SaveUnwritable_ReturnsFalse()
    {
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "a file where a folder should be");
        var logged = new List<string>();

        var saved = new LauncherState(false).Save(Path.Combine(blocker, "state.json"), logged.Add);

        Assert.False(saved);
        Assert.Single(logged);
    }

    [Fact]
    public void State_OldFile_LocationAndLastWindowsNull()
    {
        var path = Path.Combine(_dir, "state.json");
        File.WriteAllText(path, "{\"BoostOn\":true}");

        var state = LauncherState.Load(path);

        Assert.True(state.BoostOn);
        Assert.Null(state.Location);
        Assert.Null(state.LastWindows);
    }

    [Theory]
    [InlineData(ChoiceKind.Picked)]
    [InlineData(ChoiceKind.Off)]
    [InlineData(ChoiceKind.Windows)]
    public void State_SaveThenLoad_RoundTripsLocation(ChoiceKind kind)
    {
        var path = Path.Combine(_dir, "state.json");
        var choice = new LocationChoice(kind, kind == ChoiceKind.Picked ? new Coordinates(51.5, -0.12) : null);
        var last = new Coordinates(53.48, -2.24);

        Assert.True(new LauncherState(false, choice, last).Save(path));
        var loaded = LauncherState.Load(path);

        Assert.False(loaded.BoostOn);
        Assert.Equal(choice, loaded.Location);
        Assert.Equal(last, loaded.LastWindows);
    }

    [Fact]
    public void State_Save_WritesKindAsItsName()
    {
        var path = Path.Combine(_dir, "state.json");

        new LauncherState(true, new LocationChoice(ChoiceKind.Picked, new Coordinates(51.5, -0.12))).Save(path);

        var text = File.ReadAllText(path);
        Assert.Contains("\"Kind\":\"Picked\"", text);
        Assert.Contains("\"Latitude\":51.5", text);
    }

    [Fact]
    public void State_Load_RoundsStoredCoordinates()
    {
        var path = Path.Combine(_dir, "state.json");
        File.WriteAllText(path, "{\"BoostOn\":true,\"Location\":{\"Kind\":\"Picked\",\"Place\":{\"Latitude\":51.50432,\"Longitude\":-0.12678}},\"LastWindows\":{\"Latitude\":53.4812,\"Longitude\":-2.2449}}");

        var state = LauncherState.Load(path);

        Assert.Equal(new LocationChoice(ChoiceKind.Picked, new Coordinates(51.5, -0.13)), state.Location);
        Assert.Equal(new Coordinates(53.48, -2.24), state.LastWindows);
    }

    [Fact]
    public void State_UnknownKind_LoadsAsNoChoiceAndKeepsBoost()
    {
        var path = Path.Combine(_dir, "state.json");
        File.WriteAllText(path, "{\"BoostOn\":false,\"Location\":{\"Kind\":\"Banana\"}}");

        var state = LauncherState.Load(path);

        Assert.False(state.BoostOn);
        Assert.Null(state.Location);
    }

    [Theory]
    [InlineData("{\"BoostOn\":true,\"Location\":{\"Kind\":\"Picked\"}}")]
    [InlineData("{\"BoostOn\":true,\"Location\":{\"Kind\":\"Picked\",\"Place\":{\"Latitude\":95,\"Longitude\":0}}}")]
    [InlineData("{\"BoostOn\":true,\"Location\":{\"Kind\":\"Picked\",\"Place\":{\"Latitude\":0,\"Longitude\":181}}}")]
    public void State_PickedWithoutValidPlace_LoadsAsNoChoice(string json)
    {
        var path = Path.Combine(_dir, "state.json");
        File.WriteAllText(path, json);

        var state = LauncherState.Load(path);

        Assert.True(state.BoostOn);
        Assert.Null(state.Location);
    }

    [Fact]
    public void State_SaveThenLoad_RoundTripsApps()
    {
        var path = Path.Combine(_dir, "state.json");
        var apps = new AppChoice[] { new("b", false, "y"), new("a", true, "default") };

        Assert.True(new LauncherState(true, Apps: apps).Save(path));
        var loaded = LauncherState.Load(path);

        Assert.Equal(apps, loaded.Apps);
        Assert.Contains("\"VersionId\":\"y\"", File.ReadAllText(path));
    }

    [Fact]
    public void State_OldFile_AppsNull()
    {
        var path = Path.Combine(_dir, "state.json");
        File.WriteAllText(path, "{\"BoostOn\":false,\"Location\":{\"Kind\":\"Off\"}}");

        var state = LauncherState.Load(path);

        Assert.Null(state.Apps);
        Assert.False(state.BoostOn);
        Assert.Equal(new LocationChoice(ChoiceKind.Off, null), state.Location);
    }

    [Fact]
    public void State_AppWithNullId_IsSkipped()
    {
        var path = Path.Combine(_dir, "state.json");
        File.WriteAllText(path, "{\"BoostOn\":true,\"Apps\":[{\"Shown\":true,\"VersionId\":\"x\"},{\"Id\":\"a\",\"Shown\":false,\"VersionId\":\"default\"}]}");

        var state = LauncherState.Load(path);

        Assert.Equal(new[] { new AppChoice("a", false, "default") }, state.Apps);
    }

    [Theory]
    [InlineData("\"junk\"")]
    [InlineData("{\"Id\":\"a\"}")]
    [InlineData("42")]
    [InlineData("null")]
    public void State_AppsNotAnArray_LoadsAsNull_AndKeepsTheRest(string apps)
    {
        var path = Path.Combine(_dir, "state.json");
        File.WriteAllText(path, "{\"BoostOn\":false,\"Location\":{\"Kind\":\"Off\"},\"LastWindows\":{\"Latitude\":53.48,\"Longitude\":-2.24},\"Apps\":" + apps + "}");

        var state = LauncherState.Load(path);

        Assert.Null(state.Apps);
        Assert.False(state.BoostOn);
        Assert.Equal(new LocationChoice(ChoiceKind.Off, null), state.Location);
        Assert.Equal(new Coordinates(53.48, -2.24), state.LastWindows);
    }

    [Fact]
    public void State_BadAppsElements_AreSkippedOrDefaulted_AndKeepTheRest()
    {
        var path = Path.Combine(_dir, "state.json");
        File.WriteAllText(path, "{\"BoostOn\":false,\"Location\":{\"Kind\":\"Windows\"},\"Apps\":[\"x\",null,7,{\"Id\":5},{\"Id\":\"a\",\"Shown\":\"no\",\"VersionId\":3},{\"Id\":\"b\",\"Shown\":false,\"VersionId\":\"y\"}]}");

        var state = LauncherState.Load(path);

        Assert.Equal(new[] { new AppChoice("a", true, ""), new AppChoice("b", false, "y") }, state.Apps);
        Assert.False(state.BoostOn);
        Assert.Equal(new LocationChoice(ChoiceKind.Windows, null), state.Location);
    }

    [Fact]
    public void State_InvalidLastWindows_LoadsAsNull()
    {
        var path = Path.Combine(_dir, "state.json");
        File.WriteAllText(path, "{\"BoostOn\":true,\"LastWindows\":{\"Latitude\":100,\"Longitude\":0}}");

        Assert.Null(LauncherState.Load(path).LastWindows);
    }
}
