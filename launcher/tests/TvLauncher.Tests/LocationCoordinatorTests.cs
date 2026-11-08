using System.Text.Json;
using TvLauncher.Config;
using TvLauncher.Weather;

namespace TvLauncher.Tests;

public sealed class LocationCoordinatorTests
{
    private sealed class FakeSource(params LocationReading[] readings) : ILocationSource
    {
        private int _next;
        public List<TimeSpan> Timeouts { get; } = new();

        public Task<LocationReading> ReadAsync(TimeSpan timeout)
        {
            Timeouts.Add(timeout);
            return Task.FromResult(readings[Math.Min(_next++, readings.Length - 1)]);
        }
    }

    // The first read completes only when the test says so (a TaskCompletionSource nobody sets models a read that never ends).
    private sealed class GatedSource : ILocationSource
    {
        public TaskCompletionSource<LocationReading> Gate { get; } = new();
        public Task<LocationReading> ReadAsync(TimeSpan timeout) => Gate.Task;
    }

    private readonly List<LauncherState> _saved = new();
    private readonly List<string> _posts = new();
    private readonly List<string> _log = new();
    private LauncherState _state = new(BoostOn: true);

    private LocationCoordinator Make(ILocationSource source, Coordinates? config = null, string? region = "GB") =>
        new(source, config, region, () => _state, s => { _state = s; _saved.Add(s); },
            message => _posts.Add(JsonSerializer.Serialize(message)), _log.Add);

    private static LocationReading Fix(double lat, double lon) => new(true, new Coordinates(lat, lon));

    private JsonElement LastPost() => JsonDocument.Parse(_posts[^1]).RootElement.Clone();

    [Fact]
    public async Task Refresh_WithNoChoiceNoConfigAndAFix_PostsWindowsAndSavesTheLastFix()
    {
        var source = new FakeSource(Fix(55.95, -3.19));
        var coordinator = Make(source);

        await coordinator.RefreshAsync();

        Assert.Equal([TimeSpan.FromSeconds(10)], source.Timeouts);
        var post = LastPost();
        Assert.Equal("location", post.GetProperty("type").GetString());
        Assert.Equal("windows", post.GetProperty("source").GetString());
        Assert.Equal(55.95, post.GetProperty("latitude").GetDouble());
        Assert.Equal(-3.19, post.GetProperty("longitude").GetDouble());
        Assert.True(post.GetProperty("windowsOn").GetBoolean());
        Assert.Equal("GB", post.GetProperty("region").GetString());
        Assert.False(post.GetProperty("canTurnOn").GetBoolean());
        Assert.Equal(new Coordinates(55.95, -3.19), Assert.Single(_saved).LastWindows);
    }

    [Fact]
    public async Task Post_HasExactlyTheContractKeys()
    {
        await Make(new FakeSource(Fix(55.95, -3.19))).RefreshAsync();

        var keys = LastPost().EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(["canTurnOn", "latitude", "longitude", "region", "source", "type", "windowsOn"], keys);
    }

    [Fact]
    public async Task Refresh_WithAConfigPlaceAndNoChoice_PostsConfigAndDoesNotSaveAnUnchangedFix()
    {
        _state = _state with { LastWindows = new Coordinates(55.95, -3.19) };
        var coordinator = Make(new FakeSource(Fix(55.95, -3.19)), config: new Coordinates(55.9, -3.1));

        await coordinator.RefreshAsync();

        var post = LastPost();
        Assert.Equal("config", post.GetProperty("source").GetString());
        Assert.Equal(55.9, post.GetProperty("latitude").GetDouble());
        Assert.Empty(_saved);
    }

    [Fact]
    public async Task Refresh_WithoutAFix_UsesTheLastWindowsFix()
    {
        _state = _state with { LastWindows = new Coordinates(51.5, -0.12), Location = new LocationChoice(ChoiceKind.Windows, null) };

        await Make(new FakeSource(new LocationReading(true, null))).RefreshAsync();

        Assert.Equal("lastWindows", LastPost().GetProperty("source").GetString());
        Assert.Empty(_saved);
    }

    [Fact]
    public async Task Pick_SavesAPickedChoiceAndPostsPicked()
    {
        var coordinator = Make(new FakeSource(Fix(55.95, -3.19)), config: new Coordinates(55.9, -3.1));
        await coordinator.RefreshAsync();

        await coordinator.PickAsync(new Coordinates(51.5, -0.12));

        var post = LastPost();
        Assert.Equal("picked", post.GetProperty("source").GetString());
        Assert.Equal(51.5, post.GetProperty("latitude").GetDouble());
        Assert.Equal(-0.12, post.GetProperty("longitude").GetDouble());
        Assert.Equal(new LocationChoice(ChoiceKind.Picked, new Coordinates(51.5, -0.12)), _state.Location);
    }

    [Fact]
    public async Task ChooseOff_PostsOffWithNoCoordinateKeys()
    {
        var coordinator = Make(new FakeSource(Fix(55.95, -3.19)));
        await coordinator.RefreshAsync();

        await coordinator.ChooseOffAsync();

        var post = LastPost();
        Assert.Equal("off", post.GetProperty("source").GetString());
        Assert.False(post.TryGetProperty("latitude", out _));
        Assert.False(post.TryGetProperty("longitude", out _));
        Assert.True(post.GetProperty("windowsOn").GetBoolean());
        Assert.Equal(ChoiceKind.Off, _state.Location!.Kind);
    }

    [Fact]
    public async Task ChooseWindows_WithNoFixAndNoLastFix_PostsNone()
    {
        var coordinator = Make(new FakeSource(new LocationReading(false, null)), config: new Coordinates(55.9, -3.1));

        await coordinator.ChooseWindowsAsync();

        var post = LastPost();
        Assert.Equal("none", post.GetProperty("source").GetString());
        Assert.False(post.TryGetProperty("latitude", out _));
        Assert.False(post.GetProperty("windowsOn").GetBoolean());
        Assert.Equal(ChoiceKind.Windows, _state.Location!.Kind);
    }

    [Fact]
    public async Task ChooseWindows_ReadsWindowsAgainBeforeResolving()
    {
        var source = new FakeSource(new LocationReading(false, null), Fix(53.48, -2.24));
        var coordinator = Make(source);
        await coordinator.RefreshAsync();

        await coordinator.ChooseWindowsAsync();

        Assert.Equal(2, source.Timeouts.Count);
        var post = LastPost();
        Assert.Equal("windows", post.GetProperty("source").GetString());
        Assert.Equal(53.48, post.GetProperty("latitude").GetDouble());
        Assert.True(post.GetProperty("windowsOn").GetBoolean());
        Assert.Equal(new Coordinates(53.48, -2.24), _state.LastWindows);
    }

    [Fact]
    public async Task Region_IsNullInThePostWhenUnknown()
    {
        await Make(new FakeSource(Fix(55.95, -3.19)), region: null).RefreshAsync();

        Assert.Equal(JsonValueKind.Null, LastPost().GetProperty("region").ValueKind);
    }

    [Fact]
    public async Task Log_NamesTheSourceOncePerPostAndNeverCoordinates()
    {
        var coordinator = Make(new FakeSource(Fix(55.95, -3.19)), config: new Coordinates(55.9, -3.1));

        await coordinator.RefreshAsync();
        await coordinator.PickAsync(new Coordinates(51.5, -0.12));
        await coordinator.ChooseWindowsAsync();

        // Refresh posts the config place at once, then again after the read: two "config" lines.
        Assert.Equal(["location: config", "location: config", "location: picked", "location: windows"],
            _log.Where(l => l.StartsWith("location: ")).ToArray());
        Assert.Equal(_posts.Count, _log.Count(l => l.StartsWith("location: ")));
        Assert.DoesNotContain(_log, l => l.Contains("55.9") || l.Contains("3.19") || l.Contains("51.5") || l.Contains("0.12"));
    }

    [Fact]
    public async Task Refresh_WithAConfigPlace_PostsConfigBeforeTheReadCompletes()
    {
        var source = new GatedSource();
        var coordinator = Make(source, config: new Coordinates(55.9, -3.1));

        var refresh = coordinator.RefreshAsync();

        Assert.False(refresh.IsCompleted);
        Assert.Equal("config", LastPost().GetProperty("source").GetString());
        Assert.Equal(55.9, LastPost().GetProperty("latitude").GetDouble());
        Assert.False(LastPost().GetProperty("windowsOn").GetBoolean());

        source.Gate.SetResult(Fix(55.95, -3.19));
        await refresh;

        Assert.Equal(2, _posts.Count);
        Assert.Equal("config", LastPost().GetProperty("source").GetString());
        Assert.True(LastPost().GetProperty("windowsOn").GetBoolean());
    }

    [Fact]
    public void Refresh_WithAReadThatNeverCompletes_StillHasPostedTheConfigPlace()
    {
        var coordinator = Make(new GatedSource(), config: new Coordinates(55.9, -3.1));

        _ = coordinator.RefreshAsync(); // never awaited: the read never ends

        Assert.Equal("config", Assert.Single(_posts.Select(p => JsonDocument.Parse(p).RootElement.GetProperty("source").GetString())));
    }

    [Fact]
    public async Task Refresh_WithAPickedPlace_PostsPickedBeforeTheReadCompletes()
    {
        _state = _state with { Location = new LocationChoice(ChoiceKind.Picked, new Coordinates(51.5, -0.12)) };
        var source = new GatedSource();

        var refresh = Make(source, config: new Coordinates(55.9, -3.1)).RefreshAsync();

        Assert.Equal("picked", LastPost().GetProperty("source").GetString());
        source.Gate.SetResult(new LocationReading(false, null));
        await refresh;
        Assert.Equal("picked", LastPost().GetProperty("source").GetString());
    }

    [Fact]
    public async Task Refresh_WithNothingToShow_PostsNothingUntilTheReadCompletes()
    {
        var source = new GatedSource();

        var refresh = Make(source).RefreshAsync();

        Assert.Empty(_posts);

        source.Gate.SetResult(new LocationReading(false, null));
        await refresh;

        Assert.Equal("none", Assert.Single(_posts.Select(p => JsonDocument.Parse(p).RootElement.GetProperty("source").GetString())));
    }

    [Fact]
    public async Task ChooseWindows_APickMadeWhileTheReadIsPending_Survives()
    {
        var source = new GatedSource();
        var coordinator = Make(source);

        var choose = coordinator.ChooseWindowsAsync();
        await coordinator.PickAsync(new Coordinates(51.5, -0.12));
        source.Gate.SetResult(Fix(53.48, -2.24));
        await choose;

        Assert.Equal(new LocationChoice(ChoiceKind.Picked, new Coordinates(51.5, -0.12)), _state.Location);
        Assert.Equal("picked", LastPost().GetProperty("source").GetString());
        Assert.Equal(51.5, LastPost().GetProperty("latitude").GetDouble());
    }

    [Fact]
    public async Task Post_CanTurnOnIsTrueWhileWindowsIsOffAndFalseWhileItIsOn()
    {
        var off = Make(new FakeSource(new LocationReading(false, null)), config: new Coordinates(55.9, -3.1));
        await off.RefreshAsync();
        Assert.True(LastPost().GetProperty("canTurnOn").GetBoolean());
        Assert.False(LastPost().GetProperty("windowsOn").GetBoolean());

        _posts.Clear();
        await Make(new FakeSource(Fix(55.95, -3.19))).RefreshAsync();
        Assert.False(LastPost().GetProperty("canTurnOn").GetBoolean());
    }

    [Fact]
    public void BeginTurnOn_SetsPendingAndLogsWithoutPosting()
    {
        var coordinator = Make(new FakeSource(new LocationReading(false, null)));
        Assert.False(coordinator.TurnOnPending);

        coordinator.BeginTurnOn();

        Assert.True(coordinator.TurnOnPending);
        Assert.Contains("location: turn on, settings opened", _log);
        Assert.Empty(_posts);
    }

    [Fact]
    public async Task FinishTurnOn_WithWindowsNowOnAndAFix_SavesAWindowsChoicePostsWindowsThenOnTrue()
    {
        var coordinator = Make(new FakeSource(new LocationReading(false, null), Fix(55.95, -3.19)));
        await coordinator.RefreshAsync();
        coordinator.BeginTurnOn();
        _posts.Clear();

        await coordinator.FinishTurnOnAsync();

        Assert.False(coordinator.TurnOnPending);
        Assert.Equal(new LocationChoice(ChoiceKind.Windows, null), _state.Location);
        Assert.Equal(2, _posts.Count);
        var location = JsonDocument.Parse(_posts[0]).RootElement;
        Assert.Equal("location", location.GetProperty("type").GetString());
        Assert.Equal("windows", location.GetProperty("source").GetString());
        Assert.True(location.GetProperty("windowsOn").GetBoolean());
        Assert.False(location.GetProperty("canTurnOn").GetBoolean());
        var turnOn = LastPost();
        Assert.Equal("locationTurnOn", turnOn.GetProperty("type").GetString());
        Assert.True(turnOn.GetProperty("on").GetBoolean());
        Assert.Equal(["type", "on"], turnOn.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Contains("location: turn on, windows on", _log);
    }

    [Fact]
    public async Task FinishTurnOn_WithWindowsStillOff_KeepsAPickedChoicePostsPickedThenOnFalse()
    {
        _state = _state with { Location = new LocationChoice(ChoiceKind.Picked, new Coordinates(51.5, -0.12)) };
        var coordinator = Make(new FakeSource(new LocationReading(false, null)));
        coordinator.BeginTurnOn();

        await coordinator.FinishTurnOnAsync();

        Assert.False(coordinator.TurnOnPending);
        Assert.Equal(new LocationChoice(ChoiceKind.Picked, new Coordinates(51.5, -0.12)), _state.Location);
        Assert.Empty(_saved);
        Assert.Equal(2, _posts.Count);
        var location = JsonDocument.Parse(_posts[0]).RootElement;
        Assert.Equal("picked", location.GetProperty("source").GetString());
        Assert.True(location.GetProperty("canTurnOn").GetBoolean());
        var turnOn = LastPost();
        Assert.Equal("locationTurnOn", turnOn.GetProperty("type").GetString());
        Assert.False(turnOn.GetProperty("on").GetBoolean());
        Assert.Contains("location: turn on, windows off", _log);
    }

    [Fact]
    public async Task FinishTurnOn_WithWindowsStillOffAndAnOffChoice_KeepsOff()
    {
        _state = _state with { Location = new LocationChoice(ChoiceKind.Off, null) };
        var coordinator = Make(new FakeSource(new LocationReading(false, null)));

        await coordinator.FinishTurnOnAsync();

        Assert.Equal(ChoiceKind.Off, _state.Location!.Kind);
        Assert.Equal("off", JsonDocument.Parse(_posts[0]).RootElement.GetProperty("source").GetString());
    }

    [Fact]
    public async Task TurnOnLogLines_NeverHoldCoordinates()
    {
        var coordinator = Make(new FakeSource(Fix(55.95, -3.19)));
        coordinator.BeginTurnOn();

        await coordinator.FinishTurnOnAsync();

        Assert.Contains(_log, l => l.StartsWith("location: turn on"));
        Assert.DoesNotContain(_log, l => l.Contains("55.9") || l.Contains("3.19"));
    }
}
