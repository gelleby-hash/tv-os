using System.IO;
using TvLauncher.Apps;
using TvLauncher.Bridge;
using TvLauncher.Config;
using TvLauncher.Diagnostics;
using TvLauncher.SystemActions;
using TvLauncher.Weather;

namespace TvLauncher.Tests;

public sealed class HostBridgeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tvlauncher-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _created = new();
    private readonly List<string> _viewCalls = new();
    private readonly FakePower _power = new();
    private readonly ManualTime _time = new();
    private readonly TimingLog _timing;
    private readonly AppSwitcher _switcher;
    private readonly HostBridge _bridge;

    public HostBridgeTests()
    {
        Directory.CreateDirectory(_dir);
        _timing = new TimingLog(LogPath, _time);
        _switcher = new AppSwitcher(Config(), CreateView, () => { }, () => { }, _ => { });
        _bridge = new HostBridge(_switcher, _power, _timing);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string LogPath => Path.Combine(_dir, "launcher.log");

    private LauncherConfig Config()
    {
        var path = Path.Combine(_dir, "launcher.json");
        File.WriteAllText(path, """
            {
              "Apps": [
                { "Id": "youtube", "Name": "YouTube", "Url": "https://www.youtube.com/tv" }
              ],
              "HomeKeyVk": 172,
              "Location": { "Latitude": 10, "Longitude": 20 }
            }
            """);
        return LauncherConfig.Load(path);
    }

    private Task<IAppView> CreateView(AppEntry entry)
    {
        _created.Add(entry.Id);
        return Task.FromResult<IAppView>(new FakeAppView(entry.Id, _viewCalls));
    }

    private string[] LogLines() => File.Exists(LogPath) ? File.ReadAllLines(LogPath) : [];

    [Fact]
    public async Task Launch_CallsOpenWithId()
    {
        await _bridge.HandleAsync("""{"type":"launch","id":"youtube"}""");

        Assert.Equal(["youtube"], _created);
        Assert.Equal(["youtube.show"], _viewCalls);
        Assert.Equal("youtube", _switcher.CurrentAppId);
    }

    [Fact]
    public async Task Launch_UnknownId_IsLoggedNotThrown()
    {
        await _bridge.HandleAsync("""{"type":"launch","id":"nope"}""");

        Assert.Empty(_created);
        Assert.Contains(LogLines(), l => l.Contains("launch failed") && l.Contains("nope"));
    }

    [Fact]
    public async Task Shutdown_CallsPowerShutdown()
    {
        await _bridge.HandleAsync("""{"type":"shutdown"}""");

        Assert.Equal(["shutdown"], _power.Calls);
    }

    [Fact]
    public async Task Restart_CallsPowerRestart()
    {
        await _bridge.HandleAsync("""{"type":"restart"}""");

        Assert.Equal(["restart"], _power.Calls);
    }

    [Fact]
    public async Task Desktop_ShowsDesktopAndLeavesTheSwitcherAlone()
    {
        await _bridge.HandleAsync("""{"type":"desktop"}""");

        Assert.Equal(["desktop"], _power.Calls);
        Assert.Empty(_created);
        Assert.Null(_switcher.CurrentAppId);
    }

    [Fact]
    public async Task Ready_RaisesPageReady()
    {
        var ready = 0;
        _bridge.PageReady += () => ready++;

        await _bridge.HandleAsync("""{"type":"ready"}""");

        Assert.Equal(1, ready);
    }

    [Fact]
    public async Task ToggleBoost_RaisesBoostToggled()
    {
        var toggled = 0;
        _bridge.BoostToggled += () => toggled++;

        await _bridge.HandleAsync("""{"type":"toggleBoost"}""");

        Assert.Equal(1, toggled);
        Assert.Empty(_power.Calls);
        Assert.Empty(_created);
    }

    [Fact]
    public async Task LocationPick_RaisesPlacePickedWithRoundedCoordinates()
    {
        var picked = new List<Coordinates>();
        _bridge.PlacePicked += picked.Add;

        await _bridge.HandleAsync("""{"type":"locationPick","latitude":55.953,"longitude":-3.188}""");

        Assert.Equal([new Coordinates(55.95, -3.19)], picked);
        Assert.DoesNotContain(LogLines(), l => l.Contains("55.9") || l.Contains("3.18"));
    }

    [Theory]
    [InlineData("""{"type":"locationPick","latitude":91,"longitude":0}""")]
    [InlineData("""{"type":"locationPick","latitude":0,"longitude":-180.5}""")]
    [InlineData("""{"type":"locationPick","latitude":"55.9","longitude":-3.1}""")]
    [InlineData("""{"type":"locationPick","latitude":55.9}""")]
    [InlineData("""{"type":"locationPick","longitude":-3.1}""")]
    [InlineData("""{"type":"locationPick","latitude":null,"longitude":-3.1}""")]
    [InlineData("""{"type":"locationPick"}""")]
    public async Task LocationPick_WithAMissingNonNumericOrOutOfRangeValue_IsIgnored(string json)
    {
        var picked = 0;
        _bridge.PlacePicked += _ => picked++;

        await _bridge.HandleAsync(json);

        Assert.Equal(0, picked);
    }

    [Fact]
    public async Task LocationPick_WithANumberTooBigForADouble_RaisesNothingAndDoesNotThrow()
    {
        var picked = 0;
        _bridge.PlacePicked += _ => picked++;

        await _bridge.HandleAsync("""{"type":"locationPick","latitude":1e999,"longitude":1}""");

        Assert.Equal(0, picked);
    }

    [Fact]
    public async Task LocationOff_RaisesWeatherOffChosen()
    {
        var chosen = 0;
        _bridge.WeatherOffChosen += () => chosen++;

        await _bridge.HandleAsync("""{"type":"locationOff"}""");

        Assert.Equal(1, chosen);
    }

    [Fact]
    public async Task LocationWindows_RaisesWindowsLocationChosen()
    {
        var chosen = 0;
        _bridge.WindowsLocationChosen += () => chosen++;

        await _bridge.HandleAsync("""{"type":"locationWindows"}""");

        Assert.Equal(1, chosen);
    }

    [Fact]
    public async Task LocationTurnOn_RaisesTurnOnRequested()
    {
        var requested = 0;
        _bridge.TurnOnRequested += () => requested++;

        await _bridge.HandleAsync("""{"type":"locationTurnOn"}""");

        Assert.Equal(1, requested);
    }

    [Fact]
    public async Task AppsMove_RaisesAppMoveRequestedWithTheIdAndIndex()
    {
        var moves = new List<(string, int)>();
        _bridge.AppMoveRequested += (id, to) => moves.Add((id, to));

        await _bridge.HandleAsync("""{"type":"appsMove","id":"youtube","to":2}""");

        Assert.Equal([("youtube", 2)], moves);
    }

    [Theory]
    [InlineData("""{"type":"appsMove","id":"youtube"}""")]
    [InlineData("""{"type":"appsMove","to":1}""")]
    [InlineData("""{"type":"appsMove","id":"youtube","to":"1"}""")]
    [InlineData("""{"type":"appsMove","id":"youtube","to":null}""")]
    [InlineData("""{"type":"appsMove","id":7,"to":1}""")]
    [InlineData("""{"type":"appsMove","id":"youtube","to":1.5}""")]
    [InlineData("""{"type":"appsMove","id":"youtube","to":3000000000}""")]
    [InlineData("""{"type":"appsMove","id":"youtube","to":1e999}""")]
    public async Task AppsMove_WithAMissingOrWrongTypedFieldOrAnIndexThatIsNotAnInt_IsIgnored(string json)
    {
        var moves = 0;
        _bridge.AppMoveRequested += (_, _) => moves++;

        await _bridge.HandleAsync(json);

        Assert.Equal(0, moves);
    }

    [Theory]
    [InlineData("""{"type":"appsShow","id":"youtube","on":true}""", true)]
    [InlineData("""{"type":"appsShow","id":"youtube","on":false}""", false)]
    public async Task AppsShow_RaisesAppShowRequestedWithTheIdAndFlag(string json, bool on)
    {
        var shows = new List<(string, bool)>();
        _bridge.AppShowRequested += (id, flag) => shows.Add((id, flag));

        await _bridge.HandleAsync(json);

        Assert.Equal([("youtube", on)], shows);
    }

    [Theory]
    [InlineData("""{"type":"appsShow","id":"youtube"}""")]
    [InlineData("""{"type":"appsShow","on":true}""")]
    [InlineData("""{"type":"appsShow","id":"youtube","on":"true"}""")]
    [InlineData("""{"type":"appsShow","id":"youtube","on":1}""")]
    [InlineData("""{"type":"appsShow","id":null,"on":true}""")]
    public async Task AppsShow_WithAMissingOrWrongTypedField_IsIgnored(string json)
    {
        var shows = 0;
        _bridge.AppShowRequested += (_, _) => shows++;

        await _bridge.HandleAsync(json);

        Assert.Equal(0, shows);
    }

    [Fact]
    public async Task AppsVersion_RaisesAppVersionRequestedWithTheIdAndVersion()
    {
        var versions = new List<(string, string)>();
        _bridge.AppVersionRequested += (id, version) => versions.Add((id, version));

        await _bridge.HandleAsync("""{"type":"appsVersion","id":"channel4","version":"web"}""");

        Assert.Equal([("channel4", "web")], versions);
    }

    [Theory]
    [InlineData("""{"type":"appsVersion","id":"channel4"}""")]
    [InlineData("""{"type":"appsVersion","version":"web"}""")]
    [InlineData("""{"type":"appsVersion","id":"channel4","version":2}""")]
    [InlineData("""{"type":"appsVersion","id":"channel4","version":null}""")]
    public async Task AppsVersion_WithAMissingOrWrongTypedField_IsIgnored(string json)
    {
        var versions = 0;
        _bridge.AppVersionRequested += (_, _) => versions++;

        await _bridge.HandleAsync(json);

        Assert.Equal(0, versions);
    }

    [Theory]
    [InlineData("""{"type":"hdr","on":true}""", "hdr on")]
    [InlineData("""{"type":"hdr","on":false}""", "hdr off")]
    public async Task Hdr_IsLogged(string json, string expected)
    {
        await _bridge.HandleAsync(json);

        Assert.Contains(LogLines(), line => line.Split('	')[1] == expected);
        Assert.Empty(_power.Calls);
        Assert.Empty(_created);
    }

    [Theory]
    [InlineData("{\"type\":\"hdr\"}")]
    [InlineData("{\"type\":\"hdr\",\"on\":\"yes\"}")]
    public async Task Hdr_WithoutBoolean_IsIgnored(string json)
    {
        await _bridge.HandleAsync(json);

        Assert.DoesNotContain(LogLines(), line => line.Contains("hdr"));
    }

    [Fact]
    public async Task Key_IsLoggedWithWhatHadTheFocus()
    {
        await _bridge.HandleAsync("""{"type":"key","code":39,"id":"home:tile 0"}""");

        Assert.Contains(LogLines(), line => line.Split('\t')[1] == "page key 39 (home:tile 0)");
        Assert.Empty(_power.Calls);
        Assert.Empty(_created);
    }

    [Theory]
    [InlineData("""{"type":"key"}""")]
    [InlineData("""{"type":"key","code":"39"}""")]
    public async Task Key_WithoutCode_IsIgnored(string json)
    {
        await _bridge.HandleAsync(json);

        Assert.DoesNotContain(LogLines(), line => line.Contains("page key"));
    }

    [Fact]
    public async Task Pointer_IsLogged()
    {
        await _bridge.HandleAsync("""{"type":"pointer"}""");

        Assert.Contains(LogLines(), line => line.Split('\t')[1] == "page pointer");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"type\":")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"shutdown\"")]
    [InlineData("{}")]
    [InlineData("{\"type\":5}")]
    [InlineData("{\"type\":\"format-disk\"}")]
    [InlineData("{\"type\":\"launch\"}")]
    [InlineData("{\"type\":\"launch\",\"id\":7}")]
    public async Task Malformed_IsIgnored(string json)
    {
        var events = 0;
        _bridge.PageReady += () => events++;

        await _bridge.HandleAsync(json);

        Assert.Empty(_power.Calls);
        Assert.Empty(_created);
        Assert.Equal(0, events);
        Assert.Null(_switcher.CurrentAppId);
    }

    [Fact]
    public async Task HomeShown_LogsTimingSinceHomeKey()
    {
        _timing.Mark(TimingLog.HomeKey);
        _time.Advance(TimeSpan.FromMilliseconds(250));

        await _bridge.HandleAsync("""{"type":"homeShown","at":1234.5}""");

        var last = LogLines()[^1].Split('\t');
        Assert.Equal("homeShown", last[1]);
        Assert.Equal("250", last[2]);
    }

    private sealed class FakePower : IPowerActions
    {
        public List<string> Calls { get; } = new();
        public void Shutdown() => Calls.Add("shutdown");
        public void Restart() => Calls.Add("restart");
        public void ShowDesktop() => Calls.Add("desktop");
    }
}
